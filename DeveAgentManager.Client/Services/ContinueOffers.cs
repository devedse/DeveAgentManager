using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// For which usage limit of each agent this browser answered the offer to turn on auto-continue (Not now, or the full
/// options were opened), so the same reset time isn't offered again even after a fresh observation. Kept in this
/// browser only; without storage, an answer lasts for the current visit.
/// </summary>
public sealed class ContinueOffers(IJSRuntime js)
{
    private readonly Dictionary<Guid, long> _answered = [];

    public event Action? Changed;

    /// <param name="resetAt">The detected reset instant, independent of when or how the rejection was observed.</param>
    public async Task<bool> AnsweredAsync(Guid agentId, DateTime resetAt)
    {
        if (!_answered.TryGetValue(agentId, out var answered))
        {
            try { answered = long.TryParse(await js.InvokeAsync<string?>("localStorage.getItem", Key(agentId)), out var stored) ? stored : 0; }
            catch (JSException) { answered = 0; }
            _answered[agentId] = answered;
        }
        return answered == resetAt.Ticks;
    }

    public async Task AnswerAsync(Guid agentId, DateTime resetAt)
    {
        if (_answered.TryGetValue(agentId, out var answered) && answered == resetAt.Ticks) return;
        _answered[agentId] = resetAt.Ticks;
        try { await js.InvokeVoidAsync("localStorage.setItem", Key(agentId), resetAt.Ticks.ToString()); }
        catch (JSException) { } // Private browsing may disallow storage.
        Changed?.Invoke();
    }

    // Separate from the old observation-time key: an observation timestamp is not a dismissed reset.
    private static string Key(Guid agentId) => $"dam-continue-offer-reset-{agentId:N}";
}
