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
    AuditLog? audit = null, Setup.UserSettingsStore? userSettings = null)
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
        using var lease = await sessions.LockAsync(cancellationToken);
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
        using var lease = await sessions.LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        RequireNotMoving(agent);
        var runner = RequireReady(agent.Runner!);
        if (!runner.HarnessIds.Contains(harnessId))
        {
            throw new InvalidOperationException($"{target.DisplayName} is not installed on '{runner.Name}'. Install it first (Runner → Operations).");
        }

        var changesHarness = harnessId != agent.HarnessId;
        if (!changesHarness && accountId == agent.AccountId && !agent.LoginBlocked)
        {
            return;
        }

        logger.LogInformation("Switching agent {Agent} from {From} to {To} (account {Account})", agent.Name, agent.HarnessId, harnessId, accountId);
        try
        {
            (await shell.RunAsync(runner, BuildStopScript(ShellQuote.Quote("=" + agent.TmuxSession + ":")), TimeSpan.FromMinutes(1), cancellationToken))
                .EnsureSuccess("Stopping the harness");

            if (changesHarness)
            {
                // The latest messages first, then the conversation moves to the new harness's own format.
                await sessions.SyncRunnerLockedAsync(runner, cancellationToken, agent.Id);
                var sessionId = target.NewSessionId(Guid.NewGuid());
                await sessions.MoveToHarnessAsync(runner, agent, harnessId, sessionId, cancellationToken);
                agent.HarnessId = harnessId;
                agent.HarnessSessionId = sessionId;
            }

            agent.AccountId = accountId;
            agent.LoginBlocked = false;
            agent.LoginStopPending = false;
            db.AgentLoginGrants.RemoveRange(await db.AgentLoginGrants.Where(g => g.AgentId == agent.Id).ToListAsync(cancellationToken));
            AgentActivityTracker.Reset(agent);
            agent.State = AgentState.Starting;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            events.AgentChanged(runner.Id, agent.Id);

            await LaunchAsync(runner, agent, restart: true, cancellationToken, transferred: changesHarness, loginActor: actor);
            agent.State = AgentState.Running;
            agent.StateMessage = null;
            agent.ExitCode = null;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            events.AgentChanged(runner.Id, agent.Id);
        }
        catch (Exception ex)
        {
            // Keep the selected native session available for an explicit Start; never retry a failed switch.
            agent.State = AgentState.Error;
            agent.StateMessage = ex.Message;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            events.AgentChanged(runner.Id, agent.Id);
            throw;
        }
    }

    private async Task RequireOwnLoginAsync(CurrentUser actor, Guid agentId, IHarness harness, Guid? accountId, CancellationToken cancellationToken)
    {
        if (harness.AccountSupport is null) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var owner = await db.Agents.Where(a => a.Id == agentId).Select(a => a.OwnerId).SingleAsync(cancellationToken);
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
        if (!await db.AccountGrants.AnyAsync(g => g.AccountId == accountId && g.UserId == actor.Id && !g.User.Disabled
            && g.Access >= AccountAccess.Use && g.Account.Enabled && g.Account.HarnessId == harness.Id, cancellationToken))
            throw new ArgumentException($"Choose a {harness.DisplayName} login you have Use access to.");
    }

    /// <summary>
    /// Replaces this workspace's conversation with a saved one of its project, in that conversation's own harness, or with
    /// a new one in the workspace's current harness. The current conversation is saved. With <paramref name="update"/>,
    /// the workspace's branch is pulled first; that is refused while it has uncommitted or unpushed work.
    /// </summary>
    public async Task SelectSessionAsync(Guid agentId, Guid? sessionId, string? title, CancellationToken ct = default, bool update = false)
    {
        using var lease = await sessions.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Runner).SingleAsync(a => a.Id == agentId, ct);
        RequireNotMoving(agent);
        var runner = RequireReady(agent.Runner!);
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
        var nativeId = target.NewSessionId(Guid.NewGuid());
        // Before stopping anything: a refused or failed pull leaves the current conversation running.
        if (update) await UpdateWorkspaceAsync(runner, agent, ct);
        try
        {
            agent.State = AgentState.Starting;
            await db.SaveChangesAsync(ct);
            events.AgentChanged(runner.Id, agent.Id);
            (await shell.RunAsync(runner, BuildStopScript(ShellQuote.Quote("=" + agent.TmuxSession + ":")), TimeSpan.FromMinutes(1), ct))
                .EnsureSuccess("Stopping the current session");
            await sessions.SyncRunnerLockedAsync(runner, ct, agent.Id);
            await sessions.SelectLockedAsync(runner, agent, sessionId, title, nativeId, ct);
            AgentActivityTracker.Reset(agent);
            agent.State = AgentState.Starting;
            agent.StateMessage = null;
            await db.SaveChangesAsync(ct);
            events.AgentChanged(runner.Id, agent.Id);
            await LaunchAsync(runner, agent, restart: true, ct);
            agent.State = AgentState.Running;
            agent.ExitCode = null;
        }
        catch (Exception ex)
        {
            // Selection is committed before launching. Start can retry the selected chat without losing either history.
            agent.State = AgentState.Error;
            agent.StateMessage = $"Session change failed: {ex.Message}. Use Start to resume the selected session.";
            throw;
        }
        finally
        {
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            events.AgentChanged(runner.Id, agent.Id);
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
        using var lease = await sessions.LockAsync(cancellationToken);
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

    /// <summary>Stops the agent and forgets it. Its folder and the harness's own session files stay on the Runner.</summary>
    public async Task DeleteAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        using var lease = await sessions.LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        if (agent.MoveJson is not null) throw new InvalidOperationException("Finish the agent move before deleting it.");
        if (agent.IsClone)
            throw new InvalidOperationException("Use Remove workspace: it checks the workspace's uncommitted and unpushed work before deleting its folder.");
        if (agent.ProjectId is { } projectId && await db.Agents.AnyAsync(a => a.ProjectId == projectId && a.Id != agent.Id, cancellationToken))
            throw new InvalidOperationException("Keep Main while the project has other workspaces. Remove those workspaces first.");
        if (agent.Runner!.State == RunnerState.Ready)
        {
            // Keep the latest conversation: the session stays in the Manager after the agent is gone.
            await shell.RunAsync(agent.Runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", cancellationToken: cancellationToken);
            try { await sessions.SyncRunnerLockedAsync(agent.Runner, cancellationToken, agent.Id); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Final session sync failed for {Agent}", agentId); }
        }

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
        using var lease = await sessions.LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Agents.Where(a => a.RunnerId == runner.Id && a.State != AgentState.Moving).ToListAsync(cancellationToken);
        agents.RemoveAll(MoveBlocksActions);

        var script = BuildStateScript(agents.Select(a => (a.TmuxSession, _harnesses.GetValueOrDefault(a.HarnessId)?.BusyPattern)), captureScreens: true,
            nativeSessions: agents.ToDictionary(a => a.TmuxSession, a => new NativeActivityTarget(a.HarnessId, a.HarnessSessionId)));
        if (leadingScript is not null) script = leadingScript.TrimEnd('\n') + $"\necho '{StateStartMarker}'\n" + script;
        var result = (await shell.RunAsync(runner, script, TimeSpan.FromSeconds(30), cancellationToken)).EnsureSuccess("Reading agent state");
        var output = SplitLeadingOutput(result.Output, leadingScript is not null, leadingOutput);
        var (_, panes, _) = ParsePanes(output);
        var knownLimits = await db.AgentLimits.AsNoTracking().Where(l => db.Agents.Any(a => a.Id == l.AgentId && a.RunnerId == runner.Id)).ToDictionaryAsync(l => l.AgentId, cancellationToken);
        var changedAgents = new List<Guid>();
        var screens = ParseScreens(output);

        foreach (var agent in agents)
        {
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

        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in changedAgents) events.AgentChanged(runner.Id, id);
        foreach (var agent in agents)
        {
            if (changedAgents.Contains(agent.Id))
            {
                var activity = AgentActivityTracker.Read(agent).Snapshot;
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agents = await db.Agents.Where(a => a.RunnerId == runnerId && a.State == AgentState.Running).ToListAsync(ct);
        var changed = new List<Guid>();
        foreach (var agent in agents)
            if (AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Unknown, "Terminal observation failed. Check the Runner connection and Manager logs.", "observation-failed"), AgentActivityTracker.Read(agent).Identity, DateTime.UtcNow)) changed.Add(agent.Id);
        await db.SaveChangesAsync(ct);
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
        if (agent.LoginStopPending || agent.LoginBlocked) throw new InvalidOperationException("Choose a permitted login after pending enforcement completes.");
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
                agent.RunningAccountId = savedLoginState.RunningAccountId;
                return;
            }
        }
        // The owner's default model and effort (My settings → Models), else the Manager's. The harness decides when they
        // apply: when a conversation starts in it, after a transfer, and for what it doesn't keep per conversation.
        var model = harness.ConfigSupport is null ? new Setup.EffectiveModel(null, null, null) : Setup.EffectiveModel.Resolve(
            (await settings.GetSetupProfileAsync(cancellationToken)).GetHarness(agent.HarnessId),
            userSettings is null ? null : (await userSettings.GetModelDefaultsAsync(agent.OwnerId, cancellationToken)).GetValueOrDefault(agent.HarnessId));
        var instructions = runner.SetupVersion >= Runners.RunnerSetupService.AgentToolsSetupVersion ? LaunchInstructions : null;
        var harnessCommand = harness.BuildLaunchCommand(new HarnessLaunch(agent.HarnessSessionId, agent.Name, agent.WorkingDirectory, model.Model, instructions,
            model.Effort, model.Provider, transferred));

        // The git identity's environment (the owner's unless switched: git config, GitHub token and variables), then the
        // harness login's, so e.g. a subscription wins over an API key meant for pi. A restart keeps the identity.
        var identity = GitIdentity(agent);
        if (identity != agent.OwnerId && !await loginDb.Users.AnyAsync(u => u.Id == identity && !u.Disabled, cancellationToken))
            throw new InvalidOperationException("The agent's git identity belongs to a disabled or deleted user. Choose another git identity.");
        await users.EnsureAsync(runner, identity, cancellationToken);
        var ownerEnvironment = $". \"{Runners.RunnerUsers.EnvironmentFile(identity)}\"; ";
        var account = await accounts.ResolveAsync(agent, cancellationToken);
        var accountEnvironment = "";
        if (agent.IsClone && agent.HarnessId == "pi")
            accountEnvironment = await accounts.PreparePiWorkspaceAsync(runner, agent, cancellationToken);
        if (account is not null)
        {
            if (loginActor is not null) await RequireOwnLoginAsync(loginActor, harness, account.Id, cancellationToken);
            await accounts.EnsureOnRunnerAsync(runner, account, cancellationToken);
            accountEnvironment = $". \"{Accounts.AccountService.EnvironmentFile(account.Id)}\"; " + await accounts.PrepareRuntimeAsync(runner, agent, account, cancellationToken);
        }

        agent.RunningAccountId = account?.Id;
        agent.RunningGitIdentityUserId = identity;
        await loginDb.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.RunningAccountId, account == null ? (Guid?)null : account.Id)
            .SetProperty(a => a.RunningGitIdentityUserId, (Guid?)identity), cancellationToken);
        // The browser terminal (xterm.js) and tmux support 24-bit color, but programs inside tmux only use it when told.
        var zone = Runners.RunnerTimeZones.Validate(runner.TimeZoneId ?? await settings.GetDefaultTimeZoneAsync(cancellationToken));
        var stored = await sessions.GetForAgentAsync(agent.Id, cancellationToken);
        if (artifactTokens is not null) await artifactTokens.InstallAsync(shell, runner, agent, cancellationToken);
        var imageContext = $"export DAM_AGENT_ID={ShellQuote.Quote(agent.Id.ToString("N"))}; export DAM_SESSION_ID={ShellQuote.Quote(stored?.Id.ToString("N") ?? "")}; ";
        var launch = "bash -c " + ShellQuote.Quote($". \"{RunnerShell.EnvScript}\"; {ownerEnvironment}{accountEnvironment}{imageContext}export TZ={ShellQuote.Quote(zone)}; export COLORTERM=truecolor; exec {harnessCommand}");
        var session = ShellQuote.Quote("=" + agent.TmuxSession);
        var directory = ShellQuote.Quote(agent.WorkingDirectory);

        // Keep the pane after the harness exits, so its last output stays visible and it can be restarted in place.
        // The session starts with a shell, remain-on-exit is switched on, and only then the harness replaces the shell,
        // so even a harness that exits immediately leaves its output behind. Targets need the trailing ':' (session:window).
        var target = ShellQuote.Quote("=" + agent.TmuxSession + ":");
        var script = BuildLaunchScript(session, ShellQuote.Quote(agent.TmuxSession), target, directory, ShellQuote.Quote(launch), restart);
        logger.LogInformation("{Action} agent {Agent} on {Runner}", restart ? "Restarting" : "Starting", agent.Name, runner.Name);
        var result = await shell.RunAsync(runner, script, TimeSpan.FromMinutes(1), cancellationToken);
        result.EnsureSuccess($"Starting {harness.DisplayName}");
        if (result.Output.Contains(AlreadyRunningMarker, StringComparison.Ordinal))
        {
            logger.LogInformation("Agent {Agent} on {Runner} was already running; left it alone", agent.Name, runner.Name);
        }
    }

    internal const string AlreadyRunningMarker = "DAM_ALREADY_RUNNING";

    /// <summary>Seconds a running harness gets to exit after SIGTERM before a restart kills it.</summary>
    internal const int GracefulStopSeconds = 10;

    /// <summary>
    /// The script that starts the harness in the agent's tmux pane. The pane of a session created here runs a plain shell,
    /// which is replaced. A pane whose harness is still running is only replaced when restarting (a start leaves it alone),
    /// and then the harness first gets SIGTERM and a grace period, so it can shut down instead of dying with its terminal.
    /// </summary>
    internal static string BuildLaunchScript(string session, string sessionName, string target, string directory, string launch, bool restart) => $$"""
        set -e
        created=0
        if ! tmux has-session -t {{session}} 2>/dev/null; then
          tmux -u new-session -d -s {{sessionName}} -x 200 -y 50 -c {{directory}}
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
        tmux respawn-pane -k -t {{target}} -c {{directory}} {{launch}}
        """;

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
