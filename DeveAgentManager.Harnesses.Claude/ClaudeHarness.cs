using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace DeveAgentManager.Harnesses.Claude;

/// <summary>
/// Claude Code (npm package @anthropic-ai/claude-code). Runs with --dangerously-skip-permissions: Runners are
/// throwaway VMs, and an agent that stops to ask for every command is no use when nobody is watching.
/// </summary>
public sealed class ClaudeHarness : IHarness
{
    public const string HarnessId = "claude";
    public const string NpmPackage = "@anthropic-ai/claude-code";

    /// <summary>Sets what Claude Code asks on its first start (theme, trusting the folder), so agents start straight away.</summary>
    public const string PrepareScript = "$HOME/.deveagentmanager/claude-prepare.js";

    public string Id => HarnessId;

    public string DisplayName => "Claude Code";

    public string Description => "Anthropic's Claude Code. Connect a Claude subscription under Settings → Accounts.";

    public string InstallScript => $$"""
        set -euo pipefail
        echo "==> Installing Claude Code ({{NpmPackage}})"
        npm install -g --no-fund --no-audit {{NpmPackage}}@latest
        claude --version
        mkdir -p "$HOME/.deveagentmanager" "$HOME/.claude"
        cat > "{{PrepareScript}}" <<'DAM_PREPARE'
        // Written by DeveAgentManager: skips Claude Code's first-run questions for the working directory.
        const fs = require("fs");
        const os = require("os");
        const path = require("path");
        function update(file, change) {
          let data = {};
          try { data = JSON.parse(fs.readFileSync(file, "utf8")); } catch {}
          change(data);
          fs.mkdirSync(path.dirname(file), { recursive: true });
          fs.writeFileSync(file, JSON.stringify(data, null, 2));
        }
        update(path.join(os.homedir(), ".claude.json"), data => {
          data.hasCompletedOnboarding = true;
          data.theme ??= "dark";
          data.projects ??= {};
          const cwd = process.cwd();
          data.projects[cwd] = { ...(data.projects[cwd] ?? {}), hasTrustDialogAccepted: true };
        });
        const apiKey = process.env.ANTHROPIC_API_KEY;
        if (apiKey) {
          // Claude Code asks once whether to use an API key from the environment; the account is the answer.
          update(path.join(os.homedir(), ".claude.json"), data => {
            data.customApiKeyResponses ??= { approved: [], rejected: [] };
            const suffix = apiKey.slice(-20);
            if (!data.customApiKeyResponses.approved.includes(suffix)) data.customApiKeyResponses.approved.push(suffix);
            data.customApiKeyResponses.rejected = (data.customApiKeyResponses.rejected ?? []).filter(s => s !== suffix);
          });
        }
        update(path.join(os.homedir(), ".claude", "settings.json"), data => {
          // The warning that --dangerously-skip-permissions shows once, on every new Runner.
          data.skipDangerousModePermissionPrompt = true;
        });
        DAM_PREPARE
        node "{{PrepareScript}}"
        """;

    public string VersionCommand => "claude --version | cut -d' ' -f1";

    public string NewSessionId(Guid agentId) => agentId.ToString();

    /// <summary><c>--session-id</c> only creates a conversation ("already in use" otherwise), so a restart uses <c>--resume</c>.</summary>
    public string BuildLaunchCommand(HarnessLaunch launch)
    {
        var id = ShellQuote.Quote(launch.SessionId);
        var name = ShellQuote.Quote(launch.DisplayName);
        var script = $"""
            node "{PrepareScript}" 2>/dev/null || true
            if ls "$HOME"/.claude/projects/*/{id}.jsonl >/dev/null 2>&1; then
              exec claude --dangerously-skip-permissions --resume {id} --name {name}
            fi
            exec claude --dangerously-skip-permissions --session-id {id} --name {name}
            """;
        return "bash -c " + ShellQuote.Quote(script);
    }

    public IReadOnlyList<string> ConfigFiles { get; } = ["~/.claude/settings.json", "~/.claude/CLAUDE.md"];

    /// <summary>Settings → Claude Code: the default model and effort (empty: Claude Code chooses), and extra settings.</summary>
    public HarnessConfigSupport ConfigSupport { get; } = new(
        PackageExample: "",
        SuggestedPackages: [],
        SettingsFile: "~/.claude/settings.json",
        KeybindingsFile: "",
        // What settings.json can hold; "max" is only for a session (--effort).
        ThinkingLevels: ["low", "medium", "high", "xhigh"],
        DocsUrl: "https://code.claude.com/docs/en/settings",
        HasPackages: false,
        HasProvider: false,
        HasKeybindings: false,
        HasExtraSettings: true,
        // Aliases always mean the latest model of that family.
        ModelSuggestions:
        [
            new("opus", "latest Opus", []),
            new("sonnet", "latest Sonnet", []),
            new("haiku", "latest Haiku", []),
            new("fable", "latest Fable", []),
        ],
        EffortName: "Effort",
        SettingsExample: "{ \"includeCoAuthoredBy\": false, \"cleanupPeriodDays\": 60 }");

    /// <summary>
    /// Writes the model and effort into ~/.claude/settings.json (removing them when empty, so Claude Code chooses again)
    /// and merges the extra settings.
    /// </summary>
    public string BuildConfigureScript(HarnessConfig config)
    {
        config.Validate();
        var wanted = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = Blank(config.DefaultModel),
            ["effortLevel"] = Blank(config.DefaultThinkingLevel),
            ["extra"] = string.IsNullOrWhiteSpace(config.Settings) ? new System.Text.Json.Nodes.JsonObject() : System.Text.Json.Nodes.JsonNode.Parse(config.Settings),
        };
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(wanted.ToJsonString()));
        return $$"""
            set -euo pipefail
            echo "==> Writing Claude Code settings"
            node - {{encoded}} <<'DAM_SETTINGS'
            const fs = require("fs");
            const os = require("os");
            const path = require("path");
            const wanted = JSON.parse(Buffer.from(process.argv[2], "base64").toString("utf8"));
            const file = path.join(os.homedir(), ".claude", "settings.json");
            let data = {};
            try { data = JSON.parse(fs.readFileSync(file, "utf8")); } catch {}
            const isObject = value => value !== null && typeof value === "object" && !Array.isArray(value);
            const merge = (target, source) => {
              for (const [key, value] of Object.entries(source)) {
                target[key] = isObject(value) && isObject(target[key]) ? merge(target[key], value) : value;
              }
              return target;
            };
            merge(data, wanted.extra ?? {});
            for (const key of ["model", "effortLevel"]) {
              if (wanted[key]) data[key] = wanted[key]; else delete data[key];
            }
            fs.mkdirSync(path.dirname(file), { recursive: true });
            fs.writeFileSync(file, JSON.stringify(data, null, 2) + "\n");
            DAM_SETTINGS
            """;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A subscription logs in with <c>claude setup-token</c>: a token valid for a year that works on every Runner, so
    /// Runners never need a login of their own. The terminal is made very wide, so the token is printed on one line.
    /// </summary>
    public HarnessAccountSupport AccountSupport { get; } = new(
        SubscriptionName: "Claude subscription (Pro, Max)",
        LoginCommand: "stty cols 500 2>/dev/null; claude setup-token",
        LoginHelp: "Open the link below (tap it, or select and copy it), sign in with your Claude account and approve. Copy the code Claude shows, paste it here (Ctrl+V, or long-press on a phone) and press Enter. The token Claude prints at the end is picked up automatically: it is valid for a year and works on every Runner.",
        TokenPattern: @"sk-ant-oat01-[A-Za-z0-9_\-]{20,}",
        LoginFile: null,
        ApiKeyName: "ANTHROPIC_API_KEY",
        PasteHint: "Already have a token? Run claude setup-token anywhere and paste the token it prints (sk-ant-oat01-…).");

    public HarnessAccountSetup BuildAccountSetup(string accountFolder, AccountKind kind, string secret) => new(
        [],
        kind == AccountKind.Subscription
            // A key from the Setup Profile (for pi) would win over the subscription: this agent uses the subscription.
            ? $"unset ANTHROPIC_API_KEY\nexport CLAUDE_CODE_OAUTH_TOKEN={ShellQuote.Quote(secret.Trim())}\n"
            : $"unset CLAUDE_CODE_OAUTH_TOKEN\nexport ANTHROPIC_API_KEY={ShellQuote.Quote(secret.Trim())}\n",
        null);
}

public static class ClaudeServiceCollectionExtensions
{
    public static IServiceCollection AddClaudeHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, ClaudeHarness>().AddSingleton<ISessionFormat, ClaudeSessionFormat>();
}
