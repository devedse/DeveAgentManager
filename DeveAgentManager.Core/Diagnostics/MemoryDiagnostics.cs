using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DeveAgentManager.Core.Configuration;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Diagnostics;

/// <summary>Bounded, automatic evidence collection. Never forces GC or records conversation/credential contents.</summary>
public sealed class MemoryDiagnostics : BackgroundService
{
    private readonly ILogger<MemoryDiagnostics> _logger;
    private readonly object _gate = new();
    private readonly object _archiveGate = new();
    private readonly AsyncLocal<long> _parent = new();
    private readonly string _root, _run;
    private readonly bool _enabled;
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _nextOperation;
    private int _active, _startedOnce, _captures;
    private long _lastWarning;
    private long _lastCaptureRss;
    private bool _lastCaptureSucceeded;
    private DateTime _lastCaptureEnd = DateTime.MinValue;
    private DateTime _lastArchive = DateTime.MinValue;
    private Task? _capture;
    internal TimeSpan TraceDuration { get; init; } = TimeSpan.FromSeconds(30);
    internal long LogLimit { get; init; } = 5 * 1024 * 1024;
    internal long TraceLimit { get; init; } = 32 * 1024 * 1024;
    internal string RunDirectory => _run;
    internal string BundlePath => Path.Combine(_root, "latest.zip");

    public MemoryDiagnostics(ManagerOptions options, ILogger<MemoryDiagnostics> logger)
    {
        _logger = logger;
        _root = Path.Combine(options.DataDirectory, "diagnostics", "memory");
        _run = Path.Combine(_root, $"run-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        if (!options.MemoryDiagnosticsEnabled) return;
        try
        {
            Directory.CreateDirectory(_run);
            _enabled = true;
            var manifest = new { kind = "startup", at = DateTime.UtcNow, pid = Environment.ProcessId,
                build = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                runtime = Environment.Version.ToString(), cpuCount = Environment.ProcessorCount,
                serverGc = System.Runtime.GCSettings.IsServerGC, gcMemoryBudgetBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                traceDurationSeconds = TraceDuration.TotalSeconds, traceLimitBytes = TraceLimit,
                note = "Allocation deltas are process-wide, not exclusive to an operation. No heap dump or forced GC." };
            File.WriteAllText(Path.Combine(_run, "manifest.json"), JsonSerializer.Serialize(manifest));
            Write(manifest);
            foreach (var old in Directory.EnumerateDirectories(_root, "run-*").Where(p => p != _run).OrderDescending().Skip(2))
                try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Archive();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _enabled = false; Warn(ex); }
    }

    public override Task StartAsync(CancellationToken cancellationToken) =>
        Interlocked.Exchange(ref _startedOnce, 1) == 0 ? base.StartAsync(cancellationToken) : Task.CompletedTask;

    public Operation? Begin(string phase, Guid? session = null, long? bytes = null, string? harness = null)
    {
        if (!_enabled) return null;
        var operation = new Operation(this, Interlocked.Increment(ref _nextOperation), _parent.Value, phase,
            session is { } id ? Convert.ToHexString(SHA256.HashData(id.ToByteArray()))[..12] : "", bytes, harness);
        _parent.Value = operation.Id;
        Interlocked.Increment(ref _active);
        RecordOperation(operation, "begin");
        return operation;
    }

    public Operation? BeginFile(string phase, Guid session, string path)
    {
        if (!_enabled) return null;
        long? bytes = null;
        try { if (File.Exists(path)) bytes = new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Begin(phase, session, bytes);
    }

    public sealed class Operation : IDisposable
    {
        private readonly MemoryDiagnostics _owner;
        private int _disposed;
        internal readonly long Id, Parent, Started = Stopwatch.GetTimestamp(), Allocated = GC.GetTotalAllocatedBytes();
        internal readonly string Phase, Session;
        internal readonly string? Harness;
        internal readonly long? Bytes;
        internal bool Completed;
        internal Operation(MemoryDiagnostics owner, long id, long parent, string phase, string session, long? bytes, string? harness)
        { _owner = owner; Id = id; Parent = parent; Phase = phase; Session = session; Bytes = bytes; Harness = harness; }
        public void Complete() => Completed = true;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner._parent.Value = Parent;
            Interlocked.Decrement(ref _owner._active);
            _owner.RecordOperation(this, "end");
        }
    }

    private void RecordOperation(Operation op, string edge)
    {
        Write(new { kind = "operation", at = DateTime.UtcNow, edge, operation = op.Id, parent = op.Parent,
            phase = op.Phase, session = op.Session, harness = op.Harness, sourceBytes = op.Bytes,
            elapsedMs = Stopwatch.GetElapsedTime(op.Started).TotalMilliseconds,
            success = edge == "end" ? (bool?)op.Completed : null,
            processAllocatedDeltaBytes = GC.GetTotalAllocatedBytes() - op.Allocated, activeOperations = Volatile.Read(ref _active) });
        MemoryEvents.Log.Operation(op.Id, op.Parent, op.Phase, op.Session, op.Harness ?? "", edge, op.Bytes ?? -1);
    }

    internal MemoryReading Sample()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var container = ContainerMemory();
        var result = new MemoryReading(DateTime.UtcNow, process.WorkingSet64, process.PrivateMemorySize64,
            GC.GetTotalMemory(false), gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
            GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            AvailableMemory(), container.Used, container.Limit, process.TotalProcessorTime.TotalMilliseconds, Volatile.Read(ref _active));
        if (_enabled) Write(new { kind = "sample", reading = result });
        return result;
    }

    internal sealed record MemoryReading(DateTime At, long RssBytes, long PrivateBytes, long EstimatedManagedBytes,
        long LastGcHeapBytes, long GcCommittedBytes, long LastGcFragmentationBytes, long TotalAllocatedBytes,
        int Gen0Collections, int Gen1Collections, int Gen2Collections, long? SystemAvailableBytes,
        long? ContainerUsedBytes, long? ContainerLimitBytes, double CpuTotalMs, int ActiveOperations)
    {
        public long? HeadroomBytes => EffectiveHeadroom(SystemAvailableBytes, ContainerUsedBytes, ContainerLimitBytes);
    }

    internal static long? EffectiveHeadroom(long? system, long? used, long? limit)
    {
        if (used is null || limit is null) return system;
        var remaining = Math.Max(0, limit.Value - used.Value);
        return system is { } available ? Math.Min(available, remaining) : remaining;
    }

    private static (long? Used, long? Limit) ContainerMemory()
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        try
        {
            var v2 = File.Exists("/sys/fs/cgroup/memory.current");
            var used = File.ReadAllText(v2 ? "/sys/fs/cgroup/memory.current" : "/sys/fs/cgroup/memory/memory.usage_in_bytes").Trim();
            var limit = File.ReadAllText(v2 ? "/sys/fs/cgroup/memory.max" : "/sys/fs/cgroup/memory/memory.limit_in_bytes").Trim();
            return (long.TryParse(used, out var u) ? u : null, long.TryParse(limit, out var l) ? l : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    private static long? AvailableMemory()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var line = File.ReadLines("/proc/meminfo").FirstOrDefault(x => x.StartsWith("MemAvailable:", StringComparison.Ordinal));
            return line is null ? null : long.Parse(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                var sample = Sample();
                var ready = _capture is null || _capture.IsCompleted;
                var trigger = _captures == 0
                    ? sample.RssBytes >= 512L * 1024 * 1024 || Stopwatch.GetElapsedTime(_started) >= TimeSpan.FromSeconds(10)
                    : DateTime.UtcNow - _lastCaptureEnd >= TimeSpan.FromMinutes(1) && (!_lastCaptureSucceeded || sample.RssBytes >= _lastCaptureRss + 256L * 1024 * 1024);
                if (ready && _captures < 2 && trigger && sample.HeadroomBytes is not < 256L * 1024 * 1024)
                    _capture = CaptureAsync(stoppingToken);
                if (ready && DateTime.UtcNow - _lastArchive >= TimeSpan.FromMinutes(1)) Archive();
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { Warn(ex); }
        finally
        {
            if (_capture is not null) await _capture;
            Archive();
        }
    }

    internal async Task CaptureAsync(CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _captures);
        _lastCaptureSucceeded = false;
        var path = Path.Combine(_run, $"allocations-{number}.partial.nettrace");
        Write(new { kind = "trace-start", at = DateTime.UtcNow, number });
        try
        {
            // GC + Type + sampled allocations + stacks + loader/JIT. Runtime events, never heap contents.
            var providers = new[] {
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x40280019),
                new EventPipeProvider("DeveAgentManager-Memory", EventLevel.Informational)
            };
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var session = await new DiagnosticsClient(Environment.ProcessId).StartEventPipeSessionAsync(
                providers, requestRundown: true, circularBufferMB: 16, token: connectTimeout.Token);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true);
            var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var copyTimeout = new CancellationTokenSource();
            var copying = CopyTraceAsync(session.EventStream, output, full, copyTimeout.Token);
            var reason = "duration";
            try
            {
                using var windowStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var window = CaptureWindowAsync(windowStop.Token);
                await Task.WhenAny(copying, full.Task, window);
                if (window.IsCompletedSuccessfully) reason = window.Result;
                windowStop.Cancel();
                if (full.Task.IsCompleted) reason = "size-limit";
                else if (cancellationToken.IsCancellationRequested) reason = "shutdown";
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await session.StopAsync(stopTimeout.Token);
                await copying.WaitAsync(TimeSpan.FromSeconds(5));
                await output.FlushAsync(CancellationToken.None);
                if (full.Task.IsCompleted) reason = "size-limit"; // Rundown can cross the cap after Stop.
                if (reason != "size-limit")
                {
                    await output.DisposeAsync();
                    File.Move(path, path.Replace(".partial.nettrace", ".nettrace"), true);
                    _lastCaptureSucceeded = true;
                }
                Write(new { kind = "trace-end", at = DateTime.UtcNow, number, reason, bytes = output.CanWrite ? output.Length : new FileInfo(path.Replace(".partial.nettrace", ".nettrace")).Length });
            }
            finally
            {
                copyTimeout.Cancel();
                try { await copying; } catch (Exception) { }
            }
        }
        catch (Exception ex)
        { Write(new { kind = "trace-error", at = DateTime.UtcNow, number, errorType = ex.GetType().Name }); Warn(ex); }
        finally
        {
            _lastCaptureEnd = DateTime.UtcNow;
            using var process = Process.GetCurrentProcess();
            _lastCaptureRss = process.WorkingSet64;
            Archive();
        }
    }

    private async Task<string> CaptureWindowAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TraceDuration)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, TraceDuration.TotalMilliseconds)), ct);
            var container = ContainerMemory();
            if (EffectiveHeadroom(AvailableMemory(), container.Used, container.Limit) is < 128L * 1024 * 1024) return "headroom";
        }
        return "duration";
    }

    private async Task CopyTraceAsync(Stream input, Stream output, TaskCompletionSource full, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            var take = (int)Math.Min(read, Math.Max(0, TraceLimit - written));
            if (take > 0) { await output.WriteAsync(buffer.AsMemory(0, take), ct); written += take; }
            if (written >= TraceLimit) full.TrySetResult(); // Keep draining until the session stops.
        }
    }

    private void Write<T>(T value)
    {
        if (!_enabled) return;
        try
        {
            lock (_gate)
            {
                var path = Path.Combine(_run, "samples.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= LogLimit) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(value) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(ex); }
    }

    internal void Archive()
    {
        if (!_enabled) return;
        lock (_archiveGate)
        try
        {
            Write(new { kind = "archive-start", at = DateTime.UtcNow });
            var snapshot = Path.Combine(_run, "samples-capture.jsonl");
            var previous = snapshot + ".previous";
            lock (_gate)
            {
                File.Copy(Path.Combine(_run, "samples.jsonl"), snapshot, true);
                var old = Path.Combine(_run, "samples.jsonl.previous");
                if (File.Exists(old)) File.Copy(old, previous, true);
            }
            var temp = BundlePath + ".tmp";
            using (var stream = File.Create(temp))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(_run, "manifest.json"), "manifest.json");
                zip.CreateEntryFromFile(snapshot, "samples.jsonl");
                if (File.Exists(previous)) zip.CreateEntryFromFile(previous, "samples.previous.jsonl");
                foreach (var trace in Directory.EnumerateFiles(_run, "*.nettrace"))
                    zip.CreateEntryFromFile(trace, Path.GetFileName(trace), CompressionLevel.Fastest);
                // A restart must not hide the previous process's final samples or partial trace.
                var prior = Directory.EnumerateDirectories(_root, "run-*").Where(p => p != _run)
                    .OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
                if (prior is not null)
                    foreach (var file in Directory.EnumerateFiles(prior).Where(p =>
                        Path.GetFileName(p) is "manifest.json" or "samples.jsonl" or "samples.jsonl.previous"
                        || Path.GetFileName(p).StartsWith("allocations-", StringComparison.Ordinal) && p.EndsWith(".nettrace", StringComparison.Ordinal)))
                        zip.CreateEntryFromFile(file, "previous-run/" + Path.GetFileName(file), CompressionLevel.Fastest);
            }
            File.Move(temp, BundlePath, true);
            _lastArchive = DateTime.UtcNow;
            Write(new { kind = "archive-end", at = DateTime.UtcNow });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(ex); }
    }

    private void Warn(Exception ex)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastWarning);
        if (previous != 0 && now - previous < 60_000) return;
        if (Interlocked.CompareExchange(ref _lastWarning, now, previous) == previous)
            _logger.LogWarning("Memory evidence collection problem: {ErrorType}", ex.GetType().Name);
    }
}

[EventSource(Name = "DeveAgentManager-Memory")]
internal sealed class MemoryEvents : EventSource
{
    public static readonly MemoryEvents Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Operation(long id, long parent, string phase, string session, string harness, string edge, long bytes)
    { if (IsEnabled()) WriteEvent(1, id, parent, phase, session, harness, edge, bytes); }
}
