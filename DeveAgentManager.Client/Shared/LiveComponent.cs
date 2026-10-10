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
    private readonly CancellationTokenSource _refreshStop = new();

    protected virtual TimeSpan? RefreshInterval => null;

    [Inject]
    protected LiveUpdates Live { get; set; } = default!;

    [Inject]
    protected ManagerApi Api { get; set; } = default!;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    /// <summary>True once data loaded successfully; until then show the section as loading (AGENTS.md, "UI: loading states").</summary>
    protected bool Loaded { get; private set; }

    /// <summary>Why the data couldn't be loaded yet; a section shows it with Retry instead of looking empty.</summary>
    protected string? LoadError { get; private set; }

    protected Task RetryAsync() => ReloadAsync();

    protected abstract Task LoadAsync();

    /// <summary>Return false to ignore events that don't concern this component.</summary>
    protected virtual bool IsRelevant(ManagerEvent managerEvent) => true;

    protected override async Task OnInitializedAsync()
    {
        Live.Changed += OnManagerEvent;
        Live.Reconnected += OnReconnected;
        await SafeLoadAsync();
        if (RefreshInterval is { } interval) _ = RefreshPeriodicallyAsync(interval);
    }

    private async Task RefreshPeriodicallyAsync(TimeSpan interval)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_refreshStop.Token)) await ReloadAsync();
        }
        catch (OperationCanceledException) when (_refreshStop.IsCancellationRequested) { }
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
            Loaded = true;
            LoadError = null;
        }
        catch (ApiException ex)
        {
            // Before anything loaded, the section shows it; afterwards the data stays and a message says why it is stale.
            if (Loaded) Snackbar.Add(ex.Message, Severity.Error);
            else LoadError = ex.Message;
        }
        catch (HttpRequestException)
        {
            // The Manager is not reachable right now; the live connection reloads once it is back.
            if (!Loaded) LoadError = LoadingSection.Unreachable;
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
        _refreshStop.Cancel();
        Live.Changed -= OnManagerEvent;
        Live.Reconnected -= OnReconnected;
        GC.SuppressFinalize(this);
    }
}
