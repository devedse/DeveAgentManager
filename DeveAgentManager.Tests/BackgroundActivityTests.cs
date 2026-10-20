using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using Xunit;

namespace DeveAgentManager.Tests;

public class BackgroundActivityTests
{
    [Theory]
    [InlineData("claude", "❯ \n────────\n bypass permissions on · 1 shell · ↓ to manage", 1)]
    [InlineData("claude", "❯ \n────────\n bypass permissions on\n · 3 shells · ↓ to manage", 3)]
    [InlineData("claude", "❯ 4 shells\n────────\n bypass permissions on", null)]
    [InlineData("claude", "❯ \n────────\n bypass permissions on", null)]
    [InlineData("codex", "› \n 2 background terminals running · /ps to view\n? for shortcuts", 2)]
    [InlineData("codex", "› 2 background terminals running\n? for shortcuts", null)]
    public void CountsOnlyCurrentFooter(string harness, string footer, int? count)
    {
        var screen = "✻ Brewed for 3m · done · 1 shell still running\n────────\n" + footer;
        var result = AgentScreenDetector.Detect(harness, screen, DateTime.UtcNow);
        Assert.Equal(AgentActivityStatus.Ready, result.Status);
        Assert.Equal(count, result.BackgroundTaskCount);
    }

    [Fact]
    public void BackgroundCountChangesPublishWithoutChangingPromptReadiness()
    {
        var now = DateTime.UtcNow;
        var agent = new Agent { State = AgentState.Running, UpdatedAt = now,
            Runner = new Runner { State = RunnerState.Ready } };
        var ready = new AgentScreenDetection(AgentActivityStatus.Ready, "ready", "ready", BackgroundTaskCount: 1);
        AgentActivityTracker.Apply(agent, ready, "process", now);
        Assert.False(agent.Working);
        Assert.Equal(1, AgentActivityTracker.GetDto(agent, now).BackgroundTaskCount);
        Assert.True(AgentActivityTracker.Apply(agent, ready with { BackgroundTaskCount = 2 }, "process", now.AddSeconds(5)));
        Assert.Null(AgentActivityTracker.GetDto(agent, now.AddMinutes(1)).BackgroundTaskCount);
        AgentActivityTracker.Apply(agent, ready with { BackgroundTaskCount = null }, "new-process", now.AddMinutes(1));
        Assert.Null(AgentActivityTracker.GetDto(agent, now.AddMinutes(1)).BackgroundTaskCount);
    }

    [Fact]
    public void CompletedClaudeTurnNotifiesWhileServicesRemainAndCountChangesDoNotNotifyAgain()
    {
        var now = DateTime.UtcNow;
        var agent = new Agent { State = AgentState.Running, HarnessId = "claude", UpdatedAt = now,
            Runner = new Runner { State = RunnerState.Ready } };
        var working = AgentScreenDetector.Detect("claude", AgentActivityTests.Screen("claude-working"), now);
        Assert.Equal(AgentActivityStatus.Working, working.Status);
        AgentActivityTracker.Apply(agent, working, "process", now);

        // The screenshot includes an unsent draft and three intentionally persistent services.
        var screen = AgentActivityTests.Screen("claude-completed-background-shells");
        var ready = AgentScreenDetector.Detect("claude", screen, now.AddSeconds(5));
        Assert.Equal(AgentActivityStatus.Ready, ready.Status);
        Assert.Equal(3, ready.BackgroundTaskCount);
        Assert.Contains("Crunched for 17s", ready.CompletionEvidence);
        AgentActivityTracker.Apply(agent, ready, "process", now.AddSeconds(5));
        AgentActivityTracker.Apply(agent, ready, "process", now.AddSeconds(10));
        var completed = AgentActivityTracker.GetDto(agent, now.AddSeconds(10));
        Assert.True(completed.IsFinished);
        Assert.False(agent.Working);
        var notification = AgentAttentionService.Attention(agent, false, now.AddSeconds(10))!;
        Assert.Equal(AgentAttentionKind.Finished, notification.Kind);

        var recap = screen.Replace("· 3 shells still running\n", "· 3 shells still running\n\n※ recap: The build is ready for the next playtest.\n");
        var redrawn = AgentScreenDetector.Detect("claude", recap, now.AddSeconds(12));
        Assert.False(AgentActivityTracker.Apply(agent, redrawn, "process", now.AddSeconds(12)));
        Assert.True(AgentActivityTracker.GetDto(agent, now.AddSeconds(12)).IsFinished);
        Assert.Equal(notification.Token, AgentAttentionService.Attention(agent, false, now.AddSeconds(12))!.Token);

        foreach (var count in new int?[] { 2, null })
        {
            AgentActivityTracker.Apply(agent, ready with { BackgroundTaskCount = count }, "process", now.AddSeconds(15));
            var current = AgentActivityTracker.GetDto(agent, now.AddSeconds(15));
            Assert.True(current.IsFinished);
            Assert.Equal(completed.CompletionSequence, current.CompletionSequence);
            Assert.Equal(notification.Token, AgentAttentionService.Attention(agent, false, now.AddSeconds(15))!.Token);
        }

        agent.SeenCompletionSequence = completed.CompletionSequence;
        AgentActivityTracker.Apply(agent, ready, "process", now.AddSeconds(20));
        var seen = AgentActivityTracker.GetDto(agent, now.AddSeconds(20));
        Assert.Equal(AgentActivityStatus.Ready, seen.Status);
        Assert.Equal(3, seen.BackgroundTaskCount);
        Assert.False(seen.IsFinished);
        Assert.Null(AgentAttentionService.Attention(agent, false, now.AddSeconds(20)));

        AgentActivityTracker.Apply(agent, working with { BackgroundTaskCount = 3 }, "process", now.AddSeconds(25));
        var next = AgentActivityTracker.GetDto(agent, now.AddSeconds(25));
        Assert.Equal(AgentActivityStatus.Working, next.Status);
        Assert.False(next.CurrentTurnCompleted);
        Assert.False(next.IsFinished);
    }

    [Fact]
    public void ReadyBackgroundCommandsAloneDoNotEstablishCompletion()
    {
        var now = DateTime.UtcNow;
        var agent = new Agent { State = AgentState.Running, HarnessId = "claude", UpdatedAt = now,
            Runner = new Runner { State = RunnerState.Ready } };
        var ready = AgentScreenDetector.Detect("claude", AgentActivityTests.Screen("claude-completed-background-shells"), now);
        AgentActivityTracker.Apply(agent, ready, "process", now);
        AgentActivityTracker.Apply(agent, ready, "process", now.AddSeconds(5));
        Assert.Equal(AgentActivityStatus.Ready, AgentActivityTracker.GetDto(agent, now.AddSeconds(5)).Status);
        Assert.False(AgentActivityTracker.GetDto(agent, now.AddSeconds(5)).IsFinished);
        Assert.Null(AgentAttentionService.Attention(agent, false, now.AddSeconds(5)));
    }
}
