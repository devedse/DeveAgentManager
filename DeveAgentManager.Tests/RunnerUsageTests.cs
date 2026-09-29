using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class RunnerUsageTests
{
    [Fact]
    public void KeepsOneHourOfCpuAndMemoryReadings_WithGapsWhenMetricsDisappear()
    {
        var cache = new RunnerStatusCache();
        var runner = Guid.NewGuid();
        var now = DateTime.UtcNow;
        cache.Set(runner, new MachineStatus(MachinePowerState.Running, CpuUsage: 10, MemoryUsedBytes: 1, MemoryTotalBytes: 2), now.AddMinutes(-61));
        cache.Set(runner, new MachineStatus(MachinePowerState.Running, CpuUsage: 25, MemoryUsedBytes: 3, MemoryTotalBytes: 4), now.AddMinutes(-30));
        cache.Set(runner, new MachineStatus(MachinePowerState.Stopped), now.AddMinutes(-1));
        cache.Set(runner, new MachineStatus(MachinePowerState.Running, CpuUsage: 120, MemoryUsedBytes: 3, MemoryTotalBytes: 4), now);

        var samples = cache.History(runner);
        Assert.Equal(3, samples.Count);
        Assert.Equal((25d, 75d), (samples[0].CpuPercent, samples[0].MemoryPercent));
        Assert.Equal((null, null), (samples[1].CpuPercent, samples[1].MemoryPercent));
        Assert.Equal((100d, 75d), (samples[2].CpuPercent, samples[2].MemoryPercent));

        cache.RemoveMissing(new HashSet<Guid>());
        Assert.Empty(cache.History(runner));
    }

    [Fact]
    public void DoesNotStoreReadingsFromProvidersWithoutUsageMetrics()
    {
        var cache = new RunnerStatusCache();
        var runner = Guid.NewGuid();
        cache.Set(runner, new MachineStatus(MachinePowerState.Running));
        Assert.Empty(cache.History(runner));
    }

    [Fact]
    public void KeepsSecondReadingsForMinute_AndDownsamplesHourWithStrictExpiry()
    {
        var cache = new RunnerStatusCache();
        var runner = Guid.NewGuid();
        var now = DateTime.UtcNow;
        for (var second = 3660; second >= 0; second--)
        {
            cache.Set(runner, new MachineStatus(MachinePowerState.Running,
                CpuUsage: second % 2 == 0 ? 20 : 40, MemoryUsedBytes: 1, MemoryTotalBytes: 2),
                now.AddSeconds(-second));
        }

        var minute = cache.History(runner, 1);
        var hour = cache.History(runner);
        Assert.InRange(minute.Count, 59, 61);
        Assert.All(minute, sample => Assert.True(sample.At >= DateTime.UtcNow.AddMinutes(-1)));
        Assert.InRange(hour.Count, 295, 305); // 240 quarter-minute buckets plus 60 raw readings.
        Assert.All(hour, sample => Assert.True(sample.At >= DateTime.UtcNow.AddHours(-1)));
        Assert.Contains(hour, sample => sample.At < now.AddMinutes(-1) && sample.CpuPercent is > 20 and < 40);
    }

    [Fact]
    public void FastUsageReadingDoesNotReplaceFullMachineStatus()
    {
        var cache = new RunnerStatusCache();
        var runner = Guid.NewGuid();
        cache.Set(runner, new MachineStatus(MachinePowerState.Running, Address: "10.0.0.2", CpuUsage: 5));
        cache.RecordUsage(runner, new MachineStatus(MachinePowerState.Running, CpuUsage: 25));

        Assert.Equal("10.0.0.2", cache.Get(runner)?.Address);
        Assert.Equal(25, cache.History(runner, 1).Last().CpuPercent);
    }
}
