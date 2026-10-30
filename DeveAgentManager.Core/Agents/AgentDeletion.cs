using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>
/// Deleting an agent: on its own, leaving its folder on the Runner, or with its folder. A workspace is removed with
/// Remove workspace instead, which checks its ownership marker.
/// </summary>
public sealed partial class AgentService
{
    private async Task RequireDeletableAsync(ManagerDbContext db, Agent agent, CancellationToken ct)
    {
        if (agent.MoveJson is not null) throw new InvalidOperationException("Finish the agent move before deleting it.");
        if (InLongOperation(agent.Id)) throw new InvalidOperationException(Busy(agent.Id));
        if (agent.IsClone)
            throw new InvalidOperationException("Use Remove workspace: it checks the workspace's uncommitted and unpushed work before deleting its folder.");
        if (agent.ProjectId is { } projectId && await db.Agents.AnyAsync(a => a.ProjectId == projectId && a.Id != agent.Id, ct))
            throw new InvalidOperationException("Keep Main while the project has other workspaces. Remove those workspaces first.");
    }

    /// <summary>Ends the agent's terminal and syncs its conversation one last time: it stays in the Manager after the agent is gone.</summary>
    private async Task StopForDeletionAsync(Runner runner, Agent agent, CancellationToken ct)
    {
        await shell.RunAsync(runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true\nrm -f -- \"{LaunchFile(agent.Id)}\"",
            cancellationToken: ct);
        try { await sessions.SyncRunnerLockedAsync(runner, ct, agent.Id); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Final session sync failed for {Agent}", agent.Id); }
    }

    /// <summary>Why the agent's folder is not only its own, or null: another agent's folder or a kept workspace folder overlaps it.</summary>
    private static async Task<string?> FolderInUseAsync(ManagerDbContext db, Agent agent, CancellationToken ct)
    {
        var others = await db.Agents.Where(a => a.RunnerId == agent.RunnerId && a.Id != agent.Id).Select(a => new { a.Name, a.WorkingDirectory }).ToListAsync(ct);
        if (others.FirstOrDefault(o => OverlappingFolders(o.WorkingDirectory, agent.WorkingDirectory)) is { } other)
            return $"Agent '{other.Name}' works in this folder or in a folder inside it.";
        foreach (var project in await db.Projects.Where(p => p.RunnerId == agent.RunnerId && p.KeptFoldersJson != null).ToListAsync(ct))
            if (KeptFolders(project).FirstOrDefault(k => OverlappingFolders(k.Path, agent.WorkingDirectory)) is { } kept)
                return $"The kept workspace folder {kept.Path} is inside it.";
        return null;
    }

    /// <summary>What deleting the agent would affect, for the delete dialog. A folder that can't be checked only rules out deleting it.</summary>
    public async Task<AgentDeletionDto> GetDeletionAsync(Guid agentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.AsNoTracking().Include(a => a.Runner).SingleOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new KeyNotFoundException("Agent not found.");
        await RequireDeletableAsync(db, agent, ct);
        var conversations = await db.Sessions.CountAsync(s => s.WorkspaceAgentId == agentId || s.AgentId == agentId, ct);
        AgentDeletionDto Unchecked(string why) => new(agent.WorkingDirectory, conversations, false, false, why, false, 0, [], 0, null);
        var runner = agent.Runner!;
        if (runner.State != RunnerState.Ready)
            return Unchecked($"Runner '{runner.Name}' isn't ready ({runner.State}), so its folder can't be checked or deleted.");
        System.Text.Json.JsonElement e;
        try { e = await WorkspacePhaseAsync(runner, "folder", new { Path = agent.WorkingDirectory }, TimeSpan.FromMinutes(1), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Couldn't check the folder of {Agent}", agentId);
            return Unchecked("The folder couldn't be checked on the Runner: " + ex.Message);
        }
        var exists = e.GetProperty("Exists").GetBoolean();
        var blocked = exists ? await FolderInUseAsync(db, agent, ct) ?? Text(e, "Refusal") : null;
        return new(agent.WorkingDirectory, conversations, true, exists, blocked, e.GetProperty("Git").GetBoolean(), e.GetProperty("Uncommitted").GetInt32(),
            e.GetProperty("Sample").EnumerateArray().Select(s => s.GetString()!).ToList(), e.GetProperty("Unpushed").GetInt32(), Text(e, "GitProblem"));
    }

    /// <summary>The disk space of the agent's folder. The Runner-side timeout ends a measurement that outlives a closed dialog.</summary>
    public async Task<FolderSizeDto> MeasureFolderAsync(Guid agentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.AsNoTracking().Include(a => a.Runner).SingleOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new KeyNotFoundException("Agent not found.");
        var e = await WorkspacePhaseAsync(RequireReady(agent.Runner!), "measure", new { Path = agent.WorkingDirectory }, TimeSpan.FromMinutes(2), ct, timeoutSeconds: 110);
        return new(e.GetProperty("Bytes").GetInt64(), e.GetProperty("FreedBytes").GetInt64(), e.GetProperty("Files").GetInt64());
    }

    /// <summary>
    /// Stops the agent, deletes its folder on the Runner and forgets the agent. Its conversations stay in the Manager.
    /// Stopping and the final sync run under the locks; deleting the folder doesn't, so other agents keep working meanwhile,
    /// and it finishes even when the browser stops waiting. When deleting fails, the agent stays and says why.
    /// </summary>
    public async Task DeleteWithFolderAsync(Guid agentId, CancellationToken ct)
    {
        Runner runner;
        Agent agent;
        IDisposable deleting;
        await ProjectGate.WaitAsync(ct);
        try
        {
            // Checked before anything stops; the Runner checks again right before deleting.
            await using (var check = await dbFactory.CreateDbContextAsync(ct))
            {
                var found = await check.Agents.AsNoTracking().Include(a => a.Runner).SingleOrDefaultAsync(a => a.Id == agentId, ct)
                    ?? throw new KeyNotFoundException("Agent not found.");
                var refusal = Text(await WorkspacePhaseAsync(RequireReady(found.Runner!), "folder", new { Path = found.WorkingDirectory }, TimeSpan.FromMinutes(1), ct), "Refusal");
                if (refusal is not null) throw new WorkspaceRefusedException($"The folder can't be deleted. {refusal} Delete the agent only instead.");
            }

            using (await LockAgentAsync(agentId, ct))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                agent = await db.Agents.Include(a => a.Runner).SingleAsync(a => a.Id == agentId, ct);
                await RequireDeletableAsync(db, agent, ct);
                runner = RequireReady(agent.Runner!);
                if (await FolderInUseAsync(db, agent, ct) is { } inUse)
                    throw new WorkspaceRefusedException($"The folder can't be deleted. {inUse} Delete the agent only instead.");
                deleting = await BeginLongOperationAsync(agent.Id, "being deleted", ct);
                try
                {
                    await StopForDeletionAsync(runner, agent, ct);
                    await sessions.PreserveArtifactsAsync(agent.Id, ct);
                    agent.State = AgentState.Stopped;
                    agent.StateMessage = "Deleting the agent and its folder…";
                    agent.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    events.AgentChanged(runner.Id, agent.Id);
                }
                catch { deleting.Dispose(); throw; }
            }
        }
        finally { ProjectGate.Release(); }

        using (deleting)
        {
            try { await WorkspacePhaseAsync(runner, "delete", new { Path = agent.WorkingDirectory }, TimeSpan.FromMinutes(30), CancellationToken.None); }
            catch (Exception ex)
            {
                await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.StateMessage, $"Deleting its folder failed: {ex.Message} Delete it again to retry, or delete the agent only.")
                    .SetProperty(a => a.UpdatedAt, DateTime.UtcNow), CancellationToken.None);
                events.AgentChanged(runner.Id, agent.Id);
                throw;
            }

            using (await sessions.LockAsync(CancellationToken.None))
            {
                await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                db.Agents.Remove(await db.Agents.SingleAsync(a => a.Id == agent.Id, CancellationToken.None));
                await db.SaveChangesAsync(CancellationToken.None);
            }
            events.Publish(new ManagerEvent(ManagerEventKind.AgentRemoved, agent.RunnerId, agent.Id));
        }
    }
}
