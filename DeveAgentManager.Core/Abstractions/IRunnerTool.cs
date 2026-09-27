namespace DeveAgentManager.Core.Abstractions;

/// <summary>
/// Software a Runner can have besides the agent harnesses, like VS Code in the browser. Chosen per Runner, installed
/// with its setup (or later from the Runner page) and updated the same way as a harness.
/// </summary>
public interface IRunnerTool
{
    /// <summary>Stable id stored on the Runner, e.g. "vscode".</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>The logo: SVG content for a 24×24 view box.</summary>
    string? Icon { get; }

    /// <summary>Idempotent bash script that installs or updates the tool. Runs as the Runner Account with the Manager's env.sh sourced.</summary>
    string InstallScript { get; }

    /// <summary>Prints the installed version.</summary>
    string VersionCommand { get; }
}
