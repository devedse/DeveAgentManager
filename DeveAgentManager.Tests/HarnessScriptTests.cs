using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using DeveAgentManager.Harnesses.Grok;
using DeveAgentManager.Core.Abstractions;
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
        foreach (var tool in new[] { "npm", "claude", "codex", "grok" })
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
        Assert.DoesNotContain("--model", command); // Ordinary restarts retain the session model.
    }

    [Theory]
    [InlineData(false, "opus", "opus")]
    [InlineData(true, "opus", "opus")]
    [InlineData(true, null, "default")]
    [InlineData(true, "opus[1m]", "opus[1m]")]
    [InlineData(true, "model with spaces; $(touch injected)", "model with spaces; $(touch injected)")]
    public void Claude_TransferLaunch_SelectsExplicitModelForNewAndResumedChats(bool exists, string? model, string expected)
    {
        if (!HasTools()) return;
        const string id = "11111111-2222-3333-4444-555555555555";
        if (exists)
        {
            var directory = Path.Combine(_home, ".claude", "projects", "test");
            Directory.CreateDirectory(directory);
            // This is the compatibility model written for non-Anthropic assistant messages during conversion.
            var imported = new DeveSession { Entries = [new() { Kind = DeveEntryKind.Message, Role = DeveRole.Assistant,
                Content = [DeveBlock.OfText("Previous Codex response")], Model = new() { Provider = "openai", Id = "gpt-6" } }] };
            File.WriteAllText(Path.Combine(directory, id + ".jsonl"), new ClaudeSessionFormat().Write(imported, id, _work));
        }
        File.WriteAllText(Path.Combine(_bin, "claude"), "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$HOME/launch-args\"\n");
        Run(new ClaudeHarness().BuildLaunchCommand(new(id, "test", _work, model, Transferred: true)));
        var args = File.ReadAllLines(Path.Combine(_home, "launch-args"));
        Assert.Contains(exists ? "--resume" : "--session-id", args);
        var position = Array.IndexOf(args, "--model");
        Assert.True(position >= 0);
        Assert.Equal(expected, args[position + 1]);
        Assert.Single(args, a => a == "--model");
        Assert.False(File.Exists(Path.Combine(_work, "injected")));
    }

    [Theory]
    [InlineData("claude", "--append-system-prompt")]
    [InlineData("codex", "-c")]
    [InlineData("pi", "--append-system-prompt")]
    [InlineData("grok", "--rules")]
    public void Launch_PassesInstructionsUnchanged(string harnessId, string flag)
    {
        if (!HasTools() || !Which("python3")) return;
        const string hint = "Run `dam-tools share-image <file> --caption \"x\"`; it's $(touch injected) C:\\tmp\nsecond line";
        IHarness harness = harnessId switch { "claude" => new ClaudeHarness(), "codex" => new CodexHarness(), "grok" => new GrokHarness(), _ => new PiHarness() };
        if (harnessId == "grok") Run(harness.InstallScript);
        var stub = Path.Combine(_bin, harnessId);
        // NUL-separated, so arguments with newlines stay whole. Codex's version check runs it with --help first.
        File.WriteAllText(stub, "#!/bin/sh\n[ \"$1\" = --help ] && exit 0\nprintf '%s\\0' \"$@\" > \"$HOME/launch-args\"\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Run($"cd {_work} && {harness.BuildLaunchCommand(new("11111111-2222-3333-4444-555555555555", "test", _work, Instructions: hint))}");

        var args = File.ReadAllText(Path.Combine(_home, "launch-args")).TrimEnd('\0').Split('\0');
        var value = args[Array.IndexOf(args, flag) + 1];
        if (harnessId == "codex")
        {
            Assert.StartsWith("developer_instructions=", value);
            value = Python("import sys, tomllib; sys.stdout.write(tomllib.loads(sys.argv[1])['developer_instructions'])", value);
        }

        Assert.Equal(hint, value);
        Assert.False(File.Exists(Path.Combine(_work, "injected")));
        Assert.DoesNotContain(flag == "-c" ? "developer_instructions" : flag,
            harness.BuildLaunchCommand(new("11111111-2222-3333-4444-555555555555", "test", _work)));
    }

    private static string Python(string code, string argument)
    {
        using var process = Process.Start(new ProcessStartInfo("python3", ["-c", code, argument]) { RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Claude_GivesTheEffortAtEveryStart_AndTheModelWhenAConversationStarts(bool exists)
    {
        if (!HasTools()) return;
        const string id = "11111111-2222-3333-4444-555555555555";
        if (exists)
        {
            var directory = Path.Combine(_home, ".claude", "projects", "test");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, id + ".jsonl"), "");
        }

        // Claude restores a resumed conversation's model but not its effort, and ignores a top-level effortLevel in
        // settings.json for current models.
        var args = Launch(new ClaudeHarness(), new(id, "test", _work, "opus", Effort: "xhigh"), "  --effort <level>  Effort level for the current session");
        Assert.Contains(exists ? "--resume" : "--session-id", args);
        Assert.Equal("xhigh", args[Array.IndexOf(args, "--effort") + 1]);
        Assert.Equal(!exists, args.Contains("--model"));

        // A version without the flag would refuse to start with it.
        Assert.DoesNotContain("--effort", Launch(new ClaudeHarness(), new(id, "test", _work, "opus", Effort: "xhigh"), ""));
    }

    [Fact]
    public void Codex_GivesTheDefaultsUntilTheConversationHasATurn()
    {
        if (!HasTools()) return;
        var harness = new CodexHarness();
        Run(harness.InstallScript);
        var id = harness.NewSessionId(Guid.NewGuid());
        var launch = new HarnessLaunch(id, "test", _work, "gpt-6.1-sol", Effort: "xhigh");

        // Every start is a resume, a new conversation's too, and without a recorded turn Codex would use its own default
        // effort instead of config.toml's.
        Assert.Equal(["-m", "gpt-6.1-sol", "-c", "model_reasoning_effort=\"xhigh\"", "resume", id], Launch(harness, launch, "")[^6..]);
        Assert.Equal(["-c", "model_reasoning_effort=\"xhigh\"", "resume", id], Launch(harness, launch with { Model = null }, "")[^4..]);

        // After a turn it keeps the conversation's own model and effort, which /model may have changed.
        var file = Directory.GetFiles(Path.Combine(_home, ".codex", "sessions"), $"rollout-*-{id}.jsonl", SearchOption.AllDirectories).Single();
        File.AppendAllText(file, """{"timestamp":"2026-10-25T10:00:01.000Z","type":"turn_context","payload":{"model":"gpt-6.1-sol","effort":"medium"}}""" + "\n");
        Assert.Equal(["--dangerously-bypass-approvals-and-sandbox", "resume", id], Launch(harness, launch, ""));

        // Converted from another harness, earlier Codex turns of the conversation don't decide.
        Assert.Equal(["-m", "gpt-6.1-sol", "-c", "model_reasoning_effort=\"xhigh\"", "resume", id], Launch(harness, launch with { Transferred = true }, "")[^6..]);
    }

    [Fact]
    public void Pi_GivesTheDefaultsWhenAConversationStarts()
    {
        if (!HasTools()) return;
        const string id = "11111111-2222-3333-4444-555555555555";
        var launch = new HarnessLaunch(id, "test", _work, "claude-opus-5-5", Effort: "high", Provider: "anthropic");
        string[] defaults = ["--provider", "anthropic", "--model", "claude-opus-5-5", "--thinking", "high"];
        Assert.Equal(defaults, Launch(new PiHarness(), launch, "")[^6..]);

        // pi resumes with the model and thinking level recorded in the conversation.
        var sessions = Path.Combine(_home, ".pi", "agent", "sessions", "--work--");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, $"2026-10-25T10-00-00-000Z_{id}.jsonl"), "");
        Assert.DoesNotContain("--model", Launch(new PiHarness(), launch, ""));
        Assert.Equal(defaults, Launch(new PiHarness(), launch with { Transferred = true }, "")[^6..]);
    }

    [Fact]
    public void Grok_GivesTheModelWhenAConversationStarts_AndTheEffortAtEveryStart()
    {
        if (!HasTools() || !Which("python3")) return;
        var harness = new GrokHarness();
        Run(harness.InstallScript);
        var id = harness.NewSessionId(Guid.NewGuid());
        var launch = new HarnessLaunch(id, "test", _work, "grok-4.7", Effort: "high");
        const string help = "      --reasoning-effort <EFFORT>";
        Assert.Equal(["--session-id", id, "--model", "grok-4.7", "--reasoning-effort", "high"], Launch(harness, launch, help)[^6..]);

        Directory.CreateDirectory(Path.Combine(_home, ".grok", "sessions", "work", id));
        Assert.Equal(["--resume", id, "--reasoning-effort", "high"], Launch(harness, launch, help)[^4..]);
        Assert.Equal(["--resume", id, "--model", "grok-4.7", "--reasoning-effort", "high"], Launch(harness, launch with { Transferred = true }, help)[^6..]);
        // A version without the flag would refuse to start with it.
        Assert.Equal(["--resume", id], Launch(harness, launch, "")[^2..]);
    }

    /// <summary>Starts the harness's launch command with a stub that records its arguments; <paramref name="help"/> is what its --help shows.</summary>
    private string[] Launch(IHarness harness, HarnessLaunch launch, string help)
    {
        var stub = Path.Combine(_bin, harness.Id);
        // NUL-separated, so arguments with spaces stay whole.
        File.WriteAllText(stub, $"#!/bin/sh\nif [ \"$1\" = --help ]; then printf '%s\\n' {DeveAgentManager.Core.Ssh.ShellQuote.Quote(help)}; exit 0; fi\nprintf '%s\\0' \"$@\" > \"$HOME/launch-args\"\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Run($"cd {_work} && {harness.BuildLaunchCommand(launch)}");
        return File.ReadAllText(Path.Combine(_home, "launch-args")).TrimEnd('\0').Split('\0');
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
        // Codex's own update question would stop the agent at start; the Manager updates it. Once, before the tables.
        Assert.StartsWith("check_for_update_on_startup = false\n", config);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(config, "check_for_update_on_startup"));
    }

    [Fact]
    public void Claude_Configure_SetsAndClearsTheModel()
    {
        if (!HasTools())
        {
            return;
        }

        var harness = new ClaudeHarness();
        var settings = Path.Combine(_home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, """{ "skipDangerousModePermissionPrompt": true, "env": { "A": "1" } }""");

        Run(harness.BuildConfigureScript(new Core.Setup.HarnessConfig { DefaultModel = "opus", DefaultThinkingLevel = "high", Settings = """{ "env": { "B": "2" } }""" }));
        var data = JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal(("opus", "high"), ((string?)data["model"], (string?)data["effortLevel"]));
        Assert.Equal(("1", "2"), ((string?)data["env"]!["A"], (string?)data["env"]!["B"]));
        Assert.True((bool)data["skipDangerousModePermissionPrompt"]!);

        // Empty: Claude Code chooses again.
        Run(harness.BuildConfigureScript(new Core.Setup.HarnessConfig()));
        data = JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Null(data["model"]);
        Assert.Null(data["effortLevel"]);
        Assert.True((bool)data["skipDangerousModePermissionPrompt"]!);
    }

    [Fact]
    public void Codex_Configure_PutsTheModelAboveTheTables()
    {
        if (!HasTools())
        {
            return;
        }

        var harness = new CodexHarness();
        var config = Path.Combine(_home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "model = \"old\"\n[tui]\nscreen_reader_detection_done = true\n\n[projects.\"/w\"]\ntrust_level = \"trusted\"\n");

        Run(harness.BuildConfigureScript(new Core.Setup.HarnessConfig { DefaultModel = "gpt-6-sol", DefaultThinkingLevel = "high" }));
        var lines = File.ReadAllLines(config);
        Assert.Equal("model = \"gpt-6-sol\"", lines[0]);
        Assert.Equal("model_reasoning_effort = \"high\"", lines[1]);
        Assert.Single(lines, l => l.StartsWith("model ", StringComparison.Ordinal));
        Assert.Contains("[projects.\"/w\"]", lines);
        Assert.True(Array.IndexOf(lines, "[tui]") > 1);

        Run(harness.BuildConfigureScript(new Core.Setup.HarnessConfig()));
        var text = File.ReadAllText(config);
        Assert.DoesNotContain("model", text);
        Assert.Contains("trust_level = \"trusted\"", text);
    }

    [Fact]
    public void Codex_ParseModels_ReadsTheCatalog()
    {
        var output = "warning: something\n" + """{"models":[{"slug":"gpt-6-sol","display_name":"GPT-6-Sol","visibility":"list","default_reasoning_level":"medium","supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"ultra"}]},{"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide"}]}""";
        var model = Assert.Single(new CodexHarness().ParseModels(output));
        Assert.Equal(("gpt-6-sol", "GPT-6-Sol", "medium"), (model.Id, model.Name, model.DefaultEffort));
        Assert.Equal(["low", "medium", "ultra"], model.Efforts);
    }

    private void Run(string script)
    {
        var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["HOME"] = _home;
        start.Environment["PATH"] = _bin + ":" + Environment.GetEnvironmentVariable("PATH");
        // An agent running these tests has its own harness homes; the scripts must use this test's.
        foreach (var home in new[] { "CODEX_HOME", "CLAUDE_CONFIG_DIR", "GROK_HOME", "PI_CODING_AGENT_DIR" }) start.Environment.Remove(home);
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
