using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace DeveAgentManager.Harnesses.Pi;

/// <summary>
/// The pi coding agent (https://pi.dev, npm package @earendil-works/pi-coding-agent).
/// Logins live in ~/.pi/agent/auth.json or come from environment variables such as ANTHROPIC_API_KEY.
/// </summary>
public sealed class PiHarness : IHarness
{
    public const string HarnessId = "pi";
    public const string NpmPackage = "@earendil-works/pi-coding-agent";

    public string Id => HarnessId;

    public string DisplayName => "pi";

    public string Description => "The pi coding agent. Log in with /login in a terminal, or set an API key in the Setup Profile.";

    public string InstallScript => $"""
        set -euo pipefail
        echo "==> Installing pi ({NpmPackage})"
        npm install -g --ignore-scripts --no-fund --no-audit {NpmPackage}@latest
        pi --version
        if [ -f "$HOME/.pi/agent/settings.json" ]; then
          echo "==> Installing pi packages from settings.json"
          pi update --extensions
        fi
        """;

    public string VersionCommand => "pi --version";

    /// <summary>
    /// <c>--session-id</c> opens the session with that id or creates it, so the same command both starts and resumes.
    /// </summary>
    public string BuildLaunchCommand(HarnessLaunch launch) =>
        $"pi --session-id {ShellQuote.Quote(launch.SessionId)} --name {ShellQuote.Quote(launch.DisplayName)}";

    public IReadOnlyList<string> ConfigFiles { get; } = ["~/.pi/agent/auth.json", "~/.pi/agent/settings.json"];
}

public static class PiServiceCollectionExtensions
{
    public static IServiceCollection AddPiHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, PiHarness>().AddSingleton<ISessionFormat, PiSessionFormat>();
}
