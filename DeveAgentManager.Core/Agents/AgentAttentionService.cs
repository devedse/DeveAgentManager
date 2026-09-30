using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Agents;

public sealed class AgentAttentionService(IDbContextFactory<ManagerDbContext> factory, ManagerEvents events, TimeProvider clock)
{
    public async Task<List<AgentAttentionDto>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var agents = await db.Agents.AsNoTracking().Include(a => a.Runner).ToListAsync(ct);
        var managedLimits = await db.AgentLimits.AsNoTracking().Where(l => l.Status != "Cleared" && l.NativeAutoContinue || l.Status == "Scheduled" || l.Status == "Sending" || l.Status == "Sent")
            .Select(l => l.AgentId).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return agents.Select(a => Attention(a, managedLimits.Contains(a.Id), now)).OfType<AgentAttentionDto>()
            .OrderBy(a => a.Kind).ThenBy(a => a.At).ThenBy(a => a.Name).ToList();
    }

    public static AgentAttentionDto? Attention(Agent agent, bool limitManaged, DateTime now)
    {
        var activity = AgentActivityTracker.GetDto(agent, now);
        AgentAttentionKind? kind = null;
        var reason = activity.Reason;
        var at = activity.Since;
        if (agent.State is AgentState.Exited or AgentState.Error || agent.State == AgentState.Stopped && agent.StateMessage is not null)
        { kind = AgentAttentionKind.UnexpectedExit; reason = agent.StateMessage ?? "The harness exited unexpectedly."; at = agent.UpdatedAt; }
        else if (agent.State == AgentState.Running)
        {
            if (activity.Status == AgentActivityStatus.NeedsInput) kind = AgentAttentionKind.NeedsInput;
            else if (activity.QuestionWaiting) { kind = AgentAttentionKind.QuestionWaiting; reason = "A queued question can be answered while the agent continues working."; }
            else if (activity.Status == AgentActivityStatus.UsageLimited && !limitManaged) kind = AgentAttentionKind.UsageLimit;
            else if (activity.UnseenCompletion) { kind = AgentAttentionKind.Finished; reason = "Finished since you last viewed this agent."; at = activity.CompletedAt!.Value; }
            else if (activity.Status == AgentActivityStatus.Unknown && now - activity.Since >= TimeSpan.FromSeconds(30)) kind = AgentAttentionKind.Unclear;
        }
        return kind is null ? null : new(agent.Id, agent.Name, agent.Runner?.Name ?? "Runner", agent.HarnessId, kind.Value, reason, at, activity.CompletionSequence);
    }

    public async Task MarkSeenAsync(Guid id, long completionSequence, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var current = AgentActivityTracker.Read(agent).Snapshot.CompletionSequence;
        if (completionSequence < 0 || completionSequence > current) throw new ArgumentException("Review the current completion before marking it seen.");
        // A delayed request can acknowledge only what that screen actually displayed, never a newer completion.
        var changed = await db.Agents.Where(a => a.Id == id && a.SeenCompletionSequence < completionSequence)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SeenCompletionSequence, completionSequence), ct);
        if (changed > 0) events.AgentChanged(agent.RunnerId, id);
    }
}
