using DeveAgentManager.Core.Abstractions;
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
}

public static class ClaudeServiceCollectionExtensions
{
    public static IServiceCollection AddClaudeHarness(this IServiceCollection services) =>
        services.AddSingleton<IHarness, ClaudeHarness>().AddSingleton<ISessionFormat, ClaudeSessionFormat>();
}
