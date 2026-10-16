using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Core.Agents;

internal static class UsageLimitState
{
    internal static string Session(Agent agent) => $"{agent.HarnessId}|{agent.HarnessSessionId}|{agent.AccountId}";

    // Anchor time-only reset messages to when they were first seen, never to the current day.
    internal static DateTime? DetectedReset(AgentLimit limit) => UsageLimitParser.InspectStored(
        limit.SessionId.Split('|')[0], limit.Message, limit.TimeZoneId, limit.DetectedAt).Limit?.ResetAt;

    internal static bool HasExpired(AgentLimit limit, DateTime now) => DetectedReset(limit) is { } reset && reset <= now;

    internal static AgentScreenDetection ApplyResetStatus(Agent agent, AgentLimit? limit, string screen, string? identity,
        AgentScreenDetection detection, DateTime now, UsageLimitReader? reader = null)
    {
        // Batched activity snapshots use pipes; the continuation probe stores the same fields with colons.
        if (detection.Status != AgentActivityStatus.UsageLimited || limit is null || limit.SessionId != Session(agent)
            || limit.PaneIdentity != identity?.Replace('|', ':')) return detection;
        reader ??= UsageLimitReader.Default;
        var stored = reader.Stored(agent.Id, limit);
        if (!stored.Available) return detection with { Status = AgentActivityStatus.Unknown, Rule = "usage-limit-unavailable", Reason = stored.Problem! };
        if (stored.Limit?.ResetAt is not { } reset || reset > now) return detection;
        var current = reader.Screen(agent, new(screen, identity ?? "", limit.TimeZoneId, false, ""), now);
        if (!current.Available) return detection with { Status = AgentActivityStatus.Unknown, Rule = "usage-limit-unavailable", Reason = current.Problem! };
        if (current.Limit?.Message != limit.Message) return detection;
        return detection with { Rule = "usage-limit-reset-passed",
            Reason = "The advertised reset has passed, but the usage-limit error is still visible. The agent has not resumed." };
    }
}
