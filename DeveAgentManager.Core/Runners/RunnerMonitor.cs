using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>
/// Latest machine status per Runner, for the Portal: power and address from the provider, CPU, memory and disk read
/// inside the Runner (<see cref="GuestUsage"/>).
/// </summary>
public sealed class RunnerStatusCache
{
    private readonly ConcurrentDictionary<Guid, MachineStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, GuestUsage.CpuCounters> _cpu = new();
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
        public double CpuSum, MemorySum, DiskSum;
        public int CpuCount, MemoryCount, DiskCount;
        public void Add(RunnerUsageSample sample)
        {
            if (sample.CpuPercent is { } cpu) { CpuSum += cpu; CpuCount++; }
            if (sample.MemoryPercent is { } memory) { MemorySum += memory; MemoryCount++; }
            if (sample.DiskPercent is { } disk) { DiskSum += disk; DiskCount++; }
        }
        public RunnerUsageSample Sample(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc),
            CpuCount == 0 ? null : CpuSum / CpuCount, MemoryCount == 0 ? null : MemorySum / MemoryCount, DiskCount == 0 ? null : DiskSum / DiskCount);
    }

    public MachineStatus? Get(Guid runnerId) => _statuses.GetValueOrDefault(runnerId);

    public void Set(Guid runnerId, MachineStatus status, DateTime? at = null)
    {
        _statuses[runnerId] = status;
        RecordUsage(runnerId, status, at);
    }

    // The provider reports power, address and uptime. While the machine keeps running, the readings from inside it stay;
    // a machine that stopped has none.
    public void SetProviderStatus(Guid runnerId, MachineStatus status)
    {
        var merged = _statuses.AddOrUpdate(runnerId, status, (_, previous) =>
            status.PowerState == MachinePowerState.Running && previous.PowerState == MachinePowerState.Running
                ? status with
                {
                    CpuUsage = previous.CpuUsage, MemoryUsedBytes = previous.MemoryUsedBytes, MemoryTotalBytes = previous.MemoryTotalBytes,
                    DiskUsedBytes = previous.DiskUsedBytes, DiskTotalBytes = previous.DiskTotalBytes,
                }
                : status with { CpuUsage = null, MemoryUsedBytes = null, MemoryTotalBytes = null, DiskUsedBytes = null, DiskTotalBytes = null });
        if (merged.PowerState != MachinePowerState.Running) _cpu.TryRemove(runnerId, out _);
    }

    /// <summary>
    /// A reading from inside the Runner. CPU is the use since the previous reading, so the first one after a start, a
    /// reboot or an outage has none. A reading without disk keeps the last disk reading.
    /// </summary>
    public void SetGuestReading(Guid runnerId, GuestUsage.Reading reading, DateTime? at = null)
    {
        double? cpu = null;
        if (reading.Cpu is { } counters)
        {
            cpu = GuestUsage.CpuPercent(_cpu.TryGetValue(runnerId, out var previous) ? previous : null, counters);
            _cpu[runnerId] = counters;
        }
        else _cpu.TryRemove(runnerId, out _);

        var status = Get(runnerId) ?? new MachineStatus(MachinePowerState.Running);
        Set(runnerId, status with
        {
            CpuUsage = cpu, MemoryUsedBytes = reading.MemoryUsedBytes, MemoryTotalBytes = reading.MemoryTotalBytes,
            DiskUsedBytes = reading.HasDisk ? reading.DiskUsedBytes : status.DiskUsedBytes,
            DiskTotalBytes = reading.HasDisk ? reading.DiskTotalBytes : status.DiskTotalBytes,
        }, at);
    }

    /// <summary>The Runner can't be read (e.g. SSH fails): no CPU, memory or disk until it can, and a gap in the history.</summary>
    public void ClearGuestReading(Guid runnerId, DateTime? at = null)
    {
        _cpu.TryRemove(runnerId, out _);
        if (Get(runnerId) is { } status)
            Set(runnerId, status with { CpuUsage = null, MemoryUsedBytes = null, MemoryTotalBytes = null, DiskUsedBytes = null, DiskTotalBytes = null }, at);
    }

    private void RecordUsage(Guid runnerId, MachineStatus status, DateTime? at = null)
    {
        var cpu = Valid(status.CpuUsage);
        var memory = status is { MemoryUsedBytes: { } used, MemoryTotalBytes: > 0 and var total }
            ? Valid(used * 100.0 / total) : null;
        var disk = status is { DiskUsedBytes: { } diskUsed, DiskTotalBytes: > 0 and var diskTotal } ? Valid(diskUsed * 100.0 / diskTotal) : null;
        if (cpu is null && memory is null && disk is null && !_history.ContainsKey(runnerId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var time = at ?? now;
        if (time < now - Retain) return;
        var history = _history.GetOrAdd(runnerId, _ => new UsageHistory());
        lock (history)
        {
            var sample = new RunnerUsageSample(time, cpu, memory, disk);
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
        _cpu.TryRemove(runnerId, out _);
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
/// Polls providers and Runners in the background: power state and address from the provider; agent state from tmux and
/// CPU and memory from inside the Runner every pass, in the same SSH command; disk every third pass; and copies changed
/// conversations into the session store.
/// </summary>
public sealed class RunnerMonitor(
    RunnerService runners,
    AgentService agents,
    SessionService sessions,
    RunnerShell shell,
    RunnerStatusCache statusCache,
    ManagerEvents events,
    ILogger<RunnerMonitor> logger) : BackgroundService
{
    /// <summary>Agents (running, working or idle) and usage are checked this often; the provider, disk and conversations every third time.</summary>
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
            if (runners.CurrentOperation(runner.Id) is not null && runner.State != RunnerState.Ready)
            {
                return;
            }

            try
            {
                // Usage is read inside the Runner, in the agent check's SSH command; Runners without agents get their own
                // command every third pass. A Runner that can't be reached shows no usage rather than an old reading.
                GuestUsage.Reading? guest = null;
                var unreachable = false;
                if (runner.State == RunnerState.Ready && runner.Agents.Count > 0)
                {
                    try { await agents.RefreshRunnerAsync(runner, ct, GuestUsage.Script(disk: full), output => guest = GuestUsage.Parse(output)); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not observe terminal state for runner {Runner}", runner.Name);
                        await agents.MarkObservationUnavailableAsync(runner.Id, ct);
                        unreachable = guest is null;
                    }
                }

                var provider = runners.FindProvider(runner.ProviderId);
                if (full && provider is not null && runner.MachineId is not null && runner.State is RunnerState.Ready or RunnerState.Stopped or RunnerState.Error)
                {
                    try
                    {
                        var status = await provider.GetStatusAsync(runner.ToMachine(), ct);
                        statusCache.SetProviderStatus(runner.Id, status);
                        await runners.ApplyObservedStatusAsync(runner.Id, status, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { logger.LogWarning(ex, "Provider status unavailable for runner {Runner}; terminal observation will still run", runner.Name); }
                }
                if (full && runner.State == RunnerState.Ready && guest is null && !unreachable)
                {
                    try
                    {
                        var result = (await shell.RunAsync(runner, GuestUsage.Script(disk: true), TimeSpan.FromSeconds(10), ct)).EnsureSuccess("Reading usage");
                        guest = GuestUsage.Parse(result.Output);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        unreachable = true;
                        logger.LogDebug(ex, "Usage unavailable for runner {Runner}", runner.Name);
                    }
                }

                if (runner.State == RunnerState.Ready && guest is not null) statusCache.SetGuestReading(runner.Id, guest);
                else if (unreachable) statusCache.ClearGuestReading(runner.Id);
                // Cards refresh every third pass; the usage graph polls the history itself.
                if (full && runner.State == RunnerState.Ready) events.RunnerChanged(runner.Id);

                if (full && runner.State == RunnerState.Ready && runner.Agents.Count > 0)
                    await sessions.TrySyncRunnerAsync(runner, ct);

            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh runner {Runner}", runner.Name);
            }
        });
    }
}
