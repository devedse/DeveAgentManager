using System.Globalization;
using System.Text.Json;

namespace DeveAgentManager.Diagnostics;

internal static class LinuxCpuMeasurements
{
    internal static async Task RecordThreadsAsync(string path, Func<string> phase, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true));
        var ticks = Native.Sysconf(2); // _SC_CLK_TCK on Linux.
        if (ticks <= 0) throw new IOException("Could not read Linux CPU counter frequency.");
        while (true)
        {
            foreach (var task in Directory.EnumerateDirectories("/proc/self/task"))
            {
                try
                {
                    var line = await File.ReadAllTextAsync(Path.Combine(task, "stat"), ct);
                    var open = line.IndexOf('('); var close = line.LastIndexOf(')');
                    var fields = line[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var user = long.Parse(fields[11], CultureInfo.InvariantCulture);
                    var kernel = long.Parse(fields[12], CultureInfo.InvariantCulture);
                    await output.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, phase = phase(),
                        tid = int.Parse(Path.GetFileName(task), CultureInfo.InvariantCulture), name = line[(open + 1)..close],
                        startTicks = long.Parse(fields[19], CultureInfo.InvariantCulture), userCpuSeconds = user / (double)ticks,
                        kernelCpuSeconds = kernel / (double)ticks }));
                }
                catch (IOException) { } // A thread can exit between directory enumeration and reading stat.
            }
            await output.FlushAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    internal static Task SaveMapsAsync(string path, CancellationToken ct) => OperatingSystem.IsLinux()
        ? CopyAsync(path, ct) : Task.CompletedTask;
    private static async Task CopyAsync(string path, CancellationToken ct) => await File.WriteAllTextAsync(path, await File.ReadAllTextAsync("/proc/self/maps", ct), ct);
    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "sysconf")]
        internal static extern long Sysconf(int name);
    }
}
