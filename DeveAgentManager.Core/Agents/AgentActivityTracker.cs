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
        AgentActivityStatus? Pending, DateTime? PendingSince, string? LastCompletion, string? PendingCompletion = null,
        string? SessionIdentity = null, NativeTurnObservation? NativeTurn = null, string? LastNativeCompletion = null);

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
        var saved = Read(agent);
        var snapshot = saved.Snapshot with { SeenCompletionSequence = agent.SeenCompletionSequence,
            CurrentTurnCompleted = saved.Snapshot.CurrentTurnCompleted && saved.SessionIdentity == UsageLimitState.Session(agent) };
        var now = at ?? DateTime.UtcNow;
        if (agent.State == AgentState.Running && snapshot.Rule == "not-observed" && now - snapshot.Since > TimeSpan.FromSeconds(30))
            snapshot = snapshot with { Reason = "The Manager has not saved a terminal observation for this running agent. Check the Runner connection and Manager monitor logs." };
        if (agent.State == AgentState.Running && ((runnerState ?? agent.Runner?.State) is { } state && state != RunnerState.Ready || snapshot.ObservedAt is { } observed && now - observed > TimeSpan.FromSeconds(30)))
            return snapshot with { Status = AgentActivityStatus.Unknown, Rule = "observation-stale",
                Reason = "A fresh terminal observation is unavailable. The previous status may be out of date.",
                Since = snapshot.ObservedAt?.AddSeconds(30) ?? snapshot.Since, QuestionWaiting = false, BackgroundTaskCount = null };
        return snapshot;
    }

    public static bool Apply(Agent agent, AgentScreenDetection detection, string identity, DateTime now,
        NativeTurnObservation? nativeTurn = null)
    {
        var old = Read(agent);
        var previous = old.Snapshot;
        var session = UsageLimitState.Session(agent);
        var screenFingerprint = detection.CompletionEvidence is { } screenEvidence ? Hash(screenEvidence) : null;
        if (agent.HarnessId != "codex" || nativeTurn?.SessionId != agent.HarnessSessionId
            || string.IsNullOrWhiteSpace(nativeTurn?.TurnId) || nativeTurn.At > now.AddSeconds(5)
            || nativeTurn.State is not ("Started" or "Completed" or "Aborted")) nativeTurn = null;
        // A native lifecycle event identifies a turn independently of polling cadence and footer formatting.
        // Live approval/limit/working controls keep precedence over an older completion in the session file.
        var nativeReady = detection.Status == AgentActivityStatus.Ready;
        if (nativeTurn is { State: "Started" } && nativeReady)
            detection = detection with { Status = AgentActivityStatus.Working, Rule = "codex/native-turn-started",
                Reason = "Codex is working on its current turn.", Evidence = "Native session recorded task_started.", CompletionEvidence = null };
        var nativeComplete = nativeTurn is { State: "Completed" } && nativeReady;
        if (nativeComplete)
            detection = detection with { Reason = "Codex completed its turn and its prompt is ready for input.",
                Rule = "codex/native-turn-completed", Evidence = "Native session recorded task_complete." };
        else if (nativeTurn is { State: "Aborted" } && nativeReady)
            detection = detection with { Reason = "Codex's last turn was interrupted; its prompt is ready for input.",
                Rule = "codex/native-turn-aborted", Evidence = "Native session recorded turn_aborted." };
        var changedProcess = old.Identity != identity || old.SessionIdentity is not null && old.SessionIdentity != session;
        var completionEvidence = nativeComplete ? $"native:{nativeTurn!.SessionId}:{nativeTurn.TurnId}"
            : nativeTurn is not null ? null : detection.CompletionEvidence;
        var fingerprint = completionEvidence is { } evidence ? Hash(evidence) : null;
        var observation = changedProcess ? old with { Identity = identity, TurnObserved = false, Pending = null, PendingSince = null,
            LastCompletion = nativeTurn is not null ? old.LastCompletion : fingerprint,
            Snapshot = previous with { CurrentTurnCompleted = false } } : old;
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
                LastCompletion = !observation.TurnObserved && screenFingerprint is not null ? screenFingerprint : observation.LastCompletion };
        if (nativeTurn is { State: "Aborted" }) observation = observation with { TurnObserved = false };
        // Upgrade a previously recorded completion using fresh, session-bound proof. Keep its existing
        // acknowledgement sequence, so viewing it before the upgrade cannot create a second notification.
        var recovered = nativeComplete && fingerprint != old.LastNativeCompletion
            && (old.NativeTurn is null || previous.CurrentTurnCompleted) && previous.CompletedAt is { } recorded
            && nativeTurn!.At <= recorded && (old.SessionIdentity == session || old.SessionIdentity is null && old.Identity == identity)
            && previous.Status == AgentActivityStatus.Ready;
        var completed = recovered || status == AgentActivityStatus.Ready && (observation.TurnObserved || nativeComplete)
            && fingerprint is not null && fingerprint != (nativeComplete ? observation.LastNativeCompletion : observation.LastCompletion)
            && observation.PendingSince is { } stableSince && now - stableSince >= TimeSpan.FromSeconds(5);
        var turnCompleted = completed || previous.CurrentTurnCompleted && detection.Status is not
            (AgentActivityStatus.Working or AgentActivityStatus.NeedsInput or AgentActivityStatus.UsageLimited)
            && nativeTurn is not { State: "Aborted" or "Started" };
        var meaningfulChange = changedProcess || status != previous.Status || turnCompleted != previous.CurrentTurnCompleted || detection.QuestionWaiting != previous.QuestionWaiting
            || detection.BackgroundTaskCount != previous.BackgroundTaskCount
            || reason != previous.Reason || detection.Rule != previous.Rule || completed;
        var sequence = previous.Sequence + (meaningfulChange ? 1 : 0);
        var snapshot = new AgentActivityDto(status, reason, detection.Rule, detection.Evidence, now,
            changedProcess || status != previous.Status ? now : previous.Since, sequence,
            status == AgentActivityStatus.Working && detection.QuestionWaiting,
            completed ? nativeComplete ? nativeTurn!.At : now : previous.CompletedAt,
            completed && !recovered ? sequence : previous.CompletionSequence, agent.SeenCompletionSequence, detection.BackgroundTaskCount, turnCompleted);
        if (status == AgentActivityStatus.Ready && fingerprint is not null && (completed || !observation.TurnObserved && !nativeComplete))
            observation = observation with { LastCompletion = screenFingerprint ?? observation.LastCompletion,
                LastNativeCompletion = completed && nativeComplete ? fingerprint : observation.LastNativeCompletion,
                TurnObserved = false, Pending = null, PendingSince = null };
        observation = observation with { Snapshot = snapshot, SessionIdentity = session, NativeTurn = nativeTurn ?? observation.NativeTurn };
        agent.ActivityJson = JsonSerializer.Serialize(observation);
        agent.Working = status == AgentActivityStatus.Working;
        return meaningfulChange;
    }

    private static string Hash(string evidence) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));

    public static void Reset(Agent agent)
    {
        var old = Read(agent);
        agent.SeenCompletionSequence = old.Snapshot.Sequence; // acknowledge the conversation being restarted/switched
        // Retain completion identity across a harness restart, so resuming the same native turn does not
        // manufacture a new completion. A later native turn still has a different identity.
        agent.ActivityJson = JsonSerializer.Serialize(old with { Identity = "", TurnObserved = false, Pending = null,
            PendingSince = null, PendingCompletion = null, Snapshot = old.Snapshot with { Status = AgentActivityStatus.Unknown,
                Rule = "not-observed", Reason = "Waiting for the restarted harness to be observed.",
                CurrentTurnCompleted = false, CompletedAt = null, SeenCompletionSequence = agent.SeenCompletionSequence } });
        agent.Working = false;
    }
}
