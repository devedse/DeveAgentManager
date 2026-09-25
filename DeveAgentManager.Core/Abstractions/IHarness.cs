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

    /// <summary>Packages, default model and settings from the Setup Profile; null when the harness doesn't support that.</summary>
    HarnessConfigSupport? ConfigSupport => null;

    /// <summary>
    /// Idempotent bash script that applies the configuration: installs the packages, removes packages it installed before
    /// that are no longer listed, and merges the settings. Only called when <see cref="ConfigSupport"/> is set.
    /// </summary>
    string BuildConfigureScript(Setup.HarnessConfig config) => throw new NotSupportedException();
}

/// <summary>How the Portal presents a harness's configuration.</summary>
/// <param name="PackageExample">Placeholder for a package source.</param>
/// <param name="SuggestedPackages">Offered as one-click additions.</param>
/// <param name="SettingsFile">Where the settings end up on the Runner.</param>
/// <param name="KeybindingsFile">Where the keybindings end up on the Runner.</param>
/// <param name="ThinkingLevels">Choices for the default thinking level.</param>
/// <param name="DocsUrl">Where packages and settings are documented.</param>
public sealed record HarnessConfigSupport(
    string PackageExample,
    IReadOnlyList<SuggestedPackage> SuggestedPackages,
    string SettingsFile,
    string KeybindingsFile,
    IReadOnlyList<string> ThinkingLevels,
    string DocsUrl);

public sealed record SuggestedPackage(string Source, string Description);

public sealed record HarnessLaunch(string SessionId, string DisplayName, string WorkingDirectory);
