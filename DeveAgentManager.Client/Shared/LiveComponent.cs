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

    protected Task RetryAsync() => ReloadAsync();

    protected abstract Task LoadAsync();

    /// <summary>Return false to ignore events that don't concern this component.</summary>
    protected virtual bool IsRelevant(ManagerEvent managerEvent) => true;

    protected override async Task OnInitializedAsync()
    {
        Live.Changed += OnManagerEvent;
        Live.Reconnected += OnReconnected;
        Visibility.Shown += OnShown;
        await SafeLoadAsync();
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
        _refreshStop.Cancel();
        Live.Changed -= OnManagerEvent;
        Live.Reconnected -= OnReconnected;
        Visibility.Shown -= OnShown;
        GC.SuppressFinalize(this);
    }
}
