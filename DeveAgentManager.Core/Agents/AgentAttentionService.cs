using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Agents;

/// <summary>
/// Agents that need attention (a question, an unexpected exit, a finished task...), for the notification bell. Each user
/// gets those of their own agents and of agents shared with them. Seen and dismissed are kept per agent, so they count
/// for everyone who shares it.
/// </summary>
public sealed class AgentAttentionService(IDbContextFactory<ManagerDbContext> factory, ManagerEvents events, TimeProvider clock)
{
    public async Task<List<AgentAttentionDto>> ListAsync(CurrentUser user, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var agents = await db.Agents.AsNoTracking().Include(a => a.Runner)
            .Where(a => a.OwnerId == user.Id || a.Shares.Any(s => s.UserId == user.Id)).ToListAsync(ct);
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
            else if ((activity.Status == AgentActivityStatus.UsageLimited && !limitManaged) || activity.Rule == "usage-limit-expired") kind = AgentAttentionKind.UsageLimit;
            else if (activity.UnseenCompletion) { kind = AgentAttentionKind.Finished; reason = "Finished since you last viewed this agent."; at = activity.CompletedAt!.Value; }
            else if (activity.Status == AgentActivityStatus.Unknown && now - activity.Since >= TimeSpan.FromSeconds(30)) kind = AgentAttentionKind.Unclear;
        }
        if (kind is null) return null;
        // Poll timestamps and elapsed working timers must not revive a dismissed alert. A new
        // semantic observation, process or changed blocking message does create a new alert.
        var identity = AgentActivityTracker.Read(agent).Identity;
        var key = kind == AgentAttentionKind.UsageLimit
            ? JsonSerializer.Serialize(new object?[] { kind, identity, activity.Evidence })
            : kind == AgentAttentionKind.Finished
            ? JsonSerializer.Serialize(new object?[] { kind, identity, activity.CompletionSequence })
            : JsonSerializer.Serialize(new object?[] { kind, identity, activity.Sequence,
                kind is AgentAttentionKind.NeedsInput or AgentAttentionKind.UsageLimit ? activity.Evidence : null,
                kind == AgentAttentionKind.UnexpectedExit ? agent.UpdatedAt.ToString("O") + reason : null });
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return token == agent.DismissedAttentionToken ? null
            : new(agent.Id, agent.Name, agent.Runner?.Name ?? "Runner", agent.HarnessId, kind.Value, reason, at, activity.CompletionSequence, token);
    }

    public async Task DismissAsync(Guid id, string token, CancellationToken ct)
    {
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("Choose a current notification to dismiss.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var agent = await db.Agents.AsNoTracking().Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException("Agent not found.");
        var managed = await db.AgentLimits.AnyAsync(l => l.AgentId == id &&
            ((l.Status != "Cleared" && l.NativeAutoContinue) || l.Status == "Scheduled" || l.Status == "Sending" || l.Status == "Sent"), ct);
        if (Attention(agent, managed, clock.GetUtcNow().UtcDateTime)?.Token != token) return;
        // Save only the dismissed identity; a newer alert remains visible even if it races this write.
        await db.Agents.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DismissedAttentionToken, token), ct);
        events.AgentChanged(agent.RunnerId, id);
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
