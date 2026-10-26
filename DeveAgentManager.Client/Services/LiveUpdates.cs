using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DeveAgentManager.Client.Services;

/// <summary>
/// The live connection to the Manager (/hubs/live): changes, setup logs and operation results. Reconnects by itself
/// (e.g. after a phone slept) and then raises <see cref="Reconnected"/>, so pages reload what they missed.
/// </summary>
public sealed class LiveUpdates : IAsyncDisposable
{
    /// <summary>Waits before each attempt: quickly at first (an update restarts the Manager in seconds), then every 5 seconds.</summary>
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)];

    private readonly HubConnection _connection;
    private readonly PageVisibility _visibility;
    private readonly HashSet<Guid> _followedLogs = [];
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _connecting;
    private bool _disposed;

    public LiveUpdates(NavigationManager navigation, PageVisibility visibility)
    {
        // Reconnecting is ours (StartAsync), not SignalR's automatic reconnect, so a tab shown again needn't wait.
        _connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(LiveHub.Path))
            .AddJsonProtocol(options => ApiJson.Configure(options.PayloadSerializerOptions))
            .Build();

        _connection.On<ManagerEvent>(LiveHub.Changed, e => Changed?.Invoke(e));
        _connection.On<Guid, RunnerLogLine>(LiveHub.Log, (runnerId, line) => Log?.Invoke(runnerId, line));
        _connection.On<OperationFinished>(LiveHub.OperationFinished, e => OperationFinished?.Invoke(e));
        _connection.On(LiveHub.CpuTraceChanged, () => CpuTraceChanged?.Invoke());
        _connection.Closed += error =>
        {
            StateChanged?.Invoke();
            _ = StartAsync();
            return Task.CompletedTask;
        };

        // A browser that froze a background tab, or a computer that slept, often loses the connection: when the tab is
        // shown again, the next attempt starts at once instead of after the rest of its wait.
        _visibility = visibility;
        _visibility.VisibleObserved += Resume;
    }

    /// <summary>Something changed in the Manager (a Runner, an agent, a session, the settings).</summary>
    public event Action<ManagerEvent>? Changed;

    /// <summary>A line in the setup log of a Runner followed with <see cref="FollowLogAsync"/>.</summary>
    public event Action<Guid, RunnerLogLine>? Log;

    public event Action<OperationFinished>? OperationFinished;

    /// <summary>The connection is back after it was lost: reload what may have changed meanwhile.</summary>
    public event Action? Reconnected;

    public event Action? StateChanged;
    public event Action? CpuTraceChanged;

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>Connects, trying again until the Manager is reachable; a lost connection starts this again.</summary>
    public async Task StartAsync()
    {
        if (_connecting || _disposed || _connection.State != HubConnectionState.Disconnected)
        {
            return;
        }

        _connecting = true;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await WaitAsync(RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)]);
                if (_disposed || _connection.State != HubConnectionState.Disconnected)
                {
                    return;
                }

                try
                {
                    await _connection.StartAsync();
                    break;
                }
                catch (Exception)
                {
                    // The Manager is not reachable right now (restarting, or the network is still waking up).
                    StateChanged?.Invoke();
                }
            }
        }
        finally
        {
            _connecting = false;
        }

        // Groups are per connection: follow the logs again.
        foreach (var runnerId in _followedLogs.ToList())
        {
            try
            {
                await _connection.InvokeAsync<IReadOnlyList<RunnerLogLine>>(LiveHub.FollowLog, runnerId);
            }
            catch (Exception)
            {
                // Lost again (Closed tries again) or no longer allowed: the other logs still follow.
            }
        }

        StateChanged?.Invoke();
        Reconnected?.Invoke();
    }

    /// <summary>Follows a Runner's setup log (<see cref="Log"/>) and returns its recent lines.</summary>
    public async Task<IReadOnlyList<RunnerLogLine>> FollowLogAsync(Guid runnerId)
    {
        _followedLogs.Add(runnerId);
        await WaitConnectedAsync();
        return await _connection.InvokeAsync<IReadOnlyList<RunnerLogLine>>(LiveHub.FollowLog, runnerId);
    }

    public async Task UnfollowLogAsync(Guid runnerId)
    {
        _followedLogs.Remove(runnerId);
        if (IsConnected)
        {
            await _connection.InvokeAsync(LiveHub.UnfollowLog, runnerId);
        }
    }

    private async Task WaitConnectedAsync()
    {
        for (var i = 0; i < 100 && !IsConnected; i++)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>Waits before an attempt, unless the tab is shown again (<see cref="Resume"/>) meanwhile.</summary>
    private async Task WaitAsync(TimeSpan delay)
    {
        if (delay == TimeSpan.Zero)
        {
            return;
        }

        _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAny(Task.Delay(delay), _resume.Task);
    }

    private void Resume() => _resume.TrySetResult();

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _visibility.VisibleObserved -= Resume;
        Resume();
        return _connection.DisposeAsync();
    }
}
