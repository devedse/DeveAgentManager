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

public sealed record SessionExport(string FileName, string ContentType, string Content);

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
    ManagerOptions options,
    ManagerEvents events,
    ILogger<SessionService> logger)
{
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly Dictionary<Guid, (DateTime UpdatedAt, string Text)> _previews = new();
    /// <summary>The latest reply of each agent whose conversations <paramref name="user"/> can read.</summary>
    public async Task<List<DeveAgentManager.Contracts.AgentReplyPreviewDto>> ReplyPreviewsAsync(CurrentUser user, CancellationToken ct)
    {
        await _previewGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var saved = await db.Sessions.AsNoTracking()
                .Where(s => s.AgentId != null && (s.Agent!.OwnerId == user.Id || s.Agent.Shares.Any(x => x.UserId == user.Id))).ToListAsync(ct);
            var ids = saved.Select(s => s.Id).ToHashSet();
            foreach (var id in _previews.Keys.Where(id => !ids.Contains(id)).ToArray()) _previews.Remove(id);
            var result = new List<DeveAgentManager.Contracts.AgentReplyPreviewDto>();
            foreach (var session in saved.OrderByDescending(s => s.UpdatedAt).DistinctBy(s => s.AgentId))
            {
                if (!_previews.TryGetValue(session.Id, out var cached) || cached.UpdatedAt != session.UpdatedAt)
                {
                    try
                    {
                        var conversation = await LoadAsync(session.Id, ct);
                        cached = (session.UpdatedAt, ReplyPreview(conversation));
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
        .Include(s => s.Agent).ThenInclude(a => a!.Account)
        .Include(s => s.WorkspaceAgent).ThenInclude(a => a!.Shares);

    public async Task<StoredSession?> GetForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.AgentId == agentId, cancellationToken);
    }

    /// <summary>The stored conversation, or an empty one when nothing was synced yet.</summary>
    public async Task<DeveSession> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(sessionId);
        return File.Exists(path) ? DeveSession.FromJson(await File.ReadAllTextAsync(path, cancellationToken)) : new DeveSession();
    }

    /// <summary>
    /// Registers the session of a new agent. With <paramref name="continueSessionId"/>, the stored conversation is written
    /// to the Runner first, so the harness starts with the full history. Someone else's conversation (one shared with the
    /// agent's owner) is continued as a copy that belongs to the agent's owner; the original stays as it is.
    /// </summary>
    public async Task AttachToAgentAsync(Runner runner, Agent agent, Guid? continueSessionId, CancellationToken cancellationToken)
    {
        using var lease = await LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        StoredSession stored;
        if (continueSessionId is { } existingId)
        {
            stored = await db.Sessions.FirstOrDefaultAsync(s => s.Id == existingId, cancellationToken) ?? throw new ArgumentException("Session not found.");
            if (stored.OwnerId != agent.OwnerId)
            {
                stored = await CopyAsync(db, stored, agent.OwnerId, cancellationToken);
            }
            else if (stored.AgentId is not null || stored.WorkspaceAgentId is not null)
            {
                throw new InvalidOperationException("This session already belongs to a running agent. Open that agent instead.");
            }

            await WriteForHarnessAsync(runner, stored, agent.HarnessId, agent.HarnessSessionId, agent.WorkingDirectory, cancellationToken);
        }
        else
        {
            stored = await db.Sessions.FirstOrDefaultAsync(s => s.AgentId == agent.Id, cancellationToken)
                ?? new StoredSession { Id = Guid.NewGuid(), OwnerId = agent.OwnerId, Title = agent.Name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            if (db.Entry(stored).State == EntityState.Detached) db.Sessions.Add(stored);
        }

        stored.AgentId = agent.Id;
        stored.WorkspaceAgentId = agent.Id;
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
    /// conversation is converted (as text, see <see cref="SessionFlattener"/>) and written as the new harness's own
    /// session under <paramref name="harnessSessionId"/>, which the harness then resumes. The conversation as it was
    /// before is kept next to it in /data/sessions. Sync the Runner first, so the latest messages move along.
    /// </summary>
    public async Task MoveToHarnessAsync(Runner runner, Agent agent, string harnessId, string harnessSessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Sessions.FirstOrDefaultAsync(s => s.AgentId == agent.Id, cancellationToken)
            ?? throw new InvalidOperationException("This agent has no stored conversation to move.");
        Backup(stored);

        stored.TitlePinned = true; // Keep the conversation name across format conversion.
        await WriteForHarnessAsync(runner, stored, harnessId, harnessSessionId, agent.WorkingDirectory, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        events.SessionChanged(stored.Id, agent.Id);
    }

    /// <summary>
    /// Writes a stored conversation to a Runner in a harness's format. Its own harness gets it unchanged; another harness
    /// gets it as text. Updates the stored session to point at the new file (the next sync reads it).
    /// </summary>
    private async Task WriteForHarnessAsync(Runner runner, StoredSession stored, string harnessId, string harnessSessionId, string workingDirectory, CancellationToken cancellationToken)
    {
        var format = _formats.GetValueOrDefault(harnessId) ?? throw new InvalidOperationException($"Conversations can't be continued in '{harnessId}' yet.");
        if (stored.MessageCount > 0 && !File.Exists(PathFor(stored.Id)))
            throw new IOException("The saved history is missing. Restore it before switching sessions.");
        var session = await LoadAsync(stored.Id, cancellationToken);
        session.Title = stored.Title;
        var from = session.Sources.LastOrDefault()?.Harness ?? session.Entries.LastOrDefault(e => e.Harness is not null)?.Harness ?? stored.HarnessId;
        if (session.Entries.Count > 0 && from != harnessId)
        {
            session = SessionFlattener.Flatten(session, DisplayName(from), DisplayName(harnessId));
        }

        // Nothing to move (e.g. pi writes its file only with the first message): the new harness starts a conversation
        // of its own under the id, instead of being handed an empty one (claude --resume refuses those).
        if (session.Entries.Count > 0)
        {
            var path = format.GetPath(harnessSessionId, workingDirectory, DateTime.UtcNow);
            await shell.UploadAsync(runner, path, format.Write(session, harnessSessionId, workingDirectory), "600", cancellationToken);
        }

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

    private void Backup(StoredSession stored)
    {
        var before = PathFor(stored.Id);
        if (File.Exists(before)) File.Copy(before, Path.Combine(options.SessionsDirectory,
            $"{stored.Id:N}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.{stored.HarnessId}.json"));
    }

    private string DisplayName(string harnessId) => harnessDisplayNames.GetValueOrDefault(harnessId, harnessId);

    /// <summary>Copies the session files of all agents on a Runner that changed since the last sync. One SSH command when nothing changed.</summary>
    public async Task SyncRunnerAsync(Runner runner, CancellationToken cancellationToken = default)
    {
        using var lease = await LockAsync(cancellationToken);
        await SyncRunnerLockedAsync(runner, cancellationToken);
    }

    internal async Task SyncRunnerLockedAsync(Runner runner, CancellationToken cancellationToken, Guid? strictAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Agents created before sessions were stored (or whose registration failed) get a session now.
        var missing = await db.Agents.Where(a => a.RunnerId == runner.Id && !db.Sessions.Any(s => s.AgentId == a.Id)).ToListAsync(cancellationToken);
        foreach (var agent in missing)
        {
            db.Sessions.Add(new StoredSession
            {
                Id = Guid.NewGuid(),
                OwnerId = agent.OwnerId,
                Title = agent.Name,
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

        var sessions = await db.Sessions.Include(s => s.Agent)
            .Where(s => s.Agent != null && s.Agent.RunnerId == runner.Id && s.HarnessSessionId != null)
            .ToListAsync(cancellationToken);
        sessions = sessions.Where(s => _formats.ContainsKey(s.HarnessId)).ToList();
        if (sessions.Count == 0)
        {
            return;
        }

        var script = new StringBuilder();
        foreach (var session in sessions)
        {
            script.Append($"echo {LocateMarker} {session.Id:N}; ").Append(_formats[session.HarnessId].LocateCommand(session.HarnessSessionId!)).Append('\n');
        }

        var result = await shell.RunAsync(runner, script.ToString(), TimeSpan.FromSeconds(30), cancellationToken);
        result.EnsureSuccess("Locating session histories");
        var located = ParseLocated(result.Output);
        foreach (var session in sessions)
        {
            if (!located.TryGetValue(session.Id, out var file))
            {
                if (session.AgentId == strictAgentId && (session.SourcePath != null || session.MessageCount > 0 || session.Agent?.LastAssistantMessageAt != null))
                    throw new IOException("The current history could not be found. Session selection was not changed.");
                continue;
            }
            if (file.Size == session.SourceSize && file.Path == session.SourcePath)
            {
                continue;
            }

            try
            {
                var content = await shell.DownloadAsync(runner, file.Path, cancellationToken);
                if (content is null) throw new IOException("Could not download the current conversation.");

                var deve = _formats[session.HarnessId].Read(content);
                await SaveAsync(session.Id, deve, cancellationToken);
                Apply(session, deve, session.Agent?.Name);
                if (session.Agent is { } agent) UpdateLastAssistantMessage(agent, deve);
                session.SourcePath = file.Path;
                session.SourceSize = file.Size;
                UpdateSizes(session);
                session.LastSyncedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                events.SessionChanged(session.Id, session.AgentId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not sync session {Session} from {Runner}", session.Id, runner.Name);
                if (session.AgentId == strictAgentId) throw;
            }
        }
    }

    /// <summary>A last sync before an agent or its Runner goes away. Never throws.</summary>
    public async Task TrySyncRunnerAsync(Runner runner, CancellationToken cancellationToken = default)
    {
        try
        {
            await SyncRunnerAsync(runner, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Final session sync from {Runner} failed", runner.Name);
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
        var stored = await GetAsync(sessionId, cancellationToken) ?? throw new ArgumentException("Session not found.");
        var session = await LoadAsync(sessionId, cancellationToken);
        session.Title = stored.Title;
        var name = SafeFileName(stored.Title);
        if (format == "deve")
        {
            return new SessionExport($"{name}.deve.json", "application/json", session.ToJson(indented: true));
        }

        var harnessFormat = _formats.GetValueOrDefault(format) ?? throw new ArgumentException($"Unknown export format '{format}'.");
        var harnessSessionId = stored.HarnessSessionId ?? sessionId.ToString("N");
        var cwd = session.Cwd ?? stored.WorkingDirectory ?? "/";
        return new SessionExport($"{name}.{format}.jsonl", "application/x-ndjson", harnessFormat.Write(session, harnessSessionId, cwd));
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        using var lease = await LockAsync(cancellationToken);
        if (await db.Sessions.AnyAsync(s => s.Id == sessionId && s.AgentId != null, cancellationToken))
            throw new InvalidOperationException("Select another session before deleting the active conversation.");
        await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
        File.Delete(PathFor(sessionId));
        images.DeleteSession(sessionId);
        if (Directory.Exists(options.SessionsDirectory))
            foreach (var backup in Directory.EnumerateFiles(options.SessionsDirectory, $"{sessionId:N}.*.json")) File.Delete(backup);
        events.Publish(new ManagerEvent(ManagerEventKind.SessionRemoved, SessionId: sessionId));
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

    /// <summary>A copy of someone's conversation for <paramref name="ownerId"/>: its content and its shared images, not its history of moves.</summary>
    private async Task<StoredSession> CopyAsync(ManagerDbContext db, StoredSession source, Guid ownerId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var copy = new StoredSession
        {
            Id = Guid.NewGuid(),
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
        if (File.Exists(PathFor(source.Id)))
        {
            await SaveAsync(copy.Id, await LoadAsync(source.Id, cancellationToken), cancellationToken);
        }

        await images.CopyAsync(source.Id, copy.Id, cancellationToken);
        db.Sessions.Add(copy);
        return copy;
    }

    private void UpdateSizes(StoredSession stored)
    {
        stored.SavedSize = File.Exists(PathFor(stored.Id)) ? new FileInfo(PathFor(stored.Id)).Length : 0;
        stored.TotalSize = stored.SavedSize + (Directory.Exists(options.SessionsDirectory)
            ? Directory.EnumerateFiles(options.SessionsDirectory, $"{stored.Id:N}.*.json").Sum(p => new FileInfo(p).Length) : 0);
    }

    // Caller holds LockAsync and has stopped and synced the outgoing harness.
    internal async Task SelectLockedAsync(Runner runner, Agent agent, Guid? id, string? title, string nativeId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var selected = id.HasValue ? await db.Sessions.SingleAsync(s => s.Id == id, ct) : new StoredSession
        {
            Id = Guid.NewGuid(), OwnerId = agent.OwnerId, Title = string.IsNullOrWhiteSpace(title) ? "New session" : ValidateTitle(title),
            TitlePinned = !string.IsNullOrWhiteSpace(title), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            HarnessId = agent.HarnessId, WorkspaceAgentId = agent.Id
        };
        if (selected.WorkspaceAgentId != agent.Id) throw new InvalidOperationException("Choose a session from this workspace.");
        if (selected.AgentId != null && selected.AgentId != agent.Id) throw new InvalidOperationException("Session is already active elsewhere.");
        if (id.HasValue)
        {
            if (selected.HarnessId != agent.HarnessId) { Backup(selected); selected.TitlePinned = true; }
            await WriteForHarnessAsync(runner, selected, agent.HarnessId, nativeId, agent.WorkingDirectory, ct);
        }
        else { selected.HarnessSessionId = nativeId; db.Sessions.Add(selected); }
        selected.WorkingDirectory = agent.WorkingDirectory;
        selected.RepositoryUrl = agent.RepositoryUrl;
        selected.RepositoryBranch = agent.RepositoryBranch;
        selected.RunnerName = runner.Name;
        selected.HarnessTrail ??= agent.HarnessId;
        // Detach and attach in one transaction, while satisfying the unique active-session index.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Sessions.Where(s => s.AgentId == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentId, (Guid?)null), ct);
        selected.AgentId = agent.Id;
        await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(a => a
            .SetProperty(x => x.HarnessSessionId, nativeId).SetProperty(x => x.LastAssistantMessageAt, (DateTime?)null), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        agent.HarnessSessionId = nativeId;
        agent.LastAssistantMessageAt = null;
        events.SessionChanged(selected.Id, agent.Id);
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
            try { UpdateLastAssistantMessage(saved.Agent!, await LoadAsync(saved.Id, ct)); }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException)
            { logger.LogWarning(ex, "Could not read saved message timestamps for session {Session}", saved.Id); }
        }
        await db.SaveChangesAsync(ct);
    }

    internal static void Apply(StoredSession stored, DeveSession session, string? agentName)
    {
        // Agents start pi with --name <agent>; the first prompt says more than that, unless the user renamed the session.
        var title = session.Title is { Length: > 0 } name && name != agentName ? name : session.FirstPrompt() ?? session.Title;
        if (!stored.TitlePinned && !string.IsNullOrWhiteSpace(title))
        {
            stored.Title = title;
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
            await File.WriteAllTextAsync(path + ".tmp", session.ToJson(), cancellationToken);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string PathFor(Guid sessionId) => Path.Combine(options.SessionsDirectory, $"{sessionId:N}.json");

    private static string SafeFileName(string title)
    {
        var name = new string(title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return name.Length == 0 ? "session" : name.Length > 60 ? name[..60] : name;
    }
}
