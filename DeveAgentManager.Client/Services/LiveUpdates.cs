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
    private readonly HubConnection _connection;
    private readonly HashSet<Guid> _followedLogs = [];

    public LiveUpdates(NavigationManager navigation)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(LiveHub.Path))
            .WithAutomaticReconnect(new ForeverRetry())
            .AddJsonProtocol(options => ApiJson.Configure(options.PayloadSerializerOptions))
            .Build();

        _connection.On<ManagerEvent>(LiveHub.Changed, e => Changed?.Invoke(e));
        _connection.On<Guid, RunnerLogLine>(LiveHub.Log, (runnerId, line) => Log?.Invoke(runnerId, line));
        _connection.On<OperationFinished>(LiveHub.OperationFinished, e => OperationFinished?.Invoke(e));
        _connection.On(LiveHub.CpuTraceChanged, () => CpuTraceChanged?.Invoke());
        _connection.Reconnecting += _ =>
        {
            StateChanged?.Invoke();
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ =>
        {
            // Groups are per connection: follow the logs again.
            foreach (var runnerId in _followedLogs.ToList())
            {
                await _connection.InvokeAsync<IReadOnlyList<RunnerLogLine>>(LiveHub.FollowLog, runnerId);
            }

            StateChanged?.Invoke();
            Reconnected?.Invoke();
        };
        _connection.Closed += async _ =>
        {
            StateChanged?.Invoke();
            // Retrying forever gave up (it doesn't), or the start failed: try again in a while.
            await Task.Delay(TimeSpan.FromSeconds(5));
            await StartAsync();
        };
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

    public async Task StartAsync()
    {
        if (_connection.State != HubConnectionState.Disconnected)
        {
            return;
        }

        try
        {
            await _connection.StartAsync();
            StateChanged?.Invoke();
            Reconnected?.Invoke();
        }
        catch (Exception)
        {
            // The Manager is not reachable right now; Closed tries again.
            StateChanged?.Invoke();
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => StartAsync());
        }
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

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>Retries forever: quickly at first (an update restarts the Manager in seconds), then every 5 seconds.</summary>
    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => retryContext.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(2),
            3 => TimeSpan.FromSeconds(3),
            _ => TimeSpan.FromSeconds(5),
        };
    }
}
