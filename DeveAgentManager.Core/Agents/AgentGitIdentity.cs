using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>
/// Whose git identity, GitHub token and environment an agent's harness runs with (docs/users-plan.md §4). It starts as
/// the owner's and only an explicit switch changes it, never a start or restart: someone with read-write access can switch
/// it to themselves, whoever it is can hand it back to the owner, and a conversation manager can choose anyone. When that
/// person loses it (disabled, deleted, or their share of the agent ended), the harness is stopped like after a revoked
/// login, and the next start uses the agent's identity then.
/// </summary>
public sealed partial class AgentService
{
    public static Guid GitIdentity(Agent agent) => agent.GitIdentityUserId ?? agent.OwnerId;

    public static Guid RunningGitIdentity(Agent agent) => agent.RunningGitIdentityUserId ?? agent.OwnerId;

    /// <summary>Switches the git identity; a running harness restarts with it and resumes the same conversation.</summary>
    public async Task SetGitIdentityAsync(CurrentUser actor, Guid agentId, Guid userId, CancellationToken cancellationToken = default)
    {
        using var lease = await sessions.LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new KeyNotFoundException("Agent not found.");
        var current = GitIdentity(agent);
        var level = await access.AgentAsync(actor, agentId, cancellationToken);
        var permitted = actor.Has(UserRoles.ConversationManager)
            || level >= AgentAccess.Write && userId == actor.Id
            || level >= AgentAccess.Read && current == actor.Id && userId == agent.OwnerId;
        if (!permitted)
        {
            throw new UnauthorizedAccessException(userId == actor.Id
                ? "Committing as yourself needs read-write access to this agent."
                : "You can only switch the git identity to yourself. A conversation manager can choose someone else.");
        }

        RequireNotMoving(agent);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && !u.Disabled, cancellationToken)
            ?? throw new ArgumentException("Choose an enabled user.");
        if (userId == current)
        {
            return;
        }

        var running = agent.State is AgentState.Running or AgentState.Starting;
        var runner = running ? RequireReady(agent.Runner!) : agent.Runner!;
        agent.GitIdentityUserId = userId == agent.OwnerId ? null : userId;
        if (running)
        {
            // Not Running while it restarts, so the enforcement pass leaves the old identity's harness alone.
            agent.State = AgentState.Starting;
        }

        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null)
        {
            await audit.RecordAsync(actor, AuditActions.AgentGitIdentityChanged, $"Agent {agent.Name}", $"commits as {user.Username}", cancellationToken);
        }

        events.AgentChanged(agent.RunnerId, agent.Id);
        if (running)
        {
            logger.LogInformation("Restarting agent {Agent} with the git identity of {User}", agent.Name, user.Username);
            try
            {
                await LaunchAsync(runner, agent, restart: true, cancellationToken);
                AgentActivityTracker.Reset(agent);
                agent.State = AgentState.Running;
                agent.StateMessage = null;
                agent.ExitCode = null;
            }
            catch (Exception ex)
            {
                agent.State = AgentState.Error;
                agent.StateMessage = $"Restarting with the new git identity failed: {ex.Message}. Start it to try again.";
                throw;
            }
            finally
            {
                agent.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                events.AgentChanged(agent.RunnerId, agent.Id);
            }
        }

        SyncUserFilesLater(runner);
    }

    /// <summary>
    /// Stops harnesses still running with the git identity of someone who lost it. Runs with the login enforcement and
    /// under the same lock as every launch, so it never races one.
    /// </summary>
    public async Task EnforceGitIdentitiesAsync(CancellationToken cancellationToken)
    {
        using var lease = await Accounts.AccountCoordination.LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Agents.Include(a => a.Runner).Where(a => a.State == AgentState.Running
            && a.RunningGitIdentityUserId != null && a.RunningGitIdentityUserId != a.OwnerId).ToListAsync(cancellationToken);
        foreach (var agent in agents)
        {
            if (agent.Runner?.State != RunnerState.Ready || await GitIdentityPermittedAsync(db, agent, agent.RunningGitIdentityUserId!.Value, cancellationToken))
            {
                continue;
            }

            try
            {
                (await shell.RunAsync(agent.Runner, BuildVerifiedStopScript(ShellQuote.Quote("=" + agent.TmuxSession + ":")), TimeSpan.FromSeconds(30), cancellationToken))
                    .EnsureSuccess("Stopping agent after its git identity ended");
                var next = GitIdentity(agent);
                var name = await db.Users.Where(u => u.Id == next).Select(u => u.Username).FirstOrDefaultAsync(cancellationToken);
                agent.State = AgentState.Stopped;
                agent.Working = false;
                agent.RunningGitIdentityUserId = null;
                agent.StateMessage = $"Stopped: the git identity it ran with is no longer available to this agent. Starting it commits as {name ?? "its owner"}.";
                agent.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                events.AgentChanged(agent.RunnerId, agent.Id);
                SyncUserFilesLater(agent.Runner);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Git identity ended; stopping agent {Agent} is pending on Runner {Runner}", agent.Id, agent.RunnerId);
            }
        }
    }

    /// <summary>
    /// The owner's identity is always the agent's. Someone else's stays usable while they are enabled and are its
    /// chosen identity, or still share the agent while a switch to another identity is applied.
    /// </summary>
    internal static async Task<bool> GitIdentityPermittedAsync(ManagerDbContext db, Agent agent, Guid userId, CancellationToken cancellationToken) =>
        userId == agent.OwnerId || await db.Users.AnyAsync(u => u.Id == userId && !u.Disabled, cancellationToken)
            && (userId == agent.GitIdentityUserId || await db.AgentShares.AnyAsync(s => s.AgentId == agent.Id && s.UserId == userId, cancellationToken));

    /// <summary>Stops the harness and fails if its pane still runs.</summary>
    internal static string BuildVerifiedStopScript(string target) =>
        BuildStopScript(target) + $"\nif tmux has-session -t {target} 2>/dev/null && [ \"$(tmux display-message -p -t {target} '#{{pane_dead}}')\" != 1 ]; then exit 1; fi";

    // Puts the new identity's files on the Runner and removes a previous one's that nobody there uses any more.
    private void SyncUserFilesLater(Runner runner)
    {
        if (runner.State != RunnerState.Ready)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await users.SyncAsync(runner);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not update the user files on {Runner}", runner.Name);
            }
        });
    }
}
