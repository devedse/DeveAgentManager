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

/// <summary>How a harness's accounts log in.</summary>
/// <param name="SubscriptionName">E.g. "Claude subscription (Pro, Max)".</param>
/// <param name="LoginCommand">
/// Bash run in a browser terminal on a Runner to log in with a subscription. <c>$DAM_LOGIN_DIR</c> is an empty folder
/// for files the login writes.
/// </param>
/// <param name="LoginHelp">Shown above that terminal: what to do.</param>
/// <param name="TokenPattern">When set, the credential is the text in the terminal's output that matches this regex.</param>
/// <param name="LoginFile">When set, the credential is this file in <c>$DAM_LOGIN_DIR</c>, read after the login succeeded.</param>
/// <param name="ApiKeyName">The environment variable of an API key, e.g. ANTHROPIC_API_KEY.</param>
/// <param name="PasteHint">When set, a subscription token made elsewhere can be pasted; describes how to make one.</param>
public sealed record HarnessAccountSupport(
    string SubscriptionName,
    string LoginCommand,
    string LoginHelp,
    string? TokenPattern,
    string? LoginFile,
    string ApiKeyName,
    string? PasteHint);

/// <param name="Files">Paths relative to the account folder, with their content and mode.</param>
/// <param name="Environment">Bash that sets up an agent's environment (exports), sourced before the harness starts.</param>
/// <param name="Script">Bash run after the files are written (e.g. links to shared folders).</param>
public sealed record HarnessAccountSetup(IReadOnlyList<(string Path, string Content, string Mode)> Files, string Environment, string? Script);

/// <summary>How the Portal presents a harness's configuration.</summary>
/// <param name="PackageExample">Placeholder for a package source.</param>
/// <param name="SuggestedPackages">Offered as one-click additions.</param>
/// <param name="SettingsFile">Where the settings end up on the Runner.</param>
/// <param name="KeybindingsFile">Where the keybindings end up on the Runner.</param>
/// <param name="ThinkingLevels">Choices for the default thinking level.</param>
/// <param name="DocsUrl">Where packages and settings are documented.</param>
/// <param name="HasPackages">Offers packages (pi).</param>
/// <param name="HasProvider">The model is chosen together with a provider (pi).</param>
/// <param name="HasKeybindings">Offers keybindings (pi).</param>
/// <param name="HasExtraSettings">Offers extra settings as JSON (merged into <see cref="SettingsFile"/>).</param>
/// <param name="ModelSuggestions">Offered in the model field besides what <see cref="IHarness.ModelsCommand"/> lists.</param>
/// <param name="EffortName">What the harness calls the thinking level ("Effort" for Claude Code and Codex).</param>
public sealed record HarnessConfigSupport(
    string PackageExample,
    IReadOnlyList<SuggestedPackage> SuggestedPackages,
    string SettingsFile,
    string KeybindingsFile,
    IReadOnlyList<string> ThinkingLevels,
    string DocsUrl,
    bool HasPackages = true,
    bool HasProvider = true,
    bool HasKeybindings = true,
    bool HasExtraSettings = true,
    IReadOnlyList<string>? ModelSuggestions = null,
    string EffortName = "Thinking level",
    string SettingsExample = "{ \"enabledModels\": [\"anthropic/*\"], \"hideThinkingBlock\": true }");

/// <param name="Id">What the harness takes as its model setting.</param>
/// <param name="Efforts">The effort levels this model supports (empty: the harness's usual ones).</param>
public sealed record ModelChoice(string Id, string Name, IReadOnlyList<string> Efforts, string? DefaultEffort = null);

public sealed record SuggestedPackage(string Source, string Description);

public sealed record HarnessLaunch(string SessionId, string DisplayName, string WorkingDirectory);
