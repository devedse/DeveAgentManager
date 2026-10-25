using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// For which usage limit of each agent this browser answered the offer to turn on auto-continue (Not now, or the full
/// options were opened), so the same limit isn't offered again; the next limit is. Kept in this browser only; without
/// storage, an answer lasts for the current visit.
/// </summary>
public sealed class ContinueOffers(IJSRuntime js)
{
    private readonly Dictionary<Guid, long> _answered = [];

    public event Action? Changed;

    /// <param name="detectedAt">When the limit was first seen, which identifies it.</param>
    public async Task<bool> AnsweredAsync(Guid agentId, DateTime detectedAt)
    {
        if (!_answered.TryGetValue(agentId, out var answered))
        {
            try { answered = long.TryParse(await js.InvokeAsync<string?>("localStorage.getItem", Key(agentId)), out var stored) ? stored : 0; }
            catch (JSException) { answered = 0; }
            _answered[agentId] = answered;
        }
        return answered == detectedAt.Ticks;
    }

    public async Task AnswerAsync(Guid agentId, DateTime detectedAt)
    {
        if (_answered.TryGetValue(agentId, out var answered) && answered == detectedAt.Ticks) return;
        _answered[agentId] = detectedAt.Ticks;
        try { await js.InvokeVoidAsync("localStorage.setItem", Key(agentId), detectedAt.Ticks.ToString()); }
        catch (JSException) { } // Private browsing may disallow storage.
        Changed?.Invoke();
    }

    private static string Key(Guid agentId) => $"dam-continue-offer-{agentId:N}";
}
