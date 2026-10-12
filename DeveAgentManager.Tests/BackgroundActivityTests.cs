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
}
