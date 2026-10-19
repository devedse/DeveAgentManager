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
    SessionFileStore files,
    ManagerOptions options,
    ManagerEvents events,
    ILogger<SessionService> logger,
    AgentArtifacts? artifacts = null,
    HistorySyncSettingsStore? historySettings = null)
{
    private readonly AgentArtifacts _artifacts = artifacts ?? new(dbFactory, options, files, images, new AccessControl(dbFactory), events);
    internal Task PreserveArtifactsAsync(Guid agentId, CancellationToken ct) => _artifacts.PreserveAsync(agentId, ct);
    internal Task<bool> ArtifactPublicationBelongsAsync(Guid runner, Guid agent, Guid session, CancellationToken ct) =>
        _artifacts.OwnsSourceAsync(runner, agent, session, ct);
    internal Task ArtifactsChangedAsync(Guid sessionId, CancellationToken ct) => _artifacts.NotifyAsync(sessionId, ct);

    private readonly NativeHistoryStore _native = new(options.SessionsDirectory, logger: logger);
    private readonly SessionCompanionStore _companions = new(options.SessionsDirectory);
    private readonly HistorySyncSettingsStore _historySettings = historySettings ?? new(dbFactory, events);
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
        .Include(s => s.WorkspaceAgent).ThenInclude(a => a!.Runner).ThenInclude(r => r!.Members);

    public async Task<StoredSession?> GetForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.AgentId == agentId, cancellationToken);
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
        var reportPath = MigrationPathFor(sessionId);
        if (File.Exists(reportPath))
        {
            try
            {
                await using var reportStream = File.OpenRead(reportPath);
                var report = await JsonSerializer.DeserializeAsync<SessionMigrationReport>(reportStream, DeveSession.JsonOptions, cancellationToken);
                if (report?.TargetSessionId == result.Sources.LastOrDefault()?.SessionId) result.Migration = report;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            { logger.LogWarning("Could not read migration diagnostics for {Session}: {ErrorType}", sessionId, ex.GetType().Name); }
        }
        return result;
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
            await EnsureVerifiedLockedAsync(existingId, cancellationToken);
            stored = await db.Sessions.FirstOrDefaultAsync(s => s.Id == existingId, cancellationToken) ?? throw new ArgumentException("Session not found.");
            if (stored.OwnerId != agent.OwnerId)
            {
                stored = await CopyAsync(db, stored, agent.OwnerId, cancellationToken);
            }
            else if (stored.AgentId is not null || stored.WorkspaceAgentId is not null)
            {
                throw new InvalidOperationException("This session already belongs to a running agent. Open that agent instead.");
            }
            else if (await _artifacts.HasCollectionAsync(stored.Id, cancellationToken))
            {
                // A deleted agent's archives still reference this source folder. Continue an independent copy so
                // future publications by the new agent cannot appear in the old agent's remaining archives.
                stored = await CopyAsync(db, stored, agent.OwnerId, cancellationToken);
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
    /// conversation is converted (see <see cref="SessionMigration"/>) and written as the new harness's own
    /// session under <paramref name="harnessSessionId"/>, which the harness then resumes. The conversation as it was
    /// before is kept next to it in /data/sessions. Sync the Runner first, so the latest messages move along.
    /// </summary>
    public async Task MoveToHarnessAsync(Runner runner, Agent agent, string harnessId, string harnessSessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Sessions.FirstOrDefaultAsync(s => s.AgentId == agent.Id, cancellationToken)
            ?? throw new InvalidOperationException("This agent has no stored conversation to move.");
        await EnsureVerifiedLockedAsync(stored.Id, cancellationToken);
        await db.Entry(stored).ReloadAsync(cancellationToken);
        await BackupAsync(stored, cancellationToken);

        stored.TitlePinned = true; // Keep the conversation name across format conversion.
        await WriteForHarnessAsync(runner, stored, harnessId, harnessSessionId, agent.WorkingDirectory, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        events.SessionChanged(stored.Id, agent.Id);
    }

    /// <summary>
    /// Writes a stored conversation to a Runner in a harness's format. Its own harness gets it unchanged; another harness
    /// gets validated structured tool history. Updates the stored session after verified installation.
    /// </summary>
    private async Task WriteForHarnessAsync(Runner runner, StoredSession stored, string harnessId, string harnessSessionId, string workingDirectory, CancellationToken cancellationToken)
    {
        var format = _formats.GetValueOrDefault(harnessId) ?? throw new InvalidOperationException($"Conversations can't be continued in '{harnessId}' yet.");
        if (stored.MessageCount > 0 && !HasSaved(stored.Id))
            throw new IOException("The saved history is missing. Restore it before switching sessions.");
        var session = await LoadAsync(stored.Id, cancellationToken);
        session.Title = stored.Title;
        var from = session.Sources.LastOrDefault()?.Harness ?? session.Entries.LastOrDefault(e => e.Harness is not null)?.Harness ?? stored.HarnessId;
        if (session.Entries.Count > 0 && from != harnessId)
        {
            session = SessionMigration.Prepare(session, DisplayName(from), DisplayName(harnessId), harnessId);
        }

        // Nothing to move (e.g. pi writes its file only with the first message): the new harness starts a conversation
        // of its own under the id, instead of being handed an empty one (claude --resume refuses those).
        if (session.Entries.Count > 0)
        {
            var path = format.GetPath(harnessSessionId, workingDirectory, DateTime.UtcNow);
            var content = format.Write(session, harnessSessionId, workingDirectory);
            if (session.Migration is not null && from != harnessId)
                SessionMigration.ValidateWritten(session, format.Read(content));
            foreach (var (name, companion) in format.WriteCompanionFiles(session, harnessSessionId, workingDirectory))
                await shell.UploadAtomicAsync(runner, path[..path.LastIndexOf('/')] + "/" + SessionCompanionStore.ValidatePath(name), companion, cancellationToken);
            await shell.UploadAtomicAsync(runner, path, content, cancellationToken);
            if (session.Migration is { } report)
            {
                report.TargetSessionId = harnessSessionId;
                await SaveMigrationAsync(stored.Id, report, cancellationToken);
                logger.LogInformation("Session {Session} migration v{Version} {From} to {To}: {Preserved} calls, {Archived} archived, {Omitted} omitted exchanges, {Images} omitted images",
                    stored.Id, report.Version, report.From, report.To, report.PreservedCalls, report.ArchivedExchanges, report.OmittedExchanges, report.OmittedImages);
            }
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

    private bool HasSaved(Guid id) => _native.Exists(id) || File.Exists(PathFor(id));

    private async Task BackupAsync(StoredSession stored, CancellationToken ct)
    {
        if (!HasSaved(stored.Id)) return;
        var backup = Path.Combine(options.SessionsDirectory, $"{stored.Id:N}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.{stored.HarnessId}.json");
        if (_native.Exists(stored.Id))
        {
            var conversation = await LoadAsync(stored.Id, ct);
            await using var output = File.Create(backup);
            await conversation.WriteAsync(output, ct);
        }
        else File.Copy(PathFor(stored.Id), backup);
    }

    private string DisplayName(string harnessId) => harnessDisplayNames.GetValueOrDefault(harnessId, harnessId);

    /// <summary>Copies the session files of all agents on a Runner that changed since the last sync. One SSH command when nothing changed.</summary>
    public async Task SyncRunnerAsync(Runner runner, CancellationToken cancellationToken = default, bool forceVerification = false)
    {
        using var lease = await LockAsync(cancellationToken);
        await SyncRunnerLockedAsync(runner, cancellationToken, forceVerification: forceVerification);
    }

    internal async Task SyncRunnerLockedAsync(Runner runner, CancellationToken cancellationToken, Guid? strictAgentId = null,
        bool forceVerification = false, Guid? onlySessionId = null)
    {
        using var timing = Diagnostics.CaptureOperations.Start("runner.history-sync", runner: runner.Id);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Agents created before sessions were stored (or whose registration failed) get a session now.
        var missing = await db.Agents.Where(a => onlySessionId == null && a.RunnerId == runner.Id && !db.Sessions.Any(s => s.AgentId == a.Id)).ToListAsync(cancellationToken);
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
            .Where(s => s.Agent != null && s.Agent.RunnerId == runner.Id && (s.Agent.State != AgentState.Moving || s.Agent.Id == strictAgentId) && s.HarnessSessionId != null && (onlySessionId == null || s.Id == onlySessionId))
            .ToListAsync(cancellationToken);
        sessions = sessions.Where(s => _formats.ContainsKey(s.HarnessId)).ToList();
        if (sessions.Count == 0)
        {
            return;
        }

        var historySettings = await _historySettings.GetAsync(cancellationToken);
        var script = new StringBuilder();
        foreach (var session in sessions)
        {
            script.Append($"echo {LocateMarker} {session.Id:N}; f=; ").Append(_formats[session.HarnessId].LocateCommand(session.HarnessSessionId!)).Append("; if [ -n \"$f\" ]; then printf '__DAM_STAMP__ %s\\n' \"$(stat -L -c '%d:%i:%y:%z' \"$f\")\"; fi\n");
        }

        var result = await shell.RunAsync(runner, script.ToString(), TimeSpan.FromSeconds(30), cancellationToken);
        result.EnsureSuccess("Locating session histories");
        var located = ParseLocated(result.Output);
        var stamps = ParseStamps(result.Output);
        foreach (var session in sessions)
        {
            if (!located.TryGetValue(session.Id, out var file))
            {
                if (session.AgentId == strictAgentId && (session.SourcePath != null || session.MessageCount > 0 || session.Agent?.LastAssistantMessageAt != null))
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
                var policy = new NativeHistorySyncPolicy(historySettings.Mode,
                    Force: forceVerification || session.AgentId == strictAgentId,
                    Idle: session.Agent is { Working: false });
                var incremental = _formats[session.HarnessId].SupportsIncrementalRead && stamp is not null;
                if (incremental && !_formats[session.HarnessId].HasCompanionFiles && session.SourceSize == file.Size && session.SourcePath == file.Path
                    && await _native.IsCurrentAsync(session.Id, session.HarnessId, file.Path, file.Size, stamp!, cancellationToken, policy)) continue;
                DeveSession deve;
                if (incremental)
                {
                    var sync = await _native.SyncAsync(session.Id, _formats[session.HarnessId], file.Path, file.Size, stamp!,
                        async (offset, hash, output, ct) =>
                        {
                            var ok = await shell.DownloadHistoryRangeAsync(runner, file.Path, offset, file.Size, hash, output, ct);
                            return new NativeHistoryTransfer(ok is not null, ok);
                        }, cancellationToken, policy, (offset, samples, output, ct) =>
                            shell.DownloadHistoryAppendAsync(runner, file.Path, offset, file.Size, stamp!, samples, output, ct),
                        async (metadata, ct) =>
                        {
                            var generation = await _companions.SyncAsync(shell, runner, session.Id, _formats[session.HarnessId], file.Path, metadata, ct, policy);
                            if (generation is not null)
                            {
                                var current = await shell.RunAsync(runner, "stat -L -c '%d:%i:%y:%z' -- " + ShellQuote.Quote(file.Path), cancellationToken: ct);
                                current.EnsureSuccess("Checking native session snapshot");
                                if (current.Output.TrimEnd('\r', '\n') != stamp) throw new IOException("The native log changed while its replay files were saved; synchronization will retry.");
                            }
                            return generation;
                        });
                    if (!sync.Changed && session.SourceSize == file.Size && session.SourcePath == file.Path) continue;
                    deve = sync.Metadata;
                    await _companions.PruneAsync(session.Id, (await _native.SummaryAsync(session.Id, cancellationToken))?.CompanionManifest, cancellationToken);
                    File.Delete(PathFor(session.Id)); // The verified native copy is now the authoritative full history.
                }
                else
                {
                    var content = await shell.DownloadAsync(runner, file.Path, cancellationToken)
                        ?? throw new IOException("Could not download the current conversation.");
                    deve = _formats[session.HarnessId].Read(content);
                    await SaveAsync(session.Id, deve, cancellationToken);
                }
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
        using var lease = await LockAsync(cancellationToken);
        await EnsureVerifiedLockedAsync(sessionId, cancellationToken);
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
        var from = session.Sources.LastOrDefault()?.Harness ?? stored.HarnessId;
        if (from != format && session.Entries.Count > 0)
            session = SessionMigration.Prepare(session, DisplayName(from), DisplayName(format), format);
        var content = harnessFormat.Export(session, harnessSessionId, cwd);
        if (from != format) SessionMigration.ValidateWritten(session, harnessFormat.Read(content));
        return new SessionExport($"{name}.{format}.{harnessFormat.ExportExtension}", harnessFormat.ExportContentType, content);
    }

    // Caller holds the operation lock. Never export/continue a sampled checkpoint while its source is unavailable.
    private async Task EnsureVerifiedLockedAsync(Guid id, CancellationToken ct)
    {
        var state = await _native.SummaryAsync(id, ct);
        if (state is null || state.Sha256.Length > 0 && !await _companions.HasPendingVerificationAsync(id, state.CompanionManifest, ct)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Sessions.Include(s => s.Agent).ThenInclude(a => a!.Runner)
            .SingleAsync(s => s.Id == id, ct);
        if (stored.Agent?.Runner is not { State: RunnerState.Ready } runner)
            throw new IOException("This history has pending verification. Reconnect its Runner and synchronize before exporting or continuing it.");
        await SyncRunnerLockedAsync(runner, ct, stored.Agent.Id, onlySessionId: id);
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
            foreach (var backup in Directory.EnumerateFiles(options.SessionsDirectory, $"{sessionId:N}.*.json")) File.Delete(backup);
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
    private async Task<StoredSession> CopyAsync(ManagerDbContext db, StoredSession source, Guid ownerId, CancellationToken cancellationToken)
    {
        await EnsureVerifiedLockedAsync(source.Id, cancellationToken);
        await db.Entry(source).ReloadAsync(cancellationToken);
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
        if (HasSaved(source.Id))
        {
            await SaveAsync(copy.Id, await LoadAsync(source.Id, cancellationToken), cancellationToken);
        }

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
        var changesHarness = selected.HarnessId != agent.HarnessId;
        if (changesHarness && agent.LoginStopPending) throw new InvalidOperationException("Wait for pending login enforcement before changing harnesses.");
        var accountId = changesHarness ? null : agent.AccountId;
        if (id.HasValue)
        {
            await EnsureVerifiedLockedAsync(selected.Id, ct);
            await db.Entry(selected).ReloadAsync(ct);
            await WriteForHarnessAsync(runner, selected, selected.HarnessId, nativeId, agent.WorkingDirectory, ct);
        }
        else { selected.HarnessSessionId = nativeId; db.Sessions.Add(selected); }
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
