using System.Text.Json;
using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

internal sealed record AgentMoveState(string Id, Guid AgentId, Guid SourceRunner, Guid TargetRunner,
    string Workspace, string Owner, string Agent, AgentState PreviousState, bool Resume,
    string Phase = "Queued", string? Home = null, bool Source = true, bool SourceStopped = false,
    bool TargetLaunchAttempted = false, bool FallbackLaunchAttempted = false, string? ArchiveSha256 = null,
    IReadOnlyList<MoveHistory>? Histories = null, IReadOnlyList<string>? OtherSourceFolders = null, IReadOnlyList<string>? OtherTargetFolders = null,
    string? TmuxSession = null);
internal sealed record MoveHistory(string Path, string Harness);

// Queueing a move and beginning runner maintenance must be mutually exclusive.
internal static class AgentMoveCoordination
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    internal static readonly HashSet<Guid> OperatingRunners = [];
    internal static readonly ConcurrentDictionary<Guid, WorkspaceClaim> Creating = new();
    internal sealed record WorkspaceClaim(Guid Runner, string Name, string Path);
    internal sealed class CreationLease(Guid key) : IDisposable
    {
        public void Dispose() => Creating.TryRemove(key, out _);
    }
}

public sealed partial class AgentService
{
    private static readonly string MoveScript = ReadMoveScript();
    private static string ReadMoveScript()
    {
        using var stream = typeof(AgentService).Assembly.GetManifestResourceStream("DeveAgentManager.Core.Scripts.agent-move.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    internal static string MoveCommand(string phase, AgentMoveState move) =>
        "python3 - " + ShellQuote.Quote(phase) + " " + ShellQuote.Quote(JsonSerializer.Serialize(move)) + " <<'DAM_MOVE'\n" + MoveScript + "\nDAM_MOVE";
    internal static bool MoveBlocksActions(Agent agent) => agent.MoveJson is not null
        && JsonSerializer.Deserialize<AgentMoveState>(agent.MoveJson)?.Phase is not ("Cleanup" or "CleanupFailed" or "TargetCleanup" or "TargetCleanupFailed");
    private static void RequireNotMoving(Agent agent)
    {
        if (MoveBlocksActions(agent)) throw new InvalidOperationException("This agent has a move in progress. Wait for it or recover the failed move first.");
    }

    private async Task<IDisposable> ReserveWorkspaceAsync(Guid runner, string name, string path, CancellationToken ct)
    {
        await AgentMoveCoordination.Gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pending = await db.Agents.Where(a => a.MoveJson != null).Select(a => new { a.Name, a.MoveJson }).ToListAsync(ct);
            if (pending.Any(a => JsonSerializer.Deserialize<AgentMoveState>(a.MoveJson!) is { } move
                && (move.SourceRunner == runner || move.TargetRunner == runner)
                && (a.Name == name || OverlappingFolders(move.Workspace, path))))
                throw new InvalidOperationException("This agent name or workspace is reserved by a pending move. Finish the move and its cleanup first.");
            var key = Guid.NewGuid();
            AgentMoveCoordination.Creating[key] = new(runner, name, path);
            return new AgentMoveCoordination.CreationLease(key);
        }
        finally { AgentMoveCoordination.Gate.Release(); }
    }

    public async Task RequestMoveAsync(CurrentUser actor, Guid agentId, Guid targetRunnerId, CancellationToken ct)
    {
        await AgentMoveCoordination.Gate.WaitAsync(ct);
        try
        {
            using var lease = await sessions.LockAsync(ct);
            using var loginLease = await Accounts.AccountCoordination.LockAsync(ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var agent = await db.Agents.Include(a => a.Runner).SingleAsync(a => a.Id == agentId, ct);
            if (agent.MoveJson is not null) throw new InvalidOperationException("Finish the previous move or its cleanup first.");
            if (await access.AgentAsync(actor, agentId, ct) < AgentAccess.Owner)
                throw new UnauthorizedAccessException("Only the agent owner can move it.");
            if (agent.RunnerId == targetRunnerId) throw new ArgumentException("Choose another runner.");
            var target = await db.Runners.SingleOrDefaultAsync(r => r.Id == targetRunnerId, ct) ?? throw new KeyNotFoundException("Runner not found.");
            var owner = await accounts.GetActorAsync(agent.OwnerId, ct);
            if (await access.RunnerAsync(actor, targetRunnerId, ct) < RunnerAccess.Use || await access.RunnerAsync(owner, targetRunnerId, ct) < RunnerAccess.Use)
                throw new UnauthorizedAccessException("The agent owner needs Use access to the destination runner.");
            RequireReady(agent.Runner!); RequireReady(target);
            if (AgentMoveCoordination.OperatingRunners.Contains(agent.RunnerId) || AgentMoveCoordination.OperatingRunners.Contains(target.Id))
                throw new InvalidOperationException("Wait for runner maintenance to finish before moving an agent.");
            if (AgentMoveCoordination.Creating.Values.Any(claim => (claim.Runner == agent.RunnerId || claim.Runner == target.Id)
                && (claim.Name == agent.Name || OverlappingFolders(claim.Path, agent.WorkingDirectory))))
                throw new InvalidOperationException("Wait for agent creation in this workspace to finish before moving it.");
            if (agent.Runner!.Username != target.Username) throw new ArgumentException("Choose a runner with the same SSH username to preserve absolute file references.");
            if (!target.HarnessIds.Contains(agent.HarnessId)) throw new ArgumentException("Install this agent's harness on the destination runner first.");
            if (await db.Agents.AnyAsync(a => a.RunnerId == target.Id && a.Name == agent.Name, ct))
                throw new InvalidOperationException("The destination already has an agent with this name.");
            var folders = await db.Agents.Where(a => a.Id != agent.Id && (a.RunnerId == agent.RunnerId || a.RunnerId == target.Id))
                .Select(a => a.WorkingDirectory).ToListAsync(ct);
            if (folders.Any(path => OverlappingFolders(path, agent.WorkingDirectory)))
                throw new InvalidOperationException("Another agent shares this workspace or a parent folder. Separate their workspaces before moving it.");
            await accounts.ResolveAsync(agent, ct);
            var move = new AgentMoveState(Guid.NewGuid().ToString("N"), agent.Id, agent.RunnerId, target.Id,
                agent.WorkingDirectory, agent.OwnerId.ToString("N"), agent.Id.ToString("N"), agent.State,
                agent.State is AgentState.Running or AgentState.Starting,
                OtherSourceFolders: await db.Agents.Where(a => a.Id != agent.Id && a.RunnerId == agent.RunnerId).Select(a => a.WorkingDirectory).ToListAsync(ct),
                OtherTargetFolders: await db.Agents.Where(a => a.RunnerId == target.Id).Select(a => a.WorkingDirectory).ToListAsync(ct));
            agent.MoveJson = JsonSerializer.Serialize(move);
            agent.State = AgentState.Moving;
            agent.StateMessage = $"Moving to {target.Name}…";
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            events.AgentChanged(agent.RunnerId, agent.Id);
        }
        finally { AgentMoveCoordination.Gate.Release(); }
    }

    internal static bool OverlappingFolders(string a, string b)
    {
        a = Path.GetFullPath(a).TrimEnd('/'); b = Path.GetFullPath(b).TrimEnd('/');
        return a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);
    }

    internal async Task EnsureRunnerNotMovingAsync(Guid runnerId, CancellationToken ct, bool deleting = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.Agents.Where(a => a.MoveJson != null).Select(a => a.MoveJson!).ToListAsync(ct);
        if (pending.Select(json => JsonSerializer.Deserialize<AgentMoveState>(json)!).Any(move =>
            (move.SourceRunner == runnerId || move.TargetRunner == runnerId)
            && (deleting || move.Phase is not ("Failed" or "Cleanup" or "CleanupFailed" or "TargetCleanup" or "TargetCleanupFailed"))))
            throw new AgentMoveBusyException("An agent is moving to or from this runner. Wait for the move to finish.");
    }

    public async Task RecoverMoveAsync(CurrentUser actor, Guid agentId, CancellationToken ct)
    {
        using var lease = await sessions.LockAsync(ct);
        if (await access.AgentAsync(actor, agentId, ct) < AgentAccess.Owner) throw new UnauthorizedAccessException("Only the owner can recover this move.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.SingleAsync(a => a.Id == agentId, ct);
        var move = JsonSerializer.Deserialize<AgentMoveState>(agent.MoveJson ?? throw new InvalidOperationException("There is no pending move."))!;
        if (move.Phase is not ("Failed" or "CleanupFailed" or "TargetCleanupFailed")) throw new InvalidOperationException("The move is still running.");
        agent.MoveJson = JsonSerializer.Serialize(move with { Phase = move.Phase == "CleanupFailed" ? "Cleanup" : move.Phase == "TargetCleanupFailed" ? "TargetCleanup" : "Recover", FallbackLaunchAttempted = false });
        if (move.Phase == "Failed") agent.State = AgentState.Moving;
        await db.SaveChangesAsync(ct);
        events.AgentChanged(agent.RunnerId, agent.Id);
    }

    private async Task SaveMoveAsync(AgentMoveState move, string message, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var payload = JsonSerializer.Serialize(move);
        await db.Agents.Where(a => a.Id == move.AgentId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.MoveJson, payload)
            .SetProperty(a => a.StateMessage, message).SetProperty(a => a.UpdatedAt, DateTime.UtcNow), ct);
        events.AgentChanged(move.SourceRunner, move.AgentId);
        events.AgentChanged(move.TargetRunner, move.AgentId);
    }

    private async Task<CommandResult> MovePhaseAsync(Runner runner, string phase, AgentMoveState move, CancellationToken ct) =>
        (await shell.RunAsync(runner, MoveCommand(phase, move), TimeSpan.FromMinutes(30), ct)).EnsureSuccess("Agent move " + phase);

    private async Task<bool> MovePaneAliveAsync(Runner runner, Agent agent, CancellationToken ct)
    {
        var result = (await shell.RunAsync(runner, "if tmux has-session -t " + ShellQuote.Quote("=" + agent.TmuxSession)
            + " 2>/dev/null; then tmux display-message -p -t " + ShellQuote.Quote("=" + agent.TmuxSession + ":")
            + " '#{pane_dead}'; else echo absent; fi", TimeSpan.FromSeconds(20), ct)).EnsureSuccess("Verifying agent process");
        return result.Output.Trim() switch { "0" => true, "1" or "absent" => false, _ => throw new IOException("Agent process state is uncertain.") };
    }

    internal async Task ExecuteMoveAsync(AgentMoveState move, string localFolder, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Runner).SingleAsync(a => a.Id == move.AgentId, ct);
        var source = await db.Runners.SingleAsync(r => r.Id == move.SourceRunner, ct);
        var target = await db.Runners.SingleAsync(r => r.Id == move.TargetRunner, ct);
        move = move with { TmuxSession = agent.TmuxSession };
        if (move.Phase != "Queued")
        {
            if (move.Phase == "Cleanup") { await FinishMoveCleanupAsync(move, source, ct); return; }
            if (move.Phase == "TargetCleanup") { await MovePhaseAsync(target, "cleanup-target", move, ct); await ClearMoveAsync(move, ct); return; }
            await RollbackMoveAsync(move, agent, source, target, "Interrupted move", ct); return;
        }
        try
        {
            var sourceCheck = await MovePhaseAsync(source, "preflight", move, ct);
            await MovePhaseAsync(source, "check-background", move, ct);
            var targetCheck = await MovePhaseAsync(target, "preflight", move with { Source = false }, ct);
            using var first = JsonDocument.Parse(sourceCheck.Output); using var second = JsonDocument.Parse(targetCheck.Output);
            if (first.RootElement.GetProperty("home").GetString() != second.RootElement.GetProperty("home").GetString()
                || first.RootElement.GetProperty("workspace").GetString() != second.RootElement.GetProperty("workspace").GetString())
                throw new InvalidOperationException("The runners resolve home or workspace paths differently. The source is unchanged.");
            var existingTerminal = await shell.RunAsync(target, "tmux has-session -t " + ShellQuote.Quote("=" + agent.TmuxSession) + " 2>/dev/null", cancellationToken: ct);
            if (existingTerminal.ExitCode == 0) throw new InvalidOperationException("A terminal with this agent name already exists on the destination. Remove that terminal before moving.");
            if (existingTerminal.ExitCode != 1) throw new IOException("Could not verify that the destination terminal is absent.");
            move = move with { Home = first.RootElement.GetProperty("home").GetString(), Phase = "Stopping",
                Resume = move.Resume || await MovePaneAliveAsync(source, agent, ct) };
            await SaveMoveAsync(move, "Stopping and verifying source history…", ct);
            using (await sessions.LockAsync(ct))
            {
                (await shell.RunAsync(source, BuildStopScript(ShellQuote.Quote("=" + agent.TmuxSession + ":")), TimeSpan.FromMinutes(1), ct)).EnsureSuccess("Stopping source agent");
                if (await MovePaneAliveAsync(source, agent, ct)) throw new IOException("Source agent did not stop.");
                await MovePhaseAsync(source, "quiesce", move, ct);
                move = move with { SourceStopped = true, Phase = "Packing" };
                await SaveMoveAsync(move, "Packing workspace, native history and attachments…", ct);
                await sessions.SyncRunnerLockedAsync(source, ct, agent.Id, forceVerification: true);
                await sessions.PreserveArtifactsAsync(agent.Id, ct);
                var stored = await db.Sessions.AsNoTracking().Where(s => s.AgentId == agent.Id || s.WorkspaceAgentId == agent.Id)
                    .Where(s => s.SourcePath != null).Select(s => new MoveHistory(s.SourcePath!, s.HarnessId)).ToListAsync(ct);
                move = move with { Histories = stored };
                await SaveMoveAsync(move, "Packing workspace, native history and attachments…", ct);
            }
            var pack = await MovePhaseAsync(source, "pack", move, ct);
            var hash = pack.Output.Trim();
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new IOException("Invalid move archive checksum.");
            move = move with { ArchiveSha256 = hash, Phase = "Transferring" };
            await SaveMoveAsync(move, "Transferring and checking every file…", ct);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(localFolder);
            else Directory.CreateDirectory(localFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var archive = Path.Combine(localFolder, move.Id + ".tar");
            var remote = $"~/.deveagentmanager/moves/{move.Id}/bundle.tar";
            try
            {
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(archive, fileOptions)) await shell.DownloadToAsync(source, remote, output, ct);
                (await shell.RunAsync(target, "umask 077\nmkdir -p " + ShellQuote.Path($"~/.deveagentmanager/moves/{move.Id}"), cancellationToken: ct)).EnsureSuccess("Preparing destination");
                await using (var input = File.OpenRead(archive)) await shell.UploadFromAsync(target, remote, input, ct);
            }
            finally { File.Delete(archive); }
            await MovePhaseAsync(target, "unpack", move, ct);
            move = move with { Phase = "Installing" };
            await SaveMoveAsync(move, "Installing verified files on the destination…", ct);
            await MovePhaseAsync(target, "install", move, ct);
            await MovePhaseAsync(source, "verify-source", move, ct);
            using (await sessions.LockAsync(ct))
            {
                db.Entry(agent).State = EntityState.Detached;
                agent = await db.Agents.SingleAsync(a => a.Id == move.AgentId, ct);
                var owner = await accounts.GetActorAsync(agent.OwnerId, ct);
                if (await access.RunnerAsync(owner, target.Id, ct) < RunnerAccess.Use)
                    throw new UnauthorizedAccessException("The agent owner no longer has Use access to the destination.");
                agent.RunnerId = target.Id; agent.Runner = target;
                await db.Sessions.Where(s => s.AgentId == agent.Id || s.WorkspaceAgentId == agent.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RunnerName, target.Name), ct);
                await db.SaveChangesAsync(ct);
                if (move.Resume)
                {
                    move = move with { Phase = "StartingTarget", TargetLaunchAttempted = true };
                    await SaveMoveAsync(move, "Starting the agent on the destination…", ct);
                    await LaunchAsync(target, agent, restart: false, ct);
                    await Task.Delay(1000, ct);
                    if (!await MovePaneAliveAsync(target, agent, ct)) throw new IOException("The destination harness exited during startup.");
                }
                move = move with { Phase = "Cleanup" };
                agent.MoveJson = JsonSerializer.Serialize(move);
                agent.State = move.Resume ? AgentState.Running : AgentState.Stopped;
                agent.StateMessage = "Moved; removing source files…";
                agent.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            events.AgentChanged(source.Id, agent.Id); events.AgentChanged(target.Id, agent.Id);
            await FinishMoveCleanupAsync(move, source, ct);
        }
        catch (Exception error)
        {
            if (move.Phase == "Cleanup")
            {
                await SaveMoveAsync(move with { Phase = "CleanupFailed" }, "Moved successfully; source cleanup needs attention: " + error.Message, CancellationToken.None);
                return; // The live target is committed. Never launch it again or roll it back for a cleanup error.
            }
            using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await RollbackMoveAsync(move, agent, source, target, error.Message, rollbackTimeout.Token);
        }
    }

    private async Task FinishMoveCleanupAsync(AgentMoveState move, Runner source, CancellationToken ct)
    {
        var agent = (await GetAsync(move.AgentId, ct))!;
        if (await MovePaneAliveAsync(source, agent, ct))
            throw new IOException("A source terminal is running. Source files were retained; stop it before finishing cleanup.");
        await MovePhaseAsync(source, "quiesce", move, ct);
        await MovePhaseAsync(source, "cleanup-source", move, ct);
        (await shell.RunAsync(source, "tmux kill-session -t " + ShellQuote.Quote("=" + agent.TmuxSession)
            + " 2>/dev/null || true\nrm -f -- " + ShellQuote.Path($"~/.deveagentmanager/artifact-access/{move.Agent}.token"), cancellationToken: ct)).EnsureSuccess("Removing source agent runtime");
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var target = await db.Runners.SingleAsync(r => r.Id == move.TargetRunner, ct);
            await MovePhaseAsync(target, "discard", move, ct);
        }
        await ClearMoveAsync(move, ct);
    }

    private async Task ClearMoveAsync(AgentMoveState move, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Agents.Where(a => a.Id == move.AgentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.MoveJson, (string?)null).SetProperty(a => a.StateMessage, (string?)null), ct);
        events.AgentChanged(move.TargetRunner, move.AgentId);
    }

    private async Task RollbackMoveAsync(AgentMoveState move, Agent agent, Runner source, Runner target, string error, CancellationToken ct)
    {
        try
        {
            if (move.TargetLaunchAttempted)
            {
                (await shell.RunAsync(target, BuildStopScript(ShellQuote.Quote("=" + agent.TmuxSession + ":")), TimeSpan.FromMinutes(1), ct)).EnsureSuccess("Stopping destination for rollback");
                if (await MovePaneAliveAsync(target, agent, ct)) throw new IOException("Destination process is still running; source will not be restarted.");
            }
            var cleanupPending = false;
            if (move.Phase is not ("Queued" or "Stopping" or "Packing"))
            {
                try { await MovePhaseAsync(target, "cleanup-target", move, ct); }
                // Target liveness was checked above. Preserve changed/unreachable files without blocking source recovery.
                catch (Exception) when (!ct.IsCancellationRequested) { cleanupPending = true; }
            }
            using (await sessions.LockAsync(ct))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                agent = await db.Agents.SingleAsync(a => a.Id == move.AgentId, ct);
                agent.RunnerId = source.Id;
                agent.Runner = await db.Runners.SingleAsync(r => r.Id == source.Id, ct);
                agent.MoveJson = JsonSerializer.Serialize(move);
                await db.Sessions.Where(s => s.AgentId == agent.Id || s.WorkspaceAgentId == agent.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RunnerName, source.Name), ct);
                await db.SaveChangesAsync(ct);
                if (move.Resume && !await MovePaneAliveAsync(source, agent, ct))
                {
                    if (move.FallbackLaunchAttempted) throw new IOException("A fallback launch was already attempted. Check the source before starting it manually.");
                    move = move with { Phase = "Rollback", FallbackLaunchAttempted = true };
                    await SaveMoveAsync(move, "Resuming on the source after a failed move…", ct);
                    await LaunchAsync(source, agent, restart: false, ct);
                    if (!await MovePaneAliveAsync(source, agent, ct)) throw new IOException("Source harness did not resume.");
                }
                agent.State = move.Resume ? AgentState.Running : move.PreviousState;
                agent.StateMessage = "Move failed; source retained: " + error;
                agent.MoveJson = cleanupPending ? JsonSerializer.Serialize(move with { Phase = "TargetCleanup" }) : null;
                await db.SaveChangesAsync(ct);
            }
            events.AgentChanged(source.Id, agent.Id); events.AgentChanged(target.Id, agent.Id);
            try { await MovePhaseAsync(source, "discard", move, ct); }
            catch (Exception cleanupError) { logger.LogDebug(cleanupError, "Move staging retained on source {Runner}", source.Id); }
        }
        catch (Exception rollbackError)
        {
            await SaveMoveAsync(move with { Phase = "Failed" }, "Move needs recovery. Source files retained. " + rollbackError.Message, CancellationToken.None);
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.Agents.Where(a => a.Id == move.AgentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentState.Error), CancellationToken.None);
        }
    }
}

internal sealed class AgentMoveBusyException(string message) : InvalidOperationException(message);

/// <summary>Owns queued moves independently of a browser connection. Interrupted moves reconcile by rollback.</summary>
public sealed class AgentMoves(IDbContextFactory<ManagerDbContext> factory, AgentService agents, ManagerOptions options, ILogger<AgentMoves> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var pending = await db.Agents.AsNoTracking().Where(a => a.MoveJson != null).Select(a => a.MoveJson!).ToListAsync(stoppingToken);
                foreach (var json in pending)
                {
                    var move = JsonSerializer.Deserialize<AgentMoveState>(json)!;
                    if (move.Phase is "Failed" or "CleanupFailed" or "TargetCleanupFailed") continue;
                    try { await agents.ExecuteMoveAsync(move, Path.Combine(options.DataDirectory, "moves"), stoppingToken); }
                    catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(e, "Agent move {Move} retained for recovery", move.Id);
                        var failed = JsonSerializer.Serialize(move with { Phase = move.Phase == "Cleanup" ? "CleanupFailed" : move.Phase == "TargetCleanup" ? "TargetCleanupFailed" : "Failed" });
                        await db.Agents.Where(a => a.Id == move.AgentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.MoveJson, failed)
                            .SetProperty(a => a.StateMessage, "Move needs recovery: " + e.Message), stoppingToken);
                    }
                    finally
                    {
                        try { DeleteLocalArchive(Path.Combine(options.DataDirectory, "moves"), move.Id); }
                        catch (IOException e) { logger.LogWarning(e, "Move archive {Move} needs local cleanup", move.Id); }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning(e, "Agent move pass failed"); await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        }
    }

    internal static void DeleteLocalArchive(string folder, string moveId)
    {
        try { File.Delete(Path.Combine(folder, moveId + ".tar")); }
        catch (DirectoryNotFoundException) { } // Preflight failures never create the transport folder.
    }
}
