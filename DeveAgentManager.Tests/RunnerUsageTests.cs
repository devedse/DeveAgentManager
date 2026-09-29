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
}
