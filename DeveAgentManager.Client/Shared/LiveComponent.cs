using DeveAgentManager.Client.Services;
using DeveAgentManager.Core.Events;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace DeveAgentManager.Client.Shared;

/// <summary>
/// A component that loads its data in <see cref="LoadAsync"/> and reloads it (debounced) whenever the Manager reports a
/// change, so pages update live while Runners are created or agents start. After the live connection was lost (a phone
/// that slept), it reloads once it is back, so nothing that changed meanwhile is missed.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private int _reloadPending;
    private bool _disposed;

    [Inject]
    protected LiveUpdates Live { get; set; } = default!;

    [Inject]
    protected ManagerApi Api { get; set; } = default!;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    protected bool Loaded { get; private set; }

    protected abstract Task LoadAsync();

    /// <summary>Return false to ignore events that don't concern this component.</summary>
    protected virtual bool IsRelevant(ManagerEvent managerEvent) => true;

    protected override async Task OnInitializedAsync()
    {
        Live.Changed += OnManagerEvent;
        Live.Reconnected += OnReconnected;
        await SafeLoadAsync();
        Loaded = true;
    }

    protected Task ReloadAsync() => InvokeAsync(async () =>
    {
        await SafeLoadAsync();
        StateHasChanged();
    });

    private async Task SafeLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        catch (HttpRequestException)
        {
            // The Manager is not reachable right now; the live connection reloads once it is back.
        }
    }

    private void OnReconnected() => Schedule();

    private void OnManagerEvent(ManagerEvent managerEvent)
    {
        if (IsRelevant(managerEvent))
        {
            Schedule();
        }
    }

    private void Schedule()
    {
        if (_disposed || Interlocked.Exchange(ref _reloadPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Debounce);
            Interlocked.Exchange(ref _reloadPending, 0);
            if (!_disposed)
            {
                await ReloadAsync();
            }
        });
    }

    public virtual void Dispose()
    {
        _disposed = true;
        Live.Changed -= OnManagerEvent;
        Live.Reconnected -= OnReconnected;
        GC.SuppressFinalize(this);
    }
}
