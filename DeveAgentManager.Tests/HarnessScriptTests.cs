using DeveAgentManager.Core.Data;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Runs the Claude Code and Codex install scripts, their prepare scripts and the account setup with bash and node, in a
/// temporary home with stub npm/claude/codex commands.
/// </summary>
public sealed class HarnessScriptTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("dam-harness-home-").FullName;
    private readonly string _bin = Directory.CreateTempSubdirectory("dam-harness-bin-").FullName;
    private readonly string _work = Directory.CreateTempSubdirectory("dam-harness-work-").FullName;

    public HarnessScriptTests()
    {
        foreach (var tool in new[] { "npm", "claude", "codex" })
        {
            var stub = Path.Combine(_bin, tool);
            File.WriteAllText(stub, "#!/bin/sh\necho \"stub $*\"\n");
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Claude_Install_SkipsFirstRunQuestions()
    {
        if (!HasTools())
        {
            return;
        }

        Run(new ClaudeHarness().InstallScript);
        Run($"cd {_work} && ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz node \"{ClaudeHarness.PrepareScript}\"");

        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, ".claude.json")))!;
        Assert.True((bool)state["hasCompletedOnboarding"]!);
        Assert.True((bool)state["projects"]![_work]!["hasTrustDialogAccepted"]!);
        Assert.Equal("ghijklmnopqrstuvwxyz", (string?)state["customApiKeyResponses"]!["approved"]![0]);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, ".claude", "settings.json")))!;
        Assert.True((bool)settings["skipDangerousModePermissionPrompt"]!);
    }

    [Fact]
    public void Claude_Launch_ResumesWhenTheConversationExists()
    {
        var command = new ClaudeHarness().BuildLaunchCommand(new("11111111-2222-3333-4444-555555555555", "maze", "/w"));
        Assert.Contains("--resume", command);
        Assert.Contains("--session-id", command);
        Assert.Contains("--dangerously-skip-permissions", command);
    }

    [Fact]
    public void Claude_Accounts_SubscriptionWinsOverAnApiKeyFromTheProfile()
    {
        var harness = new ClaudeHarness();
        var subscription = harness.BuildAccountSetup("$HOME/a", AccountKind.Subscription, " sk-ant-oat01-token ").Environment;
        Assert.Contains("unset ANTHROPIC_API_KEY", subscription);
        Assert.Contains("export CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat01-token", subscription);
        Assert.Contains("export ANTHROPIC_API_KEY=sk-ant-api", harness.BuildAccountSetup("$HOME/a", AccountKind.ApiKey, "sk-ant-api").Environment);
        Assert.Matches(harness.AccountSupport.TokenPattern!, "Your token: sk-ant-oat01-AbC_d-1234567890123456789012 keep it safe");
    }

    [Fact]
    public void Codex_PrepareCreatesTheSession_AndAccountsShareSessions()
    {
        if (!HasTools())
        {
            return;
        }

        var harness = new CodexHarness();
        Run(harness.InstallScript);

        // An account's own CODEX_HOME, linked to the shared sessions and config.
        var folder = Path.Combine(_home, ".deveagentmanager", "accounts", "a1");
        var setup = harness.BuildAccountSetup(folder, AccountKind.ApiKey, "sk-test");
        Assert.Equal("codex/auth.json", setup.Files.Single().Path);
        Assert.Contains("\"OPENAI_API_KEY\":\"sk-test\"", setup.Files.Single().Content);
        Run(setup.Script!);
        Assert.Equal(Path.Combine(_home, ".codex", "sessions"), new DirectoryInfo(Path.Combine(folder, "codex", "sessions")).LinkTarget);

        var id = harness.NewSessionId(Guid.NewGuid());
        Run($"cd {_work} && {setup.Environment.Replace("\n", "; ")} node \"{CodexHarness.PrepareScript}\" {id} && node \"{CodexHarness.PrepareScript}\" {id}");

        // Created once, in the shared folder, where CodexSessionFormat looks.
        var files = Directory.GetFiles(Path.Combine(_home, ".codex", "sessions"), $"rollout-*-{id}.jsonl", SearchOption.AllDirectories);
        var meta = JsonNode.Parse(File.ReadAllText(Assert.Single(files)))!;
        Assert.Equal(("session_meta", id, _work), ((string?)meta["type"], (string?)meta["payload"]!["id"], (string?)meta["payload"]!["cwd"]));
        var config = File.ReadAllText(Path.Combine(_home, ".codex", "config.toml"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(config, System.Text.RegularExpressions.Regex.Escape($"[projects.\"{_work}\"]")));
        Assert.Contains("trust_level = \"trusted\"", config);
    }

    private void Run(string script)
    {
        var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["HOME"] = _home;
        start.Environment["PATH"] = _bin + ":" + Environment.GetEnvironmentVariable("PATH");
        start.Environment.Remove("CODEX_HOME");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {output}");
    }

    private static bool HasTools() => !OperatingSystem.IsWindows() && Which("bash") && Which("node");

    private static bool Which(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sh", ["-c", $"command -v {tool}"]) { RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _home, _bin, _work })
        {
            Directory.Delete(dir, true);
        }
    }
}
