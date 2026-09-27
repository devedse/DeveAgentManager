using System.Collections.Concurrent;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>Latest machine status (power, CPU, memory) per Runner, for the Portal.</summary>
public sealed class RunnerStatusCache
{
    private readonly ConcurrentDictionary<Guid, MachineStatus> _statuses = new();

    public MachineStatus? Get(Guid runnerId) => _statuses.GetValueOrDefault(runnerId);

    public void Set(Guid runnerId, MachineStatus status) => _statuses[runnerId] = status;

    public void Remove(Guid runnerId) => _statuses.TryRemove(runnerId, out _);
}

/// <summary>
/// Polls providers and Runners in the background: power state and resources from the provider, agent state from tmux,
/// and copies changed conversations into the session store.
/// </summary>
public sealed class RunnerMonitor(
    RunnerService runners,
    AgentService agents,
    SessionService sessions,
    RunnerStatusCache statusCache,
    ManagerEvents events,
    ILogger<RunnerMonitor> logger) : BackgroundService
{
    /// <summary>Agents (running, working or idle) are checked this often; the provider and conversations every third time.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private const int FullPassEvery = 3;

    private int _pass;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await runners.RecoverInterruptedAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Runner monitor pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var full = _pass++ % FullPassEvery == 0;
        var all = await runners.ListAsync(cancellationToken);
        await Parallel.ForEachAsync(all, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (runner, ct) =>
        {
            if (runners.CurrentOperation(runner.Id) is not null)
            {
                return;
            }

            try
            {
                var provider = runners.FindProvider(runner.ProviderId);
                if (full && provider is not null && runner.MachineId is not null && runner.State is RunnerState.Ready or RunnerState.Stopped or RunnerState.Error)
                {
                    var status = await provider.GetStatusAsync(runner.ToMachine(), ct);
                    statusCache.Set(runner.Id, status);
                    events.RunnerChanged(runner.Id);
                    await runners.ApplyObservedStatusAsync(runner.Id, status, ct);
                }

                if (runner.State == RunnerState.Ready && runner.Agents.Count > 0)
                {
                    await agents.RefreshRunnerAsync(runner, ct);
                    if (full)
                    {
                        await sessions.SyncRunnerAsync(runner, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not refresh runner {Runner}", runner.Name);
            }
        });
    }
}
