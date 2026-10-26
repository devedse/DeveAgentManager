using DeveAgentManager.Contracts.Pets;
using DeveAgentManager.Core.Events;
namespace DeveAgentManager.Client.Services;

/// <summary>One pet snapshot per Portal tab; page navigation never owns its lifetime.</summary>
public sealed class PetClient(ManagerApi api, LiveUpdates live, SignedInUser me)
{
    public MyPetDto? State { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public bool Threatened { get; private set; }
    public void PreviewRetirement(bool value)
    {
        if (Threatened == value) return;
        Threatened = value; Changed?.Invoke();
    }
    private bool started;
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task StartAsync()
    {
        if (started) return; started = true;
        live.Changed += e => { if (e.Kind == ManagerEventKind.PetChanged) _ = ReloadAsync(); };
        live.Reconnected += () => _ = ReloadAsync();
        me.Changed += () => _ = ReloadAsync();
        await ReloadAsync();
    }
    public async Task ReloadAsync()
    {
        await gate.WaitAsync();
        try { State = await api.MyPetAsync(); Error = null; }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { Error = ex is ApiException ? ex.Message : "The Manager could not be reached."; }
        finally { gate.Release(); }
        Changed?.Invoke();
    }
}
