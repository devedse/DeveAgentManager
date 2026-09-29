using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
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
    private readonly ConcurrentDictionary<Guid, List<RunnerUsageSample>> _history = new();
    private static readonly TimeSpan Retain = TimeSpan.FromHours(1);

    public MachineStatus? Get(Guid runnerId) => _statuses.GetValueOrDefault(runnerId);

    public void Set(Guid runnerId, MachineStatus status, DateTime? at = null)
    {
        _statuses[runnerId] = status;
        var cpu = Valid(status.CpuUsage);
        var memory = status is { MemoryUsedBytes: { } used, MemoryTotalBytes: > 0 and var total }
            ? Valid(used * 100.0 / total) : null;
        if (cpu is null && memory is null && !_history.ContainsKey(runnerId))
        {
            return;
        }

        var time = at ?? DateTime.UtcNow;
        var samples = _history.GetOrAdd(runnerId, _ => []);
        lock (samples)
        {
            samples.Add(new RunnerUsageSample(time, cpu, memory));
            samples.RemoveAll(s => s.At < time - Retain);
        }
    }

    public IReadOnlyList<RunnerUsageSample> History(Guid runnerId)
    {
        if (!_history.TryGetValue(runnerId, out var samples))
        {
            return [];
        }

        lock (samples)
        {
            samples.RemoveAll(s => s.At < DateTime.UtcNow - Retain);
            return samples.ToArray();
        }
    }

    private static double? Valid(double? value) => value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : null;

    public void Remove(Guid runnerId)
    {
        _statuses.TryRemove(runnerId, out _);
        _history.TryRemove(runnerId, out _);
    }

    public void RemoveMissing(IReadOnlySet<Guid> runnerIds)
    {
        foreach (var id in _statuses.Keys)
        {
            if (!runnerIds.Contains(id))
            {
                Remove(id);
            }
        }
    }
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
        if (full)
        {
            statusCache.RemoveMissing(all.Select(r => r.Id).ToHashSet());
        }
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
