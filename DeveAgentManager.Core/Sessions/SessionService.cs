using System.Text;
using System.Text.Json;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
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
    ManagerOptions options,
    ManagerEvents events,
    ILogger<SessionService> logger)
{
    private const string LocateMarker = "__DAM_SESSION__";

    private readonly Dictionary<string, ISessionFormat> _formats = formats.ToDictionary(f => f.HarnessId);
    private readonly Dictionary<string, string> harnessDisplayNames = harnesses.ToDictionary(h => h.Id, h => h.DisplayName);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool SupportsHarness(string harnessId) => _formats.ContainsKey(harnessId);

    public async Task<List<StoredSession>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.AsNoTracking().Include(s => s.Agent).ThenInclude(a => a!.Runner)
            .OrderByDescending(s => s.UpdatedAt).ToListAsync(cancellationToken);
    }

    public async Task<StoredSession?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.AsNoTracking().Include(s => s.Agent).ThenInclude(a => a!.Runner)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
    }

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
    /// to the Runner first, so the harness starts with the full history.
    /// </summary>
    public async Task AttachToAgentAsync(Runner runner, Agent agent, Guid? continueSessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        StoredSession stored;
        if (continueSessionId is { } existingId)
        {
            stored = await db.Sessions.FirstOrDefaultAsync(s => s.Id == existingId, cancellationToken) ?? throw new ArgumentException("Session not found.");
            if (stored.AgentId is not null)
            {
                throw new InvalidOperationException("This session already belongs to a running agent. Open that agent instead.");
            }

            await WriteForHarnessAsync(runner, stored, agent.HarnessId, agent.HarnessSessionId, agent.WorkingDirectory, cancellationToken);
        }
        else
        {
            stored = new StoredSession { Id = Guid.NewGuid(), Title = agent.Name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Sessions.Add(stored);
        }

        stored.AgentId = agent.Id;
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
        var before = PathFor(stored.Id);
        if (File.Exists(before))
        {
            File.Copy(before, Path.Combine(options.SessionsDirectory, $"{stored.Id:N}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.{stored.HarnessId}.json"), overwrite: true);
        }

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
        var session = await LoadAsync(stored.Id, cancellationToken);
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
    }

    private string DisplayName(string harnessId) => harnessDisplayNames.GetValueOrDefault(harnessId, harnessId);

    /// <summary>Copies the session files of all agents on a Runner that changed since the last sync. One SSH command when nothing changed.</summary>
    public async Task SyncRunnerAsync(Runner runner, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Agents created before sessions were stored (or whose registration failed) get a session now.
        var missing = await db.Agents.Where(a => a.RunnerId == runner.Id && !db.Sessions.Any(s => s.AgentId == a.Id)).ToListAsync(cancellationToken);
        foreach (var agent in missing)
        {
            db.Sessions.Add(new StoredSession
            {
                Id = Guid.NewGuid(),
                Title = agent.Name,
                AgentId = agent.Id,
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

        var located = ParseLocated((await shell.RunAsync(runner, script.ToString(), TimeSpan.FromSeconds(30), cancellationToken)).Output);
        foreach (var session in sessions)
        {
            if (!located.TryGetValue(session.Id, out var file) || (file.Size == session.SourceSize && file.Path == session.SourcePath))
            {
                continue;
            }

            try
            {
                var content = await shell.DownloadAsync(runner, file.Path, cancellationToken);
                if (content is null)
                {
                    continue;
                }

                var deve = _formats[session.HarnessId].Read(content);
                await SaveAsync(session.Id, deve, cancellationToken);
                Apply(session, deve, session.Agent?.Name);
                session.SourcePath = file.Path;
                session.SourceSize = file.Size;
                session.LastSyncedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                events.SessionChanged(session.Id, session.AgentId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not sync session {Session} from {Runner}", session.Id, runner.Name);
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

    /// <summary>Imports a DeveSession file or a harness's own session file (pi JSONL).</summary>
    public async Task<StoredSession> ImportAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var (session, harnessId) = Parse(content);
        var stored = new StoredSession { Id = Guid.NewGuid(), HarnessId = harnessId, CreatedAt = session.CreatedAt == default ? DateTime.UtcNow : session.CreatedAt };
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
        await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
        File.Delete(PathFor(sessionId));
        events.Publish(new ManagerEvent(ManagerEventKind.SessionRemoved, SessionId: sessionId));
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

    private static void Apply(StoredSession stored, DeveSession session, string? agentName)
    {
        // Agents start pi with --name <agent>; the first prompt says more than that, unless the user renamed the session.
        var title = session.Title is { Length: > 0 } name && name != agentName ? name : session.FirstPrompt() ?? session.Title;
        if (!string.IsNullOrWhiteSpace(title))
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
