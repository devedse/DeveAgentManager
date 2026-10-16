using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using Xunit;

namespace DeveAgentManager.Tests;

public class NativeActivityTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 6, 0, 0, DateTimeKind.Utc);
    private const string Session = "00000000-0000-0000-0000-000000000001";
    private static Agent Agent() => new() { HarnessId = "codex", HarnessSessionId = Session, State = AgentState.Running, UpdatedAt = Now };
    private static AgentScreenDetection Ready => new(AgentActivityStatus.Ready, "ready", "ready");
    private static NativeTurnObservation Turn(string id, string state = "Completed", int seconds = 0) => new(Session, id, state, Now.AddSeconds(seconds));
    private static AgentActivityDto Observe(Agent agent, NativeTurnObservation turn, int seconds = 0)
    {
        AgentActivityTracker.Apply(agent, Ready, "pane", Now.AddSeconds(seconds), turn);
        AgentActivityTracker.Apply(agent, Ready, "pane", Now.AddSeconds(seconds + 5), turn);
        return AgentActivityTracker.GetDto(agent, Now.AddSeconds(seconds + 5));
    }

    [Fact]
    public void ColdStartAndFastTurnsRecoverWithoutDependingOnFooterOrObservedWorking()
    {
        var agent = Agent();
        var first = Observe(agent, Turn("first"));
        Assert.True(first.IsFinished);
        agent.SeenCompletionSequence = first.CompletionSequence;
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(5)).IsFinished);
        var second = Observe(agent, Turn("second", seconds: 10), 10);
        Assert.True(second.IsFinished);
        Assert.True(second.CompletionSequence > first.CompletionSequence);
        Assert.Equal(Now.AddSeconds(10), second.CompletedAt);
        Assert.Equal(second.CompletionSequence, Observe(agent, Turn("second", seconds: 10), 20).CompletionSequence);
    }

    [Fact]
    public void TemporaryNativeReadFailureCannotDuplicateCompletionOrCompleteOldFooterDuringNewWork()
    {
        var agent = Agent();
        var ready = Ready with { CompletionEvidence = "same footer" };
        AgentActivityTracker.Apply(agent, ready, "pane", Now, Turn("first"));
        AgentActivityTracker.Apply(agent, ready, "pane", Now.AddSeconds(5), Turn("first"));
        var first = AgentActivityTracker.GetDto(agent, Now.AddSeconds(5));
        agent.SeenCompletionSequence = first.CompletionSequence;
        AgentActivityTracker.Apply(agent, ready, "pane", Now.AddSeconds(10));
        Assert.False(Observe(agent, Turn("first"), 15).IsFinished);
        Assert.Equal(first.CompletionSequence, AgentActivityTracker.Read(agent).Snapshot.CompletionSequence);
        AgentActivityTracker.Apply(agent, ready, "pane", Now.AddSeconds(25), Turn("second", "Started", 25));
        AgentActivityTracker.Apply(agent, ready, "pane", Now.AddSeconds(30));
        AgentActivityTracker.Apply(agent, ready, "pane", Now.AddSeconds(35));
        Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(35)).IsFinished);
        Assert.True(Observe(agent, Turn("second", seconds: 40), 40).IsFinished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpgradeRestoresCompletionWithoutRevivingViewedResults(bool seen)
    {
        var agent = Agent();
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Working, "working", "busy"), "pane", Now.AddSeconds(-10));
        var ready = Ready with { CompletionEvidence = "old footer" };
        AgentActivityTracker.Apply(agent, ready, "pane", Now.AddSeconds(-5));
        AgentActivityTracker.Apply(agent, ready, "pane", Now);
        var oldSequence = AgentActivityTracker.Read(agent).Snapshot.CompletionSequence;
        if (seen) agent.SeenCompletionSequence = oldSequence;
        agent.ActivityJson = agent.ActivityJson!.Replace("\"CurrentTurnCompleted\":true", "\"LegacyCompletion\":true");
        var restored = Observe(agent, Turn("finished-before-upgrade"));
        Assert.Equal(oldSequence, restored.CompletionSequence);
        Assert.Equal(!seen, restored.IsFinished);
        Assert.Equal(oldSequence, Observe(agent, Turn("finished-before-upgrade"), 10).CompletionSequence);
    }

    [Fact]
    public void ManagerRestartPreservesUnseenButHarnessRestartDoesNotResurrectAcknowledgedTurn()
    {
        var agent = Agent();
        var done = Observe(agent, Turn("first"));
        var restored = Agent();
        restored.ActivityJson = agent.ActivityJson;
        Assert.True(Observe(restored, Turn("first"), 10).IsFinished);
        Assert.Equal(done.CompletionSequence, AgentActivityTracker.GetDto(restored, Now.AddSeconds(15)).CompletionSequence);
        AgentActivityTracker.Reset(restored);
        Assert.False(Observe(restored, Turn("first"), 20).IsFinished);
        Assert.True(Observe(restored, Turn("next", seconds: 30), 30).IsFinished);
    }

    [Theory]
    [InlineData("Started")]
    [InlineData("Aborted")]
    public void NewStartedOrAbortedTurnInvalidatesPreviousCompletion(string state)
    {
        var agent = Agent();
        Observe(agent, Turn("first"));
        var result = Observe(agent, Turn("next", state, 10), 10);
        Assert.False(result.IsFinished);
        Assert.Equal(state == "Started" ? AgentActivityStatus.Working : AgentActivityStatus.Ready, result.Status);
    }

    [Fact]
    public void NativeCompletionRespectsLiveWorkBlockersBackgroundTasksAndSessionBinding()
    {
        foreach (var status in new[] { AgentActivityStatus.Working, AgentActivityStatus.NeedsInput, AgentActivityStatus.UsageLimited })
        {
            var agent = Agent();
            var detection = Ready with { Status = status };
            AgentActivityTracker.Apply(agent, detection, "pane", Now, Turn("first"));
            AgentActivityTracker.Apply(agent, detection, "pane", Now.AddSeconds(5), Turn("first"));
            Assert.Equal(status, AgentActivityTracker.GetDto(agent, Now.AddSeconds(5)).Status);
            Assert.False(AgentActivityTracker.GetDto(agent, Now.AddSeconds(5)).IsFinished);
        }
        var background = Agent();
        AgentActivityTracker.Apply(background, Ready with { BackgroundTaskCount = 1 }, "pane", Now, Turn("first"));
        AgentActivityTracker.Apply(background, Ready with { BackgroundTaskCount = 1 }, "pane", Now.AddSeconds(5), Turn("first"));
        Assert.False(AgentActivityTracker.GetDto(background, Now.AddSeconds(5)).IsFinished);
        Assert.True(Observe(background, Turn("first"), 10).IsFinished);
        Assert.False(Observe(Agent(), Turn("wrong") with { SessionId = "another-session" }).IsFinished);
    }

    [Theory]
    [InlineData(AgentActivityStatus.Unknown, "usage-limit-unavailable")]
    [InlineData(AgentActivityStatus.Unknown, "codex/matching-timeout")]
    [InlineData(AgentActivityStatus.Unknown, "snapshot-unavailable")]
    [InlineData(AgentActivityStatus.Ready, "usage-limit-expired")]
    public void NativeEventsPreserveUnavailableAndExpiredLimitObservations(AgentActivityStatus status, string rule)
    {
        foreach (var nativeState in new[] { "Started", "Completed", "Aborted" })
        {
            var agent = Agent();
            Observe(agent, Turn("previous"));
            var detection = new AgentScreenDetection(status, "keep this explanation", rule);
            AgentActivityTracker.Apply(agent, detection, "pane", Now.AddSeconds(10), Turn("next", nativeState, 10));
            AgentActivityTracker.Apply(agent, detection, "pane", Now.AddSeconds(15), Turn("next", nativeState, 10));
            var current = AgentActivityTracker.GetDto(agent, Now.AddSeconds(15));
            Assert.Equal(status, current.Status);
            Assert.Equal(rule, current.Rule);
            Assert.Equal("keep this explanation", current.Reason);
            Assert.False(current.IsFinished);
        }
    }

    [Theory]
    [InlineData("· 06:00")]
    [InlineData("• 06:00")]
    [InlineData("· Oct 16 at 06:00")]
    [InlineData("• Oct 16 at 06:00")]
    public void ActualCodexFooterVariantsStillProvideFallbackEvidence(string suffix)
    {
        var screen = $"• Response\nWorked for 16m 39s {suffix}\n› Ask Codex to do anything\n? for shortcuts";
        Assert.NotNull(AgentScreenDetector.Detect("codex", screen, Now).CompletionEvidence);
    }

    [Fact]
    public void ScrolledInlineHistoryDoesNotEstablishLiveReadinessOrBackgroundCount()
    {
        var screen = "Worked for 12s • 06:00\n↓ Back to bottom · esc\n› Ask Codex to do anything\nenter/esc latest · ? shortcuts";
        var detection = AgentScreenDetector.Detect("codex", screen, Now);
        Assert.Equal(AgentActivityStatus.Unknown, detection.Status);
        Assert.Null(detection.CompletionEvidence);
    }

    [Fact]
    public async Task ProbeReadsOnlyLifecycleEventsAndRejectsOtherSessionsAndPartialRecords()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("dam-native-activity-").FullName;
        try
        {
            var file = Path.Combine(root, "session.jsonl");
            var header = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = Session } });
            string Event(string type, string turn) => JsonSerializer.Serialize(new { timestamp = Now, type = "event_msg", payload = new { type, turn_id = turn } });
            async Task<NativeTurnObservation?> Read()
            {
                var code = "import pathlib,json,datetime\n" + NativeActivityProbe.Script + "\nprint(json.dumps(read_codex_turn(pathlib.Path(" + JsonSerializer.Serialize(file) + "),'" + Session + "')))";
                using var process = Process.Start(new ProcessStartInfo("python3", ["-c", code]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
                var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, process.ExitCode);
                return JsonSerializer.Deserialize<NativeTurnObservation>(output);
            }
            await File.WriteAllTextAsync(file, header + "\n" + Event("task_started", "one") + "\n" + Event("agent_message", "one") + "\n", TestContext.Current.CancellationToken);
            Assert.Equal("Started", (await Read())!.State); // An assistant message, even a final answer, is not completion.
            await File.AppendAllTextAsync(file, Event("task_complete", "one") + "\n{\"partial\":", TestContext.Current.CancellationToken);
            Assert.Equal("Completed", (await Read())!.State);
            await File.WriteAllTextAsync(file, header + "\n" + Event("task_complete", "one") + "\n" + new string('x', 300_000), TestContext.Current.CancellationToken);
            Assert.Null(await Read()); // A bounded tail cannot reuse an event outside its read window.
            await File.WriteAllTextAsync(file, header.Replace(Session, "another-session") + "\n" + Event("task_complete", "one") + "\n", TestContext.Current.CancellationToken);
            Assert.Null(await Read());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BatchProbeBindsNativeEvidenceToRunningProcessAndKeepsScreenIfNativeReadFails()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("dam-native-process-").FullName;
        Process? harness = null;
        try
        {
            var home = Path.Combine(root, "codex-home");
            var folder = Directory.CreateDirectory(Path.Combine(home, "sessions/2026/10/16")).FullName;
            var history = Path.Combine(folder, $"rollout-test-{Session}.jsonl");
            await File.WriteAllTextAsync(history, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = Session } }) + "\n"
                + JsonSerializer.Serialize(new { timestamp = Now, type = "event_msg", payload = new { type = "task_complete", turn_id = "completed" } }) + "\n", TestContext.Current.CancellationToken);
            var stub = Path.Combine(root, "codex");
            await File.WriteAllTextAsync(stub, "import time\ntime.sleep(60)\n", TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo("python3", [stub, "resume", Session]);
            start.Environment["CODEX_HOME"] = home;
            harness = Process.Start(start)!;
            var tmux = Path.Combine(root, "tmux");
            await File.WriteAllTextAsync(tmux, $"#!/bin/sh\ncase \"$1\" in list-panes) echo 'agent|0||123' ;; display-message) echo '%1|{harness.Id}|0|3|1' ;; capture-pane) printf '• Response\\nWorked for 3s • 06:00\\n\\n›\\n? for shortcuts\\n' ;; esac\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(tmux, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            async Task<AgentService.ObservedScreen> Read(string sessionId)
            {
                var script = AgentService.BuildStateScript([("agent", null)], true,
                    new Dictionary<string, AgentService.NativeActivityTarget> { ["agent"] = new("codex", sessionId) });
                var probe = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
                probe.Environment["PATH"] = root + ":" + Environment.GetEnvironmentVariable("PATH");
                using var process = Process.Start(probe)!;
                var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, process.ExitCode);
                return AgentService.ParseScreens(output)["agent"];
            }
            var matching = await Read(Session);
            Assert.Equal("Completed", matching.NativeTurn!.State);
            Assert.Equal("completed", matching.NativeTurn.TurnId);
            Assert.Null((await Read("00000000-0000-0000-0000-000000000002")).NativeTurn);
            var header = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = Session } });
            string Started(DateTime at) => header + "\n" + JsonSerializer.Serialize(new { timestamp = at, type = "event_msg", payload = new { type = "task_started", turn_id = "active" } }) + "\n";
            await File.WriteAllTextAsync(history, Started(DateTime.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
            Assert.Null((await Read(Session)).NativeTurn); // A previous process's unfinished turn is not current work.
            await File.WriteAllTextAsync(history, Started(DateTime.UtcNow), TestContext.Current.CancellationToken);
            Assert.Equal("Started", (await Read(Session)).NativeTurn!.State);
            await File.WriteAllTextAsync(history, "corrupt native header\n", TestContext.Current.CancellationToken);
            var corrupt = await Read(Session);
            Assert.Null(corrupt.NativeTurn);
            Assert.Contains("Worked for", corrupt.Screen!);
        }
        finally
        {
            if (harness is { HasExited: false }) { harness.Kill(true); await harness.WaitForExitAsync(CancellationToken.None); }
            harness?.Dispose();
            Directory.Delete(root, true);
        }
    }
}
