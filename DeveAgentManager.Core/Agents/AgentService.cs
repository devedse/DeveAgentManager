using System.Text.RegularExpressions;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

public enum AgentWorkspaceMode
{
    EmptyFolder,
    CloneRepository,
    ExistingFolder,
}

public sealed record CreateAgentRequest(
    Guid RunnerId,
    string Name,
    string HarnessId,
    AgentWorkspaceMode Mode,
    string? RepositoryUrl = null,
    string? Branch = null,
    string? ExistingPath = null);

public sealed class AgentNotRunningException(string message) : Exception(message);

/// <summary>
/// Agents are harness processes that run inside a tmux session on a Runner, so they keep running when nobody is
/// watching. The tmux session name is "dam-&lt;agent name&gt;".
/// </summary>
public sealed partial class AgentService(
    IDbContextFactory<ManagerDbContext> dbFactory,
    RunnerShell shell,
    IEnumerable<IHarness> harnesses,
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
        return await db.Agents.AsNoTracking().Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
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
            HarnessSessionId = agentId.ToString("N"),
            State = AgentState.Starting,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await PrepareWorkspaceAsync(runner, agent, request.Mode, cancellationToken);

        db.Agents.Add(agent);
        await db.SaveChangesAsync(cancellationToken);
        events.AgentChanged(runner.Id, agent.Id);

        try
        {
            await LaunchAsync(runner, agent, cancellationToken);
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

    /// <summary>Starts the agent again (or restarts it when its process exited). The harness resumes the same conversation.</summary>
    public async Task StartAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        var runner = RequireReady(agent.Runner!);
        await LaunchAsync(runner, agent, cancellationToken);
        agent.State = AgentState.Running;
        agent.StateMessage = null;
        agent.ExitCode = null;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.AgentChanged(runner.Id, agent.Id);
    }

    public async Task StopAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Runner).FirstAsync(a => a.Id == agentId, cancellationToken);
        var runner = RequireReady(agent.Runner!);
        await shell.RunAsync(runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", cancellationToken: cancellationToken);
        agent.State = AgentState.Stopped;
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

        return (runner, $"tmux -u attach-session -t {ShellQuote.Quote("=" + agent.TmuxSession)}");
    }

    /// <summary>Reads the state of every agent on a Runner from tmux.</summary>
    public async Task RefreshRunnerAsync(Runner runner, CancellationToken cancellationToken = default)
    {
        var result = await shell.RunAsync(runner, "tmux list-panes -a -F '#{session_name}|#{pane_dead}|#{pane_dead_status}' 2>/dev/null || true", TimeSpan.FromSeconds(30), cancellationToken);
        var panes = ParsePanes(result.Output);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Agents.Where(a => a.RunnerId == runner.Id).ToListAsync(cancellationToken);
        foreach (var agent in agents)
        {
            AgentState state;
            int? exitCode = null;
            if (panes.TryGetValue(agent.TmuxSession, out var pane))
            {
                state = pane.Dead ? AgentState.Exited : AgentState.Running;
                exitCode = pane.Dead ? pane.ExitCode : null;
            }
            else
            {
                // Just created agents may not show up yet.
                state = agent.State == AgentState.Starting && DateTime.UtcNow - agent.UpdatedAt < TimeSpan.FromMinutes(1) ? AgentState.Starting : AgentState.Stopped;
            }

            if (agent.State != state || agent.ExitCode != exitCode)
            {
                agent.State = state;
                agent.ExitCode = exitCode;
                agent.StateMessage = state == AgentState.Exited ? $"The harness exited (code {exitCode}). Start it again to resume the conversation." : null;
                agent.UpdatedAt = DateTime.UtcNow;
                events.AgentChanged(runner.Id, agent.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllStoppedAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Agents.Where(a => a.RunnerId == runnerId && a.State != AgentState.Stopped)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentState.Stopped).SetProperty(a => a.UpdatedAt, DateTime.UtcNow), cancellationToken);
        events.RunnerChanged(runnerId);
    }

    public async Task StopAllOnRunnerAsync(Runner runner, CancellationToken cancellationToken)
    {
        foreach (var agent in runner.Agents)
        {
            await shell.RunAsync(runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    internal static Dictionary<string, (bool Dead, int? ExitCode)> ParsePanes(string output)
    {
        var panes = new Dictionary<string, (bool Dead, int? ExitCode)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            var dead = parts[1] == "1";
            int? exitCode = parts.Length > 2 && int.TryParse(parts[2], out var code) ? code : null;
            // A session has one pane per window; the agent's session has a single pane.
            panes[parts[0]] = (dead, exitCode);
        }

        return panes;
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

    private async Task LaunchAsync(Runner runner, Agent agent, CancellationToken cancellationToken)
    {
        var harness = GetHarness(agent.HarnessId);
        var harnessCommand = harness.BuildLaunchCommand(new HarnessLaunch(agent.HarnessSessionId, agent.Name, agent.WorkingDirectory));
        var launch = "bash -c " + ShellQuote.Quote($". \"{RunnerShell.EnvScript}\"; exec {harnessCommand}");
        var session = ShellQuote.Quote("=" + agent.TmuxSession);
        var directory = ShellQuote.Quote(agent.WorkingDirectory);

        // Keep the pane after the harness exits, so its last output stays visible and it can be restarted in place.
        var script = $"""
            if tmux has-session -t {session} 2>/dev/null; then
              tmux respawn-pane -k -t {ShellQuote.Quote("=" + agent.TmuxSession + ":")} -c {directory} {ShellQuote.Quote(launch)}
            else
              tmux -u new-session -d -s {ShellQuote.Quote(agent.TmuxSession)} -x 200 -y 50 -c {directory} {ShellQuote.Quote(launch)} \; set-option -t {session} remain-on-exit on
            fi
            """;
        logger.LogInformation("Starting agent {Agent} on {Runner}", agent.Name, runner.Name);
        (await shell.RunAsync(runner, script, cancellationToken: cancellationToken)).EnsureSuccess($"Starting {harness.DisplayName}");
    }

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
