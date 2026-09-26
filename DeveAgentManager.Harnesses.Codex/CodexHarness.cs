using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
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
        // Creates the session file for the id when there is none, and trusts the working directory.
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
        if (!toml.includes(table)) {
          fs.appendFileSync(config, `${toml.length && !toml.endsWith("\n") ? "\n" : ""}\n${table}\ntrust_level = "trusted"\n`);
        }
        DAM_PREPARE
        """;

    public string VersionCommand => "codex --version | awk '{print $NF}'";

    /// <summary>Codex's own ids are UUIDv7; a new one per conversation.</summary>
    public string NewSessionId(Guid agentId) => Guid.CreateVersion7().ToString();

    public string BuildLaunchCommand(HarnessLaunch launch)
    {
        var id = ShellQuote.Quote(launch.SessionId);
        var script = $"""
            node "{PrepareScript}" {id}
            exec codex --dangerously-bypass-approvals-and-sandbox resume {id}
            """;
        return "bash -c " + ShellQuote.Quote(script);
    }

    public IReadOnlyList<string> ConfigFiles { get; } = ["~/.codex/config.toml", "~/.codex/AGENTS.md"];

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
