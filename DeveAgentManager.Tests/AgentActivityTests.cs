using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using Xunit;

namespace DeveAgentManager.Tests;

public class AgentActivityTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 20, 59, 0, DateTimeKind.Utc);
    internal static string Screen(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", name + ".txt"));
    private static Agent NewAgent() => new() { Id = Guid.NewGuid(), Name = "agent", State = AgentState.Running, HarnessId = "codex", UpdatedAt = Now, Runner = new() { Name = "runner", State = RunnerState.Ready } };
    private static AgentScreenDetection Working => new(AgentActivityStatus.Working, "working", "busy");
    private static AgentScreenDetection Ready(string response = "done") => new(AgentActivityStatus.Ready, "ready", "prompt", CompletionEvidence: response);

    [Theory]
    [InlineData("pi-working", AgentActivityStatus.Working)]
    [InlineData("pi-idle", AgentActivityStatus.Ready)]
    [InlineData("claude-working", AgentActivityStatus.Working)]
    [InlineData("claude-working-narrow", AgentActivityStatus.Working)]
    [InlineData("claude-idle", AgentActivityStatus.Ready)]
    [InlineData("claude-idle-narrow", AgentActivityStatus.Ready)]
    [InlineData("codex-working", AgentActivityStatus.Working)]
    [InlineData("codex-background-wait-narrow", AgentActivityStatus.Working)]
    [InlineData("codex-compacting", AgentActivityStatus.Working)]
    [InlineData("codex-idle", AgentActivityStatus.Ready)]
    [InlineData("claude-weekly-limit", AgentActivityStatus.UsageLimited)]
    public void RealScreensHaveNamedRules(string name, AgentActivityStatus status)
    {
        var result = AgentScreenDetector.Detect(name.Split('-')[0], Screen(name), Now);
        Assert.Equal(status, result.Status);
        Assert.Contains("/", result.Rule);
        Assert.NotNull(result.Evidence);
    }

    [Theory]
    [InlineData("› Explain esc to interrupt\n? for shortcuts")]
    [InlineData("• The status says Working (6s • esc to interrupt).\n› Ask Codex to do anything\n? for shortcuts")]
    [InlineData("• Compacted context (1m 42s)\n› Ask Codex to do anything\n? for shortcuts")]
    [InlineData("• Working (6s • esc to interrupt)\n• Done.\n› Ask Codex to do anything\n? for shortcuts")]
    [InlineData("• Working (6s • esc to interrupt)\n› Explain this\n• Done.\n› Ask Codex to do anything\n? for shortcuts")]
    public void ConversationAndDraftsAreNotLiveActivity(string text) => Assert.NotEqual(AgentActivityStatus.Working, AgentScreenDetector.Detect("codex", text, Now).Status);

    [Fact]
    public void HistoricalClaudeSpinnerDoesNotOverrideCompletedPrompt()
    {
        var screen = "❯ do it\n✻ Clauding… (5s · ↓ 77 tokens)\n● Done.\n✻ Crunched for 6s · done 10:58 PM\n─────────\n❯\n─────────\nbypass permissions on (shift+tab to cycle)";
        Assert.Equal(AgentActivityStatus.Ready, AgentScreenDetector.Detect("claude", screen, Now).Status);
    }

    [Fact]
    public void QueuedQuestionDoesNotOverrideWorking()
    {
        var result = AgentScreenDetector.Detect("codex", "• Working (20s • esc to interrupt)\n• Queued follow-up inputs\n? 1 question · 20s\nshift+← to answer\n› Ask Codex to do anything\n? for shortcuts", Now);
        Assert.Equal(AgentActivityStatus.Working, result.Status);
        Assert.True(result.QuestionWaiting);
    }

    [Theory]
    [InlineData("codex", "› Question\nEnter to submit answer · Esc to cancel")]
    [InlineData("claude", "─────────\nBash command\nDo you want to proceed?\n❯ 1. Yes\n2. No\nEsc to cancel")]
    [InlineData("codex", "Would you like to run the following command?\n› 1. Yes\n2. No\nPress enter to confirm or esc to cancel")]
    public void LiveQuestionsAndApprovalsNeedInput(string harness, string screen) => Assert.Equal(AgentActivityStatus.NeedsInput, AgentScreenDetector.Detect(harness, screen, Now).Status);

    [Fact]
    public void QuotedApprovalAboveReadyComposerIsNotBlocked()
    {
        var screen = "Do you want to proceed?\n❯ 1. Yes\n2. No\nEsc to cancel\n─────────\n❯\n─────────\nbypass permissions on (shift+tab to cycle)";
        Assert.Equal(AgentActivityStatus.Ready, AgentScreenDetector.Detect("claude", screen, Now).Status);
    }

    [Fact]
    public void WeeklyLimitSurvivesCompletionFooterAndUsesExplicitTimezone()
    {
        var parsed = UsageLimitParser.Parse("claude", Screen("claude-weekly-limit"), "UTC", Now);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc), parsed.ResetAt);
        Assert.Equal("Europe/Amsterdam", parsed.TimeZoneId);
        Assert.DoesNotContain("Crunched", parsed.Message);
        Assert.False(parsed.NativeAutoContinue);
        Assert.Null(UsageLimitParser.Parse("claude", Screen("claude-weekly-limit").Replace("✻ Crunched", "● Resumed\n✻ Crunched"), "UTC", Now));
        Assert.Null(UsageLimitParser.Parse("claude", Screen("claude-weekly-limit").Replace("✻ Crunched for 0s · done 10:58 PM", "✻ Clauding… (5s · ↓ 77 tokens)"), "UTC", Now));
    }

    [Fact]
    public void CompletionRequiresObservedTurnAndStableNewResponse()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Ready("old"), "process", Now);
        Assert.False(AgentActivityTracker.GetDto(agent, Now).UnseenCompletion);
        AgentActivityTracker.Apply(agent, Working, "process", Now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, Ready(), "process", Now.AddSeconds(10));
        Assert.True(agent.Working);
        AgentActivityTracker.Apply(agent, Ready("still changing"), "process", Now.AddSeconds(15));
        Assert.True(agent.Working);
        AgentActivityTracker.Apply(agent, Ready("still changing"), "process", Now.AddSeconds(20));
        var done = AgentActivityTracker.GetDto(agent, Now.AddSeconds(20));
        Assert.True(done.UnseenCompletion);
        Assert.Equal(AgentActivityStatus.Ready, done.Status);
        Assert.Equal(Now.AddSeconds(20), done.CompletedAt);
        AgentActivityTracker.Apply(agent, Ready("still changing"), "process", Now.AddSeconds(25));
        Assert.Equal(done.CompletionSequence, AgentActivityTracker.GetDto(agent, Now.AddSeconds(25)).CompletionSequence);
    }

    [Fact]
    public void RedrawGapAndEditablePromptCannotFinishTurn()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        var draft = new AgentScreenDetection(AgentActivityStatus.Ready, "draft", "prompt");
        AgentActivityTracker.Apply(agent, draft, "process", Now.AddSeconds(5));
        Assert.True(agent.Working);
        AgentActivityTracker.Apply(agent, draft, "process", Now.AddSeconds(15));
        Assert.Equal(AgentActivityStatus.Unknown, AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).Status);
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).UnseenCompletion);
    }

    [Fact]
    public void RestartsAndStaleObservationsDoNotEstablishCompletion()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        AgentActivityTracker.Apply(agent, Ready(), "new-process", Now.AddSeconds(5));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(5)).UnseenCompletion);
        Assert.Equal(AgentActivityStatus.Unknown, AgentActivityTracker.GetDto(agent, Now.AddMinutes(1)).Status);
        AgentActivityTracker.Reset(agent);
        AgentActivityTracker.Apply(agent, Working, "next", Now.AddMinutes(1));
        AgentActivityTracker.Apply(agent, Ready(), "next", Now.AddMinutes(1).AddSeconds(5));
        AgentActivityTracker.Apply(agent, Ready(), "next", Now.AddMinutes(1).AddSeconds(10));
        Assert.True(AgentActivityTracker.GetDto(agent, Now.AddMinutes(1).AddSeconds(10)).UnseenCompletion);
    }

    [Fact]
    public void PaneParsingKeepsJsonOutOfLegacyBusyList()
    {
        var output = "123\nagent|0||123\n--busy--\n--screens--\n{\"Session\":\"agent\",\"Identity\":\"pane\",\"Screen\":\"hello\\nworld\"}\ninvalid\n";
        Assert.Empty(AgentService.ParsePanes(output).Busy);
        Assert.Equal("hello\nworld", AgentService.ParseScreens(output)["agent"].Screen);
    }
    [Fact]
    public async Task SnapshotScriptCapturesJoinedUnicodeScreenAndProcessIdentity()
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("dam-observation-").FullName;
        try
        {
            var tmux = Path.Combine(folder, "tmux");
            File.WriteAllText(tmux, $"#!/bin/sh\ncase \"$1\" in list-panes) echo 'agent|0||123' ;; display-message) echo '%1|{Environment.ProcessId}' ;; capture-pane) printf '• done\\n❯\\n' ;; esac\n");
            File.SetUnixFileMode(tmux, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new System.Diagnostics.ProcessStartInfo("bash", ["-c", AgentService.BuildStateScript([("agent", null)], captureScreens: true)]) { RedirectStandardOutput = true };
            start.Environment["PATH"] = folder + ":" + Environment.GetEnvironmentVariable("PATH");
            start.Environment["LC_ALL"] = "C";
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            var snapshot = AgentService.ParseScreens(output)["agent"];
            Assert.Equal("• done\n❯\n", snapshot.Screen);
            Assert.StartsWith($"%1|{Environment.ProcessId}|", snapshot.Identity);
        }
        finally { Directory.Delete(folder, true); }
    }

}
