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
    [InlineData("invalid json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void InvalidSavedObservationIsDistinctFromNeverObserved(string json)
    {
        var agent = NewAgent();
        agent.ActivityJson = json;
        var status = AgentActivityTracker.GetDto(agent, Now);
        Assert.Equal("observation-invalid", status.Rule);
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        Assert.Equal(AgentActivityStatus.Working, AgentActivityTracker.GetDto(agent, Now).Status);
    }

    [Fact]
    public void ViewingCompletionClearsFinishedAndAnInterruptedLaterTurnDoesNotReuseIt()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        AgentActivityTracker.Apply(agent, Ready("reply"), "process", Now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, Ready("reply"), "process", Now.AddSeconds(10));
        var done = AgentActivityTracker.GetDto(agent, Now.AddSeconds(10));
        Assert.True(done.IsFinished);
        agent.SeenCompletionSequence = done.CompletionSequence;
        var seen = AgentActivityTracker.GetDto(agent, Now.AddSeconds(10));
        Assert.False(seen.UnseenCompletion);
        Assert.False(seen.IsFinished);
        // The durable JSON is sufficient across a Manager restart.
        var restored = new Agent { HarnessId = agent.HarnessId, HarnessSessionId = agent.HarnessSessionId,
            State = agent.State, ActivityJson = agent.ActivityJson, SeenCompletionSequence = agent.SeenCompletionSequence };
        Assert.False(AgentActivityTracker.GetDto(restored, Now.AddSeconds(10)).IsFinished);
        Assert.False(AgentActivityTracker.GetDto(restored, Now.AddSeconds(60)).IsFinished);
        AgentActivityTracker.Apply(agent, Working, "process", Now.AddSeconds(15));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).IsFinished);
        Assert.Null(AgentAttentionService.Attention(agent, false, Now.AddSeconds(15)));
        AgentActivityTracker.Apply(agent, Ready("reply"), "process", Now.AddSeconds(20));
        AgentActivityTracker.Apply(agent, Ready("reply"), "process", Now.AddSeconds(25));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(25)).IsFinished);
    }

    [Fact]
    public void CompletionIsBoundToSessionAndOldSerializedStateDoesNotManufactureFinished()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        AgentActivityTracker.Apply(agent, Ready("reply"), "process", Now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, Ready("reply"), "process", Now.AddSeconds(10));
        Assert.True(AgentActivityTracker.GetDto(agent, Now.AddSeconds(10)).IsFinished);
        agent.HarnessSessionId = "another-conversation";
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(10)).IsFinished);
        AgentActivityTracker.Apply(agent, Ready("reply"), "process", Now.AddSeconds(15));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).IsFinished);
        agent.ActivityJson = agent.ActivityJson!.Replace("\"CurrentTurnCompleted\":false", "\"LegacyCompletion\":true");
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).IsFinished);
    }

    [Fact]
    public void OverdueInitialObservationExplainsBackgroundMonitorProblem()
    {
        var agent = NewAgent();
        Assert.Equal("The harness has not been observed yet.", AgentActivityTracker.GetDto(agent, Now).Reason);
        Assert.Contains("Manager monitor logs", AgentActivityTracker.GetDto(agent, Now.AddMinutes(1)).Reason);
        Assert.Equal("not-observed", AgentActivityTracker.GetDto(agent, Now.AddMinutes(1)).Rule);
    }

    [Theory]
    [InlineData("pi-working", AgentActivityStatus.Working)]
    [InlineData("pi-idle", AgentActivityStatus.Ready)]
    [InlineData("pi-idle-cursor-line", AgentActivityStatus.Ready)]
    [InlineData("pi-quota-exceeded", AgentActivityStatus.UsageLimited)]
    [InlineData("claude-working", AgentActivityStatus.Working)]
    [InlineData("claude-working-narrow", AgentActivityStatus.Working)]
    [InlineData("claude-working-feedback", AgentActivityStatus.Working)]
    [InlineData("claude-idle", AgentActivityStatus.Ready)]
    [InlineData("claude-idle-narrow", AgentActivityStatus.Ready)]
    [InlineData("codex-working", AgentActivityStatus.Working)]
    [InlineData("codex-background-wait-narrow", AgentActivityStatus.Working)]
    [InlineData("codex-compacting", AgentActivityStatus.Working)]
    [InlineData("codex-idle", AgentActivityStatus.Ready)]
    [InlineData("codex-idle-draft-warnings", AgentActivityStatus.Ready)]
    [InlineData("claude-weekly-limit", AgentActivityStatus.UsageLimited)]
    [InlineData("claude-session-limit", AgentActivityStatus.UsageLimited)]
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
    public void WrappedOptionalClaudeFeedbackDoesNotInterruptWorking()
    {
        var screen = Screen("claude-working-feedback")
            .Replace("How is Claude doing this session? (optional)", "How is Claude doing this\n  session? (optional)")
            .Replace("1: Bad    2: Fine   3: Good   0: Dismiss", "1: Bad    2: Fine\n  3: Good   0: Dismiss");
        var result = AgentScreenDetector.Detect("claude", screen, Now);
        Assert.Equal(AgentActivityStatus.Working, result.Status);
        Assert.Contains("Transfiguring", result.Evidence);
        Assert.Null(result.CompletionEvidence);
    }

    [Theory]
    [InlineData("● Done.")]
    [InlineData("✻ Crunched for 9m 21s · done 6:33 PM")]
    [InlineData("● How is Claude doing this session? Please answer before I proceed.")]
    [InlineData("❯ another prompt\n● Done.")]
    public void ClaudeFeedbackDoesNotHideLaterTurnBoundaries(string following)
    {
        var screen = Screen("claude-working-feedback").Replace("───────────────────────── queuecache", following + "\n───────────────────────── queuecache");
        Assert.Equal(AgentActivityStatus.Ready, AgentScreenDetector.Detect("claude", screen, Now).Status);
    }

    [Fact]
    public void ClaudeApprovalStillNeedsInputWithOptionalFeedback()
    {
        var screen = Screen("claude-working-feedback") + "\n─────────\nBash command\nDo you want to proceed?\n❯ 1. Yes\n2. No\nEsc to cancel";
        Assert.Equal(AgentActivityStatus.NeedsInput, AgentScreenDetector.Detect("claude", screen, Now).Status);
    }

    [Theory]
    [InlineData("● Done.")]
    [InlineData("✻ Crunched for 9m 21s · done 6:33 PM")]
    public void ClaudeFeedbackDoesNotReviveAnAlreadyStaleSpinner(string completion)
    {
        var screen = Screen("claude-working-feedback").Replace("● How is Claude", completion + "\n● How is Claude");
        Assert.Equal(AgentActivityStatus.Ready, AgentScreenDetector.Detect("claude", screen, Now).Status);
    }

    [Fact]
    public void IncompleteOrChangedClaudeSurveyIsNotAssumedToBeOptional()
    {
        var screen = Screen("claude-working-feedback").Replace("0: Dismiss", "0: Proceed");
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
    public void SessionLimitSurvivesClaudesOwnNoticeAndWaitsWithClaude()
    {
        // Claude Code 2.1 follows the error with its own notice, and says below the prompt that it continues by itself.
        var now = new DateTime(2026, 10, 6, 15, 25, 0, DateTimeKind.Utc);
        var screen = Screen("claude-session-limit");
        var parsed = UsageLimitParser.Parse("claude", screen, "UTC", now);
        Assert.NotNull(parsed);
        Assert.Equal("You've hit your session limit · resets 6:40pm (Europe/Amsterdam)", parsed.Message);
        Assert.Equal(new DateTime(2026, 10, 6, 16, 40, 0, DateTimeKind.Utc), parsed.ResetAt);
        Assert.Equal("Europe/Amsterdam", parsed.TimeZoneId);
        Assert.True(parsed.NativeAutoContinue);

        // On a narrow screen the notice comes first and wraps.
        var narrow = screen.Replace("""
              ⎿  You've hit your session limit · resets 6:40pm (Europe/Amsterdam)

            ● Usage limit reached · continuing automatically at 6:40pm · esc to cancel
            """, """
            ● Usage limit reached again · continuing automatically at 6:40pm · esc to
              cancel
              ⎿  You've hit your session limit · resets 6:40pm (Europe/Amsterdam)
            """);
        Assert.NotEqual(screen, narrow);
        Assert.Equal(new DateTime(2026, 10, 6, 16, 40, 0, DateTimeKind.Utc), UsageLimitParser.Parse("claude", narrow, "UTC", now)?.ResetAt);

        // With the error scrolled away, the footer still says it, in the agent's timezone.
        var footerOnly = string.Join('\n', screen.Split('\n').Skip(5));
        var fromFooter = UsageLimitParser.Parse("claude", footerOnly, "Europe/Amsterdam", now);
        Assert.Equal(new DateTime(2026, 10, 6, 16, 40, 0, DateTimeKind.Utc), fromFooter?.ResetAt);
        Assert.True(fromFooter?.NativeAutoContinue);

        // Once the limit was reset and the agent went on, the error is old, whatever the footer still says.
        Assert.Null(UsageLimitParser.Parse("claude", screen.Replace("● Usage limit reached · continuing automatically at 6:40pm · esc to cancel",
            "● Usage limit reset · continuing automatically\n\n● Back to work."), "UTC", now));
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
    public void RecognizedIdleComposerDoesNotRequireCompletionEvidence()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        var draft = new AgentScreenDetection(AgentActivityStatus.Ready, "draft", "prompt");
        AgentActivityTracker.Apply(agent, draft, "process", Now.AddSeconds(5));
        Assert.True(agent.Working);
        AgentActivityTracker.Apply(agent, draft, "process", Now.AddSeconds(15));
        Assert.Equal(AgentActivityStatus.Ready, AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).Status);
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).UnseenCompletion);
    }

    [Theory]
    [InlineData("› it doesn't work")]
    [InlineData("› Ask Codex to do anything")]
    [InlineData("›")]
    public void CompletedTurnWithWarningFooterReturnsReadyAndMarksCompletion(string prompt)
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        var screen = Screen("codex-idle-draft-warnings").Replace("› it doesn't work", prompt);
        var detection = AgentScreenDetector.Detect("codex", screen, Now);
        Assert.Equal(AgentActivityStatus.Ready, detection.Status);
        Assert.NotNull(detection.CompletionEvidence);
        AgentActivityTracker.Apply(agent, detection, "process", Now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, detection, "process", Now.AddSeconds(10));
        Assert.Equal(AgentActivityStatus.Ready, AgentActivityTracker.GetDto(agent, Now.AddSeconds(10)).Status);
        Assert.True(AgentActivityTracker.GetDto(agent, Now.AddSeconds(10)).UnseenCompletion);
        var completionSequence = AgentActivityTracker.GetDto(agent, Now.AddSeconds(10)).CompletionSequence;
        AgentActivityTracker.Apply(agent, AgentScreenDetector.Detect("codex", screen.Replace(prompt, "› a different draft"), Now), "process", Now.AddSeconds(15));
        Assert.Equal(completionSequence, AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).CompletionSequence);
    }

    [Fact]
    public void WorkingIndicatorWinsWithWarningFooterAndDraft()
    {
        var screen = "• Working (20s • esc to interrupt)\n› a queued draft\nGPT-6.1-Sol medium · ~/work/project\n⚠ 3 · f2";
        Assert.Equal(AgentActivityStatus.Working, AgentScreenDetector.Detect("codex", screen, Now).Status);
    }

    [Theory]
    [InlineData("Exploring the project (12s)", "› Ask Codex to do anything")]
    [InlineData("• Exploring the project (12s)", "› a draft")]
    [InlineData("Exploring the project (1m 12s • ctrl+c to interrupt)", "›⠁a queued draft")]
    [InlineData("Waiting for background terminal (23m…", "› Ask Codex to do anything")]
    public void CodexLiveTimersDoNotDependOnBulletOrInterruptHint(string line, string prompt)
    {
        var screen = $"{line}\n{prompt}\nGPT-6.1-Sol medium · ~/work/project\n? for shortcuts";
        Assert.Equal(AgentActivityStatus.Working, AgentScreenDetector.Detect("codex", screen, Now).Status);
    }

    [Fact]
    public void TranscriptViewerCannotPublishHistoricalActivity()
    {
        var screen = "• Working (12s • esc to interrupt)\n› old input\n? for shortcuts\n↑/↓ to scroll · pgup/pgdn to page · home/end to jump · q to quit · esc to edit prev";
        Assert.Equal(AgentActivityStatus.Unknown, AgentScreenDetector.Detect("codex", screen, Now).Status);
    }

    [Fact]
    public void AssistantCommentaryIsNotCompletionEvidence()
    {
        var screen = "• I'll inspect those files now.\n› Ask Codex to do anything\n? for shortcuts";
        Assert.Null(AgentScreenDetector.Detect("codex", screen, Now).CompletionEvidence);
    }

    [Fact]
    public void CompletionAfterUnknownStillNeedsStableEvidence()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        var unknown = new AgentScreenDetection(AgentActivityStatus.Unknown, "redraw", "unknown");
        AgentActivityTracker.Apply(agent, unknown, "process", Now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, unknown, "process", Now.AddSeconds(15));
        AgentActivityTracker.Apply(agent, Ready(), "process", Now.AddSeconds(20));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(20)).UnseenCompletion);
        AgentActivityTracker.Apply(agent, Ready(), "process", Now.AddSeconds(25));
        Assert.True(AgentActivityTracker.GetDto(agent, Now.AddSeconds(25)).UnseenCompletion);
    }

    [Fact]
    public void OldCompletionVisibleDuringWorkCannotCompleteTheNewTurn()
    {
        var agent = NewAgent();
        const string previous = "• Old response\nWorked for 10s · 10:00\n";
        const string composer = "› Ask Codex to do anything\n? for shortcuts";
        var working = AgentScreenDetector.Detect("codex", previous + "• Working (5s • esc to interrupt)\n" + composer, Now);
        AgentActivityTracker.Apply(agent, working, "process", Now);
        var oldReady = AgentScreenDetector.Detect("codex", previous + composer, Now);
        AgentActivityTracker.Apply(agent, oldReady, "process", Now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, oldReady, "process", Now.AddSeconds(10));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(10)).UnseenCompletion);
        var newReady = AgentScreenDetector.Detect("codex", previous.Replace("10:00", "10:01") + composer, Now);
        AgentActivityTracker.Apply(agent, newReady, "process", Now.AddSeconds(15));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(15)).UnseenCompletion);
        AgentActivityTracker.Apply(agent, newReady, "process", Now.AddSeconds(20));
        Assert.True(AgentActivityTracker.GetDto(agent, Now.AddSeconds(20)).UnseenCompletion);
    }

    [Fact]
    public void CompletionEvidenceDoesNotChangeWhenTheViewportReflows()
    {
        var screen = Screen("codex-idle-draft-warnings");
        var before = AgentScreenDetector.Detect("codex", screen, Now);
        var after = AgentScreenDetector.Detect("codex", screen.Replace("Fixed in 1.0.63. Update the Manager\n  to that version", "Fixed in 1.0.63.\n  Update the Manager to that version"), Now);
        Assert.NotNull(before.CompletionEvidence);
        Assert.Equal(before.CompletionEvidence, after.CompletionEvidence);
    }

    [Theory]
    [InlineData("› a draft")]
    [InlineData("› Ask Codex to do anything")]
    [InlineData("›")]
    public void IdleComposerWithWarningsNeedsNoCompletionNotification(string prompt)
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, Working, "process", Now);
        var detection = AgentScreenDetector.Detect("codex", $"• Ran a command\n{prompt}\nGPT-6.1-Sol medium · ~/work/project\n⚠ 3 · f2", Now);
        Assert.Equal(AgentActivityStatus.Ready, detection.Status);
        Assert.Null(detection.CompletionEvidence);
        AgentActivityTracker.Apply(agent, detection, "process", Now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, detection, "process", Now.AddSeconds(10));
        var status = AgentActivityTracker.GetDto(agent, Now.AddSeconds(10));
        Assert.Equal(AgentActivityStatus.Ready, status.Status);
        Assert.False(status.UnseenCompletion);
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
            File.WriteAllText(tmux, $"#!/bin/sh\ncase \"$1\" in list-panes) echo 'agent|0||123' ;; display-message) echo '%1|{Environment.ProcessId}|0|1|2' ;; capture-pane) printf '• done\\n❯\\n' ;; esac\n");
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
            Assert.NotNull(snapshot.ObservedAt);
            Assert.Equal(DateTimeKind.Utc, snapshot.ObservedAt.Value.Kind);
            // Usage-limit parsing converts from UTC; a +00:00 JSON DateTime becomes Local
            // in System.Text.Json and used to throw, aborting the entire runner pass.
            var limitScreen = "■ You've hit your usage limit. Try again at 11:49 PM.\n› Ask Codex to do anything\n? for shortcuts";
            Assert.Equal(AgentActivityStatus.UsageLimited,
                AgentScreenDetector.Detect("codex", limitScreen, snapshot.ObservedAt.Value).Status);
            Assert.NotNull(UsageLimitParser.Parse("codex", limitScreen, "Europe/Amsterdam", snapshot.ToLimitScreen()!.ObservedAt!.Value));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task MissingSessionDoesNotDiscardRunningAgentObservation()
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("dam-missing-pane-").FullName;
        try
        {
            var tmux = Path.Combine(folder, "tmux");
            // tmux reports success and empty metadata when the requested session has disappeared.
            File.WriteAllText(tmux, $"#!/bin/sh\ncase \"$1\" in list-panes) echo 'running|0||123' ;; display-message) case \"$*\" in *missing*) echo '|' ;; *) echo '%1|{Environment.ProcessId}|0|1|2' ;; esac ;; capture-pane) printf '• Working (5s • esc to interrupt)\\n› Ask Codex to do anything\\n? for shortcuts\\n' ;; esac\n");
            File.SetUnixFileMode(tmux, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new System.Diagnostics.ProcessStartInfo("bash", ["-c", AgentService.BuildStateScript([("missing-before", null), ("running", null), ("missing-after", null)], captureScreens: true)]) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = folder + ":" + Environment.GetEnvironmentVariable("PATH");
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(error);
            var screens = AgentService.ParseScreens(output);
            Assert.Equal(3, screens.Count);
            Assert.Null(screens["missing-before"].Screen);
            Assert.Null(screens["missing-after"].Identity);
            Assert.Equal(AgentActivityStatus.Working, AgentScreenDetector.Detect("codex", screens["running"].Screen!, Now).Status);
            Assert.True(AgentService.ParsePanes(output).Panes.ContainsKey("running"));
        }
        finally { Directory.Delete(folder, true); }
    }

}
