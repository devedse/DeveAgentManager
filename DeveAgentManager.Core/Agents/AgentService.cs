using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

public sealed class AgentNotRunningException(string message) : Exception(message);

/// <summary>
/// Agents are harness processes that run inside a tmux session on a Runner, so they keep running when nobody is
/// watching. The tmux session name is "dam-&lt;agent name&gt;".
/// </summary>
public sealed partial class AgentService(
    IDbContextFactory<ManagerDbContext> dbFactory,
    RunnerShell shell,
    IEnumerable<IHarness> harnesses,
    SessionService sessions,
    Accounts.AccountService accounts,
    Setup.SettingsStore settings,
    Runners.RunnerUsers users,
    AccessControl access,
    ManagerEvents events,
    ILogger<AgentService> logger,
    AgentContinuations? continuations = null, AgentDiagnostics? diagnostics = null, UsageLimitReader? limitReader = null, ArtifactTokens? artifactTokens = null,
    AuditLog? audit = null, Setup.UserSettingsStore? userSettings = null, DeveAgentManager.Core.Pets.PetService? pets = null)
{
    private readonly Dictionary<string, IHarness> _harnesses = harnesses.ToDictionary(h => h.Id);

    public IReadOnlyCollection<IHarness> Harnesses => _harnesses.Values;

    /// <summary>The agents a user can see, with what deciding their access needs (owner, shares, the Runner's members).</summary>
    public async Task<List<Agent>> ListVisibleAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await WithAccess(AccessControl.VisibleAgents(db, user)).OrderByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        await AgentAccountNames.LoadAsync(db, agents, cancellationToken);
        return agents;
    }

    public async Task<Agent?> GetAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await WithAccess(db.Agents).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is not null) await AgentAccountNames.LoadAsync(db, [agent], cancellationToken);
        return agent;
    }

    private static IQueryable<Agent> WithAccess(IQueryable<Agent> agents) => agents.AsNoTracking().AsSplitQuery()
        .Include(a => a.Project).Include(a => a.Runner).ThenInclude(r => r!.Members)
        .Include(a => a.Account).ThenInclude(a => a!.CreatedBy).Include(a => a.Owner).Include(a => a.Shares).ThenInclude(s => s.User);

    /// <summary>
    /// Creates an agent owned by <paramref name="actor"/>, who needs Use access to the Runner. Its harness login is one of
    /// theirs (or their default); a stored conversation to continue must be readable by them.
    /// </summary>
    public async Task<Agent> CreateAsync(CurrentUser actor, CreateAgentRequest request, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? "").Trim().ToLowerInvariant();
        if (!AgentName().IsMatch(name))
        {
            throw new ArgumentException("Use lowercase letters, digits and dashes for the name, for example 'fix-maze-perf'.");
        }

        var harness = GetHarness(request.HarnessId);
        if (await access.RunnerAsync(actor, request.RunnerId, cancellationToken) < RunnerAccess.Use)
        {
            throw new KeyNotFoundException("Runner not found.");
        }

        if (request.AccountId is { } accountId)
        {
            await RequireOwnLoginAsync(actor, harness, accountId, cancellationToken);
        }

        if (request.ContinueSessionId is { } continuing && await access.SessionAsync(actor, continuing, cancellationToken) == SessionAccess.None)
        {
            throw new KeyNotFoundException("Session not found.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.FirstAsync(r => r.Id == request.RunnerId, cancellationToken);
        if (runner.State != RunnerState.Ready)
        {
            throw new InvalidOperationException($"Runner '{runner.Name}' is not ready.");
        }

        if (!runner.HarnessIds.Contains(harness.Id))
        {
            throw new InvalidOperationException($"{harness.DisplayName} is not installed on '{runner.Name}'. Add it and re-run setup.");
        }

        if (await db.Agents.AnyAsync(a => a.RunnerId == runner.Id && a.Name == name, cancellationToken))
        {
            throw new ArgumentException($"An agent named '{name}' already exists on '{runner.Name}'.");
        }

        var home = runner.HomeDirectory ?? $"/home/{runner.Username}";
        // Everyone shares the Runner Account: new folders go under the owner's name, so two people's clones don't collide.
        var workingDirectory = request.Mode switch
        {
            AgentWorkspaceMode.ExistingFolder => ExpandHome(RequireValue(request.ExistingPath, "Enter the folder on the Runner."), home),
            _ => $"{home}/work/{actor.Username}/{name}",
        };

        using var workspaceLease = await ReserveWorkspaceAsync(runner.Id, name, workingDirectory, cancellationToken);
        var existingFolders = await db.Agents.Where(a => a.RunnerId == runner.Id).Select(a => a.WorkingDirectory).ToListAsync(cancellationToken);
        if (existingFolders.Any(path => OverlappingFolders(path, workingDirectory)))
            throw new InvalidOperationException("Another agent already owns this folder or a parent folder. Open that agent, or create a new workspace from it.");

        var agentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var agent = new Agent
        {
            Id = agentId,
            RunnerId = runner.Id,
            OwnerId = actor.Id,
            Name = name,
            HarnessId = harness.Id,
            WorkingDirectory = workingDirectory,
            RepositoryUrl = request.Mode == AgentWorkspaceMode.CloneRepository ? request.RepositoryUrl?.Trim() : null,
            RepositoryBranch = request.Mode == AgentWorkspaceMode.CloneRepository && !string.IsNullOrWhiteSpace(request.Branch) ? request.Branch.Trim() : null,
            TmuxSession = $"dam-{name}",
            HarnessSessionId = harness.NewSessionId(agentId),
            AccountId = request.AccountId,
            State = AgentState.Starting,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var importingConversation = request.ContinueSessionId is { } continueId
            && (await sessions.GetAsync(continueId, cancellationToken))?.HarnessId != agent.HarnessId;
        await PrepareWorkspaceAsync(runner, agent, request.Mode, cancellationToken);

        agent.Project = new Project { Id = agent.Id, RunnerId = runner.Id, OwnerId = actor.Id,
            Name = agent.Name, RootDirectory = agent.WorkingDirectory, CreatedAt = now };
        // Until it runs, the new agent is busy (actions on it wait) and its conversation is its own to prepare.
        using var creating = await BeginLongOperationAsync(agent.Id, "being created", cancellationToken);
        using var preparing = sessions.PrepareConversation(agent.Id);
        db.Agents.Add(agent);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            // Registers the conversation; when continuing a stored session, writes it to the Runner before the harness starts.
            await sessions.AttachToAgentAsync(runner, agent, request.ContinueSessionId, cancellationToken);
        }
        catch
        {
            db.Agents.Remove(agent);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        events.AgentChanged(runner.Id, agent.Id);

        try
        {
            await LaunchAsync(runner, agent, restart: false, cancellationToken, transferred: importingConversation, loginActor: actor);
            agent.State = AgentState.Running;
        }
        catch (Exception ex)
        {
            agent.State = AgentState.Error;
            agent.StateMessage = ex.Message;
            throw;
        }
        finally
        {
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            events.AgentChanged(runner.Id, agent.Id);
        }

        return agent;
    }

    /// <summary>
    /// Starts the agent when its harness is not running. The harness resumes the same conversation.
    /// A harness that is still running is left alone, unless <paramref name="restart"/> is set: then it is asked to exit
    /// (SIGTERM, then killed after a grace period) and started again. Killing it outright would lose the tool call it is running.
    /// </summary>
    public async Task StartAsync(Guid agentId, bool restart = false, CancellationToken cancellationToken = default)
    {
        using var lease = await LockAgentAsync(agentId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        RequireNotMoving(agent);
        var runner = RequireReady(agent.Runner!);
        await VerifyWorkspaceAsync(runner, agent, cancellationToken);
        await LaunchAsync(runner, agent, restart, cancellationToken);
        if (restart || agent.State != AgentState.Running) AgentActivityTracker.Reset(agent);
        agent.State = AgentState.Running;
        agent.StateMessage = null;
        agent.ExitCode = null;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.AgentChanged(runner.Id, agent.Id);
    }

    /// <summary>
    /// Continues the agent in another harness and/or with another harness login, in the same folder on the same Runner
    /// (e.g. when a subscription ran out). The harness is stopped gracefully and the conversation synced. For another
    /// harness the conversation moves with completed tool exchanges (thinking stays behind); for another
    /// login of the same harness the conversation simply resumes.
    /// <paramref name="accountId"/> must be one of <paramref name="actor"/>'s own logins; null means the owner's default,
    /// which only the owner can pick (nobody switches an agent to someone else's login).
    /// </summary>
    public async Task SwitchAsync(CurrentUser actor, Guid agentId, string harnessId, Guid? accountId, CancellationToken cancellationToken = default)
    {
        var target = GetHarness(harnessId);
        await RequireOwnLoginAsync(actor, agentId, target, accountId, cancellationToken);
        // Converting a conversation runs without the session lock, so other agents keep working; actions on this agent
        // wait for the switch (RequireNotMoving), and the state monitor leaves it alone.
        using (await BeginLongOperationAsync(agentId, "switching harness", cancellationToken))
            await SwitchStepsAsync(actor, agentId, target, accountId, cancellationToken);
    }

    private async Task SwitchStepsAsync(CurrentUser actor, Guid agentId, IHarness target, Guid? accountId, CancellationToken cancellationToken)
    {
        var harnessId = target.Id;
        var stopping = false;
        SessionService.HarnessMove? move = null;
        try
        {
            Runner runner;
            Agent selected;
            using (await sessions.LockAsync(cancellationToken))
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                selected = await db.Agents.AsNoTracking().Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
                if (MoveBlocksActions(selected)) throw new InvalidOperationException(MoveInProgress);
                runner = RequireReady(selected.Runner!);
                if (!runner.HarnessIds.Contains(harnessId))
                    throw new InvalidOperationException($"{target.DisplayName} is not installed on '{runner.Name}'. Install it first (Runner → Operations).");
                if (harnessId == selected.HarnessId && accountId == selected.AccountId && !selected.LoginBlocked) return;
            }

            // The per-agent operation owns the whole switch. Network work never owns the global session lock.
            stopping = true;
            (await shell.RunAsync(runner, BuildStopScript(ShellQuote.Quote("=" + selected.TmuxSession + ":")), TimeSpan.FromMinutes(1), CancellationToken.None))
                .EnsureSuccess("Stopping the harness");
            var transferred = harnessId != selected.HarnessId;
            if (transferred)
            {
                await sessions.SyncRunnerAsync(runner, CancellationToken.None, forceVerification: true, strictAgentId: agentId);
                using (await sessions.LockAsync(CancellationToken.None))
                {
                    move = await sessions.BeginMoveToHarnessAsync(selected, CancellationToken.None);
                    await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                    var agent = await db.Agents.SingleAsync(a => a.Id == agentId, CancellationToken.None);
                    agent.State = AgentState.Starting;
                    agent.StateMessage = $"Switching to {target.DisplayName}…";
                    agent.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                    events.AgentChanged(runner.Id, agent.Id);
                }
                var sessionId = target.NewSessionId(Guid.NewGuid());
                await sessions.ConvertMoveAsync(runner, move, harnessId, sessionId, selected.WorkingDirectory, CancellationToken.None);
                using (await sessions.LockAsync(CancellationToken.None))
                {
                    await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                    var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, CancellationToken.None)
                        ?? throw new InvalidOperationException("The agent was removed while its conversation was being converted.");
                    if (agent.RunnerId != runner.Id || agent.HarnessId != move.Stored.HarnessId || agent.HarnessSessionId != move.Stored.HarnessSessionId || MoveBlocksActions(agent))
                        throw new InvalidOperationException("The agent changed while its conversation was being converted, so it was not switched.");
                    await sessions.CompleteMoveToHarnessAsync(agent, move, harnessId, sessionId, CancellationToken.None);
                    agent.HarnessId = harnessId;
                    agent.HarnessSessionId = sessionId;
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            await using var launchDb = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var launching = await launchDb.Agents.Include(a => a.Runner).SingleAsync(a => a.Id == agentId, CancellationToken.None);
            if (launching.RunnerId != runner.Id || MoveBlocksActions(launching))
                throw new InvalidOperationException("The agent changed before its harness could start.");
            await StartSwitchedAsync(launchDb, RequireReady(launching.Runner!), launching, actor, accountId, transferred);
        }
        catch (Exception ex) when (stopping)
        {
            // Keep the selected native session available for an explicit Start; never retry a failed switch.
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            if (await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, CancellationToken.None) is { } agent)
            {
                agent.State = AgentState.Error;
                agent.StateMessage = ex.Message;
                agent.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                events.AgentChanged(agent.RunnerId, agent.Id);
            }
            throw;
        }
        finally { move?.Dispose(); }
    }

    /// <summary>The last step of a switch: authorize and launch while the per-agent operation remains busy.</summary>
    private async Task StartSwitchedAsync(ManagerDbContext db, Runner runner, Agent agent, CurrentUser actor, Guid? accountId, bool transferred)
    {
        var ct = CancellationToken.None;
        agent.AccountId = accountId;
        agent.LoginBlocked = false;
        agent.LoginStopPending = false;
        db.AgentLoginGrants.RemoveRange(await db.AgentLoginGrants.Where(g => g.AgentId == agent.Id).ToListAsync(ct));
        AgentActivityTracker.Reset(agent);
        agent.State = AgentState.Starting;
        agent.StateMessage = null;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        events.AgentChanged(runner.Id, agent.Id);

        await LaunchAsync(runner, agent, restart: true, ct, transferred: transferred, loginActor: actor);
        agent.State = AgentState.Running;
        agent.StateMessage = null;
        agent.ExitCode = null;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        events.AgentChanged(runner.Id, agent.Id);
    }

    private async Task RequireOwnLoginAsync(CurrentUser actor, Guid agentId, IHarness harness, Guid? accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var owner = await db.Agents.Where(a => a.Id == agentId).Select(a => a.OwnerId).SingleAsync(cancellationToken);
        if (harness.Id == "pi" && userSettings is not null)
        {
            var ids = (await userSettings.GetModelDefaultsAsync(owner, cancellationToken)).GetValueOrDefault("pi")?.PiAccountIds;
            if (ids is not null || accountId is null)
            {
                foreach (var login in await accounts.ResolvePiLoginsAsync(owner, ids ?? [], cancellationToken))
                    await RequireOwnLoginAsync(actor, harness, login.Id, cancellationToken);
                return;
            }
        }
        if (harness.AccountSupport is null && !(harness.Id == "pi" && accountId is not null)) return;
        var id = accountId ?? await db.UserHarnessDefaults.Where(p => p.UserId == owner && p.HarnessId == harness.Id)
            .Select(p => (Guid?)p.AccountId).FirstOrDefaultAsync(cancellationToken);
        if (id is null) throw new ArgumentException("Choose a permitted harness login or set a default.");
        await RequireOwnLoginAsync(actor, harness, id.Value, cancellationToken);
        if (!await db.AccountGrants.AnyAsync(g => g.AccountId == id && g.UserId == owner && g.Access >= AccountAccess.Use, cancellationToken))
            throw new ArgumentException("Share this login with the agent's owner before selecting it.");
    }

    private async Task RequireOwnLoginAsync(CurrentUser actor, IHarness harness, Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var login = await db.AccountGrants.Where(g => g.AccountId == accountId && g.UserId == actor.Id && !g.User.Disabled
            && g.Access >= AccountAccess.Use && g.Account.Enabled)
            .Select(g => new { g.Account.HarnessId, g.Account.Kind }).FirstOrDefaultAsync(cancellationToken);
        if (login is null || !Accounts.AccountService.Compatible(harness.Id, login.HarnessId, login.Kind))
            throw new ArgumentException($"Choose a {harness.DisplayName} login you have Use access to.");
    }

    /// <summary>
    /// Replaces this workspace's conversation with a saved one of its project, in that conversation's own harness, or with
    /// a new one in the workspace's current harness. The current conversation is saved. With <paramref name="update"/>,
    /// the workspace's branch is pulled first; that is refused while it has uncommitted or unpushed work.
    /// </summary>
    public async Task SelectSessionAsync(Guid agentId, Guid? sessionId, string? title, CancellationToken ct = default, bool update = false)
    {
        Runner runner;
        Agent checkedAgent;
        string nativeId;
        IDisposable changing;
        // 1. Under the session lock: check the choice. The agent is then busy until it is done (other actions on it wait),
        // and the lock is released while the branch is pulled and the conversation installed.
        using (await LockAgentAsync(agentId, ct))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var agent = await db.Agents.AsNoTracking().Include(a => a.Runner).SingleAsync(a => a.Id == agentId, ct);
            RequireNotMoving(agent);
            runner = RequireReady(agent.Runner!);
            var targetHarnessId = agent.HarnessId;
            if (sessionId is { } id)
            {
                var selected = await sessions.GetAsync(id, ct) ?? throw new ArgumentException("Conversation not found.");
                if (selected.WorkspaceAgentId != agent.Id && !(agent.ProjectId is { } projectId && await InProjectAsync(db, selected, projectId, ct)))
                    throw new InvalidOperationException("Choose a conversation from this project.");
                if (selected.AgentId == agent.Id) return;
                if (selected.AgentId is not null) throw new InvalidOperationException("This conversation is active in another workspace. Open that workspace instead.");
                targetHarnessId = selected.HarnessId;
            }
            if (!string.IsNullOrWhiteSpace(title)) SessionService.ValidateTitle(title);
            var target = GetHarness(targetHarnessId);
            var changesHarness = targetHarnessId != agent.HarnessId;
            if (changesHarness && !runner.HarnessIds.Contains(targetHarnessId))
                throw new InvalidOperationException($"{target.DisplayName} is not installed on '{runner.Name}'. Install it first (Runner → Operations).");
            if (agent.LoginStopPending || !changesHarness && agent.LoginBlocked)
                throw new InvalidOperationException("Choose a permitted login after pending enforcement completes.");
            // A login belongs to a harness. When restoring another harness, resolve the owner's permitted default for it
            // before stopping anything; never carry an incompatible login over or use ambient Runner credentials.
            await accounts.ResolveAsync(new Agent { Id = agent.Id, OwnerId = agent.OwnerId, HarnessId = targetHarnessId,
                AccountId = changesHarness ? null : agent.AccountId }, ct);
            nativeId = target.NewSessionId(Guid.NewGuid());
            checkedAgent = agent;
            changing = await BeginLongOperationAsync(agentId, "changing its conversation", ct);
        }

        using (changing)
        {
            // 2. Before stopping anything: a refused or failed pull leaves the current conversation running.
            if (update) await UpdateWorkspaceAsync(runner, checkedAgent, ct);
            var stopping = false;
            var selected = false;
            try
            {
                // 3. Under the lock: stop the harness and take the latest histories. Once stopping, the change finishes or fails
                // by itself: a dropped request must not leave it halfway.
                SessionService.Selection selection;
                using (await sessions.LockAsync(ct))
                {
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    var agent = await db.Agents.SingleAsync(a => a.Id == agentId, ct);
                    if (MoveBlocksActions(agent) || agent.HarnessId != checkedAgent.HarnessId || agent.HarnessSessionId != checkedAgent.HarnessSessionId)
                        throw new InvalidOperationException("The agent changed while the conversation change was being prepared. Try again.");
                    stopping = true;
                    agent.State = AgentState.Starting;
                    agent.StateMessage = "Changing its conversation…";
                    agent.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                    events.AgentChanged(runner.Id, agent.Id);
                    (await shell.RunAsync(runner, BuildStopScript(ShellQuote.Quote("=" + agent.TmuxSession + ":")), TimeSpan.FromMinutes(1), CancellationToken.None))
                        .EnsureSuccess("Stopping the current session");
                    await sessions.SyncRunnerLockedAsync(runner, CancellationToken.None, agent.Id);
                    selection = await sessions.BeginSelectAsync(sessionId, CancellationToken.None);
                }

                using (selection)
                {
                    // 4. Without it: write the chosen conversation to the Runner under the new session id.
                    await sessions.InstallSelectionAsync(runner, checkedAgent, selection, nativeId, CancellationToken.None);

                    // 5. Under the lock again: record the choice and start.
                    using (await sessions.LockAsync(CancellationToken.None))
                    {
                        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                        var agent = await db.Agents.Include(a => a.Runner).SingleAsync(a => a.Id == agentId, CancellationToken.None);
                        if (MoveBlocksActions(agent) || agent.HarnessId != checkedAgent.HarnessId || agent.HarnessSessionId != checkedAgent.HarnessSessionId)
                            throw new InvalidOperationException("The agent changed while its conversation was being changed.");
                        await sessions.SelectLockedAsync(runner, agent, sessionId, title, nativeId, CancellationToken.None, selection);
                        selected = true;
                        AgentActivityTracker.Reset(agent);
                        agent.State = AgentState.Starting;
                        agent.StateMessage = null;
                        agent.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(CancellationToken.None);
                        events.AgentChanged(runner.Id, agent.Id);
                        await LaunchAsync(RequireReady(agent.Runner!), agent, restart: true, CancellationToken.None);
                        agent.State = AgentState.Running;
                        agent.ExitCode = null;
                        agent.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(CancellationToken.None);
                        events.AgentChanged(runner.Id, agent.Id);
                    }
                }
            }
            catch (Exception ex) when (stopping)
            {
                // Selection is committed before launching. Start can retry the selected chat without losing either history.
                await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                if (await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, CancellationToken.None) is { } agent)
                {
                    agent.State = AgentState.Error;
                    agent.StateMessage = selected ? $"Session change failed: {ex.Message}. Use Start to resume the selected session."
                        : $"Session change failed: {ex.Message}. The current conversation was kept; use Start to resume it.";
                    agent.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                    events.AgentChanged(runner.Id, agent.Id);
                }
                throw;
            }
        }
    }

    /// <summary>Asks the harness in the agent's pane to exit (SIGTERM, then killed after the grace period). The pane stays.</summary>
    internal static string BuildStopScript(string target) => $$"""
        if tmux has-session -t {{target}} 2>/dev/null && [ "$(tmux display-message -p -t {{target}} '#{pane_dead}')" != 1 ]; then
          kill -TERM "$(tmux display-message -p -t {{target}} '#{pane_pid}')" 2>/dev/null || true
          i=0
          while [ $i -lt {{GracefulStopSeconds * 2}} ] && [ "$(tmux display-message -p -t {{target}} '#{pane_dead}')" != 1 ]; do
            sleep 0.5
            i=$((i + 1))
          done
          if [ "$(tmux display-message -p -t {{target}} '#{pane_dead}')" != 1 ]; then
            kill -KILL "$(tmux display-message -p -t {{target}} '#{pane_pid}')" 2>/dev/null || true
            sleep 0.5
          fi
        fi
        """;

    public async Task StopAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        using var lease = await LockAgentAsync(agentId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        RequireNotMoving(agent);
        var runner = RequireReady(agent.Runner!);
        await shell.RunAsync(runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", cancellationToken: cancellationToken);
        await sessions.SyncRunnerLockedAsync(runner, cancellationToken, agent.Id);
        agent.State = AgentState.Stopped;
        agent.StateMessage = null;
        agent.ExitCode = null;
        AgentActivityTracker.Reset(agent);
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.AgentChanged(runner.Id, agent.Id);
    }

    /// <summary>
    /// Stops the agent and forgets it. Its folder and the harness's own session files stay on the Runner;
    /// <see cref="DeleteWithFolderAsync"/> deletes the folder too.
    /// </summary>
    public async Task DeleteAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        using var lease = await LockAgentAsync(agentId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        await RequireDeletableAsync(db, agent, cancellationToken);
        if (agent.Runner!.State == RunnerState.Ready) await StopForDeletionAsync(agent.Runner, agent, cancellationToken);

        await sessions.PreserveArtifactsAsync(agentId, cancellationToken);
        db.Agents.Remove(agent);
        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.AgentRemoved, agent.RunnerId, agent.Id));
    }

    /// <summary>
    /// The command a terminal runs to attach to the agent's tmux session. A <paramref name="readOnly"/> terminal is a
    /// read-only tmux client (keys do nothing) that never sets the size.
    /// </summary>
    public async Task<(Runner Runner, string Command)> GetAttachCommandAsync(Guid agentId, bool readOnly, CancellationToken cancellationToken = default)
    {
        var agent = await GetAsync(agentId, cancellationToken) ?? throw new ArgumentException("Agent not found.");
        RequireNotMoving(agent);
        var runner = RequireReady(agent.Runner!);
        var check = await shell.RunAsync(runner, $"tmux has-session -t {ShellQuote.Quote("=" + agent.TmuxSession)}", cancellationToken: cancellationToken);
        if (!check.Succeeded)
        {
            throw new AgentNotRunningException($"Agent '{agent.Name}' is not running. Start it first.");
        }

        return (runner, "bash -c " + ShellQuote.Quote(BuildAttachScript(agent.TmuxSession, readOnly)));
    }

    internal static string BuildAttachScript(string tmuxSession, bool readOnly)
    {
        // Declare capabilities for this browser client only. Without hyperlinks, tmux strips OSC 8 URLs
        // from named links before xterm can open them locally; clicks then reach the harness instead.
        const string attach = "exec tmux -u -T RGB,hyperlinks attach-session";
        // tmux sizes the window to the screen used last (window-size latest), and every resize makes pi redraw its whole
        // conversation. So a terminal that joins while others are attached joins watching (ignore-size): opening or
        // refreshing a tab changes nothing. It takes the size once its user types (ClaimSizeAsync).
        var target = ShellQuote.Quote("=" + tmuxSession);
        return readOnly
            ? $"{attach} -f read-only,ignore-size -t {target}"
            : $"""
              if [ -n "$(tmux list-clients -t {target} 2>/dev/null)" ]; then {attach} -f ignore-size -t {target}; fi
              {attach} -t {target}
              """;
    }

    /// <summary>
    /// The terminal <paramref name="tty"/> becomes the one that sets the agent's terminal size (its user started typing).
    /// Nothing happens when it already does.
    /// </summary>
    public async Task ClaimSizeAsync(Guid agentId, string tty, CancellationToken cancellationToken = default)
    {
        var agent = await GetAsync(agentId, cancellationToken) ?? throw new ArgumentException("Agent not found.");
        var runner = RequireReady(agent.Runner!);
        var quoted = ShellQuote.Quote(RequireTty(tty));
        await shell.RunAsync(runner,
            $"if tmux list-clients -F '#{{client_tty}} #{{client_flags}}' | grep -q \"^{RequireTty(tty)} .*ignore-size\"; then tmux refresh-client -t {quoted} -f '!ignore-size'; fi",
            TimeSpan.FromSeconds(20), cancellationToken);
    }

    /// <summary>What a kicked terminal prints (instead of tmux's own goodbye), so it can say why it was disconnected.</summary>
    public const string KickedMarker = "__DAM_KICKED__";

    /// <summary>History lines the terminal's text view reads at first, and at most (tmux keeps up to 100,000).</summary>
    public const int TerminalTextLines = 2_000;
    public const int MaxTerminalTextLines = 50_000;

    /// <summary>
    /// The agent's terminal as plain text, to select and copy from: what it shows now, and up to <paramref name="lines"/>
    /// lines of tmux's history above it. The browser's terminal has no history of its own: tmux keeps it.
    /// </summary>
    public async Task<AgentTerminalText> ReadTerminalTextAsync(Guid agentId, int lines, CancellationToken cancellationToken = default)
    {
        var agent = await GetAsync(agentId, cancellationToken) ?? throw new ArgumentException("Agent not found.");
        var runner = RequireReady(agent.Runner!);
        lines = Math.Clamp(lines, 1, MaxTerminalTextLines);
        var target = ShellQuote.Quote("=" + agent.TmuxSession + ":");
        // -J joins wrapped lines. -E -1 ends at the last history line; with no history it would read the screen instead.
        var result = (await shell.RunAsync(runner, $$"""
            history=$(tmux display-message -p -t {{target}} '#{history_size}') || exit 3
            printf '%s\0' "$history"
            if [ "$history" -gt 0 ]; then tmux capture-pane -p -J -S -{{lines}} -E -1 -t {{target}}; fi
            printf '\0'
            tmux capture-pane -p -J -t {{target}}
            """, TimeSpan.FromSeconds(30), cancellationToken)).EnsureSuccess("Reading the terminal");
        return ParseTerminalText(result.Output, lines);
    }

    internal static AgentTerminalText ParseTerminalText(string output, int lines)
    {
        var parts = output.Split('\0');
        if (parts.Length != 3 || !int.TryParse(parts[0].Trim(), out var historySize))
        {
            throw new InvalidOperationException("The terminal's text could not be read.");
        }

        return new AgentTerminalText(TerminalLines(parts[1]), TerminalLines(parts[2]), historySize > lines);
    }

    /// <summary>Without the spaces that fill each line to the terminal's width, and the empty lines before and after.</summary>
    internal static string TerminalLines(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        return string.Join('\n', lines);
    }

    /// <summary>The terminals attached to the agent's tmux session: browser terminals, and SSH sessions that attached by hand.</summary>
    public async Task<IReadOnlyList<AgentViewer>> ListViewersAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await GetAsync(agentId, cancellationToken) ?? throw new ArgumentException("Agent not found.");
        var runner = RequireReady(agent.Runner!);
        var result = await shell.RunAsync(runner,
            $"tmux list-clients -t {ShellQuote.Quote("=" + agent.TmuxSession)} -F '#{{client_tty}}|#{{client_width}}|#{{client_height}}|#{{client_created}}|#{{client_activity}}|#{{client_flags}}' 2>/dev/null || true",
            TimeSpan.FromSeconds(20), cancellationToken);
        return ParseViewers(result.Output);
    }

    /// <summary>
    /// Disconnects terminals from the agent: one (<paramref name="tty"/>), or all but <paramref name="keepTty"/>. They show
    /// that another screen disconnected them, with a Reconnect button. The agent keeps running.
    /// </summary>
    public async Task DisconnectViewersAsync(Guid agentId, string? tty, string? keepTty, CancellationToken cancellationToken = default)
    {
        var agent = await GetAsync(agentId, cancellationToken) ?? throw new ArgumentException("Agent not found.");
        var runner = RequireReady(agent.Runner!);
        // Replaces the client with a command that prints the marker, so the terminal knows it was kicked.
        var goodbye = ShellQuote.Quote($"printf '\\r\\n{KickedMarker}\\r\\n'");
        string command;
        if (tty is not null)
        {
            command = $"tmux detach-client -t {ShellQuote.Quote(RequireTty(tty))} -E {goodbye}";
        }
        else
        {
            var viewers = await ListViewersAsync(agentId, cancellationToken);
            var others = viewers.Where(v => v.Tty != keepTty).Select(v => $"tmux detach-client -t {ShellQuote.Quote(RequireTty(v.Tty))} -E {goodbye}").ToList();
            if (others.Count == 0)
            {
                return;
            }

            command = string.Join(" ; ", others);
        }

        (await shell.RunAsync(runner, command, TimeSpan.FromSeconds(20), cancellationToken)).EnsureSuccess("Disconnecting the terminal");
    }

    internal static IReadOnlyList<AgentViewer> ParseViewers(string output)
    {
        var viewers = new List<AgentViewer>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 5 || !parts[0].StartsWith("/dev/", StringComparison.Ordinal)
                || !int.TryParse(parts[1], out var columns) || !int.TryParse(parts[2], out var rows))
            {
                continue;
            }

            var watching = parts.Length > 5 && parts[5].Split(',').Contains("ignore-size");
            viewers.Add(new AgentViewer(parts[0], columns, rows, FromUnix(parts[3]), FromUnix(parts[4]), watching));
        }

        return viewers.OrderBy(v => v.ConnectedAt).ToList();
    }

    private static DateTime FromUnix(string value) =>
        long.TryParse(value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;

    private static string RequireTty(string tty) =>
        System.Text.RegularExpressions.Regex.IsMatch(tty, "^/dev/[A-Za-z0-9/]+$") ? tty : throw new ArgumentException($"'{tty}' is not a terminal.");

    private const int BusyLines = 15;

    internal const string StateStartMarker = "__DAM_STATE_START__";

    /// <summary>The agent state part of the output; what a leading script printed before it goes to <paramref name="leadingOutput"/>.</summary>
    internal static string SplitLeadingOutput(string output, bool hasLeading, Action<string>? leadingOutput)
    {
        if (!hasLeading) return output;
        var marker = StateStartMarker + "\n";
        var start = output.StartsWith(marker, StringComparison.Ordinal) ? 0 : output.IndexOf("\n" + marker, StringComparison.Ordinal) is var at and >= 0 ? at + 1 : -1;
        leadingOutput?.Invoke(start < 0 ? "" : output[..start]);
        return start < 0 ? output : output[(start + marker.Length)..];
    }

    /// <summary>
    /// Reads process lifecycle and the current visible screen. Activity rules and stable completion tracking run in the Manager.
    /// <paramref name="leadingScript"/> runs first in the same SSH command (e.g. usage readings); its output goes to
    /// <paramref name="leadingOutput"/> before the agent state is parsed.
    /// </summary>
    public async Task RefreshRunnerAsync(Runner runner, CancellationToken cancellationToken = default, string? leadingScript = null, Action<string>? leadingOutput = null)
    {
        // Read without the session lock: a slow or unreachable Runner, or a long operation on another agent, never holds up
        // status checks elsewhere. Busy agents are left alone (AgentOperations).
        List<Agent> read;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            read = await db.Agents.AsNoTracking().Where(a => a.RunnerId == runner.Id && a.State != AgentState.Moving).ToListAsync(cancellationToken);
        read.RemoveAll(a => MoveBlocksActions(a) || Operations(a.Id).Running > 0);
        var begun = read.ToDictionary(a => a.Id, a => Operations(a.Id).Begun);

        var script = BuildStateScript(read.Select(a => (a.TmuxSession, _harnesses.GetValueOrDefault(a.HarnessId)?.BusyPattern)), captureScreens: true,
            nativeSessions: read.ToDictionary(a => a.TmuxSession, a => new NativeActivityTarget(a.HarnessId, a.HarnessSessionId)));
        if (leadingScript is not null) script = leadingScript.TrimEnd('\n') + $"\necho '{StateStartMarker}'\n" + script;
        var result = (await shell.RunAsync(runner, script, TimeSpan.FromSeconds(30), cancellationToken)).EnsureSuccess("Reading agent state");
        var output = SplitLeadingOutput(result.Output, leadingScript is not null, leadingOutput);
        var (_, panes, _) = ParsePanes(output);
        var changedAgents = new List<Guid>();
        var screens = ParseScreens(output);
        var agents = new List<Agent>();

        await _observationWrites.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var ids = read.Select(a => a.Id).ToList();
            var current = await db.Agents.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
            var knownLimits = await db.AgentLimits.AsNoTracking().Where(l => ids.Contains(l.AgentId)).ToDictionaryAsync(l => l.AgentId, cancellationToken);
            foreach (var before in read)
            {
                // An operation began, or the agent changed, while its terminal was read: the next check sees it as it is now.
                if (!current.TryGetValue(before.Id, out var agent) || Operations(agent.Id) is var now && (now.Running > 0 || now.Begun != begun[agent.Id])
                    || agent.State != before.State || agent.UpdatedAt != before.UpdatedAt || agent.RunnerId != before.RunnerId || agent.TmuxSession != before.TmuxSession
                    || agent.HarnessId != before.HarnessId || agent.HarnessSessionId != before.HarnessSessionId || MoveBlocksActions(agent))
                    continue;
                agents.Add(agent);
                AgentState state;
                int? exitCode = null;
                var working = false;
                var activityChanged = false;
                if (panes.TryGetValue(agent.TmuxSession, out var pane))
                {
                    state = pane.Dead ? AgentState.Exited : AgentState.Running;
                    exitCode = pane.Dead ? pane.ExitCode : null;
                    if (!pane.Dead)
                    {
                        var screen = screens.GetValueOrDefault(agent.TmuxSession);
                        var observedAt = screen?.ObservedAt ?? DateTime.UtcNow;
                        var detection = screen?.Screen is { } text ? AgentScreenDetector.Detect(agent.HarnessId, text, observedAt)
                            : new AgentScreenDetection(AgentActivityStatus.Unknown, "The terminal snapshot could not be read.", "snapshot-unavailable");
                        if (screen?.Screen is { } currentScreen)
                            detection = UsageLimitState.ApplyResetStatus(agent, knownLimits.GetValueOrDefault(agent.Id), currentScreen, screen.Identity, detection, DateTime.UtcNow, limitReader);
                        activityChanged = AgentActivityTracker.Apply(agent, detection,
                            screen?.Identity ?? AgentActivityTracker.Read(agent).Identity, observedAt, screen?.NativeTurn);
                        working = agent.Working;
                    }
                }
                else
                {
                    // Just created agents may not show up yet.
                    state = agent.State == AgentState.Starting && DateTime.UtcNow - agent.UpdatedAt < TimeSpan.FromMinutes(1) ? AgentState.Starting : AgentState.Stopped;
                    if (state == AgentState.Stopped && agent.State == AgentState.Running)
                        agent.StateMessage = "The harness terminal disappeared unexpectedly. Start it to resume the conversation.";
                }

                if (agent.State != state || agent.ExitCode != exitCode)
                {
                    agent.State = state;
                    agent.ExitCode = exitCode;
                    agent.StateMessage = state == AgentState.Exited ? $"The harness exited (code {exitCode}). Start it again to resume the conversation."
                        : state == AgentState.Stopped ? agent.StateMessage : null;
                    agent.UpdatedAt = DateTime.UtcNow;
                    agent.Working = working;
                    changedAgents.Add(agent.Id);
                }
                else if (agent.Working != working || activityChanged)
                {
                    agent.Working = working;
                    changedAgents.Add(agent.Id);
                }
            }

            for (var attempt = 0; ; attempt++)
            {
                try { await db.SaveChangesAsync(cancellationToken); break; }
                catch (DbUpdateConcurrencyException ex) when (attempt < 3)
                {
                    foreach (var entry in ex.Entries)
                    {
                        changedAgents.Remove(((Agent)entry.Entity).Id);
                        agents.Remove((Agent)entry.Entity);
                        entry.State = EntityState.Detached;
                    }
                }
            }
        }
        finally { _observationWrites.Release(); }

        foreach (var id in changedAgents) events.AgentChanged(runner.Id, id);
        foreach (var agent in agents)
        {
            if (changedAgents.Contains(agent.Id))
            {
                var activity = AgentActivityTracker.Read(agent).Snapshot;
                if (pets is not null) {
                    try { await pets.ObserveActivityAsync(agent.Id, agent.HarnessId, agent.HarnessSessionId, activity, cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Could not update pet activity"); }
                }
                diagnostics?.Record(agent.Id, "Activity", $"{agent.State} / {activity.Status}: {activity.Reason}", activity.Rule);
            }
        }
        if (continuations is null) return;
        // Every harness: limits are detected for Codex and Claude, but schedules and limits left from before a switch to
        // another harness need the observation too.
        await Parallel.ForEachAsync(agents,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (agent, ct) =>
        {
            var snapshot = screens.GetValueOrDefault(agent.TmuxSession)?.ToLimitScreen();
            // Missing snapshots are not evidence that a limit cleared. Retry on the next runner pass.
            if (snapshot is null) return;
            try { await continuations.ObserveAsync(agent.Id, snapshot, ct, UsageLimitState.Session(agent)); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics?.Record(agent.Id, "Observation", "Could not process the usage-limit observation; retrying.");
                logger.LogWarning(ex, "Could not process usage limit for {Agent}", agent.Id);
            }
        });
    }

    public async Task MarkObservationUnavailableAsync(Guid runnerId, CancellationToken ct)
    {
        var changed = new List<Guid>();
        await _observationWrites.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var agents = await db.Agents.Where(a => a.RunnerId == runnerId && a.State == AgentState.Running).ToListAsync(ct);
            foreach (var agent in agents.Where(a => Operations(a.Id).Running == 0))
                if (AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Unknown, "Terminal observation failed. Check the Runner connection and Manager logs.", "observation-failed"), AgentActivityTracker.Read(agent).Identity, DateTime.UtcNow)) changed.Add(agent.Id);
            await db.SaveChangesAsync(ct);
        }
        finally { _observationWrites.Release(); }
        foreach (var id in changed)
        {
            diagnostics?.Record(id, "Connection", "Terminal observation failed. Retrying on the next monitor pass.");
            events.AgentChanged(runnerId, id);
        }
    }

    public async Task MarkAllStoppedAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Agents.Where(a => a.RunnerId == runnerId && a.State != AgentState.Stopped)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentState.Stopped).SetProperty(a => a.StateMessage, (string?)null)
                .SetProperty(a => a.Working, false).SetProperty(a => a.ActivityJson, (string?)null)
                .SetProperty(a => a.UpdatedAt, DateTime.UtcNow), cancellationToken);
        events.RunnerChanged(runnerId);
    }

    public async Task StopAllOnRunnerAsync(Runner runner, CancellationToken cancellationToken)
    {
        foreach (var agent in runner.Agents)
        {
            await shell.RunAsync(runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    /// <summary>
    /// Bash that prints the Runner's time, tmux's panes, and after <see cref="BusyMarker"/> the sessions whose screen shows
    /// their harness's busy pattern.
    /// </summary>
    internal sealed record NativeActivityTarget(string Harness, string? SessionId);
    internal static string BuildStateScript(IEnumerable<(string Session, string? BusyPattern)> agents, bool captureScreens = false,
        IReadOnlyDictionary<string, NativeActivityTarget>? nativeSessions = null)
    {
        var targets = agents.ToArray();
        var script = new StringBuilder("""
            date +%s
            tmux list-panes -a -F '#{session_name}|#{pane_dead}|#{pane_dead_status}|#{window_activity}' 2>/dev/null || true
            echo '
            """);
        script.Append(BusyMarker).Append("'\n");
        script.Append($"busy() {{ tmux capture-pane -p -t \"=$1:\" 2>/dev/null | grep -v '^[[:space:]]*$' | tail -n {BusyLines} | grep -qE -- \"$2\" && echo \"$1\"; }}\n");
        foreach (var (session, pattern) in targets)
        {
            if (pattern is not null && !captureScreens)
            {
                script.Append("busy ").Append(ShellQuote.Quote(session)).Append(' ').Append(ShellQuote.Quote(pattern)).Append(" || true\n");
            }
        }

        if (captureScreens)
        {
            script.Append("echo '--screens--'\npython3 - ");
            foreach (var target in targets) script.Append(ShellQuote.Quote(target.Session)).Append(' ');
            script.Append("<<'DAM_STATE'\n");
            if (nativeSessions is not null)
                script.Append("import json\nactivity_targets=json.loads(").Append(JsonSerializer.Serialize(JsonSerializer.Serialize(nativeSessions)))
                    .Append(")\n").Append(NativeActivityProbe.Script).Append('\n');
            script.Append(LimitTerminal.BatchProbe).Append("\nDAM_STATE\n");
        }

        return script.ToString();
    }

    internal const string BusyMarker = "--busy--";
    internal sealed record ObservedScreen(string Session, string? Identity, string? Screen,
        string? TimeZone = null, bool InputEmpty = false, string? Hash = null, DateTime? ObservedAt = null,
        NativeTurnObservation? NativeTurn = null)
    {
        internal LimitScreen? ToLimitScreen() => Screen is not null && Identity is not null && Hash is not null
            ? new(Screen, Identity.Replace('|', ':'), TimeZone, InputEmpty, Hash, ObservedAt) : null;
    }
    internal static Dictionary<string, ObservedScreen> ParseScreens(string output)
    {
        var screens = new Dictionary<string, ObservedScreen>();
        var section = output.IndexOf("--screens--\n", StringComparison.Ordinal);
        if (section < 0) return screens;
        foreach (var line in output[(section + 12)..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            try { if (JsonSerializer.Deserialize<ObservedScreen>(line) is { } screen) screens[screen.Session] = screen; }
            catch (JsonException) { }
        return screens;
    }

    /// <summary>
    /// The output of <see cref="BuildStateScript"/>: the Runner's time (epoch seconds), per session its pane
    /// (session|dead|exit status|last output), and the sessions that show their busy pattern.
    /// </summary>
    internal static (long? Now, Dictionary<string, (bool Dead, int? ExitCode, long? LastOutput)> Panes, HashSet<string> Busy) ParsePanes(string output)
    {
        var panes = new Dictionary<string, (bool Dead, int? ExitCode, long? LastOutput)>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        long? now = null;
        var inBusy = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line == "--screens--") break;
            if (line == BusyMarker)
            {
                inBusy = true;
                continue;
            }

            if (inBusy)
            {
                busy.Add(line);
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                if (now is null && long.TryParse(line, out var seconds))
                {
                    now = seconds;
                }

                continue;
            }

            var dead = parts[1] == "1";
            int? exitCode = parts.Length > 2 && int.TryParse(parts[2], out var code) ? code : null;
            long? lastOutput = parts.Length > 3 && long.TryParse(parts[3], out var activity) ? activity : null;
            // A session has one pane per window; the agent's session has a single pane.
            panes[parts[0]] = (dead, exitCode, lastOutput);
        }

        return (now, panes, busy);
    }

    private async Task PrepareWorkspaceAsync(Runner runner, Agent agent, AgentWorkspaceMode mode, CancellationToken cancellationToken)
    {
        var directory = ShellQuote.Quote(agent.WorkingDirectory);
        switch (mode)
        {
            case AgentWorkspaceMode.EmptyFolder:
                (await shell.RunAsync(runner, $"mkdir -p {directory}", cancellationToken: cancellationToken)).EnsureSuccess("Creating the folder");
                break;
            case AgentWorkspaceMode.ExistingFolder:
                var exists = await shell.RunAsync(runner, $"test -d {directory}", cancellationToken: cancellationToken);
                if (!exists.Succeeded)
                {
                    throw new ArgumentException($"The folder {agent.WorkingDirectory} does not exist on '{runner.Name}'.");
                }

                break;
            case AgentWorkspaceMode.CloneRepository:
                var url = RequireValue(agent.RepositoryUrl, "Enter the repository URL.");
                var branch = agent.RepositoryBranch is null ? "" : $" --branch {ShellQuote.Quote(agent.RepositoryBranch)}";
                // With the owner's GitHub token: a private repository is theirs to clone.
                await users.EnsureAsync(runner, agent.OwnerId, cancellationToken);
                var script = $"""
                    if [ -e {directory} ] && [ -n "$(ls -A {directory} 2>/dev/null)" ]; then echo "The folder {agent.WorkingDirectory} already exists and is not empty." >&2; exit 3; fi
                    . "{Runners.RunnerUsers.EnvironmentFile(agent.OwnerId)}"
                    mkdir -p "$(dirname {directory})"
                    GIT_TERMINAL_PROMPT=0 git clone{branch} {ShellQuote.Quote(NormalizeRepositoryUrl(url))} {directory}
                    """;
                (await shell.RunAsync(runner, script, TimeSpan.FromMinutes(15), cancellationToken)).EnsureSuccess("Cloning the repository");
                break;
        }
    }

    /// <summary>
    /// Added to the system prompt of every harness the Manager starts, instead of a CLAUDE.md or AGENTS.md, so it never ends
    /// up in a repository. Only on runners whose setup installed dam-tools.
    /// </summary>
    internal const string LaunchInstructions =
        "This agent runs in DeveAgentManager. To show the user an image, such as a screenshot or chart, run "
        + "`dam-tools share-image <file> --caption \"<what it shows>\"`; it appears in their conversation. "
        + "To give the user a file to download, such as a built APK or installer, run "
        + "`dam-tools share-file <file> --caption \"<what it is>\"`. "
        + "Shared files and images belong to this agent across all its sessions. Use `dam-tools list-artifacts` for your collection, "
        + "`dam-tools list-artifacts --all` to discover readable agents and their artifacts, and "
        + "`dam-tools fetch-artifact <id> --agent <source-agent-id>` to get a local file, including from another runner. "
        + "For multi-step work, show the user your progress with `dam-tools tasks`: set the list when you start, update a task "
        + "each time you finish it (`dam-tools tasks done 2 --description \"what you did\"`), and keep % done and time-left "
        + "estimates current. `dam-tools tasks` prints the list, which the user may clear. "
        + "Use your harness’s built-in background tasks whenever it supports them. "
        + "Use `dam-tools run-job` only when your harness has no built-in background-job support. Long runtimes, restart survival and validation needs are not exceptions. "
        + "Run `dam-tools --help` for other tools.";

    /// <param name="transferred">The conversation was just converted from another harness (see <see cref="HarnessLaunch.Transferred"/>).</param>
    private async Task LaunchAsync(Runner runner, Agent agent, bool restart, CancellationToken cancellationToken, bool transferred = false, CurrentUser? loginActor = null)
    {
        using var loginLease = await Accounts.AccountCoordination.LockAsync(cancellationToken);
        await using var loginDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var savedLoginState = await loginDb.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id, cancellationToken);
        agent.LoginBlocked = savedLoginState.LoginBlocked;
        agent.LoginStopPending = savedLoginState.LoginStopPending;
        if (agent.LoginStopPending) throw new InvalidOperationException("Choose a permitted login after pending enforcement completes.");
        var harness = GetHarness(agent.HarnessId);
        if (!restart)
        {
            // A no-op Start must not switch a running agent's effective default or rewrite its auth symlink.
            var alive = await shell.RunAsync(runner, $"tmux display-message -p -t {ShellQuote.Quote("=" + agent.TmuxSession + ":")} '#{{pane_dead}}' 2>/dev/null || true", cancellationToken: cancellationToken);
            if (alive.Output.Trim() == "0")
            {
                if (savedLoginState.RunningAccountId is { } active &&
                    (!await loginDb.Accounts.AnyAsync(a => a.Id == active && a.Enabled, cancellationToken)
                    || !await Accounts.AccountService.CanUseForAgentAsync(loginDb, agent, active, cancellationToken)))
                    throw new InvalidOperationException("The running agent's login access ended.");
                foreach (var id in await loginDb.AgentPiLogins.Where(b => b.AgentId == agent.Id).Select(b => b.AccountId).ToListAsync(cancellationToken))
                    if (!await loginDb.Accounts.AnyAsync(a => a.Id == id && a.Enabled, cancellationToken)
                        || !await Accounts.AccountService.CanUseForAgentAsync(loginDb, agent, id, cancellationToken))
                        throw new InvalidOperationException("A running pi login's access ended.");
                agent.RunningAccountId = savedLoginState.RunningAccountId;
                return;
            }
        }
        // The owner's default model and effort (My settings → Models), else the Manager's. The harness decides when they
        // apply: when a conversation starts in it, after a transfer, and for what it doesn't keep per conversation.
        var managerConfig = (await settings.GetSetupProfileAsync(cancellationToken)).GetHarness(agent.HarnessId);
        var ownModel = userSettings is null ? null : (await userSettings.GetModelDefaultsAsync(agent.OwnerId, cancellationToken)).GetValueOrDefault(agent.HarnessId);
        var model = harness.ConfigSupport is null ? new Setup.EffectiveModel(null, null, null) : Setup.EffectiveModel.Resolve(managerConfig, ownModel);
        var instructions = runner.SetupVersion >= Runners.RunnerSetupService.AgentToolsSetupVersion ? LaunchInstructions : null;

        // The git identity's environment (the owner's unless switched: git config, GitHub token and variables), then the
        // harness login's, so e.g. a subscription wins over an API key meant for pi. A restart keeps the identity.
        var identity = GitIdentity(agent);
        if (identity != agent.OwnerId && !await loginDb.Users.AnyAsync(u => u.Id == identity && !u.Disabled, cancellationToken))
            throw new InvalidOperationException("The agent's git identity belongs to a disabled or deleted user. Choose another git identity.");
        await users.EnsureAsync(runner, identity, cancellationToken);
        var ownerEnvironment = $". \"{Runners.RunnerUsers.EnvironmentFile(identity)}\"; ";
        var piSelection = agent.HarnessId == "pi" ? ownModel?.PiAccountIds : null;
        var piLogins = piSelection is null ? [] : await accounts.ResolvePiLoginsAsync(agent.OwnerId, piSelection, cancellationToken);
        if (agent.LoginBlocked)
        {
            if (piSelection is null) throw new InvalidOperationException("Choose a permitted login after pending enforcement completes.");
            // An explicit personal selection was just reauthorized. A saved preference alone never starts a process.
            agent.LoginBlocked = false;
            await loginDb.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LoginBlocked, false), cancellationToken);
        }
        var account = piSelection is not null ? null : await accounts.ResolveAsync(agent, cancellationToken);
        var accountEnvironment = "";
        if (agent.IsClone && agent.HarnessId == "pi")
            accountEnvironment = await accounts.PreparePiWorkspaceAsync(runner, agent, cancellationToken);
        else if (agent.HarnessId == "pi")
            // A managed-login preview may already have moved this agent's history into its private pi runtime.
            // Keep opening that same history when switching back to pi's own logins.
            accountEnvironment = Accounts.AccountService.ExistingPiRuntimeEnvironment(agent);
        if (account is not null)
        {
            if (loginActor is not null) await RequireOwnLoginAsync(loginActor, harness, account.Id, cancellationToken);
            await accounts.EnsureOnRunnerAsync(runner, account, cancellationToken);
            accountEnvironment = $". \"{Accounts.AccountService.EnvironmentFile(account.Id)}\"; "
                + (agent.HarnessId == "pi"
                    ? await accounts.PreparePiManagedRuntimeAsync(runner, agent, account, cancellationToken)
                    : await accounts.PrepareRuntimeAsync(runner, agent, account, cancellationToken));
        }

        if (piSelection is not null)
        {
            foreach (var login in piLogins)
            {
                if (loginActor is not null) await RequireOwnLoginAsync(loginActor, harness, login.Id, cancellationToken);
                await accounts.EnsureOnRunnerAsync(runner, login, cancellationToken);
            }
            // Do not source any individual login into pi: each provider owns its own file or CLI wrapper.
            accountEnvironment = await accounts.PreparePiManagedRuntimeAsync(runner, agent, piLogins, cancellationToken);
        }

        var harnessScript = harness.BuildLaunchScript(new HarnessLaunch(agent.HarnessSessionId, agent.Name, agent.WorkingDirectory, model.Model, instructions,
            model.Effort, model.Provider, transferred, agent.HarnessId == "pi" ? ownModel?.ScopedModels ?? managerConfig.ScopedModels : null,
            agent.HarnessId == "pi" && account is not null ? PiManagedLogin.Provider(account.HarnessId) : null, piSelection is not null, AgentId: agent.Id));

        // Retain previous bindings until the remote launch is confirmed. A transport failure may leave the old process alive.
        var priorPiLogins = await loginDb.AgentPiLogins.Where(b => b.AgentId == agent.Id).ToListAsync(cancellationToken);
        if (piSelection is not null && savedLoginState.RunningAccountId is { } legacy
            && !priorPiLogins.Any(b => b.AccountId == legacy) && await loginDb.Accounts.AnyAsync(a => a.Id == legacy, cancellationToken))
        {
            var binding = new AgentPiLogin { AgentId = agent.Id, AccountId = legacy };
            priorPiLogins.Add(binding);
            loginDb.AgentPiLogins.Add(binding);
        }
        loginDb.AgentPiLogins.AddRange(piLogins.Where(a => !priorPiLogins.Any(b => b.AccountId == a.Id))
            .Select(a => new AgentPiLogin { AgentId = agent.Id, AccountId = a.Id }));
        await loginDb.SaveChangesAsync(cancellationToken);
        agent.RunningAccountId = account?.Id;
        agent.RunningGitIdentityUserId = identity;
        await loginDb.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.RunningAccountId, account == null ? (Guid?)null : account.Id)
            .SetProperty(a => a.RunningGitIdentityUserId, (Guid?)identity), cancellationToken);
        var zone = Runners.RunnerTimeZones.Validate(runner.TimeZoneId ?? await settings.GetDefaultTimeZoneAsync(cancellationToken));
        var stored = await sessions.GetForAgentAsync(agent.Id, cancellationToken);
        if (artifactTokens is not null) await artifactTokens.InstallAsync(shell, runner, agent, cancellationToken);
        var imageContext = $"export DAM_AGENT_ID={ShellQuote.Quote(agent.Id.ToString("N"))}; export DAM_SESSION_ID={ShellQuote.Quote(stored?.Id.ToString("N") ?? "")}; ";
        var launchFile = LaunchFile(agent.Id);
        var files = harness.LaunchFiles
            .Append((launchFile, BuildLaunchFile(ownerEnvironment + accountEnvironment + imageContext, zone, harnessScript))).ToList();
        var script = BuildStartScript(agent.TmuxSession, agent.WorkingDirectory, launchFile, files, restart);
        logger.LogInformation("{Action} agent {Agent} on {Runner}", restart ? "Restarting" : "Starting", agent.Name, runner.Name);
        var result = await shell.RunAsync(runner, script, TimeSpan.FromMinutes(1), cancellationToken);
        result.EnsureSuccess($"Starting {harness.DisplayName}");
        if (result.Output.Contains(AlreadyRunningMarker, StringComparison.Ordinal))
        {
            logger.LogInformation("Agent {Agent} on {Runner} was already running; left it alone", agent.Name, runner.Name);
        }
        else
        {
            loginDb.AgentPiLogins.RemoveRange(priorPiLogins.Where(b => !piLogins.Any(a => a.Id == b.AccountId)));
            await loginDb.SaveChangesAsync(cancellationToken);
            if (piSelection is not null)
            {
                agent.AccountId = null;
                await loginDb.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.AccountId, (Guid?)null), cancellationToken);
            }
        }
    }

    internal const string AlreadyRunningMarker = "DAM_ALREADY_RUNNING";

    /// <summary>Seconds a running harness gets to exit after SIGTERM before a restart kills it.</summary>
    internal const int GracefulStopSeconds = 10;

    /// <summary>The agent's launch file on the Runner: what its tmux pane runs.</summary>
    internal static string LaunchFile(Guid agentId) => $"$HOME/.deveagentmanager/launch/{agentId:N}.sh";

    /// <summary>
    /// The agent's launch file: the Manager's and the agent's environment, then the harness's launch script. A file keeps
    /// the pane's command one short line: inline, the script would be quoted again by every shell it passes through.
    /// COLORTERM: the browser terminal (xterm.js) and tmux support 24-bit color, but programs only use it when told.
    /// </summary>
    internal static string BuildLaunchFile(string environment, string zone, string harnessScript) => $"""
        . "{RunnerShell.EnvScript}"
        {environment.TrimEnd()}
        export TZ={ShellQuote.Quote(zone)}
        export COLORTERM=truecolor
        {harnessScript}

        """;

    /// <summary>
    /// The script that starts the harness in the agent's tmux pane. The pane of a session created here runs a plain shell,
    /// which is replaced. A pane whose harness is still running is only replaced when restarting (a start leaves it alone),
    /// and then the harness first gets SIGTERM and a grace period, so it can shut down instead of dying with its terminal.
    /// The pane keeps running after the harness exits (remain-on-exit), so its last output stays visible and it can be
    /// restarted in place; even a harness that exits immediately leaves its output behind. Only a pane that is replaced
    /// gets new <paramref name="files"/>, its <paramref name="launchFile"/> among them.
    /// </summary>
    internal static string BuildStartScript(string tmuxSession, string workingDirectory, string launchFile,
        IReadOnlyList<(string Path, string Content)> files, bool restart)
    {
        var session = ShellQuote.Quote("=" + tmuxSession);
        // Targets need the trailing ':' (session:window).
        var target = ShellQuote.Quote("=" + tmuxSession + ":");
        var directory = ShellQuote.Quote(workingDirectory);
        return $$"""
        set -e
        created=0
        if ! tmux has-session -t {{session}} 2>/dev/null; then
          tmux -u new-session -d -s {{ShellQuote.Quote(tmuxSession)}} -x 200 -y 50 -c {{directory}}
          created=1
        fi
        tmux set-option -t {{target}} remain-on-exit on
        if [ "$created" = 0 ] && [ "$(tmux display-message -p -t {{target}} '#{pane_dead}')" != 1 ]; then
          if [ {{(restart ? 1 : 0)}} = 0 ]; then
            echo {{AlreadyRunningMarker}}
            exit 0
          fi
          pid=$(tmux display-message -p -t {{target}} '#{pane_pid}')
          kill -TERM "$pid" 2>/dev/null || true
          i=0
          while [ $i -lt {{GracefulStopSeconds * 2}} ] && [ "$(tmux display-message -p -t {{target}} '#{pane_dead}')" != 1 ]; do
            sleep 0.5
            i=$((i + 1))
          done
        fi
        {{BuildLaunchFilesScript(files)}}
        tmux respawn-pane -k -t {{target}} -c {{directory}} {{ShellQuote.Quote($"exec bash \"{launchFile}\"")}}
        """;
    }

    /// <summary>
    /// Writes the files that differ, each whole by rename, so a pane never reads half a file and concurrent starts don't
    /// mix theirs. A quoted heredoc keeps the content as it is; its end marker holds the content's hash, so no line of the
    /// content ends it early.
    /// </summary>
    internal static string BuildLaunchFilesScript(IReadOnlyList<(string Path, string Content)> files) => string.Concat(files.Select(file =>
    {
        // As the heredoc writes it: Unix line endings, ending in one.
        var content = file.Content.ReplaceLineEndings("\n");
        if (!content.EndsWith('\n')) content += "\n";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var end = "DAM_FILE_" + hash[..16];
        if (content.Split('\n').Contains(end)) throw new ArgumentException($"{file.Path} contains its heredoc end marker.");
        return $$"""
            file="{{file.Path}}"
            if [ "$(sha256sum 2>/dev/null < "$file")" != "{{hash}}  -" ]; then
              mkdir -p "$(dirname "$file")"
              cat > "$file.dam-tmp-$$" <<'{{end}}'
            {{content}}{{end}}
              mv -f "$file.dam-tmp-$$" "$file"
            fi

            """;
    }));

    private IHarness GetHarness(string harnessId) =>
        _harnesses.GetValueOrDefault(harnessId) ?? throw new ArgumentException($"Unknown harness '{harnessId}'.");

    private static Runner RequireReady(Runner runner) =>
        runner.State == RunnerState.Ready ? runner : throw new InvalidOperationException($"Runner '{runner.Name}' is not ready ({runner.State}).");

    private static string RequireValue(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message) : value.Trim();

    private static string ExpandHome(string path, string home) =>
        path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? home + path[1..] : path;

    /// <summary>Accepts "github.com/owner/repo" as well as full URLs.</summary>
    internal static string NormalizeRepositoryUrl(string url)
    {
        url = url.Trim();
        if (url.Contains("://", StringComparison.Ordinal) || url.StartsWith("git@", StringComparison.Ordinal))
        {
            return url;
        }

        return "https://" + url;
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex AgentName();
}
