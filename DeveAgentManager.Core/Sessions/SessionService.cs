using System.Text;
using System.Text.Json;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Sessions;

/// <summary>An export written to a temporary file: <see cref="Open"/> streams it once and deletes it when closed.</summary>
public sealed record SessionExport(string FileName, string ContentType, string Path, long Bytes)
{
    public FileStream Open() => new(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.DeleteOnClose);

    internal string ReadAllText()
    {
        using var reader = new StreamReader(Open());
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Keeps a copy of every agent's conversation in the Manager: the harness's session file is synced from the Runner and
/// stored as a DeveSession in /data/sessions, so it survives the Runner and can be continued somewhere else.
/// </summary>
public sealed class SessionService(
    IDbContextFactory<ManagerDbContext> dbFactory,
    RunnerShell shell,
    IEnumerable<ISessionFormat> formats,
    IEnumerable<IHarness> harnesses,
    SessionImageStore images,
    SessionFileStore files,
    ManagerOptions options,
    ManagerEvents events,
    ILogger<SessionService> logger,
    AgentArtifacts? artifacts = null,
    HistorySyncSettingsStore? historySettings = null,
    DeveAgentManager.Core.Pets.PetService? pets = null)
{
    private readonly AgentArtifacts _artifacts = artifacts ?? new(dbFactory, options, files, images, new AccessControl(dbFactory), events);
    internal Task PreserveArtifactsAsync(Guid agentId, CancellationToken ct) => _artifacts.PreserveAsync(agentId, ct);
    internal Task<bool> ArtifactPublicationBelongsAsync(Guid runner, Guid agent, Guid session, CancellationToken ct) =>
        _artifacts.OwnsSourceAsync(runner, agent, session, ct);
    internal Task ArtifactsChangedAsync(Guid sessionId, CancellationToken ct) => _artifacts.NotifyAsync(sessionId, ct);

    private readonly NativeHistoryStore _native = new(options.SessionsDirectory, logger: logger);
    private readonly SessionCompanionStore _companions = new(options.SessionsDirectory);
    private readonly HistorySyncSettingsStore _historySettings = historySettings ?? new(dbFactory, events);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _petUsageVersions = new();
    private async Task CreditPetUsageAsync(Guid sessionId, string harnessId, string? nativeId, CancellationToken ct)
    {
        if (pets is null || nativeId is null) return;
        var summary = await _native.SummaryAsync(sessionId, ct);
        if (summary?.UsageFile is not { } version || _petUsageVersions.GetValueOrDefault(sessionId) == version) return;
        var usage = await _native.UsageAsync(sessionId, summary, ct);
        await pets.CreditAsync(sessionId, harnessId + ":" + nativeId, usage, ct);
        _petUsageVersions[sessionId] = version;
    }
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly Dictionary<Guid, (DateTime Version, string Text)> _previews = new();
    /// <summary>The latest reply of each agent whose conversations <paramref name="user"/> can read.</summary>
    public async Task<List<DeveAgentManager.Contracts.AgentReplyPreviewDto>> ReplyPreviewsAsync(CurrentUser user, CancellationToken ct)
    {
        await _previewGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var saved = await db.Sessions.AsNoTracking()
                .Where(s => s.AgentId != null && (user.Has(UserRoles.ConversationManager) || s.Agent!.OwnerId == user.Id || s.Agent.Shares.Any(x => x.UserId == user.Id)))
                .Select(s => new { s.Id, s.AgentId, s.UpdatedAt, s.LastSyncedAt }).ToListAsync(ct);
            var ids = saved.Select(s => s.Id).ToHashSet();
            foreach (var id in _previews.Keys.Where(id => !ids.Contains(id)).ToArray()) _previews.Remove(id);
            var result = new List<DeveAgentManager.Contracts.AgentReplyPreviewDto>();
            foreach (var session in saved.OrderByDescending(s => s.UpdatedAt).DistinctBy(s => s.AgentId))
            {
                var version = session.LastSyncedAt ?? session.UpdatedAt;
                if (!_previews.TryGetValue(session.Id, out var cached) || cached.Version != version)
                {
                    try
                    {
                        var summary = await _native.SummaryAsync(session.Id, ct);
                        cached = (version, summary?.ReplyPreview ?? ReplyPreview(await LoadAsync(session.Id, ct)));
                        _previews[session.Id] = cached;
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or FormatException)
                    { logger.LogDebug(ex, "Could not load reply preview for {Session}", session.Id); continue; }
                }
                if (cached.Text.Length > 0) result.Add(new(session.AgentId!.Value, cached.Text));
            }
            return result;
        }
        finally { _previewGate.Release(); }
    }

    internal static string ReplyPreview(DeveSession conversation)
    {
        var text = conversation.ActivePath().Where(e => e.Kind == DeveEntryKind.Message && e.Role == DeveRole.Assistant)
            .Select(e => e.Text ?? string.Join("\n", (e.Content ?? []).Where(b => b.Type == "text").Select(b => b.Text)))
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 280 ? text[..280] + "…" : text;
    }

    // Serializes selection, final sync, rename and deletion. Never hold a DB transaction during SSH.
    private readonly SemaphoreSlim _operations = new(1, 1);
    internal async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        return new Release(_operations);
    }
    private sealed class Release(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }

    private const string LocateMarker = "__DAM_SESSION__";

    private readonly Dictionary<string, ISessionFormat> _formats = formats.ToDictionary(f => f.HarnessId);
    private readonly Dictionary<string, string> harnessDisplayNames = harnesses.ToDictionary(h => h.Id, h => h.DisplayName);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool SupportsHarness(string harnessId) => _formats.ContainsKey(harnessId);

    /// <summary>The conversations a user can read, newest first.</summary>
    public async Task<List<StoredSession>> ListVisibleAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await WithAccess(AccessControl.VisibleSessions(db, user)).OrderByDescending(s => s.UpdatedAt).ToListAsync(cancellationToken);
        foreach (var row in rows) UpdateSizes(row);
        return rows;
    }

    public async Task<StoredSession?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await WithAccess(db.Sessions).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (row != null) UpdateSizes(row);
        return row;
    }

    /// <summary>With what deciding access to a conversation (and to its agent) needs: owners, shares, the Runner's members.</summary>
    private static IQueryable<StoredSession> WithAccess(IQueryable<StoredSession> sessions) => sessions.AsNoTracking().AsSplitQuery()
        .Include(s => s.Owner).Include(s => s.Shares).ThenInclude(x => x.User)
        .Include(s => s.Agent).ThenInclude(a => a!.Runner).ThenInclude(r => r!.Members)
        .Include(s => s.Agent).ThenInclude(a => a!.Shares)
        .Include(s => s.Agent).ThenInclude(a => a!.Owner)
        .Include(s => s.Agent).ThenInclude(a => a!.Account).ThenInclude(a => a!.CreatedBy)
        .Include(s => s.WorkspaceAgent).ThenInclude(a => a!.Shares)
        .Include(s => s.WorkspaceAgent).ThenInclude(a => a!.Runner).ThenInclude(r => r!.Members)
        // After its workspace is removed, a conversation's access follows its project's sharing.
        .Include(s => s.Project).ThenInclude(p => p!.Agents).ThenInclude(a => a.Shares)
        .Include(s => s.Project).ThenInclude(p => p!.Agents).ThenInclude(a => a.Runner).ThenInclude(r => r!.Members);

    public async Task<StoredSession?> GetForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.AgentId == agentId, cancellationToken);
    }

    // A whole conversation in memory takes many times its saved size (a 100 MB history about 1.2 GB while it is read and
    // written). Views and exports load one at a time, so several never add up.
    private readonly SemaphoreSlim _wholeLoads = new(1, 1);
    private const long LoggedLoadBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The conversation for the conversation page, serialized straight to <paramref name="output"/>. One larger than
    /// <see cref="ManagerOptions.ConversationViewMaxBytes"/> is refused instead of loaded.
    /// </summary>
    public async Task WriteContentAsync(Guid sessionId, Stream output, CancellationToken cancellationToken)
    {
        var stored = await GetAsync(sessionId, cancellationToken) ?? throw new KeyNotFoundException("Session not found.");
        if (stored.SavedSize > options.ConversationViewMaxBytes)
            throw new ConversationTooLargeException(
                $"This conversation is {stored.SavedSize / 1048576} MB, more than the {options.ConversationViewMaxBytes / 1048576} MB the Manager shows here.");
        await _wholeLoads.WaitAsync(cancellationToken);
        try
        {
            // Logged before loading too: if the Manager runs out of memory, the log still says what it was doing.
            var large = stored.SavedSize >= LoggedLoadBytes;
            if (large) logger.LogInformation("Loading conversation {Session} ({Size:0.0} MB saved) to show it", sessionId, Megabytes(stored.SavedSize));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var session = await LoadAsync(sessionId, cancellationToken);
            await session.WriteAsync(output, cancellationToken);
            if (large) logger.LogInformation("Showed conversation {Session} ({Entries} entries) in {Seconds:0.0} s", sessionId, session.Entries.Count, watch.Elapsed.TotalSeconds);
        }
        finally { _wholeLoads.Release(); }
    }

    /// <summary>The stored conversation, or an empty one when nothing was synced yet.</summary>
    public async Task<DeveSession> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(sessionId);
        var result = await _native.LoadAsync(sessionId, _formats, cancellationToken, (session, manifest, token) =>
            _formats.GetValueOrDefault(session.Sources.FirstOrDefault()?.Harness ?? "") is { } format
                ? _companions.ApplyAsync(sessionId, format, session, token, manifest) : Task.CompletedTask);
        if (result is null)
        {
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                result = await DeveSession.ReadAsync(stream, cancellationToken);
            }
            else result = new DeveSession();
        }
        result.Migration = await ReadMigrationAsync(sessionId, result, cancellationToken) ?? result.Migration;
        return result;
    }

    /// <summary>
    /// Registers the session of a new agent. With <paramref name="continueSessionId"/>, the stored conversation is written
    /// to the Runner first, so the harness starts with the full history. Someone else's conversation (one shared with the
    /// agent's owner) is continued as a copy that belongs to the agent's owner; the original stays as it is. The session lock
    /// is held to check and to record, not while the conversation is written to the Runner.
    /// </summary>
    public async Task AttachToAgentAsync(Runner runner, Agent agent, Guid? continueSessionId, CancellationToken cancellationToken)
    {
        Attachment? attachment;
        using (await LockAsync(cancellationToken))
            attachment = await BeginAttachAsync(agent, continueSessionId, cancellationToken);
        using (attachment)
        {
            if (attachment is not null) await InstallAttachmentAsync(runner, agent, attachment, cancellationToken);
            using (await LockAsync(CancellationToken.None))
                await CompleteAttachAsync(runner, agent, attachment, CancellationToken.None);
        }
    }

    /// <summary><see cref="AttachToAgentAsync"/> for a caller that holds the session lock throughout.</summary>
    internal async Task AttachToAgentLockedAsync(Runner runner, Agent agent, Guid? continueSessionId, CancellationToken cancellationToken)
    {
        using var attachment = await BeginAttachAsync(agent, continueSessionId, cancellationToken);
        if (attachment is not null) await InstallAttachmentAsync(runner, agent, attachment, cancellationToken);
        await CompleteAttachAsync(runner, agent, attachment, cancellationToken);
    }

    /// <summary>A saved conversation being continued by a new agent: as it was checked, its history, and whether it is copied.</summary>
    internal sealed class Attachment(StoredSession stored, bool copy, NativeHistoryStore.Snapshot? snapshot) : IDisposable
    {
        public StoredSession Stored => stored;
        public bool Copy => copy;
        public Guid CopyId { get; } = Guid.NewGuid();
        public NativeHistoryStore.Snapshot? Snapshot => snapshot;
        public SessionMigrationReport? Report { get; set; }
        public void Dispose() => snapshot?.Dispose();
    }

    /// <summary>Under the session lock: checks the conversation to continue, verifies its latest history and opens it.</summary>
    internal async Task<Attachment?> BeginAttachAsync(Agent agent, Guid? continueSessionId, CancellationToken cancellationToken)
    {
        if (continueSessionId is not { } existingId) return null;
        await EnsureVerifiedLockedAsync(existingId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == existingId, cancellationToken) ?? throw new ArgumentException("Session not found.");
        // A saved conversation of the same project resumes in another of its workspaces as it is.
        var sameProject = agent.ProjectId is { } projectId && stored.AgentId is null && stored.OwnerId == agent.OwnerId
            && (stored.ProjectId == projectId || await db.Agents.AnyAsync(a => a.Id == stored.WorkspaceAgentId && a.ProjectId == projectId, cancellationToken));
        var copy = false;
        if (!sameProject && stored.OwnerId != agent.OwnerId)
        {
            copy = true;
        }
        else if (!sameProject && (stored.AgentId is not null || stored.WorkspaceAgentId is not null))
        {
            throw new InvalidOperationException("This session already belongs to a running agent. Open that agent instead.");
        }
        else if (!sameProject && await _artifacts.HasCollectionAsync(stored.Id, cancellationToken))
        {
            // A deleted agent's archives still reference this source folder. Continue an independent copy so
            // future publications by the new agent cannot appear in the old agent's remaining archives.
            copy = true;
        }

        return new Attachment(stored, copy, await _native.OpenAsync(existingId, cancellationToken));
    }

    /// <summary>Without the session lock: saves the copy's history, if any, and writes the conversation to the Runner.</summary>
    internal async Task InstallAttachmentAsync(Runner runner, Agent agent, Attachment attachment, CancellationToken cancellationToken)
    {
        var stored = attachment.Stored;
        try
        {
            if (attachment.Copy && HasSaved(stored.Id))
            {
                if (attachment.Snapshot is { } snapshot && _formats.GetValueOrDefault(snapshot.Harness) is { HasCompanionFiles: false } format)
                    await _native.CopyAsync(snapshot, attachment.CopyId, format, cancellationToken);
                else
                {
                    await _wholeLoads.WaitAsync(cancellationToken);
                    try { await SaveAsync(attachment.CopyId, await LoadAsync(stored.Id, cancellationToken), cancellationToken); }
                    finally { _wholeLoads.Release(); }
                }
            }

            attachment.Report = await InstallForHarnessAsync(runner, stored, attachment.Snapshot, agent.HarnessId, agent.HarnessSessionId,
                agent.WorkingDirectory, stored.WorkingDirectory, cancellationToken);
        }
        catch
        {
            if (attachment.Copy) await DiscardCopyAsync(attachment.CopyId);
            throw;
        }
    }

    private async Task DiscardCopyAsync(Guid copyId)
    {
        await _native.DeleteAsync(copyId, CancellationToken.None);
        File.Delete(PathFor(copyId));
    }

    /// <summary>Under the session lock again: records the agent's conversation, unless the one to continue changed meanwhile.</summary>
    internal async Task CompleteAttachAsync(Runner runner, Agent agent, Attachment? attachment, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        StoredSession stored;
        if (attachment is not null)
        {
            try
            {
                if (attachment.Copy) stored = await CopyAsync(db, attachment.Stored, attachment.CopyId, agent.OwnerId, cancellationToken);
                else
                {
                    stored = await db.Sessions.FirstOrDefaultAsync(s => s.Id == attachment.Stored.Id, cancellationToken) ?? throw new ArgumentException("Session not found.");
                    if (stored.AgentId != attachment.Stored.AgentId || stored.WorkspaceAgentId != attachment.Stored.WorkspaceAgentId
                        || stored.HarnessId != attachment.Stored.HarnessId || stored.HarnessSessionId != attachment.Stored.HarnessSessionId)
                        throw new InvalidOperationException("The conversation changed while it was being prepared. Try again.");
                }
            }
            catch
            {
                if (attachment.Copy) await DiscardCopyAsync(attachment.CopyId);
                throw;
            }

            if (attachment.Report is { } report) await SaveMigrationReportAsync(stored.Id, report, cancellationToken);
            RecordHarness(stored, agent.HarnessId, agent.HarnessSessionId);
        }
        else
        {
            stored = await db.Sessions.FirstOrDefaultAsync(s => s.AgentId == agent.Id, cancellationToken)
                ?? new StoredSession { Id = Guid.NewGuid(), OwnerId = agent.OwnerId, Title = NewTitle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            if (db.Entry(stored).State == EntityState.Detached) db.Sessions.Add(stored);
        }

        stored.AgentId = agent.Id;
        stored.WorkspaceAgentId = agent.Id;
        stored.ProjectId = agent.ProjectId ?? stored.ProjectId;
        stored.HarnessTrail ??= agent.HarnessId;
        stored.HarnessId = agent.HarnessId;
        stored.HarnessSessionId = agent.HarnessSessionId;
        stored.RunnerName = runner.Name;
        stored.WorkingDirectory = agent.WorkingDirectory;
        stored.RepositoryUrl = agent.RepositoryUrl ?? stored.RepositoryUrl;
        stored.RepositoryBranch = agent.RepositoryBranch ?? stored.RepositoryBranch;
        await db.SaveChangesAsync(cancellationToken);
        events.SessionChanged(stored.Id, agent.Id);
    }

    /// <summary>
    /// Moves an agent's conversation to another harness, on the same Runner and in the same folder: the stored
    /// conversation is converted (see <see cref="SessionMigration"/>) and written as the new harness's own
    /// session under <paramref name="harnessSessionId"/>, which the harness then resumes. The conversation as it was
    /// before is kept next to it in /data/sessions. Sync the Runner first, so the latest messages move along. The caller
    /// holds the session lock; <see cref="AgentService"/> releases it while converting (the three steps below).
    /// </summary>
    public async Task MoveToHarnessAsync(Runner runner, Agent agent, string harnessId, string harnessSessionId, CancellationToken cancellationToken)
    {
        using var move = await BeginMoveToHarnessAsync(agent, cancellationToken);
        await ConvertMoveAsync(runner, move, harnessId, harnessSessionId, agent.WorkingDirectory, cancellationToken);
        await CompleteMoveToHarnessAsync(agent, move, harnessId, harnessSessionId, cancellationToken);
    }

    /// <summary>A conversation being moved to another harness: the stored row as it was, and its verified history.</summary>
    internal sealed class HarnessMove(StoredSession stored, NativeHistoryStore.Snapshot? snapshot) : IDisposable
    {
        public StoredSession Stored => stored;
        public NativeHistoryStore.Snapshot? Snapshot => snapshot;
        public SessionMigrationReport? Report { get; set; }
        public void Dispose() => snapshot?.Dispose();
    }

    /// <summary>Under the session lock: verifies the latest history and opens it as it is now.</summary>
    internal async Task<HarnessMove> BeginMoveToHarnessAsync(Agent agent, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var id = await db.Sessions.Where(s => s.AgentId == agent.Id).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("This agent has no stored conversation to move.");
        await EnsureVerifiedLockedAsync(id, cancellationToken);
        var stored = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id, cancellationToken);
        return new HarnessMove(stored, await _native.OpenAsync(id, cancellationToken));
    }

    /// <summary>
    /// Without the session lock: backs up the history and installs it, converted, on the Runner under the new session id.
    /// Nothing refers to that session until <see cref="CompleteMoveToHarnessAsync"/>.
    /// </summary>
    internal async Task ConvertMoveAsync(Runner runner, HarnessMove move, string harnessId, string harnessSessionId, string workingDirectory, CancellationToken cancellationToken)
    {
        await BackupAsync(move.Stored, move.Snapshot, cancellationToken);
        move.Report = await InstallForHarnessAsync(runner, move.Stored, move.Snapshot, harnessId, harnessSessionId, workingDirectory, null, cancellationToken);
    }

    /// <summary>Under the session lock again: records the move, unless the conversation changed meanwhile.</summary>
    internal async Task CompleteMoveToHarnessAsync(Agent agent, HarnessMove move, string harnessId, string harnessSessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Sessions.SingleOrDefaultAsync(s => s.Id == move.Stored.Id, cancellationToken);
        if (stored is null || stored.AgentId != agent.Id || stored.HarnessId != move.Stored.HarnessId || stored.HarnessSessionId != move.Stored.HarnessSessionId)
            throw new InvalidOperationException("The conversation changed while it was being converted, so it was not switched. Try again.");
        stored.TitlePinned = true; // Keep the conversation name across format conversion.
        if (move.Report is { } report) await SaveMigrationReportAsync(stored.Id, report, cancellationToken);
        RecordHarness(stored, harnessId, harnessSessionId);
        await db.SaveChangesAsync(cancellationToken);
        events.SessionChanged(stored.Id, agent.Id);
    }

    /// <summary>
    /// Writes a stored conversation to a Runner in a harness's format. Its own harness gets it unchanged; another harness
    /// gets validated structured tool history. Updates the stored session after verified installation. When it last ran
    /// in another folder (<paramref name="ranIn"/>), the agent is told where it works now.
    /// </summary>
    private async Task WriteForHarnessAsync(Runner runner, StoredSession stored, string harnessId, string harnessSessionId, string workingDirectory, string? ranIn, CancellationToken cancellationToken)
    {
        using var snapshot = await _native.OpenAsync(stored.Id, cancellationToken);
        var report = await InstallForHarnessAsync(runner, stored, snapshot, harnessId, harnessSessionId, workingDirectory, ranIn, cancellationToken);
        if (report is not null) await SaveMigrationReportAsync(stored.Id, report, cancellationToken);
        RecordHarness(stored, harnessId, harnessSessionId);
    }

    private static void RecordHarness(StoredSession stored, string harnessId, string harnessSessionId)
    {
        var trail = (stored.HarnessTrail ?? stored.HarnessId).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (trail.Count == 0 || trail[^1] != harnessId)
        {
            trail.Add(harnessId);
        }

        stored.HarnessTrail = string.Join(',', trail);
        stored.HarnessId = harnessId;
        stored.HarnessSessionId = harnessSessionId;
        stored.SourcePath = null;
        stored.SourceSize = 0;
        stored.LastSyncedAt = null;
    }

    /// <summary>
    /// Writes the conversation to the Runner for <paramref name="harnessId"/> and returns the conversion's report, if any.
    /// Converting from a format that reads one record at a time streams the saved history (<see cref="SessionMigration.Prepare(Func{TextReader}, ISessionFormat, string?, string, string, string, int, int, CancellationToken)"/>),
    /// so a large one is never held whole. Otherwise one whole conversation at a time (_wholeLoads) is loaded.
    /// </summary>
    private async Task<SessionMigrationReport?> InstallForHarnessAsync(Runner runner, StoredSession stored, NativeHistoryStore.Snapshot? snapshot, string harnessId, string harnessSessionId, string workingDirectory, string? ranIn, CancellationToken cancellationToken)
    {
        var format = _formats.GetValueOrDefault(harnessId) ?? throw new InvalidOperationException($"Conversations can't be continued in '{harnessId}' yet.");
        if (stored.MessageCount > 0 && !HasSaved(stored.Id))
            throw new IOException("The saved history is missing. Restore it before switching sessions.");
        if (snapshot is not null && snapshot.Harness != harnessId && _formats.GetValueOrDefault(snapshot.Harness) is { AppendsCompleteEntries: true } source)
        {
            logger.LogInformation("Converting conversation {Session} ({Size:0.0} MB saved) record by record to continue it in {Harness} on {Runner}", stored.Id, Megabytes(snapshot.Bytes), harnessId, runner.Name);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var converted = await Task.Run(() => SessionMigration.Prepare(() => new StreamReader(snapshot.Read(), Encoding.UTF8), source, stored.Title,
                DisplayName(snapshot.Harness), DisplayName(harnessId), harnessId, ct: cancellationToken), cancellationToken);
            if (converted is not null)
            {
                logger.LogInformation("Converted conversation {Session} in {Seconds:0.0} s", stored.Id, watch.Elapsed.TotalSeconds);
                return await InstallAsync(runner, stored, format, converted, snapshot.Harness, harnessId, harnessSessionId, workingDirectory, ranIn, cancellationToken);
            }
            logger.LogInformation("Conversation {Session}'s active branch isn't in file order; loading it whole to convert it", stored.Id);
        }
        else if (snapshot is not null && snapshot.Harness == harnessId && format.AppendsCompleteEntries)
        {
            await InstallStreamedAsync(runner, stored, snapshot, format, harnessSessionId, workingDirectory, ranIn, cancellationToken);
            return null;
        }

        logger.LogInformation("Loading conversation {Session} ({Size:0.0} MB saved) to continue it in {Harness} on {Runner}", stored.Id, Megabytes(stored.SavedSize), harnessId, runner.Name);
        await _wholeLoads.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadAsync(stored.Id, cancellationToken);
            session.Title = stored.Title;
            var from = session.Sources.LastOrDefault()?.Harness ?? session.Entries.LastOrDefault(e => e.Harness is not null)?.Harness ?? stored.HarnessId;
            if (session.Entries.Count > 0 && from != harnessId)
            {
                session = SessionMigration.Prepare(session, DisplayName(from), DisplayName(harnessId), harnessId);
            }
            return await InstallAsync(runner, stored, format, session, from, harnessId, harnessSessionId, workingDirectory, ranIn, cancellationToken);
        }
        finally { _wholeLoads.Release(); }
    }

    private async Task<SessionMigrationReport?> InstallAsync(Runner runner, StoredSession stored, ISessionFormat format, DeveSession session, string from, string harnessId, string harnessSessionId, string workingDirectory, string? ranIn, CancellationToken cancellationToken)
    {
        NoteFolderChange(session, ranIn, workingDirectory);

        // Nothing to move (e.g. pi writes its file only with the first message): the new harness starts a conversation
        // of its own under the id, instead of being handed an empty one (claude --resume refuses those).
        if (session.Entries.Count == 0) return null;
        var path = format.GetPath(harnessSessionId, workingDirectory, DateTime.UtcNow);
        // Written to a local file and streamed to the Runner, never held as one string as well.
        var local = NewWorkFile("jsonl");
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await WriteTextFileAsync(local, output => format.Write(session, harnessSessionId, workingDirectory, output), cancellationToken);
            if (session.Migration is not null && from != harnessId)
            {
                using var reader = new StreamReader(local);
                SessionMigration.ValidateWritten(session, format.Read(reader));
            }

            foreach (var (name, companion) in format.WriteCompanionFiles(session, harnessSessionId, workingDirectory))
                await shell.UploadAtomicAsync(runner, path[..path.LastIndexOf('/')] + "/" + SessionCompanionStore.ValidatePath(name), companion, cancellationToken);
            await shell.UploadAtomicFromFileAsync(runner, path, local, cancellationToken);
            logger.LogInformation("Wrote conversation {Session} to {Runner} for {Harness}{Conversion}: {Entries} entries, {Size:0.0} MB in {Seconds:0.0} s",
                stored.Id, runner.Name, harnessId, from != harnessId ? $" (converted from {from})" : "", session.Entries.Count,
                Megabytes(new FileInfo(local).Length), watch.Elapsed.TotalSeconds);
        }
        finally { File.Delete(local); }
        if (session.Migration is not { } report) return null;
        report.TargetSessionId = harnessSessionId;
        return report;
    }

    /// <summary>
    /// Its own harness: the saved records are written again under the new session id one at a time (sources' headers and
    /// times from a first pass), so a large history is never held whole.
    /// </summary>
    private async Task InstallStreamedAsync(Runner runner, StoredSession stored, NativeHistoryStore.Snapshot snapshot, ISessionFormat format,
        string harnessSessionId, string workingDirectory, string? ranIn, CancellationToken cancellationToken)
    {
        logger.LogInformation("Copying conversation {Session} ({Size:0.0} MB saved) record by record to continue it in {Harness} on {Runner}", stored.Id, Megabytes(snapshot.Bytes), format.HarnessId, runner.Name);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TextReader Read() => new StreamReader(snapshot.Read(), Encoding.UTF8);
        var metadata = await Task.Run(() => NativeRecords.Metadata(Read, format, cancellationToken), cancellationToken);
        // Nothing to move: the harness starts a conversation of its own under the id (see InstallAsync).
        if (metadata.Entries.Count == 0) return;
        metadata.Title = stored.Title;
        var note = FolderNote(metadata, ranIn, workingDirectory);
        IEnumerable<DeveEntry> Records()
        {
            foreach (var entry in NativeRecords.Read(Read, format, new SessionReadCursor(), cancellationToken)) yield return entry;
            if (note is not null) yield return note;
        }

        var path = format.GetPath(harnessSessionId, workingDirectory, DateTime.UtcNow);
        var local = NewWorkFile("jsonl");
        try
        {
            await Task.Run(() => WriteTextFileAsync(local, output => format.Write(metadata, Records(), harnessSessionId, workingDirectory, output), cancellationToken), cancellationToken);
            await shell.UploadAtomicFromFileAsync(runner, path, local, cancellationToken);
            logger.LogInformation("Wrote conversation {Session} to {Runner} for {Harness}: {Entries} entries, {Size:0.0} MB in {Seconds:0.0} s",
                stored.Id, runner.Name, format.HarnessId, metadata.Entries.Count + (note is null ? 0 : 1), Megabytes(new FileInfo(local).Length), watch.Elapsed.TotalSeconds);
        }
        finally { File.Delete(local); }
    }

    private async Task SaveMigrationReportAsync(Guid sessionId, SessionMigrationReport report, CancellationToken cancellationToken)
    {
        await SaveMigrationAsync(sessionId, report, cancellationToken);
        logger.LogInformation("Session {Session} migration v{Version} {From} to {To}: {Preserved} calls, {Archived} archived, {Omitted} omitted exchanges, {Images} omitted images",
            sessionId, report.Version, report.From, report.To, report.PreservedCalls, report.ArchivedExchanges, report.OmittedExchanges, report.OmittedImages);
    }

    private bool HasSaved(Guid id) => _native.Exists(id) || File.Exists(PathFor(id));

    /// <summary>
    /// When the conversation continues in another folder than <paramref name="ranIn"/>, a note for the agent at the end of
    /// its active branch: its history names files in the old folder, which may still exist with another agent working in it.
    /// </summary>
    internal static void NoteFolderChange(DeveSession session, string? ranIn, string workingDirectory)
    {
        if (FolderNote(session, ranIn, workingDirectory) is not { } note) return;
        session.Entries.Add(note);
        session.LeafId = note.Id;
    }

    /// <summary>The note of <see cref="NoteFolderChange"/>, or null; <paramref name="session"/> may hold only metadata.</summary>
    private static DeveEntry? FolderNote(DeveSession session, string? ranIn, string workingDirectory)
    {
        if (session.Entries.Count == 0 || ranIn is not { Length: > 0 } || ranIn.TrimEnd('/') == workingDirectory.TrimEnd('/')) return null;
        return new DeveEntry
        {
            Id = Guid.NewGuid().ToString("N"), ParentId = session.LeafId ?? session.Entries.LastOrDefault()?.Id, Kind = DeveEntryKind.Message, Role = DeveRole.User,
            Timestamp = DateTime.UtcNow, Content = [DeveBlock.OfText(ManagerNotes.FolderNote(ranIn, workingDirectory))],
            MigrationKind = ManagerNotes.FolderKind,
        };
    }

    /// <summary>
    /// Keeps the conversation as it was before a harness switch next to it in /data/sessions. The exact native records
    /// are copied, not loaded; a format with companion files keeps a whole-conversation copy instead.
    /// </summary>
    private async Task BackupAsync(StoredSession stored, NativeHistoryStore.Snapshot? snapshot, CancellationToken ct)
    {
        if (!HasSaved(stored.Id)) return;
        var name = Path.Combine(options.SessionsDirectory, $"{stored.Id:N}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}");
        if (snapshot is not null && _formats.GetValueOrDefault(snapshot.Harness) is { HasCompanionFiles: false })
        {
            var backup = $"{name}.{snapshot.Harness}.jsonl";
            try
            {
                await using (var output = new FileStream(backup + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                    await snapshot.Read().CopyToAsync(output, 1 << 20, ct);
                File.Move(backup + ".tmp", backup);
            }
            finally { File.Delete(backup + ".tmp"); }
        }
        else if (_native.Exists(stored.Id))
        {
            await _wholeLoads.WaitAsync(ct);
            try
            {
                var conversation = await LoadAsync(stored.Id, ct);
                await using var output = File.Create($"{name}.{stored.HarnessId}.json");
                await conversation.WriteAsync(output, ct);
            }
            finally { _wholeLoads.Release(); }
        }
        else File.Copy(PathFor(stored.Id), $"{name}.{stored.HarnessId}.json");
    }

    private string DisplayName(string harnessId) => harnessDisplayNames.GetValueOrDefault(harnessId, harnessId);

    /// <summary>
    /// Copies the session files of all agents on a Runner that changed since the last sync. One SSH command when nothing
    /// changed. Transfers and checks run without the session lock, so a large or slow history never holds up other work;
    /// each result is recorded under the lock, unless its conversation changed meanwhile (another harness or agent).
    /// </summary>
    public async Task SyncRunnerAsync(Runner runner, CancellationToken cancellationToken = default, bool forceVerification = false,
        Guid? strictAgentId = null, Guid? onlySessionId = null)
    {
        var gate = _runnerSyncs.GetOrAdd(runner.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await SyncRunnerCoreAsync(runner, cancellationToken, locked: false, strictAgentId, forceVerification, onlySessionId); }
        finally { gate.Release(); }
    }

    // One unlocked sync per Runner at a time. Syncs under the session lock (lifecycle steps) never wait for it: they would
    // otherwise wait for an unlocked sync that waits for the lock to record its result.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _runnerSyncs = new();

    /// <summary><see cref="SyncRunnerAsync"/> for a caller that holds the session lock (a lifecycle step's final sync).</summary>
    internal Task SyncRunnerLockedAsync(Runner runner, CancellationToken cancellationToken, Guid? strictAgentId = null,
        bool forceVerification = false, Guid? onlySessionId = null) =>
        SyncRunnerCoreAsync(runner, cancellationToken, locked: true, strictAgentId, forceVerification, onlySessionId);

    /// <summary>The conversations to sync, as they were when the sync began.</summary>
    private sealed record SyncTarget(Guid Id, Guid? AgentId, string HarnessId, string HarnessSessionId, string? SourcePath, long SourceSize,
        DateTime? LastSyncedAt, bool Idle);

    private async Task SyncRunnerCoreAsync(Runner runner, CancellationToken cancellationToken, bool locked, Guid? strictAgentId = null,
        bool forceVerification = false, Guid? onlySessionId = null)
    {
        using var timing = Diagnostics.CaptureOperations.Start("runner.history-sync", runner: runner.Id);
        // Agents created before sessions were stored (or whose registration failed) get a session now, under the lock.
        if (onlySessionId is null && (locked || await MissingSessionsAsync(runner, cancellationToken)))
            using (locked ? null : await LockAsync(cancellationToken))
                await AddMissingSessionsAsync(runner, cancellationToken);
        var sessions = await SyncTargetsAsync(runner, strictAgentId, onlySessionId, cancellationToken);
        if (sessions.Count == 0)
        {
            return;
        }

        var historySettings = await _historySettings.GetAsync(cancellationToken);
        // Taken before locating: a sync that located the files later saved a newer history (NativeHistoryStore.SyncAsync).
        var location = Interlocked.Increment(ref _locations);
        var script = new StringBuilder();
        foreach (var session in sessions)
        {
            script.Append($"echo {LocateMarker} {session.Id:N}; f=; ").Append(_formats[session.HarnessId].LocateCommand(session.HarnessSessionId)).Append("; if [ -n \"$f\" ]; then printf '__DAM_STAMP__ %s\\n' \"$(stat -L -c '%d:%i:%y:%z' \"$f\")\"; fi\n");
        }

        var result = await shell.RunAsync(runner, script.ToString(), TimeSpan.FromSeconds(30), cancellationToken);
        result.EnsureSuccess("Locating session histories");
        var located = ParseLocated(result.Output);
        var stamps = ParseStamps(result.Output);
        foreach (var session in sessions)
        {
            if (!located.TryGetValue(session.Id, out var file))
            {
                if (session.AgentId == strictAgentId && strictAgentId is not null && (session.SourcePath != null || await HadHistoryAsync(session, cancellationToken)))
                    throw new IOException("The current history could not be found. Session selection was not changed.");
                continue;
            }
            if (!_formats[session.HarnessId].SupportsIncrementalRead && file.Size == session.SourceSize && file.Path == session.SourcePath)
            {
                continue;
            }

            try
            {
                stamps.TryGetValue(session.Id, out var stamp);
                if (await TransferAsync(runner, session, file, stamp, location, historySettings, forceVerification || session.AgentId == strictAgentId, cancellationToken) is not { } deve)
                    continue;
                using (locked ? null : await LockAsync(cancellationToken))
                    await RecordSyncAsync(runner, session, file, deve, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not sync session {Session} from {Runner}", session.Id, runner.Name);
                if (session.AgentId == strictAgentId && strictAgentId is not null) throw;
            }
        }
    }

    // New agents whose conversation is being prepared (created, cloned, a saved one written to the Runner): syncs don't give
    // them one meanwhile, which would take the place of the conversation they continue.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _preparing = new();

    /// <summary>Until disposed, syncs leave <paramref name="agentId"/> without a conversation for its creation to attach.</summary>
    internal IDisposable PrepareConversation(Guid agentId)
    {
        _preparing.AddOrUpdate(agentId, 1, (_, n) => n + 1);
        return new Preparing(() => { if (_preparing.AddOrUpdate(agentId, 0, (_, n) => n - 1) <= 0) _preparing.TryRemove(agentId, out _); });
    }

    private sealed class Preparing(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) release(); }
    }

    private async Task<bool> MissingSessionsAsync(Runner runner, CancellationToken cancellationToken)
    {
        var preparing = _preparing.Keys.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Agents.AnyAsync(a => a.RunnerId == runner.Id && !preparing.Contains(a.Id) && !db.Sessions.Any(s => s.AgentId == a.Id), cancellationToken);
    }

    private async Task AddMissingSessionsAsync(Runner runner, CancellationToken cancellationToken)
    {
        var preparing = _preparing.Keys.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var missing = await db.Agents.Where(a => a.RunnerId == runner.Id && !preparing.Contains(a.Id) && !db.Sessions.Any(s => s.AgentId == a.Id)).ToListAsync(cancellationToken);
        foreach (var agent in missing)
        {
            db.Sessions.Add(new StoredSession
            {
                Id = Guid.NewGuid(),
                OwnerId = agent.OwnerId,
                Title = NewTitle,
                AgentId = agent.Id,
                WorkspaceAgentId = agent.Id,
                HarnessId = agent.HarnessId,
                HarnessSessionId = agent.HarnessSessionId,
                RunnerName = runner.Name,
                WorkingDirectory = agent.WorkingDirectory,
                RepositoryUrl = agent.RepositoryUrl,
                RepositoryBranch = agent.RepositoryBranch,
                CreatedAt = agent.CreatedAt,
                UpdatedAt = agent.CreatedAt,
            });
        }

        if (missing.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The conversations of this Runner's agents, as they are now; recording a result checks them again.</summary>
    private async Task<List<SyncTarget>> SyncTargetsAsync(Runner runner, Guid? strictAgentId, Guid? onlySessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sessions = await db.Sessions.AsNoTracking().Include(s => s.Agent)
            .Where(s => s.Agent != null && s.Agent.RunnerId == runner.Id && (s.Agent.State != AgentState.Moving || s.Agent.Id == strictAgentId) && s.HarnessSessionId != null
                && (onlySessionId == null || s.Id == onlySessionId) && (strictAgentId == null || s.AgentId == strictAgentId))
            .ToListAsync(cancellationToken);
        return sessions.Where(s => _formats.ContainsKey(s.HarnessId))
            .Select(s => new SyncTarget(s.Id, s.AgentId, s.HarnessId, s.HarnessSessionId!, s.SourcePath, s.SourceSize, s.LastSyncedAt, s.Agent is { Working: false }))
            .ToList();
    }

    private async Task<bool> HadHistoryAsync(SyncTarget session, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions.AnyAsync(s => s.Id == session.Id && (s.MessageCount > 0 || s.Agent!.LastAssistantMessageAt != null), ct);
    }

    /// <summary>Brings the saved native history up to date; the conversation's metadata when it changed, else null.</summary>
    private long _locations;

    private async Task<DeveSession?> TransferAsync(Runner runner, SyncTarget session, (long Size, string Path) file, string? stamp, long location,
        DeveAgentManager.Contracts.HistorySyncSettings historySettings, bool force, CancellationToken cancellationToken)
    {
        var format = _formats[session.HarnessId];
        var policy = new NativeHistorySyncPolicy(historySettings.Mode, Force: force, Idle: session.Idle);
        var incremental = format.SupportsIncrementalRead && stamp is not null;
        if (incremental && !format.HasCompanionFiles && session.SourceSize == file.Size && session.SourcePath == file.Path
            && await _native.IsCurrentAsync(session.Id, session.HarnessId, file.Path, file.Size, stamp!, cancellationToken, policy)) { await CreditPetUsageAsync(session.Id, session.HarnessId, session.HarnessSessionId, cancellationToken); return null; }
        if (!incremental)
        {
            var content = await shell.DownloadAsync(runner, file.Path, cancellationToken)
                ?? throw new IOException("Could not download the current conversation.");
            var whole = format.Read(content);
            await SaveAsync(session.Id, whole, cancellationToken);
            return whole;
        }

        (DeveSession Metadata, bool Changed) sync;
        try
        {
            sync = await _native.SyncAsync(session.Id, format, file.Path, file.Size, stamp!,
                async (offset, hash, output, ct) =>
                {
                    var ok = await shell.DownloadHistoryRangeAsync(runner, file.Path, offset, file.Size, hash, output, ct);
                    return new NativeHistoryTransfer(ok is not null, ok);
                }, cancellationToken, policy, (offset, samples, output, ct) =>
                    shell.DownloadHistoryAppendAsync(runner, file.Path, offset, file.Size, stamp!, samples, output, ct),
                async (metadata, ct) =>
                {
                    var generation = await _companions.SyncAsync(shell, runner, session.Id, format, file.Path, metadata, ct, policy);
                    if (generation is not null)
                    {
                        var current = await shell.RunAsync(runner, "stat -L -c '%d:%i:%y:%z' -- " + ShellQuote.Quote(file.Path), cancellationToken: ct);
                        current.EnsureSuccess("Checking native session snapshot");
                        if (current.Output.TrimEnd('\r', '\n') != stamp) throw new IOException("The native log changed while its replay files were saved; synchronization will retry.");
                    }
                    return generation;
                }, location: location);
        }
        catch (NativeHistoryStore.StaleLocationException)
        {
            // Another sync located and saved a newer history meanwhile; the next sync starts from it.
            return null;
        }
        if (!sync.Changed && session.SourceSize == file.Size && session.SourcePath == file.Path) { await CreditPetUsageAsync(session.Id, session.HarnessId, session.HarnessSessionId, cancellationToken); return null; }
        await _companions.PruneAsync(session.Id, (await _native.SummaryAsync(session.Id, cancellationToken))?.CompanionManifest, cancellationToken);
        File.Delete(PathFor(session.Id)); // The verified native copy is now the authoritative full history.
        return sync.Metadata;
    }

    /// <summary>
    /// Under the session lock: records a synced history on its stored conversation, unless the conversation changed while it
    /// was transferred (another harness, agent or Runner, or a newer sync recorded first): the next sync sees it as it is now.
    /// </summary>
    private async Task RecordSyncAsync(Runner runner, SyncTarget session, (long Size, string Path) file, DeveSession deve, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Sessions.Include(s => s.Agent).SingleOrDefaultAsync(s => s.Id == session.Id, cancellationToken);
        if (stored is null)
        {
            // Deleted while it was transferred: drop what the transfer saved.
            await _native.DeleteAsync(session.Id, cancellationToken);
            return;
        }
        if (stored.AgentId != session.AgentId || stored.HarnessId != session.HarnessId || stored.HarnessSessionId != session.HarnessSessionId
            || stored.Agent?.RunnerId != runner.Id || stored.LastSyncedAt != session.LastSyncedAt)
            return;
        Apply(stored, deve, stored.Agent?.Name);
        if (stored.Agent is { } agent) UpdateLastAssistantMessage(agent, deve);
        stored.SourcePath = file.Path;
        stored.SourceSize = file.Size;
        UpdateSizes(stored);
        stored.LastSyncedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await CreditPetUsageAsync(stored.Id, stored.HarnessId, stored.HarnessSessionId!, cancellationToken);
        events.SessionChanged(stored.Id, stored.AgentId);
    }

    /// <summary>Best-effort synchronization, with explicit context for lifecycle operations. Never throws.</summary>
    public async Task TrySyncRunnerAsync(Runner runner, CancellationToken cancellationToken = default, string? finalOperation = null)
    {
        try
        {
            await SyncRunnerAsync(runner, cancellationToken, forceVerification: finalOperation is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (finalOperation is null) logger.LogWarning(ex, "Session sync from {Runner} failed; periodic synchronization will retry", runner.Name);
            else logger.LogWarning(ex, "Final session sync from {Runner} before {Operation} failed", runner.Name, finalOperation);
        }
    }

    /// <summary>Imports a DeveSession file or a harness's own session file (pi JSONL), as a conversation of <paramref name="ownerId"/>.</summary>
    public async Task<StoredSession> ImportAsync(Guid ownerId, string fileName, string content, CancellationToken cancellationToken = default)
    {
        var (session, harnessId) = Parse(content);
        var stored = new StoredSession { Id = Guid.NewGuid(), OwnerId = ownerId, HarnessId = harnessId, CreatedAt = session.CreatedAt == default ? DateTime.UtcNow : session.CreatedAt };
        Apply(stored, session, null);
        if (string.IsNullOrWhiteSpace(stored.Title))
        {
            stored.Title = Path.GetFileNameWithoutExtension(fileName);
        }

        await SaveAsync(stored.Id, session, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Sessions.Add(stored);
        await db.SaveChangesAsync(cancellationToken);
        events.SessionChanged(stored.Id);
        return stored;
    }

    /// <summary>Exports as a DeveSession ("deve") or in a harness's own format (its id, e.g. "pi").</summary>
    public async Task<SessionExport> ExportAsync(Guid sessionId, string format, CancellationToken cancellationToken = default)
    {
        // Verified, then exported without the session lock: exporting a large history never holds up other work.
        await EnsureVerifiedAsync(sessionId, cancellationToken);
        var stored = await GetAsync(sessionId, cancellationToken) ?? throw new ArgumentException("Session not found.");
        logger.LogInformation("Exporting conversation {Session} ({Size:0.0} MB saved) as {Format}", sessionId, Megabytes(stored.SavedSize), format);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SessionExport export;
        using (var snapshot = await _native.OpenAsync(sessionId, cancellationToken))
        {
            if (snapshot is not null && _formats.GetValueOrDefault(snapshot.Harness) is { AppendsCompleteEntries: true } source)
                export = await ExportStreamedAsync(stored, snapshot, source, format, cancellationToken);
            else
            {
                await _wholeLoads.WaitAsync(cancellationToken);
                try { export = await ExportAsync(stored, await LoadAsync(sessionId, cancellationToken), format, cancellationToken); }
                finally { _wholeLoads.Release(); }
            }
        }
        logger.LogInformation("Exported conversation {Session} as {Format}: {Size:0.0} MB in {Seconds:0.0} s", sessionId, format, Megabytes(export.Bytes), watch.Elapsed.TotalSeconds);
        return export;
    }

    /// <summary>
    /// <see cref="ExportAsync(StoredSession, DeveSession, string, CancellationToken)"/> from the saved records, one at a time:
    /// its own format and DeveSession JSON are written record by record, another harness's format is converted by streaming.
    /// </summary>
    private async Task<SessionExport> ExportStreamedAsync(StoredSession stored, NativeHistoryStore.Snapshot snapshot, ISessionFormat source, string format, CancellationToken ct)
    {
        TextReader Read() => new StreamReader(snapshot.Read(), Encoding.UTF8);
        var name = SafeFileName(stored.Title);
        if (format != "deve" && format != snapshot.Harness)
        {
            var converted = await Task.Run(() => SessionMigration.Prepare(Read, source, stored.Title, DisplayName(snapshot.Harness), DisplayName(format), format, ct: ct), ct);
            // Out of file order: converted from the loaded conversation instead.
            if (converted is null)
            {
                await _wholeLoads.WaitAsync(ct);
                try { return await ExportAsync(stored, await LoadAsync(stored.Id, ct), format, ct); }
                finally { _wholeLoads.Release(); }
            }
            return await ExportAsync(stored, converted, format, ct, converted: true);
        }

        var metadata = await Task.Run(() => NativeRecords.Metadata(Read, source, ct), ct);
        metadata.Title = stored.Title;
        IEnumerable<DeveEntry> Records() => NativeRecords.Read(Read, source, new SessionReadCursor(), ct);
        var path = NewWorkFile("export");
        try
        {
            if (format == "deve")
            {
                metadata.Migration = await ReadMigrationAsync(stored.Id, metadata, ct);
                await Task.Run(() => WriteDeveAsync(path, metadata, Records(), ct), ct);
                return new SessionExport($"{name}.deve.json", "application/json", path, new FileInfo(path).Length);
            }

            var harnessSessionId = stored.HarnessSessionId ?? stored.Id.ToString("N");
            var cwd = metadata.Cwd ?? stored.WorkingDirectory ?? "/";
            await Task.Run(() => WriteTextFileAsync(path, output => source.Write(metadata, Records(), harnessSessionId, cwd, output), ct), ct);
            return new SessionExport($"{name}.{format}.{source.ExportExtension}", source.ExportContentType, path, new FileInfo(path).Length);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// DeveSession JSON as <see cref="JsonSerializer"/> writes it with <see cref="IndentedJson"/>, with the entries written
    /// one at a time: <paramref name="metadata"/> gives every other property.
    /// </summary>
    internal static async Task WriteDeveAsync(string path, DeveSession metadata, IEnumerable<DeveEntry> entries, CancellationToken ct)
    {
        var head = new DeveSession { Format = metadata.Format, Version = metadata.Version, Title = metadata.Title, Cwd = metadata.Cwd, CreatedAt = metadata.CreatedAt,
            UpdatedAt = metadata.UpdatedAt, LeafId = metadata.LeafId, Sources = metadata.Sources, Migration = metadata.Migration };
        var properties = JsonSerializer.SerializeToNode(head, IndentedJson)!.AsObject();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = IndentedJson.Encoder, MaxDepth = IndentedJson.MaxDepth });
        writer.WriteStartObject();
        foreach (var (property, value) in properties)
        {
            writer.WritePropertyName(property);
            if (property != "entries") { if (value is null) writer.WriteNullValue(); else value.WriteTo(writer, IndentedJson); continue; }
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                JsonSerializer.Serialize(writer, entry, IndentedJson);
                if (writer.BytesPending > 1 << 20) await writer.FlushAsync(ct);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    /// <summary>The conversion report of the conversation's current native session, as <see cref="LoadAsync"/> attaches it.</summary>
    private async Task<SessionMigrationReport?> ReadMigrationAsync(Guid sessionId, DeveSession metadata, CancellationToken ct)
    {
        var reportPath = MigrationPathFor(sessionId);
        if (!File.Exists(reportPath)) return null;
        try
        {
            await using var reportStream = File.OpenRead(reportPath);
            var report = await JsonSerializer.DeserializeAsync<SessionMigrationReport>(reportStream, DeveSession.JsonOptions, ct);
            return report?.TargetSessionId == metadata.Sources.LastOrDefault()?.SessionId ? report : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning("Could not read migration diagnostics for {Session}: {ErrorType}", sessionId, ex.GetType().Name);
            return null;
        }
    }

    // Written to a file, never one string: a large conversation is already held once as a DeveSession.
    private async Task<SessionExport> ExportAsync(StoredSession stored, DeveSession session, string format, CancellationToken ct, bool converted = false)
    {
        session.Title = stored.Title;
        var name = SafeFileName(stored.Title);
        var path = NewWorkFile("export");
        try
        {
            if (format == "deve")
            {
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                    await JsonSerializer.SerializeAsync(output, session, IndentedJson, ct);
                return new SessionExport($"{name}.deve.json", "application/json", path, new FileInfo(path).Length);
            }

            var harnessFormat = _formats.GetValueOrDefault(format) ?? throw new ArgumentException($"Unknown export format '{format}'.");
            var harnessSessionId = stored.HarnessSessionId ?? stored.Id.ToString("N");
            var cwd = session.Cwd ?? stored.WorkingDirectory ?? "/";
            var from = converted ? "" : session.Sources.LastOrDefault()?.Harness ?? stored.HarnessId;
            if (!converted && from != format && session.Entries.Count > 0)
                session = SessionMigration.Prepare(session, DisplayName(from), DisplayName(format), format);
            await WriteTextFileAsync(path, output => harnessFormat.Export(session, harnessSessionId, cwd, output), ct);
            if (from != format)
            {
                using var reader = new StreamReader(path);
                SessionMigration.ValidateWritten(session, harnessFormat.Read(reader));
            }

            return new SessionExport($"{name}.{format}.{harnessFormat.ExportExtension}", harnessFormat.ExportContentType, path, new FileInfo(path).Length);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new(DeveSession.JsonOptions) { WriteIndented = true };

    private static double Megabytes(long bytes) => bytes / 1048576.0;

    // Exports and conversations for another harness are written here first. Files a crash left behind go after a while.
    private string NewWorkFile(string extension)
    {
        var folder = Path.Combine(options.DataDirectory, "tmp", "sessions");
        Directory.CreateDirectory(folder);
        foreach (var stale in Directory.EnumerateFiles(folder).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6)))
        {
            try { File.Delete(stale); }
            catch (IOException) { } // Still being downloaded.
        }

        return Path.Combine(folder, $"{Guid.NewGuid():N}.{extension}");
    }

    private static async Task WriteTextFileAsync(string path, Action<TextWriter> write, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16);
        write(writer);
        await writer.FlushAsync(ct);
    }

    // Never export/continue a sampled checkpoint while its source is unavailable.
    private Task EnsureVerifiedLockedAsync(Guid id, CancellationToken ct) => EnsureVerifiedAsync(id, ct, locked: true);

    /// <summary>Verifies a sampled checkpoint by syncing the conversation; without the lock, as an unlocked sync does.</summary>
    private async Task EnsureVerifiedAsync(Guid id, CancellationToken ct, bool locked = false)
    {
        var state = await _native.SummaryAsync(id, ct);
        if (state is null || state.Sha256.Length > 0 && !await _companions.HasPendingVerificationAsync(id, state.CompanionManifest, ct)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Sessions.Include(s => s.Agent).ThenInclude(a => a!.Runner)
            .SingleAsync(s => s.Id == id, ct);
        if (stored.Agent?.Runner is not { State: RunnerState.Ready } runner)
            throw new IOException("This history has pending verification. Reconnect its Runner and synchronize before exporting or continuing it.");
        if (locked) await SyncRunnerLockedAsync(runner, ct, stored.Agent.Id, onlySessionId: id);
        else await SyncRunnerAsync(runner, ct, strictAgentId: stored.Agent.Id, onlySessionId: id);
        var verified = await _native.SummaryAsync(id, ct);
        if (verified?.Sha256 is not { Length: > 0 } || await _companions.HasPendingVerificationAsync(id, verified.CompanionManifest, ct))
            throw new IOException("The current history could not be fully verified. Try again after synchronization succeeds.");
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        using var lease = await LockAsync(cancellationToken);
        if (await db.Sessions.AnyAsync(s => s.Id == sessionId && s.AgentId != null, cancellationToken))
            throw new InvalidOperationException("Select another session before deleting the active conversation.");
        await _artifacts.RemoveSessionAsync(sessionId, cancellationToken);
        await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
        File.Delete(PathFor(sessionId));
        if (File.Exists(MigrationPathFor(sessionId))) File.Delete(MigrationPathFor(sessionId));
        await _native.DeleteAsync(sessionId, cancellationToken);
        _companions.Delete(sessionId);
        if (Directory.Exists(options.SessionsDirectory))
            foreach (var backup in Directory.EnumerateFiles(options.SessionsDirectory, $"{sessionId:N}.*.json")
                .Concat(Directory.EnumerateFiles(options.SessionsDirectory, $"{sessionId:N}.*.jsonl"))) File.Delete(backup);
        events.Publish(new ManagerEvent(ManagerEventKind.SessionRemoved, SessionId: sessionId));
    }

    /// <summary>Removes a file an agent shared from the conversation; false when it wasn't there.</summary>
    public async Task<bool> DeleteFileAsync(Guid sessionId, Guid fileId, CancellationToken ct, CurrentUser? actor = null)
    {
        using var lease = await LockAsync(ct);
        return await _artifacts.DeleteFileAsync(sessionId, fileId, actor, ct);
    }

    public async Task RenameAsync(Guid id, string title, CancellationToken ct)
    {
        title = ValidateTitle(title);
        using var lease = await LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new ArgumentException("Session not found.");
        stored.Title = title;
        stored.TitlePinned = true;
        await db.SaveChangesAsync(ct);
        events.SessionChanged(id, stored.WorkspaceAgentId);
    }

    internal static string ValidateTitle(string title)
    {
        title = title.Trim();
        if (title.Length is 0 or > 200) throw new ArgumentException("Use a session name between 1 and 200 characters.");
        return title;
    }

    /// <summary>A copy of someone's conversation for <paramref name="ownerId"/>: its content and its shared images and files, not its history of moves.</summary>
    /// <summary>The row of a copy of <paramref name="source"/> for <paramref name="ownerId"/>, whose history is already saved under <paramref name="copyId"/>.</summary>
    private async Task<StoredSession> CopyAsync(ManagerDbContext db, StoredSession source, Guid copyId, Guid ownerId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var copy = new StoredSession
        {
            Id = copyId,
            OwnerId = ownerId,
            Title = source.Title,
            TitlePinned = source.TitlePinned,
            HarnessId = source.HarnessId,
            HarnessTrail = source.HarnessTrail,
            RepositoryUrl = source.RepositoryUrl,
            RepositoryBranch = source.RepositoryBranch,
            MessageCount = source.MessageCount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ownerId, cancellationToken);
        await _artifacts.CopyAttachmentsAsync(source.Id, copy.Id, CurrentUser.From(owner), cancellationToken);
        db.Sessions.Add(copy);
        return copy;
    }

    private void UpdateSizes(StoredSession stored)
    {
        stored.SavedSize = _native.Exists(stored.Id) ? _native.Size(stored.Id) : File.Exists(PathFor(stored.Id)) ? new FileInfo(PathFor(stored.Id)).Length : 0;
        stored.SavedSize += _companions.Size(stored.Id);
        stored.TotalSize = stored.SavedSize + (Directory.Exists(options.SessionsDirectory)
            ? Directory.EnumerateFiles(options.SessionsDirectory, $"{stored.Id:N}.*.json").Sum(p => new FileInfo(p).Length) : 0);
        if (File.Exists(MigrationPathFor(stored.Id))) stored.TotalSize += new FileInfo(MigrationPathFor(stored.Id)).Length;
    }

    // Caller holds LockAsync and has stopped and synced the outgoing harness.
    /// <summary>A saved conversation chosen for a workspace: as it was checked, its history, and whether it is installed.</summary>
    internal sealed class Selection(StoredSession? selected, NativeHistoryStore.Snapshot? snapshot) : IDisposable
    {
        public StoredSession? Selected => selected;
        public NativeHistoryStore.Snapshot? Snapshot => snapshot;
        public SessionMigrationReport? Report { get; set; }
        public bool Installed { get; set; }
        public void Dispose() => snapshot?.Dispose();
    }

    /// <summary>Under the session lock: verifies the chosen conversation's latest history and opens it (none for a new one).</summary>
    internal async Task<Selection> BeginSelectAsync(Guid? id, CancellationToken ct)
    {
        if (id is not { } sessionId) return new Selection(null, null);
        await EnsureVerifiedLockedAsync(sessionId, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var selected = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, ct);
        return new Selection(selected, await _native.OpenAsync(sessionId, ct));
    }

    /// <summary>Without the session lock: writes the chosen conversation to the Runner under the new session id.</summary>
    internal async Task InstallSelectionAsync(Runner runner, Agent agent, Selection selection, string nativeId, CancellationToken ct)
    {
        if (selection.Selected is not { } selected) return;
        selection.Report = await InstallForHarnessAsync(runner, selected, selection.Snapshot, selected.HarnessId, nativeId, agent.WorkingDirectory, selected.WorkingDirectory, ct);
        selection.Installed = true;
    }

    /// <summary>
    /// Makes the chosen conversation the agent's (the caller holds the session lock). A <paramref name="prepared"/> one was
    /// installed without the lock (<see cref="InstallSelectionAsync"/>) and is recorded unless it changed meanwhile;
    /// otherwise it is installed here.
    /// </summary>
    internal async Task SelectLockedAsync(Runner runner, Agent agent, Guid? id, string? title, string nativeId, CancellationToken ct, Selection? prepared = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var selected = id.HasValue ? await db.Sessions.SingleAsync(s => s.Id == id, ct) : new StoredSession
        {
            Id = Guid.NewGuid(), OwnerId = agent.OwnerId, Title = string.IsNullOrWhiteSpace(title) ? NewTitle : ValidateTitle(title),
            TitlePinned = !string.IsNullOrWhiteSpace(title), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            HarnessId = agent.HarnessId, WorkspaceAgentId = agent.Id, ProjectId = agent.ProjectId
        };
        if (selected.WorkspaceAgentId != agent.Id && (agent.ProjectId is null || selected.ProjectId != agent.ProjectId
            && !await db.Agents.AnyAsync(a => a.Id == selected.WorkspaceAgentId && a.ProjectId == agent.ProjectId, ct)))
            throw new InvalidOperationException("Choose a conversation from this project.");
        if (selected.AgentId != null && selected.AgentId != agent.Id) throw new InvalidOperationException("This conversation is active in another workspace.");
        var changesHarness = selected.HarnessId != agent.HarnessId;
        if (changesHarness && agent.LoginStopPending) throw new InvalidOperationException("Wait for pending login enforcement before changing harnesses.");
        var accountId = changesHarness ? null : agent.AccountId;
        if (id.HasValue && prepared is { Installed: true, Selected: { } before })
        {
            if (selected.AgentId != before.AgentId || selected.HarnessId != before.HarnessId || selected.HarnessSessionId != before.HarnessSessionId)
                throw new InvalidOperationException("The conversation changed while it was being prepared. Try again.");
            if (prepared.Report is { } report) await SaveMigrationReportAsync(selected.Id, report, ct);
            RecordHarness(selected, selected.HarnessId, nativeId);
        }
        else if (id.HasValue)
        {
            await EnsureVerifiedLockedAsync(selected.Id, ct);
            await db.Entry(selected).ReloadAsync(ct);
            await WriteForHarnessAsync(runner, selected, selected.HarnessId, nativeId, agent.WorkingDirectory, selected.WorkingDirectory, ct);
        }
        else { selected.HarnessSessionId = nativeId; db.Sessions.Add(selected); }
        // It now runs here; it stays the project's when this workspace is removed.
        selected.WorkspaceAgentId = agent.Id;
        selected.ProjectId = agent.ProjectId ?? selected.ProjectId;
        selected.WorkingDirectory = agent.WorkingDirectory;
        selected.RepositoryUrl = agent.RepositoryUrl;
        selected.RepositoryBranch = agent.RepositoryBranch;
        selected.RunnerName = runner.Name;
        selected.HarnessTrail ??= selected.HarnessId;
        // Detach and attach in one transaction, while satisfying the unique active-session index.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Sessions.Where(s => s.AgentId == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentId, (Guid?)null), ct);
        selected.AgentId = agent.Id;
        await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(a => a
            .SetProperty(x => x.HarnessId, selected.HarnessId).SetProperty(x => x.HarnessSessionId, nativeId)
            .SetProperty(x => x.AccountId, accountId)
            .SetProperty(x => x.LoginBlocked, changesHarness ? false : agent.LoginBlocked)
            .SetProperty(x => x.RunningAccountId, changesHarness ? null : agent.RunningAccountId)
            .SetProperty(x => x.LastAssistantMessageAt, (DateTime?)null), ct);
        if (changesHarness) db.AgentLoginGrants.RemoveRange(await db.AgentLoginGrants.Where(g => g.AgentId == agent.Id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        agent.HarnessId = selected.HarnessId;
        agent.HarnessSessionId = nativeId;
        agent.AccountId = accountId;
        if (changesHarness) { agent.LoginBlocked = false; agent.RunningAccountId = null; }
        agent.LastAssistantMessageAt = null;
        events.SessionChanged(selected.Id, agent.Id);
    }

    internal static Dictionary<Guid, string> ParseStamps(string output)
    {
        var result = new Dictionary<Guid, string>();
        Guid? current = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(LocateMarker + " ", StringComparison.Ordinal)) current = Guid.TryParse(line[(LocateMarker.Length + 1)..], out var id) ? id : null;
            else if (current is { } session && line.StartsWith("__DAM_STAMP__ ", StringComparison.Ordinal)) result[session] = line[14..];
        }
        return result;
    }

    internal static Dictionary<Guid, (long Size, string Path)> ParseLocated(string output)
    {
        var located = new Dictionary<Guid, (long, string)>();
        Guid? current = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(LocateMarker + " ", StringComparison.Ordinal))
            {
                current = Guid.TryParse(line[(LocateMarker.Length + 1)..], out var id) ? id : null;
                continue;
            }

            var space = line.IndexOf(' ');
            if (current is { } sessionId && space > 0 && long.TryParse(line[..space], out var size))
            {
                located[sessionId] = (size, line[(space + 1)..]);
                current = null;
            }
        }

        return located;
    }

    private (DeveSession Session, string HarnessId) Parse(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') && trimmed.Contains($"\"{DeveSession.FormatName}\"", StringComparison.Ordinal))
        {
            try
            {
                var session = DeveSession.FromJson(content);
                return (session, session.Sources.LastOrDefault()?.Harness ?? session.Entries.LastOrDefault()?.Harness ?? "pi");
            }
            catch (JsonException)
            {
                // Not a DeveSession after all; try the harness formats.
            }
        }

        foreach (var format in _formats.Values)
        {
            var session = format.Read(content);
            if (session.Sources.Count > 0 && session.Entries.Count > 0)
            {
                return (session, format.HarnessId);
            }
        }

        throw new FormatException("This is not a DeveSession or a supported harness session file (pi .jsonl).");
    }

    internal static bool UpdateLastAssistantMessage(Agent agent, DeveSession session)
    {
        var latest = session.Entries.Where(e => e.Kind == DeveEntryKind.Message && e.Role == DeveRole.Assistant
            && e.Timestamp != default && (!string.IsNullOrWhiteSpace(e.Text)
                || e.Content?.Any(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text)) == true))
            .Select(e => (DateTime?)e.Timestamp).Max();
        if (latest is null || agent.LastAssistantMessageAt is { } previous && latest <= previous) return false;
        agent.LastAssistantMessageAt = latest;
        return true;
    }

    /// <summary>Populate existing agents from local saved conversations, including stopped/offline runners.</summary>
    public async Task BackfillMessageTimesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Sessions.Include(s => s.Agent).Where(s => s.Agent != null && s.Agent.LastAssistantMessageAt == null).ToListAsync(ct);
        foreach (var saved in existing)
        {
            try
            {
                var summary = await _native.SummaryAsync(saved.Id, ct);
                if (summary is not null) saved.Agent!.LastAssistantMessageAt = summary.LastAssistantMessageAt;
                else UpdateLastAssistantMessage(saved.Agent!, await LoadAsync(saved.Id, ct));
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException)
            { logger.LogWarning(ex, "Could not read saved message timestamps for session {Session}", saved.Id); }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Conversations titled with a note the Manager put in front of their first prompt, from before titles skipped those
    /// notes. A synced preview of that prompt is cut off inside the note, so the title comes from the whole history, once.
    /// </summary>
    public async Task RepairNoteTitlesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.Sessions.Where(s => s.Title.StartsWith("(This conversation was moved from ") || s.Title.StartsWith("[DAM ")).ToListAsync(ct);
        foreach (var saved in candidates.Where(s => ManagerNotes.WithoutNotes(s.Title) is null && HasSaved(s.Id)))
        {
            try
            {
                var prompt = (await LoadAsync(saved.Id, ct)).FirstPrompt();
                if (prompt is not null) saved.Title = prompt;
                else if (!saved.TitlePinned) saved.Title = NewTitle;
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException)
            { logger.LogWarning(ex, "Could not read the first prompt of session {Session} for its title", saved.Id); }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What a conversation is called until its harness names it or its first prompt says what it is about.</summary>
    public const string NewTitle = "New conversation";

    internal static void Apply(StoredSession stored, DeveSession session, string? agentName)
    {
        // Agents start their harness with --name <agent>: that names the workspace, not the conversation. The first
        // prompt says more, unless the user renamed the conversation.
        var title = session.Title is { Length: > 0 } name && name != agentName ? name
            : session.FirstPrompt() ?? (agentName is null ? session.Title : null);
        if (!stored.TitlePinned && !string.IsNullOrWhiteSpace(title))
        {
            stored.Title = title;
        }
        else if (!stored.TitlePinned && agentName is not null && stored.Title == agentName)
        {
            stored.Title = NewTitle;
        }

        stored.MessageCount = session.Entries.Count(e => e.Kind == DeveEntryKind.Message && e.Role is DeveRole.User or DeveRole.Assistant);
        stored.UpdatedAt = session.UpdatedAt == default ? DateTime.UtcNow : session.UpdatedAt;
        stored.WorkingDirectory = session.Cwd ?? stored.WorkingDirectory;
    }

    private async Task SaveAsync(Guid sessionId, DeveSession session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.SessionsDirectory);
        var path = PathFor(sessionId);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Write next to it and move, so a reader never sees half a file.
            await using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await session.WriteAsync(stream, cancellationToken);
            File.Move(path + ".tmp", path, overwrite: true);
            await _native.DeleteAsync(sessionId, cancellationToken);
            if (session.Migration is { } report) await SaveMigrationAsync(sessionId, report, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string PathFor(Guid sessionId) => Path.Combine(options.SessionsDirectory, $"{sessionId:N}.json");

    private string MigrationPathFor(Guid sessionId) => Path.Combine(options.SessionsDirectory, "migrations", $"{sessionId:N}.json");

    private async Task SaveMigrationAsync(Guid sessionId, SessionMigrationReport report, CancellationToken ct)
    {
        var path = MigrationPathFor(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, report, DeveSession.JsonOptions, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static string SafeFileName(string title)
    {
        var name = new string(title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return name.Length == 0 ? "session" : name.Length > 60 ? name[..60] : name;
    }
}
