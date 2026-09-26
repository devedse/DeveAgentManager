using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Runners;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeveAgentManager.Api;

/// <summary>
/// Live updates for the browser app (/hubs/live): every change the Manager reports (<see cref="LiveHub.Changed"/>),
/// the setup log of Runners a page follows (<see cref="LiveHub.Log"/>) and results of background operations.
/// </summary>
[Authorize]
public sealed class LiveUpdatesHub(RunnerLogStore logs) : Hub
{
    public static string LogGroup(Guid runnerId) => $"log:{runnerId:N}";

    /// <summary>Follows a Runner's setup log and returns its recent lines.</summary>
    public async Task<IReadOnlyList<RunnerLogLine>> FollowLog(Guid runnerId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, LogGroup(runnerId));
        return logs.GetRecent(runnerId).TakeLast(2000).ToList();
    }

    public Task UnfollowLog(Guid runnerId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, LogGroup(runnerId));
}

/// <summary>Forwards the Manager's in-process events to the connected browsers.</summary>
public sealed class LiveUpdatesBridge(ManagerEvents events, IHubContext<LiveUpdatesHub> hub, ILogger<LiveUpdatesBridge> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        events.Changed += OnChanged;
        events.RunnerLogWritten += OnLog;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        events.Changed -= OnChanged;
        events.RunnerLogWritten -= OnLog;
        return Task.CompletedTask;
    }

    private void OnChanged(ManagerEvent managerEvent) => Send(hub.Clients.All.SendAsync(LiveHub.Changed, managerEvent));

    private void OnLog(Guid runnerId, RunnerLogLine line) => Send(hub.Clients.Group(LiveUpdatesHub.LogGroup(runnerId)).SendAsync(LiveHub.Log, runnerId, line));

    private void Send(Task sending) => _ = sending.ContinueWith(t => logger.LogDebug(t.Exception, "Could not send a live update"), TaskContinuationOptions.OnlyOnFaulted);
}

/// <summary>Reports the result of a background operation (update, install, configure) to the browsers when it ends.</summary>
public sealed class OperationNotifier(IHubContext<LiveUpdatesHub> hub, ILogger<OperationNotifier> logger)
{
    public void Track(Guid runnerId, string runnerName, string operation, Task<string?> result) =>
        _ = result.ContinueWith(async t =>
        {
            var error = t.IsCompletedSuccessfully ? t.Result : t.Exception?.GetBaseException().Message ?? "Cancelled.";
            try
            {
                await hub.Clients.All.SendAsync(LiveHub.OperationFinished, new OperationFinished(runnerId, runnerName, operation, error));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not report operation {Operation} on {Runner}", operation, runnerName);
            }
        }, TaskScheduler.Default);
}
