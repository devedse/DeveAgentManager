using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Core.Agents;

internal static class UsageLimitState
{
    internal static string Session(Agent agent) => $"{agent.HarnessId}|{agent.HarnessSessionId}|{agent.AccountId}";

    // Anchor time-only reset messages to when they were first seen, never to the current day.
    internal static DateTime? DetectedReset(AgentLimit limit) => UsageLimitParser.Parse(
        limit.SessionId.Split('|')[0], limit.Message + "\n❯ ", limit.TimeZoneId, limit.DetectedAt)?.ResetAt;

    internal static bool HasExpired(AgentLimit limit, DateTime now) => DetectedReset(limit) is { } reset && reset <= now;

    internal static AgentScreenDetection ApplyExpiry(Agent agent, AgentLimit? limit, string screen, string? identity,
        AgentScreenDetection detection, DateTime now)
    {
        // Batched activity snapshots use pipes; the continuation probe stores the same fields with colons.
        if (detection.Status != AgentActivityStatus.UsageLimited || limit is null || limit.SessionId != Session(agent)
            || limit.PaneIdentity != identity?.Replace('|', ':') || !HasExpired(limit, now)) return detection;
        var current = UsageLimitParser.Parse(agent.HarnessId, screen, limit.TimeZoneId, now);
        if (current?.Message != limit.Message) return detection;
        return detection with { Status = AgentActivityStatus.Ready, Rule = "usage-limit-expired",
            Reason = "The detected usage-limit reset has passed. The prompt was paused; continue it when ready." };
    }
}
