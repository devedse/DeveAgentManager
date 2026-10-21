using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Agents;

/// <summary>Resolve display names in one query, without loading login credentials or guessing a live default.</summary>
public static class AgentAccountNames
{
    public static async Task LoadAsync(ManagerDbContext db, IEnumerable<Agent> agents, CancellationToken ct)
    {
        var items = agents.ToList();
        foreach (var agent in items)
        {
            agent.RunningAccountName = null;
            agent.RunningAccountUsername = null;
        }
        await LoadGitIdentitiesAsync(db, items, ct);
        var ids = items.Where(a => a.RunningAccountId != null).Select(a => a.RunningAccountId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;
        var names = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, Username = a.CreatedBy == null ? null : a.CreatedBy.Username })
            .ToDictionaryAsync(a => a.Id, ct);
        foreach (var agent in items)
        {
            if (agent.RunningAccountId is not { } id || !names.TryGetValue(id, out var account)) continue;
            agent.RunningAccountName = account.Name;
            agent.RunningAccountUsername = account.Username;
        }
    }

    // Whose git identity each agent commits with, now and from its next start. A deleted user stays unnamed.
    private static async Task LoadGitIdentitiesAsync(ManagerDbContext db, List<Agent> agents, CancellationToken ct)
    {
        var ids = agents.SelectMany(a => new[] { AgentService.GitIdentity(a), AgentService.RunningGitIdentity(a) }).Distinct().ToArray();
        if (ids.Length == 0) return;
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Username, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => (u.Username, u.DisplayName), ct);
        foreach (var agent in agents)
        {
            agent.GitIdentityName = names.TryGetValue(AgentService.GitIdentity(agent), out var chosen) ? chosen : null;
            agent.RunningGitIdentityName = names.TryGetValue(AgentService.RunningGitIdentity(agent), out var running) ? running : null;
        }
    }
}
