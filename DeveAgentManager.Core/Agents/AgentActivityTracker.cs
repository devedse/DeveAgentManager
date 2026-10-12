using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Core.Agents;

/// <summary>Durable observations make restarts, redraw gaps and completion acknowledgement explicit.</summary>
public static class AgentActivityTracker
{
    public sealed record Observation(AgentActivityDto Snapshot, string Identity, bool TurnObserved,
        AgentActivityStatus? Pending, DateTime? PendingSince, string? LastCompletion, string? PendingCompletion = null);

    public static Observation Read(Agent agent)
    {
        try { if (agent.ActivityJson is { } json && JsonSerializer.Deserialize<Observation>(json) is { Snapshot: not null } saved) return saved; }
        catch (JsonException) { }
        var invalid = agent.ActivityJson is not null;
        return new(new(AgentActivityStatus.Unknown, invalid ? "The saved activity observation could not be read. Waiting for the Manager to observe the terminal again." : "The harness has not been observed yet.", invalid ? "observation-invalid" : "not-observed", null,
            null, agent.UpdatedAt, agent.SeenCompletionSequence, false, null, agent.SeenCompletionSequence, agent.SeenCompletionSequence), "", false, null, null, null);
    }

    public static AgentActivityDto GetDto(Agent agent, DateTime? at = null, RunnerState? runnerState = null)
    {
        var snapshot = Read(agent).Snapshot with { SeenCompletionSequence = agent.SeenCompletionSequence };
        var now = at ?? DateTime.UtcNow;
        if (agent.State == AgentState.Running && snapshot.Rule == "not-observed" && now - snapshot.Since > TimeSpan.FromSeconds(30))
            snapshot = snapshot with { Reason = "The Manager has not saved a terminal observation for this running agent. Check the Runner connection and Manager monitor logs." };
        if (agent.State == AgentState.Running && ((runnerState ?? agent.Runner?.State) is { } state && state != RunnerState.Ready || snapshot.ObservedAt is { } observed && now - observed > TimeSpan.FromSeconds(30)))
            return snapshot with { Status = AgentActivityStatus.Unknown, Rule = "observation-stale",
                Reason = "A fresh terminal observation is unavailable. The previous status may be out of date.",
                Since = snapshot.ObservedAt?.AddSeconds(30) ?? snapshot.Since, QuestionWaiting = false, BackgroundTaskCount = null };
        return snapshot;
    }

    public static bool Apply(Agent agent, AgentScreenDetection detection, string identity, DateTime now)
    {
        var old = Read(agent);
        var previous = old.Snapshot;
        var changedProcess = old.Identity != identity;
        var fingerprint = detection.CompletionEvidence is { } evidence ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))) : null;
        var observation = changedProcess ? old with { Identity = identity, TurnObserved = false, Pending = null, PendingSince = null,
            LastCompletion = fingerprint, Snapshot = previous with { CompletedAt = null } } : old;
        previous = observation.Snapshot;
        var status = detection.Status;
        var reason = detection.Reason;
        // Readiness and completion are independent: a recognized idle composer needs no response
        // fingerprint. Only confirmed completion evidence can produce a Finished notification.
        if (status is AgentActivityStatus.Ready or AgentActivityStatus.Unknown)
        {
            var pendingSince = observation.Pending == status && (status != AgentActivityStatus.Ready || observation.PendingCompletion == fingerprint) ? observation.PendingSince ?? now : now;
            observation = observation with { Pending = status, PendingSince = pendingSince, PendingCompletion = fingerprint };
            var delay = status == AgentActivityStatus.Ready ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(10);
            if (!changedProcess && previous.Status == AgentActivityStatus.Working && now - pendingSince < delay)
            { status = AgentActivityStatus.Working; reason = "The live activity indicator changed; waiting for a stable observation."; }
        }
        else observation = observation with { Pending = null, PendingSince = null };
        if (detection.Status == AgentActivityStatus.Working)
            observation = observation with { TurnObserved = true,
                LastCompletion = !observation.TurnObserved && fingerprint is not null ? fingerprint : observation.LastCompletion };
        var completed = !changedProcess && status == AgentActivityStatus.Ready && observation.TurnObserved
            && fingerprint is not null && fingerprint != observation.LastCompletion
            && observation.PendingSince is { } stableSince && now - stableSince >= TimeSpan.FromSeconds(5);
        var meaningfulChange = changedProcess || status != previous.Status || detection.QuestionWaiting != previous.QuestionWaiting
            || detection.BackgroundTaskCount != previous.BackgroundTaskCount
            || reason != previous.Reason || detection.Rule != previous.Rule || completed;
        var sequence = previous.Sequence + (meaningfulChange ? 1 : 0);
        var snapshot = new AgentActivityDto(status, reason, detection.Rule, detection.Evidence, now,
            changedProcess || status != previous.Status ? now : previous.Since, sequence,
            status == AgentActivityStatus.Working && detection.QuestionWaiting,
            completed ? now : previous.CompletedAt, completed ? sequence : previous.CompletionSequence, agent.SeenCompletionSequence, detection.BackgroundTaskCount);
        if (status == AgentActivityStatus.Ready && fingerprint is not null && (completed || !observation.TurnObserved))
            observation = observation with { LastCompletion = fingerprint, TurnObserved = false, Pending = null, PendingSince = null };
        observation = observation with { Snapshot = snapshot };
        agent.ActivityJson = JsonSerializer.Serialize(observation);
        agent.Working = status == AgentActivityStatus.Working;
        return meaningfulChange;
    }

    public static void Reset(Agent agent)
    {
        var old = Read(agent);
        agent.ActivityJson = null;
        agent.SeenCompletionSequence = old.Snapshot.Sequence; // acknowledge the conversation being restarted/switched
        agent.Working = false;
    }
}
