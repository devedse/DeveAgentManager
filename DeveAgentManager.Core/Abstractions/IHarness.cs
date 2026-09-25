namespace DeveAgentManager.Core.Abstractions;

/// <summary>An agent CLI (pi today; Claude Code and Codex later).</summary>
public interface IHarness
{
    /// <summary>Stable id stored on every Agent, e.g. "pi".</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>Idempotent bash script that installs or updates the harness. Runs as the Runner Account with the Manager's env.sh sourced.</summary>
    string InstallScript { get; }

    /// <summary>Prints the installed version.</summary>
    string VersionCommand { get; }

    /// <summary>
    /// Command that starts the harness for an agent. It is run inside tmux in the agent's working directory and must
    /// resume the same conversation when it is run again (after a restart or a Runner reboot).
    /// </summary>
    string BuildLaunchCommand(HarnessLaunch launch);

    /// <summary>
    /// Files that hold the harness login and settings (plugins). A login done once in a Runner's terminal can be imported
    /// into the Setup Profile from these, so every new Runner gets it.
    /// </summary>
    IReadOnlyList<string> ConfigFiles { get; }
}

public sealed record HarnessLaunch(string SessionId, string DisplayName, string WorkingDirectory);
