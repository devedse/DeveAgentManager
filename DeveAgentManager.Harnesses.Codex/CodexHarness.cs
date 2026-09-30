using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace DeveAgentManager.Harnesses.Codex;

/// <summary>
/// OpenAI's Codex CLI (npm package @openai/codex). Runs with --dangerously-bypass-approvals-and-sandbox: Runners are
/// throwaway VMs, and an agent that stops to ask for every command is no use when nobody is watching.
/// </summary>
public sealed class CodexHarness : IHarness
{
    public const string HarnessId = "codex";
    public const string NpmPackage = "@openai/codex";

    /// <summary>
    /// Codex picks its own session id when it starts a conversation. So the Manager creates the session file (just its
    /// header) under the agent's id first, and every start, the first one too, is <c>codex resume &lt;id&gt;</c>.
    /// The script also marks the working directory as trusted, so Codex doesn't ask.
    /// </summary>
    public const string PrepareScript = "$HOME/.deveagentmanager/codex-prepare.js";

    /// <summary>The Codex CLI version written into session headers the Manager creates.</summary>
    public const string WrittenVersion = "0.157.1";

    public string Id => HarnessId;

    public string DisplayName => "Codex";

    /// <summary>The OpenAI logo (Simple Icons, CC0), in the text color.</summary>
    public string Icon => """<path d="M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z"/>""";

    public string Description => "OpenAI's Codex CLI. Connect a ChatGPT subscription under Settings → Accounts.";

    public string InstallScript => $$"""
        set -euo pipefail
        echo "==> Installing Codex ({{NpmPackage}})"
        npm install -g --no-fund --no-audit {{NpmPackage}}@latest
        codex --version
        mkdir -p "$HOME/.deveagentmanager" "$HOME/.codex/sessions"
        touch "$HOME/.codex/config.toml"
        cat > "{{PrepareScript}}" <<'DAM_PREPARE'
        // Written by DeveAgentManager: node codex-prepare.js <session-id>
        // Creates the session file for the id when there is none, trusts the working directory, and turns off Codex's
        // update question at start (the Manager updates Codex: Update Codex on the Runner page).
        const fs = require("fs");
        const os = require("os");
        const path = require("path");
        const id = process.argv[2];
        const home = process.env.CODEX_HOME || path.join(os.homedir(), ".codex");
        const sessions = path.join(home, "sessions");
        function find(dir) {
          let items = [];
          try { items = fs.readdirSync(dir, { withFileTypes: true }); } catch { return null; }
          for (const item of items) {
            const full = path.join(dir, item.name);
            if (item.isDirectory()) {
              const found = find(full);
              if (found) return found;
            } else if (item.name.endsWith(`-${id}.jsonl`)) {
              return full;
            }
          }
          return null;
        }
        if (!find(sessions)) {
          const now = new Date();
          const day = path.join(sessions, String(now.getUTCFullYear()), String(now.getUTCMonth() + 1).padStart(2, "0"), String(now.getUTCDate()).padStart(2, "0"));
          fs.mkdirSync(day, { recursive: true });
          const stamp = now.toISOString().slice(0, 19).replace(/:/g, "-");
          const meta = { id, timestamp: now.toISOString(), cwd: process.cwd(), originator: "codex_cli_rs", cli_version: "{{WrittenVersion}}", source: "cli", model_provider: "openai" };
          fs.writeFileSync(path.join(day, `rollout-${stamp}-${id}.jsonl`), JSON.stringify({ timestamp: now.toISOString(), type: "session_meta", payload: meta }) + "\n");
        }
        const config = path.join(home, "config.toml");
        const table = `[projects.${JSON.stringify(process.cwd())}]`;
        let toml = "";
        try { toml = fs.readFileSync(config, "utf8"); } catch {}
        if (!/^\s*check_for_update_on_startup\s*=/m.test(toml)) {
          // A top-level key: before the first table.
          toml = `check_for_update_on_startup = false\n${toml}`;
          fs.writeFileSync(config, toml);
        }
        if (!toml.includes(table)) {
          fs.appendFileSync(config, `${toml.length && !toml.endsWith("\n") ? "\n" : ""}\n${table}\ntrust_level = "trusted"\n`);
        }
        DAM_PREPARE
        """;

    public string VersionCommand => "codex --version | awk '{print $NF}'";

    /// <summary>Codex's timed status line while working, compacting context or waiting for a background terminal, including truncated phone screens.</summary>
    public string BusyPattern => "^[[:space:]]*(◦|•) (Working|Compacting context|Waiting for background terminal) \\([0-9]";

    /// <summary>Codex's own ids are UUIDv7; a new one per conversation.</summary>
    public string NewSessionId(Guid agentId) => Guid.CreateVersion7().ToString();

    public string BuildLaunchCommand(HarnessLaunch launch)
    {
        var id = ShellQuote.Quote(launch.SessionId);
        // Without its background server: that listens on a socket in CODEX_HOME, and an account's folder is too long a
        // path for a socket (108 characters), so Codex would refuse to start. Older Codex versions don't have the flag.
        var script = $"""
            node "{PrepareScript}" {id}
            flags=""
            if codex --help 2>/dev/null | grep -q -- '--no-daemon'; then flags="--no-daemon"; fi
            exec codex $flags --dangerously-bypass-approvals-and-sandbox resume {id}
            """;
        return "bash -c " + ShellQuote.Quote(script);
    }

    public IReadOnlyList<string> ConfigFiles { get; } = ["~/.codex/config.toml", "~/.codex/AGENTS.md"];

    /// <summary>Settings → Codex: the default model and reasoning effort (empty: Codex chooses).</summary>
    public HarnessConfigSupport ConfigSupport { get; } = new(
        PackageExample: "",
        SuggestedPackages: [],
        SettingsFile: "~/.codex/config.toml",
        KeybindingsFile: "",
        ThinkingLevels: ["low", "medium", "high", "xhigh", "max"],
        DocsUrl: "https://developers.openai.com/codex/config-reference",
        HasPackages: false,
        HasProvider: false,
        HasKeybindings: false,
        HasExtraSettings: false,
        EffortName: "Reasoning effort");

    /// <summary>Codex's own model catalog: the models it offers the logged-in account, with their effort levels.</summary>
    public string ModelsCommand => "CODEX_HOME=\"$(mktemp -d)\" codex debug models 2>/dev/null";

    public IReadOnlyList<ModelChoice> ParseModels(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0 || JsonNode.Parse(output[start..]) is not JsonObject catalog || catalog["models"] is not JsonArray models)
        {
            return [];
        }

        return models.OfType<JsonObject>()
            .Where(m => (string?)m["visibility"] != "hide" && m["slug"] is JsonValue)
            .Select(m => new ModelChoice(
                (string)m["slug"]!,
                (string?)m["display_name"] ?? (string)m["slug"]!,
                (m["supported_reasoning_levels"] as JsonArray ?? []).OfType<JsonObject>().Select(l => (string?)l["effort"]).OfType<string>().ToList(),
                (string?)m["default_reasoning_level"]))
            .ToList();
    }

    /// <summary>
    /// Sets <c>model</c> and <c>model_reasoning_effort</c> at the top of ~/.codex/config.toml (removed when empty, so
    /// Codex chooses again). Top-level keys must come before the first table, e.g. the trusted projects.
    /// </summary>
    public string BuildConfigureScript(HarnessConfig config)
    {
        config.Validate();
        var wanted = new JsonObject { ["model"] = Blank(config.DefaultModel), ["model_reasoning_effort"] = Blank(config.DefaultThinkingLevel) };
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(wanted.ToJsonString()));
        return $$"""
            set -euo pipefail
            echo "==> Writing Codex settings"
            mkdir -p "$HOME/.codex"
            node - {{encoded}} <<'DAM_SETTINGS'
            const fs = require("fs");
            const os = require("os");
            const path = require("path");
            const wanted = JSON.parse(Buffer.from(process.argv[2], "base64").toString("utf8"));
            const file = path.join(os.homedir(), ".codex", "config.toml");
            let text = "";
            try { text = fs.readFileSync(file, "utf8"); } catch {}
            const lines = text.split("\n");
            let end = lines.findIndex(line => /^\s*\[/.test(line));
            if (end < 0) end = lines.length;
            const keys = Object.keys(wanted);
            const top = lines.slice(0, end).filter(line => !keys.some(key => new RegExp(`^\\s*${key}\\s*=`).test(line)));
            const set = keys.filter(key => wanted[key]).map(key => `${key} = ${JSON.stringify(wanted[key])}`);
            fs.writeFileSync(file, [...set, ...top, ...lines.slice(end)].join("\n").replace(/^\n+/, ""));
            DAM_SETTINGS
            """;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A subscription logs in with <c>codex login --device-auth</c> into an empty folder; its auth.json is the account.
    /// Each account runs with its own CODEX_HOME (holding that auth.json), which links to the shared sessions folder
    /// and config.toml, so a conversation can switch accounts and keep going.
    /// </summary>
    public HarnessAccountSupport AccountSupport { get; } = new(
        SubscriptionName: "ChatGPT subscription (Plus, Pro, Business)",
        LoginCommand: "CODEX_HOME=\"$DAM_LOGIN_DIR\" codex login --device-auth",
        LoginHelp: "Open the link below on any device (your phone works), sign in to ChatGPT and enter the code shown here. When Codex says you are logged in, the Manager keeps the login. If ChatGPT says device codes are turned off, turn on device code login for Codex in ChatGPT's settings (Security) and try again.",
        TokenPattern: null,
        LoginFile: "auth.json",
        ApiKeyName: "OPENAI_API_KEY",
        PasteHint: null);

    public HarnessAccountSetup BuildAccountSetup(string accountFolder, AccountKind kind, string secret)
    {
        var auth = kind == AccountKind.Subscription
            ? secret
            : new JsonObject { ["auth_mode"] = "apikey", ["OPENAI_API_KEY"] = secret.Trim() }.ToJsonString();
        var home = $"{accountFolder}/codex";
        return new HarnessAccountSetup(
            [("codex/auth.json", auth, "600")],
            $"export CODEX_HOME=\"{home}\"\nunset OPENAI_API_KEY\n",
            $"""
            mkdir -p "$HOME/.codex/sessions" "$HOME/.codex/packages" "{home}"
            touch "$HOME/.codex/config.toml"
            ln -sfn "$HOME/.codex/sessions" "{home}/sessions"
            # Codex installs a helper daemon into packages on its first start: once, not per account.
            ln -sfn "$HOME/.codex/packages" "{home}/packages"
            ln -sf "$HOME/.codex/config.toml" "{home}/config.toml"
            for f in AGENTS.md skills; do
              if [ -e "$HOME/.codex/$f" ]; then ln -sfn "$HOME/.codex/$f" "{home}/$f"; fi
            done
            """);
    }
}

public static class CodexServiceCollectionExtensions
{
    public static IServiceCollection AddCodexHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, CodexHarness>().AddSingleton<ISessionFormat, CodexSessionFormat>();
}
