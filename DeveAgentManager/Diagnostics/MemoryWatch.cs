using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Core.Configuration;

namespace DeveAgentManager.Diagnostics;

/// <summary>
/// The kernel kills a Manager that runs out of memory without a log line. Every 5 seconds this writes a heartbeat with
/// the memory reading and the requests in progress (diagnostics/heartbeat.json), warns when memory runs low, and at
/// start-up reports a previous process that ended without shutting down, with its last heartbeat (docs/manager-memory.md).
/// </summary>
public sealed class MemoryWatch(ManagerOptions options, IHostApplicationLifetime lifetime, ILogger<MemoryWatch> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<long, Request> InFlight = new();
    private static long _nextRequest;
    private readonly DateTime _startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private DateTime _warnedAt = DateTime.MinValue;
    private readonly Lock _write = new();
    private bool _stopping;

    private string HeartbeatPath => Path.Combine(options.DataDirectory, "diagnostics", "heartbeat.json");

    public sealed record Request(string Method, string Path, DateTime StartedAt);

    public sealed record Heartbeat(DateTime ProcessStartedAt, DateTime At, long WorkingSetBytes, long GcHeapBytes, long GcLimitBytes,
        long? AvailableBytes, long? TotalBytes, IReadOnlyList<string> Requests, bool CleanShutdown = false);

    /// <summary>Middleware: records each request (method and path, never the query) while it runs. Sockets stay open, so they are left out.</summary>
    public static async Task TrackRequestsAsync(HttpContext context, Func<Task> next)
    {
        if (context.WebSockets.IsWebSocketRequest || context.Request.Path.StartsWithSegments("/hubs"))
        {
            await next();
            return;
        }

        var id = Interlocked.Increment(ref _nextRequest);
        InFlight[id] = new Request(context.Request.Method, context.Request.Path.Value ?? "", DateTime.UtcNow);
        try { await next(); }
        finally { InFlight.TryRemove(id, out _); }
    }

    /// <summary>The longest-running requests first, e.g. "GET /api/sessions/…/content for 12 s".</summary>
    internal static IReadOnlyList<string> RunningRequests(DateTime now, int take = 8) =>
        InFlight.Values.OrderBy(r => r.StartedAt).Take(take)
            .Select(r => $"{r.Method} {r.Path} for {(now - r.StartedAt).TotalSeconds:0} s").ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReportPreviousProcess();
        lifetime.ApplicationStopping.Register(() => Write(Read(DateTime.UtcNow) with { CleanShutdown = true }));
        if (lifetime.ApplicationStopping.IsCancellationRequested) return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var now = DateTime.UtcNow;
                var reading = Read(now);
                Write(reading);
                if (IsLow(reading) && now - _warnedAt >= WarnEvery)
                {
                    _warnedAt = now;
                    logger.LogWarning("Manager memory is running low: {Reading}. Requests in progress: {Requests}", Describe(reading), Requests(reading));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Memory reading failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal Heartbeat Read(DateTime now)
    {
        var gc = GC.GetGCMemoryInfo();
        var (available, total) = SystemMemory();
        return new Heartbeat(_startedAt, now, Environment.WorkingSet, gc.HeapSizeBytes, gc.TotalAvailableMemoryBytes, available, total, RunningRequests(now));
    }

    /// <summary>Under 15% of the machine's memory available, or the .NET heap past 80% of its limit.</summary>
    internal static bool IsLow(Heartbeat reading) =>
        reading is { AvailableBytes: { } available, TotalBytes: > 0 and var total } && available < total * 0.15
        || reading.GcLimitBytes > 0 && reading.GcHeapBytes > reading.GcLimitBytes * 0.8;

    internal static string Describe(Heartbeat reading) =>
        $"working set {reading.WorkingSetBytes / 1048576} MB, .NET heap {reading.GcHeapBytes / 1048576} of {reading.GcLimitBytes / 1048576} MB"
        + (reading is { AvailableBytes: { } available, TotalBytes: { } total } ? $", machine {available / 1048576} of {total / 1048576} MB available" : "");

    private static string Requests(Heartbeat reading) => reading.Requests.Count == 0 ? "none" : string.Join("; ", reading.Requests);

    // At start-up: the previous heartbeat says how a process that never shut down looked last.
    private void ReportPreviousProcess()
    {
        try
        {
            if (!File.Exists(HeartbeatPath)) return;
            var previous = JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(HeartbeatPath));
            if (previous is null || previous.CleanShutdown || previous.ProcessStartedAt == _startedAt) return;
            logger.LogWarning("The previous Manager process (started {Started:u}) ended without shutting down, after {At:u}. Its last reading: {Reading}{Low}. Requests in progress then: {Requests}",
                previous.ProcessStartedAt, previous.At, Describe(previous),
                IsLow(previous) ? "; memory was low, so the kernel most likely killed it for running out of memory" : "", Requests(previous));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "The previous heartbeat could not be read");
        }
    }

    // Once shutting down, only the clean-shutdown heartbeat is written, so a late reading can't replace it.
    private void Write(Heartbeat reading)
    {
        lock (_write)
        {
            if (_stopping) return;
            _stopping = reading.CleanShutdown;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HeartbeatPath)!);
                File.WriteAllText(HeartbeatPath + ".tmp", JsonSerializer.Serialize(reading));
                File.Move(HeartbeatPath + ".tmp", HeartbeatPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogDebug(ex, "The heartbeat could not be written"); }
        }
    }

    // MemAvailable and MemTotal from /proc/meminfo, in bytes; null elsewhere.
    internal static (long? Available, long? Total) SystemMemory(string path = "/proc/meminfo")
    {
        try
        {
            long? available = null, total = null;
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) continue;
                if (parts[0] == "MemAvailable:") available = kb * 1024;
                else if (parts[0] == "MemTotal:") total = kb * 1024;
            }

            return (available, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }
}
