using DeveAgentManager.Client.Services;
using DeveAgentManager.Core.Events;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace DeveAgentManager.Client.Shared;

/// <summary>
/// A component that loads its data in <see cref="LoadAsync"/> and reloads it (debounced) whenever the Manager reports a
/// change, so pages update live while Runners are created or agents start. After the live connection was lost (a phone
/// that slept), it reloads once it is back, so nothing that changed meanwhile is missed. While its tab is hidden it
/// neither reloads nor polls; it catches up once when the tab is shown again.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    // Changes arrive in bursts (a monitor pass observes every agent of a Runner): one reload covers them.
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private int _reloadPending;
    private int _missedWhileHidden;
    private bool _disposed;
    private readonly CancellationTokenSource _refreshStop = new();

    // One load at a time: a reload asked for while one runs waits for it, then one more covers every such request. Loads
    // of a large page (a long conversation) would otherwise pile up, each with its own copy of the data.
    private Task? _loading;
    private Task? _queued;

    /// <summary>Cancelled when the component is disposed (the page was left): pass it to slow loads so they stop.</summary>
    protected CancellationToken Disposing => _refreshStop.Token;

    protected virtual TimeSpan? RefreshInterval => null;

    [Inject]
    protected LiveUpdates Live { get; set; } = default!;

    [Inject]
    protected ManagerApi Api { get; set; } = default!;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    protected PageVisibility Visibility { get; set; } = default!;

    /// <summary>True for what must stay current in a hidden tab, such as notifications and their sounds.</summary>
    protected virtual bool ReloadWhileHidden => false;

    /// <summary>True for what shows Runner CPU, memory or disk, refreshed every 15 seconds (<see cref="ManagerEventKind.RunnerUsageChanged"/>).</summary>
    protected virtual bool ShowsRunnerUsage => false;

    /// <summary>True for what shows an agent's task list (<see cref="ManagerEventKind.TasksChanged"/>), which can change often.</summary>
    protected virtual bool ShowsTasks => false;

    /// <summary>True once data loaded successfully; until then show the section as loading (AGENTS.md, "UI: loading states").</summary>
    protected bool Loaded { get; private set; }

    /// <summary>Why the data couldn't be loaded yet; a section shows it with Retry instead of looking empty.</summary>
    protected string? LoadError { get; private set; }

    /// <summary>Retry tries again at once, even while an automatic reload still waits for an answer.</summary>
    protected Task RetryAsync() => InvokeAsync(() => _disposed ? Task.CompletedTask : _loading = LoadOnceAsync(render: true));

    protected abstract Task LoadAsync();

    /// <summary>Return false to ignore events that don't concern this component.</summary>
    protected virtual bool IsRelevant(ManagerEvent managerEvent) => true;

    protected override async Task OnInitializedAsync()
    {
        Live.Changed += OnManagerEvent;
        Live.Reconnected += OnReconnected;
        Visibility.Shown += OnShown;
        await (_loading = LoadOnceAsync(render: false));
        if (RefreshInterval is { } interval) _ = RefreshPeriodicallyAsync(interval);
    }

    private async Task RefreshPeriodicallyAsync(TimeSpan interval)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_refreshStop.Token))
            {
                if (Hidden) Interlocked.Exchange(ref _missedWhileHidden, 1);
                else await ReloadAsync();
            }
        }
        catch (OperationCanceledException) when (_refreshStop.IsCancellationRequested) { }
    }

    /// <summary>Loads the data again and renders it; completes once data loaded after this call is shown.</summary>
    protected Task ReloadAsync() => InvokeAsync(() =>
    {
        if (_disposed) return Task.CompletedTask;
        if (_loading is not { IsCompleted: false }) return _loading = LoadOnceAsync(render: true);
        return _queued ??= AfterCurrentAsync(_loading);
    });

    private async Task AfterCurrentAsync(Task current)
    {
        try { await current; }
        catch { /* Its caller saw that; this load runs anyway. */ }
        finally { _queued = null; }
        if (_disposed) return;
        // A load that started after the requests this one covers serves them too.
        if (_loading is not { IsCompleted: false }) _loading = LoadOnceAsync(render: true);
        await _loading;
    }

    private async Task LoadOnceAsync(bool render)
    {
        // A report of a frozen Portal says which sections were loading (docs/portal-freezes.md).
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        FreezeWatch.Note("load", GetType().Name);
        await SafeLoadAsync();
        if (render && !_disposed) StateHasChanged();
        FreezeWatch.Note("loaded", $"{GetType().Name} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
    }

    private async Task SafeLoadAsync()
    {
        try
        {
            await LoadAsync();
            Loaded = true;
            LoadError = null;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // The page was left while it loaded.
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

    private bool Hidden => !Visibility.Visible && !ReloadWhileHidden;

    private void OnReconnected() => ScheduleWhenShown();

    private void OnManagerEvent(ManagerEvent managerEvent)
    {
        if (managerEvent.Kind == ManagerEventKind.PetChanged) return;
        if (managerEvent.Kind == ManagerEventKind.RunnerUsageChanged && !ShowsRunnerUsage) return;
        if (managerEvent.Kind == ManagerEventKind.TasksChanged && !ShowsTasks) return;
        if (IsRelevant(managerEvent))
        {
            ScheduleWhenShown();
        }
    }

    private void OnShown()
    {
        if (Interlocked.Exchange(ref _missedWhileHidden, 0) == 1) Schedule();
    }

    private void ScheduleWhenShown()
    {
        if (Hidden) Interlocked.Exchange(ref _missedWhileHidden, 1);
        else Schedule();
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
            // Hidden meanwhile: reload once it is shown instead.
            if (_disposed) return;
            if (Hidden) Interlocked.Exchange(ref _missedWhileHidden, 1);
            else await ReloadAsync();
        });
    }

    public virtual void Dispose()
    {
        _disposed = true;
        FreezeWatch.Note("left", GetType().Name);
        _refreshStop.Cancel();
        Live.Changed -= OnManagerEvent;
        Live.Reconnected -= OnReconnected;
        Visibility.Shown -= OnShown;
        GC.SuppressFinalize(this);
    }
}
