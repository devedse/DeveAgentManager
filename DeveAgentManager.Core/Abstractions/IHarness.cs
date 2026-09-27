namespace DeveAgentManager.Core.Abstractions;

/// <summary>An agent CLI (pi today; Claude Code and Codex later).</summary>
public interface IHarness
{
    /// <summary>Stable id stored on every Agent, e.g. "pi".</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>The harness's logo: SVG content for a 24×24 view box (shapes without a fill take the text color).</summary>
    string? Icon => null;

    /// <summary>Idempotent bash script that installs or updates the harness. Runs as the Runner Account with the Manager's env.sh sourced.</summary>
    string InstallScript { get; }

    /// <summary>The harness's own id for a new agent's conversation (pi: 32 hex digits; Claude Code and Codex: a UUID).</summary>
    string NewSessionId(Guid agentId) => agentId.ToString("N");

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

    /// <summary>Packages, default model and settings from the Setup Profile; null when the harness doesn't support that.</summary>
    HarnessConfigSupport? ConfigSupport => null;

    /// <summary>
    /// Idempotent bash script that applies the configuration: installs the packages, removes packages it installed before
    /// that are no longer listed, and merges the settings. Only called when <see cref="ConfigSupport"/> is set.
    /// </summary>
    string BuildConfigureScript(Setup.HarnessConfig config) => throw new NotSupportedException();

    /// <summary>Bash that prints the models the installed harness offers (see <see cref="ParseModels"/>); null when it can't.</summary>
    string? ModelsCommand => null;

    /// <summary>The models in <see cref="ModelsCommand"/>'s output.</summary>
    IReadOnlyList<ModelChoice> ParseModels(string output) => [];

    /// <summary>How accounts (subscriptions, API keys) log in; null when the harness logs in on its own (pi: /login).</summary>
    HarnessAccountSupport? AccountSupport => null;

    /// <summary>
    /// What makes an account usable on a Runner: files under the account's folder, the environment an agent runs with,
    /// and a script run after the files are written. Only called when <see cref="AccountSupport"/> is set.
    /// </summary>
    HarnessAccountSetup BuildAccountSetup(string accountFolder, Data.AccountKind kind, string secret) => throw new NotSupportedException();
}

/// <param name="Files">Paths relative to the account folder, with their content and mode.</param>
/// <param name="Environment">Bash that sets up an agent's environment (exports), sourced before the harness starts.</param>
/// <param name="Script">Bash run after the files are written (e.g. links to shared folders).</param>
public sealed record HarnessAccountSetup(IReadOnlyList<(string Path, string Content, string Mode)> Files, string Environment, string? Script);

public sealed record HarnessLaunch(string SessionId, string DisplayName, string WorkingDirectory);
