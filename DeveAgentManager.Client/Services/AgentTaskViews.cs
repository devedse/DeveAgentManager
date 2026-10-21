using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// Which revision of each agent's task list this browser has seen, so the tasks button marks an update until its list is
/// looked at. Kept in this browser only; without storage, updates are marked for the current visit.
/// </summary>
public sealed class AgentTaskViews(IJSRuntime js)
{
    private readonly Dictionary<Guid, long> _seen = [];

    public event Action? Changed;

    public async Task<long> SeenAsync(Guid agentId)
    {
        if (_seen.TryGetValue(agentId, out var seen)) return seen;
        try { seen = long.TryParse(await js.InvokeAsync<string?>("localStorage.getItem", Key(agentId)), out var stored) ? stored : 0; }
        catch (JSException) { seen = 0; }
        return _seen[agentId] = seen;
    }

    /// <summary>The list was looked at as of <paramref name="revision"/>.</summary>
    public async Task MarkSeenAsync(Guid agentId, long revision)
    {
        // Any other revision counts as an update, also a lower one (a list that started again, e.g. after a move).
        if (_seen.TryGetValue(agentId, out var seen) && seen == revision) return;
        _seen[agentId] = revision;
        try { await js.InvokeVoidAsync("localStorage.setItem", Key(agentId), revision.ToString()); }
        catch (JSException) { } // Private browsing may disallow storage.
        Changed?.Invoke();
    }

    private static string Key(Guid agentId) => $"dam-tasks-seen-{agentId:N}";
}
