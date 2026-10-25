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

    /// <summary>The Claude logo (Simple Icons, CC0) in Claude's orange.</summary>
    public string Icon => """<path fill="#D97757" d="m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z"/>""";

    public string Description => "Anthropic's Claude Code. Connect a Claude subscription under My settings → Harness logins.";

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

    /// <summary>
    /// Claude Code's spinner line, "✢ Clauding… (5s · ↓ 77 tokens)" (idle it becomes "✻ Cooked for 12s · done").
    /// Require the spinner at the start of the line: quoted busy phrases in prompts and replies aren't status lines.
    /// This also works on narrow screens where the "esc to interrupt" footer is cut off.
    /// </summary>
    public string BusyPattern => "^[[:space:]]*(✢|✳|✶|✻|✽|\\*|·) .+… \\([0-9]+[smh]";

    public string NewSessionId(Guid agentId) => agentId.ToString();

    /// <summary>
    /// <c>--session-id</c> only creates a conversation ("already in use" otherwise), so a restart uses <c>--resume</c>.
    /// A resumed conversation keeps its model (Claude restores it from the transcript), except right after a switch from
    /// another harness: the converted messages carry a compatibility model, so the default is given ("default": the
    /// login's own). The effort is given at every start: Claude doesn't restore it, and only reads a top-level
    /// <c>effortLevel</c> in settings.json for older models.
    /// </summary>
    public string BuildLaunchCommand(HarnessLaunch launch)
    {
        var id = ShellQuote.Quote(launch.SessionId);
        var name = ShellQuote.Quote(launch.DisplayName);
        var model = string.IsNullOrWhiteSpace(launch.Model) ? "" : " --model " + ShellQuote.Quote(launch.Model.Trim());
        if (launch.Transferred && model.Length == 0) model = " --model default";
        var resumed = launch.Transferred ? model : "";
        var instructions = string.IsNullOrWhiteSpace(launch.Instructions) ? "" : " --append-system-prompt " + ShellQuote.Quote(launch.Instructions);
        // Versions before the flag would refuse to start with it.
        var effort = string.IsNullOrWhiteSpace(launch.Effort) ? ""
            : $"\nif claude --help 2>/dev/null | grep -q -- '--effort'; then effort=(--effort {ShellQuote.Quote(launch.Effort.Trim())}); fi";
        var script = $$"""
            node "{{PrepareScript}}" 2>/dev/null || true
            effort=(){{effort}}
            if ls "${CLAUDE_CONFIG_DIR:-$HOME/.claude}"/projects/*/{{id}}.jsonl >/dev/null 2>&1; then
              exec claude --dangerously-skip-permissions --resume {{id}} --name {{name}}{{resumed}} "${effort[@]}"{{instructions}}
            fi
            exec claude --dangerously-skip-permissions --session-id {{id}} --name {{name}}{{model}} "${effort[@]}"{{instructions}}
            """;
        return "bash -c " + ShellQuote.Quote(script);
    }

    public IReadOnlyList<string> ConfigFiles { get; } = ["~/.claude/settings.json", "~/.claude/CLAUDE.md"];

    /// <summary>Manager settings → Claude Code: the default model and effort (empty: Claude Code chooses), and extra settings.</summary>
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
            // A key from the user's environment (for pi) would win over the subscription: this agent uses the subscription.
            ? $"unset ANTHROPIC_API_KEY\nexport CLAUDE_CODE_OAUTH_TOKEN={ShellQuote.Quote(secret.Trim())}\n"
            : $"unset CLAUDE_CODE_OAUTH_TOKEN\nexport ANTHROPIC_API_KEY={ShellQuote.Quote(secret.Trim())}\n",
        null);
}

public static class ClaudeServiceCollectionExtensions
{
    public static IServiceCollection AddClaudeHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, ClaudeHarness>().AddSingleton<ISessionFormat, ClaudeSessionFormat>();
}
