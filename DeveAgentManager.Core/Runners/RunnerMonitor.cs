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
    private readonly ConcurrentDictionary<Guid, UsageHistory> _history = new();
    private static readonly TimeSpan Retain = TimeSpan.FromHours(1);
    private static readonly TimeSpan FullResolution = TimeSpan.FromMinutes(1);
    private const long BucketTicks = TimeSpan.TicksPerSecond * 15;

    private sealed class UsageHistory
    {
        public List<RunnerUsageSample> Recent { get; } = [];
        public SortedDictionary<long, UsageBucket> Older { get; } = [];
    }

    private sealed class UsageBucket
    {
        public double CpuSum, MemorySum;
        public int CpuCount, MemoryCount;
        public void Add(RunnerUsageSample sample)
        {
            if (sample.CpuPercent is { } cpu) { CpuSum += cpu; CpuCount++; }
            if (sample.MemoryPercent is { } memory) { MemorySum += memory; MemoryCount++; }
        }
        public RunnerUsageSample Sample(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc),
            CpuCount == 0 ? null : CpuSum / CpuCount, MemoryCount == 0 ? null : MemorySum / MemoryCount);
    }

    public MachineStatus? Get(Guid runnerId) => _statuses.GetValueOrDefault(runnerId);

    public void Set(Guid runnerId, MachineStatus status, DateTime? at = null)
    {
        _statuses[runnerId] = status;
        RecordUsage(runnerId, status, at);
    }

    public void RecordUsage(Guid runnerId, MachineStatus status, DateTime? at = null)
    {
        var cpu = Valid(status.CpuUsage);
        var memory = status is { MemoryUsedBytes: { } used, MemoryTotalBytes: > 0 and var total }
            ? Valid(used * 100.0 / total) : null;
        if (cpu is null && memory is null && !_history.ContainsKey(runnerId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var time = at ?? now;
        if (time < now - Retain) return;
        var history = _history.GetOrAdd(runnerId, _ => new UsageHistory());
        lock (history)
        {
            var sample = new RunnerUsageSample(time, cpu, memory);
            var recent = history.Recent;
            if (time < now - FullResolution)
            {
                AddOlder(history, sample);
            }
            else if (recent.Count > 0 && recent[^1].At.Ticks / TimeSpan.TicksPerSecond == time.Ticks / TimeSpan.TicksPerSecond)
            {
                recent[^1] = sample;
            }
            else
            {
                recent.Add(sample);
            }
            Trim(history, now);
        }
    }

    public IReadOnlyList<RunnerUsageSample> History(Guid runnerId, int minutes = 60)
    {
        if (!_history.TryGetValue(runnerId, out var history))
        {
            return [];
        }

        var now = DateTime.UtcNow;
        lock (history)
        {
            Trim(history, now);
            var cutoff = now.AddMinutes(minutes == 1 ? -1 : -60);
            return (minutes == 1 ? [] : history.Older.Select(pair => pair.Value.Sample(pair.Key)))
                .Concat(history.Recent).Where(s => s.At >= cutoff).OrderBy(s => s.At).ToArray();
        }
    }

    private static void AddOlder(UsageHistory history, RunnerUsageSample sample)
    {
        var key = sample.At.Ticks / BucketTicks * BucketTicks;
        if (!history.Older.TryGetValue(key, out var bucket)) history.Older[key] = bucket = new UsageBucket();
        bucket.Add(sample);
    }

    private static void Trim(UsageHistory history, DateTime now)
    {
        var cutoff = now - Retain;
        var recentCutoff = now - FullResolution;
        foreach (var sample in history.Recent.Where(s => s.At < recentCutoff && s.At >= cutoff)) AddOlder(history, sample);
        history.Recent.RemoveAll(s => s.At < recentCutoff);
        // A bucket is discarded as soon as its start leaves the hour; no older reading can remain.
        foreach (var key in history.Older.Keys.TakeWhile(key => key < cutoff.Ticks).ToArray()) history.Older.Remove(key);
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
