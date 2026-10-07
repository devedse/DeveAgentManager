using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using Xunit;

namespace DeveAgentManager.Tests;

public class UsageLimitStateTests
{
    [Theory]
    [InlineData("codex", "Europe/Amsterdam", "■ You've hit your usage limit. Try again at 3:00 PM.")]
    [InlineData("claude", "UTC", "└ You've hit your weekly limit · resets Oct 2, 3pm (Europe/Amsterdam)")]
    public void ExpiredLimitBecomesReadyWithoutLosingPausedNotification(string harness, string zone, string message)
    {
        var before = new DateTime(2026, 10, 2, 12, 59, 0, DateTimeKind.Utc);
        var after = before.AddMinutes(1);
        var agent = new Agent { Id = Guid.NewGuid(), HarnessId = harness, State = AgentState.Running };
        var screen = message + "\n❯ ";
        // Stored as parsed: Claude's └ / ⎿ connector is not part of the message.
        var limit = new AgentLimit { SessionId = UsageLimitState.Session(agent), PaneIdentity = "%1:123:456:boot-id",
            Message = UsageLimitParser.Parse(harness, screen, zone, before)!.Message,
            TimeZoneId = zone, DetectedAt = before, ResetAt = after.AddHours(1) }; // A manual override must not change expiry.
        var detection = new AgentScreenDetection(AgentActivityStatus.UsageLimited, "limit", "usage-limit", message);
        Assert.Equal(detection, UsageLimitState.ApplyExpiry(agent, limit, screen, "%1|123|456|boot-id", detection, before));
        AgentActivityTracker.Apply(agent, detection, "%1|123|456|boot-id", before);
        var token = AgentAttentionService.Attention(agent, false, before)!.Token;
        var expired = UsageLimitState.ApplyExpiry(agent, limit, screen, "%1|123|456|boot-id", detection, after);
        Assert.Equal(AgentActivityStatus.Ready, expired.Status);
        AgentActivityTracker.Apply(agent, expired, "%1|123|456|boot-id", after);
        var attention = AgentAttentionService.Attention(agent, false, after)!;
        Assert.Equal(AgentAttentionKind.UsageLimit, attention.Kind);
        Assert.Contains("paused", attention.Reason);
        Assert.Equal(token, attention.Token);
        agent.DismissedAttentionToken = token;
        Assert.Null(AgentAttentionService.Attention(agent, false, after));
        Assert.True(UsageLimitState.HasExpired(limit, after.AddDays(1))); // No time-only rollover to tomorrow.
        Assert.Equal(detection, UsageLimitState.ApplyExpiry(agent, limit, screen, "another-pane", detection, after));
        Assert.Equal(detection, UsageLimitState.ApplyExpiry(agent, limit, screen.Replace("3", "4"), "%1|123|456|boot-id", detection, after));
        var working = detection with { Status = AgentActivityStatus.Working };
        Assert.Equal(working, UsageLimitState.ApplyExpiry(agent, limit, screen, "%1|123|456|boot-id", working, after));
    }
}
