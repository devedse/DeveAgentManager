using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Setup;
using System.Text.Json.Nodes;
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

    /// <summary>The pi logo (https://pi.dev/logo-auto.svg), cropped to the mark.</summary>
    public string Icon => """<g transform="scale(0.051126) translate(-165.29 -165.29)"><path fill="#F09082" d="M165.29 165.29H517.36V400H400V282.65H165.29Z"/><path fill="#4D9ABF" d="M165.29 282.65H282.65V400H400V517.36H282.65V634.72H165.29Z"/><path fill="#F1BE58" d="M517.36 400H634.72V634.72H517.36Z"/></g>""";

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
            new("npm:@dietrichgebert/ponytail", "Lazy senior dev mode: the best code is the code you never wrote"),
            new("npm:@pi-archimedes/image-paste", "Image paste with preview (reads the Runner's clipboard: in the browser terminal, use Ctrl+V or 📎)"),
        ],
        SettingsFile: "~/.pi/agent/settings.json",
        KeybindingsFile: "~/.pi/agent/keybindings.json",
        ThinkingLevels: ["off", "minimal", "low", "medium", "high", "xhigh", "max"],
        DocsUrl: "https://pi.dev/packages");

    /// <summary>List of the packages the Manager installed, so packages removed from the Setup Profile can be removed.</summary>
    public const string ManagedPackagesFile = "$HOME/.deveagentmanager/pi-packages";

    public string BuildConfigureScript(HarnessConfig config)
    {
        config.Validate();
        var packages = Base64(string.Join("\n", config.CleanPackages()));
        var settings = Base64(config.BuildSettings().ToJsonString());
        var keybindings = BuildKeybindings(config);
        var keybindingsStep = keybindings.Count == 0 ? "" : $"""
            echo "==> Writing pi keybindings"
            node "$merge" "$agent_dir/keybindings.json" {Base64(keybindings.ToJsonString())}
            """;
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

            # Merges a JSON object (base64) into a JSON file: objects merge, other values replace.
            merge="$(mktemp)"
            trap 'rm -f "$merge"' EXIT
            cat > "$merge" <<'DAM_MERGE'
            const fs = require("fs");
            const [file, encoded] = process.argv.slice(2);
            const extra = JSON.parse(Buffer.from(encoded, "base64").toString("utf8"));
            // Packages are installed with pi install above, not written directly.
            if (file.endsWith("settings.json")) {
              delete extra.packages;
            }
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

            echo "==> Writing pi settings"
            node "$merge" "$agent_dir/settings.json" {{settings}}
            {{keybindingsStep}}

            if [ -n "$failed" ]; then
              echo "Could not install:$failed" >&2
              exit 1
            fi
            """;
    }

    /// <summary>
    /// The keybindings to merge. <c>@pi-archimedes/image-paste</c> (also in the <c>pi-archimedes</c> suite) takes over
    /// pi's image paste key and asks to clear pi's own binding (otherwise both fire, and it asks on the first start of
    /// every Runner): that is done here, unless the Setup Profile sets that binding itself.
    /// </summary>
    internal static JsonObject BuildKeybindings(HarnessConfig config)
    {
        var keybindings = config.BuildKeybindings();
        if (config.CleanPackages().Any(p => PackageName(p) is "@pi-archimedes/image-paste" or "pi-archimedes")
            && !keybindings.ContainsKey("app.clipboard.pasteImage"))
        {
            keybindings["app.clipboard.pasteImage"] = new JsonArray();
        }

        return keybindings;
    }

    /// <summary>"npm:@scope/name@1.2.3" → "@scope/name"; other sources are returned as they are.</summary>
    public static string PackageName(string source)
    {
        if (!source.StartsWith("npm:", StringComparison.Ordinal))
        {
            return source;
        }

        var name = source[4..];
        var version = name.LastIndexOf('@');
        return version > 0 ? name[..version] : name;
    }

    private static string Base64(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
}

public static class PiServiceCollectionExtensions
{
    public static IServiceCollection AddPiHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, PiHarness>().AddSingleton<ISessionFormat, PiSessionFormat>();
}
