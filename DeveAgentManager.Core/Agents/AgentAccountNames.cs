using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Agents;

/// <summary>Resolve display names in one query, without loading login credentials or guessing a live default.</summary>
public static class AgentAccountNames
{
    public static async Task LoadAsync(ManagerDbContext db, IEnumerable<Agent> agents, CancellationToken ct)
    {
        var items = agents.ToList();
        var ids = items.Where(a => a.RunningAccountId != null).Select(a => a.RunningAccountId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;
        var names = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        foreach (var agent in items)
            agent.RunningAccountName = agent.RunningAccountId is { } id ? names.GetValueOrDefault(id) : null;
    }
}
