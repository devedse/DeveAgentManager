using DeveAgentManager.Core.Events;
using Microsoft.JSInterop;
using MudBlazor;

namespace DeveAgentManager.Client.Services;

/// <summary>The account's saved preference and the browser tab's wake lock, shared by every page.</summary>
public sealed class ScreenAwakeService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly ManagerApi _api;
    private readonly SignedInUser _me;
    private readonly LiveUpdates _live;
    private readonly ISnackbar _snackbar;
    private readonly SemaphoreSlim _updates = new(1, 1);
    private Task? _initialize;
    private Guid _userId;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ScreenAwakeService>? _self;

    public ScreenAwakeService(IJSRuntime js, ManagerApi api, SignedInUser me, LiveUpdates live, ISnackbar snackbar)
    {
        (_js, _api, _me, _live, _snackbar) = (js, api, me, live, snackbar);
        live.Changed += OnManagerChanged;
        live.Reconnected += OnReconnected;
    }

    public bool Ready { get; private set; }
    public bool Busy { get; private set; }
    public bool Enabled { get; private set; }
    public bool Active { get; private set; }
    public string? Unavailable { get; private set; }
    public string Hint => Unavailable ?? (!Ready ? "Keep screen awake: loading" : !Enabled
        ? "Keep screen awake: off" : Active ? "Keep screen awake: on" : "Keep screen awake: paused");
    public event Action? Changed;

    public Task InitializeAsync() => _initialize ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            _userId = (await _me.GetAsync()).Id;
            Enabled = (await _api.GetScreenAwakeAsync()).Enabled;
            _module = await _js.InvokeAsync<IJSObjectReference>("import", "./js/screen-awake.js");
            _self = DotNetObjectReference.Create(this);
            Unavailable = await _module.InvokeAsync<string?>("start", _self, Enabled);
            Ready = true;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or JSException)
        {
            Unavailable = "Could not load the screen-awake setting. Refresh to try again.";
        }
        Changed?.Invoke();
    }

    public async Task ToggleAsync()
    {
        await InitializeAsync();
        if (!Ready || Busy || Unavailable is not null) return;
        await _updates.WaitAsync();
        Busy = true;
        Changed?.Invoke();
        try
        {
            Enabled = (await _api.SaveScreenAwakeAsync(!Enabled)).Enabled;
            await _module!.InvokeVoidAsync("setEnabled", Enabled);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or JSException)
        {
            _snackbar.Add("Could not save the screen-awake setting. Try again.", Severity.Warning);
        }
        finally
        {
            Busy = false;
            _updates.Release();
            Changed?.Invoke();
        }
    }

    [JSInvokable]
    public void ScreenAwakeChanged(bool active, string? error)
    {
        Active = active;
        if (error is not null) _snackbar.Add(error, Severity.Warning);
        Changed?.Invoke();
    }

    private void OnManagerChanged(ManagerEvent change)
    {
        if (change.Kind == ManagerEventKind.SettingsChanged && change.UserId == _userId) OnReconnected();
    }

    private void OnReconnected()
    {
        if (Ready) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        await _updates.WaitAsync();
        try
        {
            Enabled = (await _api.GetScreenAwakeAsync()).Enabled;
            if (_module is not null) await _module.InvokeVoidAsync("setEnabled", Enabled);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or JSException) { }
        finally { _updates.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _live.Changed -= OnManagerChanged;
        _live.Reconnected -= OnReconnected;
        if (_module is not null)
            try { await _module.InvokeVoidAsync("stop"); await _module.DisposeAsync(); }
            catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException) { }
        _self?.Dispose();
    }
}
