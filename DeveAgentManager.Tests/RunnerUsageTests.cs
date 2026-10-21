using System.Diagnostics;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class RunnerUsageTests
{
    private const string Marker = GuestUsage.Marker;

    private static GuestUsage.Reading Reading(ulong total, ulong idle, long memoryUsedKb = 2048, long? diskUsed = null) => GuestUsage.Parse(
        $"{Marker} cpu {total - idle} 0 0 {idle} 0 0 0 0 0 0\n{Marker} mem 8192 {8192 - memoryUsedKb}\n"
        + (diskUsed is { } used ? $"{Marker} disk {used} 60\n" : ""));

    [Fact]
    public void ParsesProcAndDfReadings_AndCountsOnlyNonIdleTime()
    {
        var reading = GuestUsage.Parse($"""
            unrelated output
            {Marker} cpu 100 10 50 800 40 0 5 3 7 1
            {Marker} mem 16314552 12000000
            {Marker} disk 30 60
            """);
        // user+nice+system+idle+iowait+irq+softirq+steal; guest time is already counted in user and nice.
        Assert.Equal(new GuestUsage.CpuCounters(1008, 168), reading.Cpu);
        Assert.Equal(((16314552L - 12000000) * 1024, 16314552L * 1024), (reading.MemoryUsedBytes, reading.MemoryTotalBytes));
        Assert.Equal((30L, 60L), (reading.DiskUsedBytes, reading.DiskTotalBytes));
        // Older kernels have fewer CPU fields.
        Assert.Equal(new GuestUsage.CpuCounters(10, 4), GuestUsage.Parse($"{Marker} cpu 1 1 2 6\n").Cpu);

        // Anything malformed is missing, not wrong, and doesn't take the other values with it.
        var broken = GuestUsage.Parse($"{Marker} cpu 1 x 2 3\n{Marker} mem 100\n{Marker} disk 300 60\n{Marker} mem 100 50\n");
        Assert.Equal((null, null, null), (broken.Cpu, broken.DiskUsedBytes, broken.DiskTotalBytes));
        Assert.Equal(50L * 1024, broken.MemoryUsedBytes);
        Assert.Null(GuestUsage.Parse($"{Marker} mem 100 \n").MemoryTotalBytes); // No MemAvailable.

        Assert.Equal(50d, GuestUsage.CpuPercent(new(1000, 100), new(1200, 200)));
        Assert.Null(GuestUsage.CpuPercent(null, new(1200, 200)));
        Assert.Null(GuestUsage.CpuPercent(new(1200, 200), new(100, 10))); // Rebooted: counters started again.
        Assert.Null(GuestUsage.CpuPercent(new(1000, 100), new(1000, 100)));
    }

    [Fact]
    public void ScriptReadsThisMachine_AndLeavesTheAgentStateParseable()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/stat")) return;
        // Its own tmux server directory, so the machine's real sessions are not listed.
        var tmux = Directory.CreateTempSubdirectory("dam-usage-tmux-").FullName;
        try
        {
            var script = GuestUsage.Script(disk: true) + $"echo '{AgentService.StateStartMarker}'\n" + AgentService.BuildStateScript([]);
            var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true };
            start.Environment["TMUX_TMPDIR"] = tmux;
            using var bash = Process.Start(start)!;
            var output = bash.StandardOutput.ReadToEnd();
            bash.WaitForExit();
            string? leading = null;
            var state = AgentService.SplitLeadingOutput(output, true, text => leading = text);
            var reading = GuestUsage.Parse(leading!);
            Assert.NotNull(reading.Cpu);
            Assert.InRange(reading.MemoryUsedBytes!.Value, 1, reading.MemoryTotalBytes!.Value);
            Assert.True(reading.DiskTotalBytes > 0);
            Assert.DoesNotContain(Marker, state);
            Assert.InRange(AgentService.ParsePanes(state).Now!.Value, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);
        }
        finally { Directory.Delete(tmux, true); }
    }

    [Fact]
    public void LeadingOutputIsSplitOffOnlyAtItsMarkerLine()
    {
        var marker = AgentService.StateStartMarker;
        string? leading = null;
        Assert.Equal("123\nstate\n", AgentService.SplitLeadingOutput($"{Marker} mem 1 1\n{marker}\n123\nstate\n", true, text => leading = text));
        Assert.Equal($"{Marker} mem 1 1\n", leading);
        Assert.Equal("123\n", AgentService.SplitLeadingOutput($"{marker}\n123\n", true, text => leading = text));
        Assert.Equal("", leading);
        // Without the marker nothing is taken away from the state, and no reading is invented.
        Assert.Equal("123\n", AgentService.SplitLeadingOutput("123\n", true, text => leading = text));
        Assert.Equal("", leading);
        Assert.Equal($"{marker}\n123\n", AgentService.SplitLeadingOutput($"{marker}\n123\n", false, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void CpuComesFromTheChangeSinceTheLastReading_AndOutagesStartAgain()
    {
        var cache = new RunnerStatusCache();
        var id = Guid.NewGuid();
        cache.SetGuestReading(id, Reading(1000, 900, diskUsed: 30));
        Assert.Null(cache.Get(id)!.CpuUsage); // One reading has no interval yet.
        Assert.Equal((2048L * 1024, 8192L * 1024, 30L), (cache.Get(id)!.MemoryUsedBytes, cache.Get(id)!.MemoryTotalBytes, cache.Get(id)!.DiskUsedBytes));

        cache.SetGuestReading(id, Reading(1200, 1000, memoryUsedKb: 4096));
        Assert.Equal(50d, cache.Get(id)!.CpuUsage);
        Assert.Equal(4096L * 1024, cache.Get(id)!.MemoryUsedBytes);
        Assert.Equal(30L, cache.Get(id)!.DiskUsedBytes); // A reading without disk keeps the last disk reading.

        cache.ClearGuestReading(id);
        var unreachable = cache.Get(id)!;
        Assert.Equal((null, null, null, null), (unreachable.CpuUsage, unreachable.MemoryUsedBytes, unreachable.DiskUsedBytes, unreachable.DiskTotalBytes));
        Assert.Equal((null, null, null), (cache.History(id)[^1].CpuPercent, cache.History(id)[^1].MemoryPercent, cache.History(id)[^1].DiskPercent));
        cache.SetGuestReading(id, Reading(1400, 1100));
        Assert.Null(cache.Get(id)!.CpuUsage); // No interval across an outage.
    }

    [Fact]
    public void ProviderStatusKeepsReadingsFromInsideWhileRunning_AndDropsThemWhenStopped()
    {
        var cache = new RunnerStatusCache();
        var id = Guid.NewGuid();
        cache.SetProviderStatus(id, new MachineStatus(MachinePowerState.Running, Address: "10.0.0.2"));
        cache.SetGuestReading(id, Reading(1000, 900, diskUsed: 30));
        cache.SetGuestReading(id, Reading(1200, 1000));
        var samples = cache.History(id).Count;

        // Power, address and uptime come from the provider; CPU, memory and disk stay those read inside.
        cache.SetProviderStatus(id, new MachineStatus(MachinePowerState.Running, Address: "10.0.0.3", CpuUsage: 99, MemoryUsedBytes: 1, MemoryTotalBytes: 1, Uptime: TimeSpan.FromHours(1)));
        var status = cache.Get(id)!;
        Assert.Equal(("10.0.0.3", TimeSpan.FromHours(1)), (status.Address, status.Uptime));
        Assert.Equal((50d, 2048L * 1024, 30L), (status.CpuUsage, status.MemoryUsedBytes, status.DiskUsedBytes));
        Assert.Equal(samples, cache.History(id).Count); // A provider status adds no reading.

        cache.SetProviderStatus(id, new MachineStatus(MachinePowerState.Stopped));
        Assert.Equal((null, null, null), (cache.Get(id)!.CpuUsage, cache.Get(id)!.MemoryUsedBytes, cache.Get(id)!.DiskTotalBytes));
        cache.SetProviderStatus(id, new MachineStatus(MachinePowerState.Running));
        Assert.Null(cache.Get(id)!.DiskTotalBytes);
        cache.SetGuestReading(id, Reading(1400, 1100));
        Assert.Null(cache.Get(id)!.CpuUsage); // Counters from before the stop don't count.
    }

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
    public void DoesNotStoreReadingsWithoutUsageMetrics()
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
}
