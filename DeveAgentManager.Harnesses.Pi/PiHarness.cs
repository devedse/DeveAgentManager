using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Setup;
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

    public HarnessConfigSupport ConfigSupport { get; } = new(
        PackageExample: "npm:pi-web-access",
        SuggestedPackages:
        [
            new("npm:pi-mcp-adapter", "MCP servers as tools"),
            new("npm:pi-web-access", "Web search, fetching pages, GitHub, PDFs"),
            new("npm:pi-subagents", "Delegating to sub-agents"),
            new("npm:pi-effort", "Thinking effort aliases"),
            new("npm:pi-plugin-manager", "Manage plugins from inside pi"),
            new("npm:pi-archimedes", "Parallel agents, task lists, MCP, polished terminal (suite)"),
        ],
        SettingsFile: "~/.pi/agent/settings.json",
        ThinkingLevels: ["off", "minimal", "low", "medium", "high", "xhigh", "max"],
        DocsUrl: "https://pi.dev/packages");

    /// <summary>List of the packages the Manager installed, so packages removed from the Setup Profile can be removed.</summary>
    public const string ManagedPackagesFile = "$HOME/.deveagentmanager/pi-packages";

    public string BuildConfigureScript(HarnessConfig config)
    {
        config.Validate();
        var packages = Base64(string.Join("\n", config.CleanPackages()));
        var settings = Base64(config.BuildSettings().ToJsonString());
        return $$"""
            set -euo pipefail
            agent_dir="${PI_CODING_AGENT_DIR:-$HOME/.pi/agent}"
            managed="{{ManagedPackagesFile}}"
            wanted="$(printf %s {{packages}} | base64 -d)"
            mkdir -p "$agent_dir" "$(dirname "$managed")"

            if [ -f "$managed" ]; then
              while IFS= read -r source; do
                if [ -n "$source" ] && ! printf '%s\n' "$wanted" | grep -qxF -- "$source"; then
                  echo "==> Removing pi package $source"
                  pi remove "$source" || echo "Could not remove $source (already gone?)"
                fi
              done < "$managed"
            fi

            failed=""
            while IFS= read -r source; do
              [ -n "$source" ] || continue
              echo "==> Installing pi package $source"
              if ! pi install "$source"; then
                failed="$failed $source"
              fi
            done <<< "$wanted"
            printf '%s\n' "$wanted" > "$managed"

            echo "==> Writing pi settings"
            node - "$agent_dir/settings.json" {{settings}} <<'DAM_MERGE'
            const fs = require("fs");
            const [file, encoded] = process.argv.slice(2);
            const extra = JSON.parse(Buffer.from(encoded, "base64").toString("utf8"));
            // Packages are installed with pi install above, not written directly.
            delete extra.packages;
            let current = {};
            if (fs.existsSync(file)) {
              current = JSON.parse(fs.readFileSync(file, "utf8"));
            }
            const isObject = value => value !== null && typeof value === "object" && !Array.isArray(value);
            const merge = (target, source) => {
              for (const [key, value] of Object.entries(source)) {
                target[key] = isObject(value) && isObject(target[key]) ? merge(target[key], value) : value;
              }
              return target;
            };
            fs.writeFileSync(file, JSON.stringify(merge(current, extra), null, 2) + "\n");
            DAM_MERGE

            if [ -n "$failed" ]; then
              echo "Could not install:$failed" >&2
              exit 1
            fi
            """;
    }

    private static string Base64(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
}

public static class PiServiceCollectionExtensions
{
    public static IServiceCollection AddPiHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, PiHarness>().AddSingleton<ISessionFormat, PiSessionFormat>();
}
