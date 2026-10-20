using DeveAgentManager.Client.Shared;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using Xunit;

namespace DeveAgentManager.Tests;

public class AgentStatusStyleTests
{
    [Theory]
    [InlineData(AgentActivityStatus.Ready, null, false, "Finished")]
    [InlineData(AgentActivityStatus.Ready, 0, false, "Finished")]
    [InlineData(AgentActivityStatus.Ready, null, true, "Ready")]
    [InlineData(AgentActivityStatus.Ready, 1, false, "Finished")]
    [InlineData(AgentActivityStatus.Ready, 3, false, "Finished")]
    [InlineData(AgentActivityStatus.Ready, 3, true, "Ready")]
    [InlineData(AgentActivityStatus.Working, null, false, "Working")]
    [InlineData(AgentActivityStatus.Working, 1, false, "Working")]
    [InlineData(AgentActivityStatus.NeedsInput, null, false, "Needs input")]
    [InlineData(AgentActivityStatus.UsageLimited, null, false, "Usage limit")]
    [InlineData(AgentActivityStatus.Unknown, null, false, "Unclear")]
    public void BackgroundCommandsDoNotOverrideTheCurrentTurnStatus(AgentActivityStatus status, int? background, bool seen, string expected)
    {
        var now = DateTime.UtcNow;
        AgentDto agent = new(Guid.NewGuid(), Guid.Empty, Guid.Empty, "owner", AgentAccess.Owner,
            "runner", RunnerState.Ready, [], [], "agent", "claude", "/work", null, null, AgentState.Running,
            null, null, null, null, Guid.Empty, "owner", now, now,
            Activity: new(status, "reason", "rule", null, now, now, 1, false, now, 1, seen ? 1 : 0, background, CurrentTurnCompleted: true));
        Assert.Equal(expected, StateStyles.For(agent).Text);
        Assert.Equal(status == AgentActivityStatus.Working, StateStyles.For(agent).Busy);
        Assert.Equal(expected == "Finished", StateStyles.ShowsFinished(agent));
        var interrupted = agent with { Activity = agent.Activity! with { CurrentTurnCompleted = false } };
        Assert.False(StateStyles.ShowsFinished(interrupted));
        if (status == AgentActivityStatus.Ready) Assert.Equal("Ready", StateStyles.For(interrupted).Text);
        Assert.False(StateStyles.ShowsFinished(agent with { State = AgentState.Stopped }));
        Assert.Equal("Stopped", StateStyles.For(agent with { State = AgentState.Stopped }).Text);
    }
}
