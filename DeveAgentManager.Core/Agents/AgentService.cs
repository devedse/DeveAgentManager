using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
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
    ManagerEvents events,
    ILogger<AgentService> logger)
{
    private readonly Dictionary<string, IHarness> _harnesses = harnesses.ToDictionary(h => h.Id);

    public IReadOnlyCollection<IHarness> Harnesses => _harnesses.Values;

    public async Task<List<Agent>> ListAsync(Guid? runnerId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Agents.AsNoTracking().Include(a => a.Runner).AsQueryable();
        if (runnerId is not null)
        {
            query = query.Where(a => a.RunnerId == runnerId);
        }

        return await query.OrderByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<Agent?> GetAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Agents.AsNoTracking().Include(a => a.Runner).Include(a => a.Account).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
    }

    public async Task<Agent> CreateAsync(CreateAgentRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim().ToLowerInvariant();
        if (!AgentName().IsMatch(name))
        {
            throw new ArgumentException("Use lowercase letters, digits and dashes for the name, for example 'fix-maze-perf'.");
        }

        var harness = GetHarness(request.HarnessId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.FirstOrDefaultAsync(r => r.Id == request.RunnerId, cancellationToken)
            ?? throw new ArgumentException("Runner not found.");
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
        var workingDirectory = request.Mode switch
        {
            AgentWorkspaceMode.ExistingFolder => ExpandHome(RequireValue(request.ExistingPath, "Enter the folder on the Runner."), home),
            _ => $"{home}/work/{name}",
        };

        var agentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var agent = new Agent
        {
            Id = agentId,
            RunnerId = runner.Id,
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

        await PrepareWorkspaceAsync(runner, agent, request.Mode, cancellationToken);

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
            await LaunchAsync(runner, agent, restart: false, cancellationToken);
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
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        var runner = RequireReady(agent.Runner!);
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
    /// Continues the agent in another harness and/or with another account, in the same folder on the same Runner (e.g.
    /// when a subscription ran out). The harness is stopped gracefully and the conversation synced. For another harness
    /// the conversation moves as text (tool calls become short descriptions; thinking stays behind); for another account
    /// of the same harness the conversation simply resumes. <paramref name="accountId"/> null means the default account.
    /// </summary>
    public async Task SwitchAsync(Guid agentId, string harnessId, Guid? accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        var runner = RequireReady(agent.Runner!);
        var target = GetHarness(harnessId);
        if (!runner.HarnessIds.Contains(harnessId))
        {
            throw new InvalidOperationException($"{target.DisplayName} is not installed on '{runner.Name}'. Install it first (Runner → Operations).");
        }

        var changesHarness = harnessId != agent.HarnessId;
        if (!changesHarness && accountId == agent.AccountId)
        {
            return;
        }

        logger.LogInformation("Switching agent {Agent} from {From} to {To} (account {Account})", agent.Name, agent.HarnessId, harnessId, accountId);
        (await shell.RunAsync(runner, BuildStopScript(ShellQuote.Quote("=" + agent.TmuxSession + ":")), TimeSpan.FromMinutes(1), cancellationToken))
            .EnsureSuccess("Stopping the harness");

        if (changesHarness)
        {
            // The latest messages first, then the conversation moves to the new harness's own format.
            await sessions.SyncRunnerAsync(runner, cancellationToken);
            var sessionId = target.NewSessionId(agent.Id);
            await sessions.MoveToHarnessAsync(runner, agent, harnessId, sessionId, cancellationToken);
            agent.HarnessId = harnessId;
            agent.HarnessSessionId = sessionId;
        }

        agent.AccountId = accountId;
        AgentActivityTracker.Reset(agent);
        agent.State = AgentState.Starting;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.AgentChanged(runner.Id, agent.Id);

        await LaunchAsync(runner, agent, restart: true, cancellationToken);
        agent.State = AgentState.Running;
        agent.StateMessage = null;
        agent.ExitCode = null;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.AgentChanged(runner.Id, agent.Id);
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
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        var runner = RequireReady(agent.Runner!);
        await shell.RunAsync(runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", cancellationToken: cancellationToken);
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
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        if (agent.Runner!.State == RunnerState.Ready)
        {
            // Keep the latest conversation: the session stays in the Manager after the agent is gone.
            await sessions.TrySyncRunnerAsync(agent.Runner, cancellationToken);
            await shell.RunAsync(agent.Runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", cancellationToken: cancellationToken);
        }

        db.Agents.Remove(agent);
        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.AgentRemoved, agent.RunnerId, agent.Id));
    }

    /// <summary>The command a terminal runs to attach to the agent's tmux session.</summary>
    public async Task<(Runner Runner, string Command)> GetAttachCommandAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await GetAsync(agentId, cancellationToken) ?? throw new ArgumentException("Agent not found.");
        var runner = RequireReady(agent.Runner!);
        var check = await shell.RunAsync(runner, $"tmux has-session -t {ShellQuote.Quote("=" + agent.TmuxSession)}", cancellationToken: cancellationToken);
        if (!check.Succeeded)
        {
            throw new AgentNotRunningException($"Agent '{agent.Name}' is not running. Start it first.");
        }

        // tmux sizes the window to the screen used last (window-size latest), and every resize makes pi redraw its whole
        // conversation. So a terminal that joins while others are attached joins watching (ignore-size): opening or
        // refreshing a tab changes nothing. It takes the size once its user types (ClaimSizeAsync).
        var target = ShellQuote.Quote("=" + agent.TmuxSession);
        var attach = $"""
            if [ -n "$(tmux list-clients -t {target} 2>/dev/null)" ]; then exec tmux -u attach-session -f ignore-size -t {target}; fi
            exec tmux -u attach-session -t {target}
            """;
        return (runner, "bash -c " + ShellQuote.Quote(attach));
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

    /// <summary>Reads process lifecycle and the current visible screen. Activity rules and stable completion tracking run in the Manager.</summary>
    public async Task RefreshRunnerAsync(Runner runner, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Agents.Where(a => a.RunnerId == runner.Id).ToListAsync(cancellationToken);

        var result = (await shell.RunAsync(runner, BuildStateScript(agents.Select(a => (a.TmuxSession, _harnesses.GetValueOrDefault(a.HarnessId)?.BusyPattern)), captureScreens: true),
            TimeSpan.FromSeconds(30), cancellationToken)).EnsureSuccess("Reading agent state");
        var (_, panes, _) = ParsePanes(result.Output);
        var changedAgents = new List<Guid>();
        var screens = ParseScreens(result.Output);

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
                    var detection = screen?.Screen is { } text ? AgentScreenDetector.Detect(agent.HarnessId, text, DateTime.UtcNow)
                        : new AgentScreenDetection(AgentActivityStatus.Unknown, "The terminal snapshot could not be read.", "snapshot-unavailable");
                    activityChanged = AgentActivityTracker.Apply(agent, detection,
                        screen?.Identity ?? AgentActivityTracker.Read(agent).Identity, DateTime.UtcNow);
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
    }

    public async Task MarkObservationUnavailableAsync(Guid runnerId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agents = await db.Agents.Where(a => a.RunnerId == runnerId && a.State == AgentState.Running).ToListAsync(ct);
        var changed = new List<Guid>();
        foreach (var agent in agents)
            if (AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Unknown, "Terminal observation failed. Check the Runner connection and Manager logs.", "observation-failed"), AgentActivityTracker.Read(agent).Identity, DateTime.UtcNow)) changed.Add(agent.Id);
        await db.SaveChangesAsync(ct);
        foreach (var id in changed) events.AgentChanged(runnerId, id);
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
    internal static string BuildStateScript(IEnumerable<(string Session, string? BusyPattern)> agents, bool captureScreens = false)
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
            script.Append("<<'DAM_STATE'\n").Append("""
                import json,pathlib,subprocess,sys
                for session in sys.argv[1:]:
                    try:
                        target='='+session+':'
                        meta=subprocess.check_output(['tmux','display-message','-p','-t',target,'#{pane_id}|#{pane_pid}'],text=True,stderr=subprocess.DEVNULL).strip()
                        pane,pid=meta.split('|')
                        start=pathlib.Path('/proc',pid,'stat').read_text().rsplit(')',1)[1].split()[19]
                        identity=meta+'|'+start+'|'+pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip()
                        screen=subprocess.check_output(['tmux','capture-pane','-p','-J','-t',target],text=True,stderr=subprocess.DEVNULL)[-32768:]
                        print(json.dumps(dict(Session=session,Identity=identity,Screen=screen)))
                    except (OSError,ValueError,subprocess.CalledProcessError):
                        print(json.dumps(dict(Session=session,Identity=None,Screen=None)))
                """).Append("\nDAM_STATE\n");
        }

        return script.ToString();
    }

    internal const string BusyMarker = "--busy--";
    internal sealed record ObservedScreen(string Session, string? Identity, string? Screen);
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
                var script = $"""
                    if [ -e {directory} ] && [ -n "$(ls -A {directory} 2>/dev/null)" ]; then echo "The folder {agent.WorkingDirectory} already exists and is not empty." >&2; exit 3; fi
                    GIT_TERMINAL_PROMPT=0 git clone{branch} {ShellQuote.Quote(NormalizeRepositoryUrl(url))} {directory}
                    """;
                (await shell.RunAsync(runner, script, TimeSpan.FromMinutes(15), cancellationToken)).EnsureSuccess("Cloning the repository");
                break;
        }
    }

    private async Task LaunchAsync(Runner runner, Agent agent, bool restart, CancellationToken cancellationToken)
    {
        var harness = GetHarness(agent.HarnessId);
        var harnessCommand = harness.BuildLaunchCommand(new HarnessLaunch(agent.HarnessSessionId, agent.Name, agent.WorkingDirectory));

        // The account's environment comes after the Setup Profile's, so e.g. a subscription wins over an API key meant for pi.
        var account = await accounts.ResolveAsync(agent, cancellationToken);
        var accountEnvironment = "";
        if (account is not null)
        {
            await accounts.EnsureOnRunnerAsync(runner, account, cancellationToken);
            accountEnvironment = $". \"{Accounts.AccountService.EnvironmentFile(account.Id)}\"; ";
        }

        // The browser terminal (xterm.js) and tmux support 24-bit color, but programs inside tmux only use it when told.
        var zone = Runners.RunnerTimeZones.Validate(runner.TimeZoneId ?? await settings.GetDefaultTimeZoneAsync(cancellationToken));
        var launch = "bash -c " + ShellQuote.Quote($". \"{RunnerShell.EnvScript}\"; {accountEnvironment}export TZ={ShellQuote.Quote(zone)}; export COLORTERM=truecolor; exec {harnessCommand}");
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
