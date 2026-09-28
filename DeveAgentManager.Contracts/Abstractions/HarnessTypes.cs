namespace DeveAgentManager.Core.Abstractions;

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
/// <param name="ModelSuggestions">Offered in the model field besides what the harness's models command lists.</param>
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
    IReadOnlyList<ModelChoice>? ModelSuggestions = null,
    string EffortName = "Thinking level",
    string SettingsExample = "{ \"enabledModels\": [\"anthropic/*\"], \"hideThinkingBlock\": true }");

/// <param name="Id">What the harness takes as its model setting.</param>
/// <param name="Efforts">The effort levels this model supports (empty: the harness's usual ones).</param>
public sealed record ModelChoice(string Id, string Name, IReadOnlyList<string> Efforts, string? DefaultEffort = null);

public sealed record SuggestedPackage(string Source, string Description);
