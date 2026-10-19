using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeveAgentManager.Api;

/// <summary>
/// Live updates for the browser app (/hubs/live): the Manager's changes that concern the signed-in user
/// (<see cref="LiveHub.Changed"/>), the setup log of Runners a page follows (<see cref="LiveHub.Log"/>) and results of
/// background operations. A connection ends when its sign-in no longer counts (see <see cref="OpenConnections"/>).
/// </summary>
[Authorize]
public sealed class LiveUpdatesHub(RunnerLogStore logs, AccessControl access, OpenConnections connections) : Hub
{
    private const string RegistrationItem = "dam:registration";
    public const string CpuCaptureGroup = "settings-managers:cpu-capture";

    public static string LogGroup(Guid runnerId) => $"log:{runnerId:N}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is null || SignIn.UsableUser(http) is not { } user)
        {
            Context.Abort();
            return;
        }

        Context.Items[RegistrationItem] = connections.Register(user, SignIn.SecurityStamp(http), (_, _) => Task.FromResult(true), Context.Abort, SignIn.SessionId(http));
        if (user.Has(UserRoles.SettingsManager)) await Groups.AddToGroupAsync(Context.ConnectionId, CpuCaptureGroup);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        (Context.Items[RegistrationItem] as IDisposable)?.Dispose();
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Follows a Runner's setup log and returns its recent lines.</summary>
    public async Task<IReadOnlyList<RunnerLogLine>> FollowLog(Guid runnerId)
    {
        if (SignIn.UsableUser(Context.GetHttpContext()!) is not { } user || await access.RunnerAsync(user, runnerId) < RunnerAccess.Use)
        {
            throw new HubException("Runner not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, LogGroup(runnerId));
        return logs.GetRecent(runnerId).TakeLast(2000).ToList();
    }

    public Task UnfollowLog(Guid runnerId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, LogGroup(runnerId));
}

/// <summary>Forwards the Manager's in-process events to the browsers of the users they concern.</summary>
public sealed class LiveUpdatesBridge(ManagerEvents events, IHubContext<LiveUpdatesHub> hub, AccessControl access, ILogger<LiveUpdatesBridge> logger) : IHostedService
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

    private void OnChanged(ManagerEvent managerEvent) => Send(SendChangedAsync(managerEvent));

    private async Task SendChangedAsync(ManagerEvent managerEvent)
    {
        var audience = await access.AudienceAsync(managerEvent);
        var clients = audience is null ? hub.Clients.All : hub.Clients.Users(audience.Select(id => id.ToString()));
        await clients.SendAsync(LiveHub.Changed, managerEvent);
    }

    private void OnLog(Guid runnerId, RunnerLogLine line) => Send(hub.Clients.Group(LiveUpdatesHub.LogGroup(runnerId)).SendAsync(LiveHub.Log, runnerId, line));

    private void Send(Task sending) => _ = sending.ContinueWith(t => logger.LogDebug(t.Exception, "Could not send a live update"), TaskContinuationOptions.OnlyOnFaulted);
}

/// <summary>Reports the result of a background operation (update, install, configure) to the Runner's people when it ends.</summary>
public sealed class OperationNotifier(IHubContext<LiveUpdatesHub> hub, AccessControl access, ILogger<OperationNotifier> logger)
{
    public void Track(Guid runnerId, string runnerName, string operation, Task<string?> result) =>
        _ = result.ContinueWith(async t =>
        {
            var error = t.IsCompletedSuccessfully ? t.Result : t.Exception?.GetBaseException().Message ?? "Cancelled.";
            try
            {
                var audience = await access.AudienceAsync(new ManagerEvent(ManagerEventKind.RunnerChanged, runnerId)) ?? [];
                await hub.Clients.Users(audience.Select(id => id.ToString())).SendAsync(LiveHub.OperationFinished, new OperationFinished(runnerId, runnerName, operation, error));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not report operation {Operation} on {Runner}", operation, runnerName);
            }
        }, TaskScheduler.Default);
}
