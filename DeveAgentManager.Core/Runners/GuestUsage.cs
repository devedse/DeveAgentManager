namespace DeveAgentManager.Core.Runners;

/// <summary>
/// CPU, memory and root filesystem usage read inside the Runner from /proc and df, not from the provider: the provider
/// sees the VM from the hypervisor (its memory figure includes the guest's file cache) and existing machines have none.
/// The script is added to SSH commands the Manager runs anyway, and never fails them.
/// </summary>
public static class GuestUsage
{
    public const string Marker = "__DAM_GUEST__";

    /// <summary>One marked line each for the CPU counters, memory (kB) and, with <paramref name="disk"/>, / (bytes).</summary>
    public static string Script(bool disk) =>
        $"awk '/^cpu /{{$1=\"{Marker} cpu\"; print}}' /proc/stat 2>/dev/null || true\n"
        + $"awk '/^MemTotal:/{{t=$2}} /^MemAvailable:/{{a=$2}} END{{print \"{Marker} mem\", t, a}}' /proc/meminfo 2>/dev/null || true\n"
        + (disk ? $"df -B1 --output=used,size / 2>/dev/null | tail -n 1 | awk '{{print \"{Marker} disk\", $1, $2}}' || true\n" : "");

    /// <summary>Cumulative CPU time in clock ticks: all of it, and the part not idle or waiting for I/O.</summary>
    public readonly record struct CpuCounters(ulong Total, ulong Busy);

    public sealed record Reading(CpuCounters? Cpu, long? MemoryUsedBytes, long? MemoryTotalBytes, long? DiskUsedBytes, long? DiskTotalBytes)
    {
        public bool HasDisk => DiskTotalBytes is not null;
    }

    /// <summary>The marked lines in <paramref name="output"/>; a missing or malformed value is null, the rest still counts.</summary>
    public static Reading Parse(string output)
    {
        CpuCounters? cpu = null;
        long? memoryUsed = null, memoryTotal = null, diskUsed = null, diskTotal = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(Marker + " ", StringComparison.Ordinal)) continue;
            var parts = line[(Marker.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts)
            {
                // user nice system idle iowait irq softirq steal [guest guest_nice]: guest time is already in user/nice.
                case ["cpu", .. var fields] when fields.Length >= 4 && fields.Take(8).All(f => ulong.TryParse(f, out _)):
                    var ticks = fields.Take(8).Select(ulong.Parse).ToArray();
                    var total = ticks.Aggregate(0UL, (sum, t) => sum + t);
                    var idle = ticks[3] + (ticks.Length > 4 ? ticks[4] : 0);
                    cpu = new CpuCounters(total, total - idle);
                    break;
                case ["mem", var totalKb, var availableKb] when long.TryParse(totalKb, out var t) && long.TryParse(availableKb, out var a)
                    && t > 0 && a >= 0 && a <= t:
                    memoryTotal = t * 1024;
                    memoryUsed = (t - a) * 1024;
                    break;
                case ["disk", var usedBytes, var sizeBytes] when long.TryParse(usedBytes, out var u) && long.TryParse(sizeBytes, out var s)
                    && u >= 0 && s > 0 && u <= s:
                    diskUsed = u;
                    diskTotal = s;
                    break;
            }
        }

        return new Reading(cpu, memoryUsed, memoryTotal, diskUsed, diskTotal);
    }

    /// <summary>CPU use between two readings, in percent of all the Runner's cores; null without a usable interval (e.g. a reboot).</summary>
    public static double? CpuPercent(CpuCounters? previous, CpuCounters? current) =>
        previous is { } before && current is { } now && now.Total > before.Total && now.Busy >= before.Busy
            ? Math.Clamp((now.Busy - before.Busy) * 100.0 / (now.Total - before.Total), 0, 100)
            : null;
}
