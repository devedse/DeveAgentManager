using DeveAgentManager.Client.Shared;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Everywhere agents are listed: the one that replied last first.</summary>
public sealed class AgentOrderTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static AgentDto Agent(string name, DateTime? lastReply, DateTime created) => new(Guid.NewGuid(), Guid.Empty, Guid.Empty, "owner", AgentAccess.Owner,
        "runner", RunnerState.Ready, [], [], name, "claude", "/work", null, null, AgentState.Running, null, null, null, null, Guid.Empty, "owner",
        created, created, LastAssistantMessageAt: lastReply);

    [Fact]
    public void LastReplyFirst_ThenAgentsThatNeverRepliedNewestFirst()
    {
        var agents = new[]
        {
            Agent("replied an hour ago", Now.AddHours(-1), Now.AddDays(-9)),
            Agent("never replied, old", null, Now.AddDays(-5)),
            Agent("replied just now", Now, Now.AddDays(-30)),
            Agent("never replied, new", null, Now.AddDays(-1)),
            Agent("b same moment", Now.AddMinutes(-5), Now.AddDays(-2)),
            Agent("a same moment", Now.AddMinutes(-5), Now.AddDays(-2)),
        };

        Assert.Equal(["replied just now", "a same moment", "b same moment", "replied an hour ago", "never replied, new", "never replied, old"],
            agents.ByLastReply().Select(a => a.Name));
    }
}
