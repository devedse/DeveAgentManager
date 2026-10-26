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

    /// <summary>The npm package whose latest tag this harness installs; null when update detection is unsupported.</summary>
    string? UpdateNpmPackage => null;

    /// <summary>
    /// What the harness shows at the bottom of its screen while it works (an extended regular expression, as grep -E
    /// takes it), e.g. its spinner line; null when it has none. An agent counts as working while this is on its screen,
    /// instead of whenever its screen changes (typing, a resize or a rotating tip change it too).
    /// </summary>
    string? BusyPattern => null;

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

    /// <summary>
    /// True when the Manager renews this harness's subscription logins itself (Codex). Runners then get only
    /// <see cref="RunnerAccountCopy"/>, which can't renew, so two Runners never use up the same rotating refresh token.
    /// </summary>
    bool RenewsAccounts => false;

    /// <summary>What a Runner gets of a subscription login the Manager renews: no refresh token.</summary>
    string RunnerAccountCopy(string secret) => secret;

    /// <summary>The renewed login when it is due, else null. Throws <see cref="AccountRenewalException"/>.</summary>
    Task<string?> RenewAccountAsync(string secret, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>A login couldn't be renewed. <paramref name="permanent"/>: the provider ended it; only connecting it again helps.</summary>
public sealed class AccountRenewalException(string message, bool permanent) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}

/// <param name="Files">Paths relative to the account folder, with their content and mode.</param>
/// <param name="Environment">Bash that sets up an agent's environment (exports), sourced before the harness starts.</param>
/// <param name="Script">Bash run after the files are written (e.g. links to shared folders).</param>
public sealed record HarnessAccountSetup(IReadOnlyList<(string Path, string Content, string Mode)> Files, string Environment, string? Script);

/// <param name="Model">The model a conversation starts with: the owner's choice in My settings, else the Manager's default; null lets
/// the harness choose. A resumed conversation keeps what it ran with where the harness records it (see docs/model-defaults.md).</param>
/// <param name="Instructions">Added to the harness's system prompt for this launch, never written to a file. Harnesses record it
/// when a conversation starts, so a resumed conversation keeps the text it started with.</param>
/// <param name="Effort">The reasoning effort or thinking level, chosen like <paramref name="Model"/>.</param>
/// <param name="Provider">pi: the provider of <paramref name="Model"/>, when it is the Manager's default.</param>
/// <param name="Transferred">The conversation was just converted from another harness: the model and effort it recorded are not a
/// choice made here, so the defaults apply although the conversation exists.</param>
public sealed record HarnessLaunch(string SessionId, string DisplayName, string WorkingDirectory, string? Model = null, string? Instructions = null,
    string? Effort = null, string? Provider = null, bool Transferred = false);
