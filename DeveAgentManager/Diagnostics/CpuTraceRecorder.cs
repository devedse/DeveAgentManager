using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;

namespace DeveAgentManager.Diagnostics;

public interface ICpuTraceRecorder
{
    string Method => "thread-samples";
    string? Warning => null;
    Task RecordAsync(string folder, CpuTracePlan plan, Action<string, int> phaseChanged, CancellationToken ct);
}

/// <summary>CPU-clock sampling in the diagnostic image, with an explicit portable EventPipe fallback.</summary>
public sealed class CpuTraceRecorder(NativeCpuProfiler? profiler = null) : ICpuTraceRecorder
{
    internal const long MaxTraceBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Method => profiler?.Available == true ? "cpu-timer" : "thread-samples";
    public string? Warning => profiler?.UnavailableReason;

    public async Task RecordAsync(string folder, CpuTracePlan plan, Action<string, int> phaseChanged, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        using var process = Process.GetCurrentProcess();
        var processStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
        var clock = Stopwatch.StartNew();
        var phase = "baseline-before";
        var phases = new List<object>();
        Measurement? phaseStart = null;
        using var metricsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var operations = new CaptureOperationWriter(Path.Combine(folder, "operations.jsonl"));

        void ChangePhase(string name, int seconds)
        {
            var boundary = Measure(process, processStartedAt, clock, phase);
            if (phaseStart is not null)
            {
                var elapsed = boundary.ElapsedSeconds - phaseStart.ElapsedSeconds;
                var cpu = boundary.CpuSeconds - phaseStart.CpuSeconds;
                phases.Add(new
                {
                    name = phase, startedAt = phaseStart.Utc, finishedAt = boundary.Utc,
                    startElapsedSeconds = phaseStart.ElapsedSeconds, endElapsedSeconds = boundary.ElapsedSeconds,
                    elapsedSeconds = elapsed, cpuSeconds = cpu, averageCores = cpu / elapsed,
                    cpuCapacityPercent = cpu / elapsed / Environment.ProcessorCount * 100,
                    allocatedBytes = boundary.AllocatedBytes - phaseStart.AllocatedBytes,
                    gcPauseMilliseconds = boundary.GcPauseMilliseconds - phaseStart.GcPauseMilliseconds
                });
            }
            phaseStart = boundary;
            Volatile.Write(ref phase, name);
            phaseChanged(name, seconds);
        }

        ChangePhase("baseline-before", (int)plan.Before.TotalSeconds);
        var metrics = SampleAsync(Path.Combine(folder, "process.jsonl"), processStartedAt, clock, () => Volatile.Read(ref phase), metricsStop.Token);
        var threads = LinuxCpuMeasurements.RecordThreadsAsync(Path.Combine(folder, "thread-cpu.jsonl"), () => Volatile.Read(ref phase), metricsStop.Token);
        // GC, loader, JIT, threading and method maps; excludes exception messages and HTTP payloads.
        EventPipeProvider[] providers = [
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x30019)
        ];
        try
        {
            await LinuxCpuMeasurements.SaveMapsAsync(Path.Combine(folder, "maps-before.txt"), ct);
            await Task.Delay(plan.Before, ct);
            ChangePhase("profiler-starting", 10);
            if (profiler?.Available == true)
            {
                profiler.Begin(folder);
                RuntimeContextTrace? runtimeEvents = null;
                try
                {
                    var sampling = Stopwatch.StartNew();
                    ChangePhase("profiling", (int)plan.Profiling.TotalSeconds);
                    runtimeEvents = await RuntimeContextTrace.StartAsync(folder, plan.Profiling, ct);
                    var remaining = plan.Profiling - sampling.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
                }
                finally
                {
                    ChangePhase("profiler-stopping", NativeCpuProfiler.FlushSeconds + 8);
                    try { await profiler.EndAsync(ct); }
                    finally { if (runtimeEvents is not null) await runtimeEvents.DisposeAsync(); }
                }
            }
            else
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                using (var session = await new DiagnosticsClient(Environment.ProcessId)
                    .StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferMB: 16, connectTimeout.Token))
                {
                    using var drain = new CancellationTokenSource(plan.Profiling + TimeSpan.FromSeconds(30));
                    var copy = CopyBoundedAsync(session.EventStream, Path.Combine(folder, "manager.nettrace"), drain.Token);
                    try
                    {
                        ChangePhase("profiling", (int)plan.Profiling.TotalSeconds);
                        var delay = Task.Delay(plan.Profiling, ct);
                        if (await Task.WhenAny(copy, delay) == copy)
                        {
                            await copy;
                            throw new IOException("The runtime ended the trace before the requested duration.");
                        }
                        await delay;
                    }
                    finally
                    {
                        ChangePhase("profiler-stopping", 8);
                        try
                        {
                            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                            await session.StopAsync(stopTimeout.Token);
                            await copy.WaitAsync(stopTimeout.Token);
                        }
                        finally
                        {
                            session.Dispose();
                            await drain.CancelAsync();
                            // Closing the session interrupts pending IPC reads, including failed/cancelled captures.
                            try { await copy; } catch { }
                        }
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            // Rundown and IPC shutdown have finished: these measurements exclude our stack profiler.
            ChangePhase("baseline-after", (int)plan.After.TotalSeconds);
            await Task.Delay(plan.After, ct);
            ChangePhase("complete", 0);
            await LinuxCpuMeasurements.SaveMapsAsync(Path.Combine(folder, "maps-after.txt"), ct);
        }
        finally
        {
            await metricsStop.CancelAsync();
            await metrics;
            try { await threads; } catch (OperationCanceledException) when (metricsStop.IsCancellationRequested) { }
        }
        ct.ThrowIfCancellationRequested();
        if (profiler?.Available == true) await NativeCpuProfiler.WriteSummaryAsync(folder, ct);
        long? sampledCpuNanoseconds = null;
        long? unresolvedSelfNanoseconds = null;
        if (profiler?.Available == true)
        {
            using var cpuSummary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "cpu-methods.json"), ct));
            sampledCpuNanoseconds = cpuSummary.RootElement.GetProperty("sampledCpuNanoseconds").GetInt64();
            unresolvedSelfNanoseconds = cpuSummary.RootElement.GetProperty("unresolvedSelfNanoseconds").GetInt64();
        }
        var symbolsManifest = Path.Combine(AppContext.BaseDirectory, "diagnostics-symbols", "manifest.json");
        if (File.Exists(symbolsManifest)) File.Copy(symbolsManifest, Path.Combine(folder, "symbols-manifest.json"));
        await File.WriteAllTextAsync(Path.Combine(folder, "capture.json"), JsonSerializer.Serialize(new
        {
            version = 3, startedAt = started, finishedAt = DateTimeOffset.UtcNow, processStartedAt,
            requestedSeconds = plan.DurationSeconds, profilingSeconds = plan.Profiling.TotalSeconds,
            baselineBeforeSeconds = plan.Before.TotalSeconds, baselineAfterSeconds = plan.After.TotalSeconds,
            processId = Environment.ProcessId, processorCount = Environment.ProcessorCount,
            runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            managerVersion = typeof(CpuTraceRecorder).Assembly.GetName().Version?.ToString(),
            configuredSamplingIntervalMilliseconds = ConfiguredSamplingInterval(),
            recorder = Method, profilerVersion = profiler?.Version,
            cpuSamplingIntervalMilliseconds = profiler?.Available == true ? NativeCpuProfiler.SamplingIntervalMilliseconds : (int?)null,
            warning = Warning,
            sampledCpuNanoseconds, unresolvedSelfNanoseconds,
            method = profiler?.Available == true ? "Per-thread CPU-clock samples with managed caller stacks, bounded local pprof export, operation timings and thread CPU counters"
                : "Unprofiled baselines before and after EventPipe managed thread samples, with runtime events and method rundown",
            interpretation = profiler?.Available == true
                ? "CPU-time estimates from 19 ms per-thread CPU timers, not wall-clock samples of waiting threads. Managed caller frames are retained; native and kernel frames are omitted, so native/syscall CPU can be charged to the nearest retained managed caller. Compare sampled CPU with process/thread counters to assess missing native threads and discarded samples. Baselines exclude the sampler, not the loaded native profiler and other capture instrumentation. Inclusive method times overlap; sampled CPU percentages are not fractions of total VM capacity. Operation timings measure elapsed time, not CPU time."
                : "Thread samples include waiting threads and are not exact on-CPU percentages. Compare process CPU-time deltas and phase summaries. Baselines exclude this collector's profiler; other workloads and external profilers can still affect them. Capture and rundown overhead are labeled separately. Startup workloads change over time, so phase differences are not pure overhead measurements.",
            phases, providers = profiler?.Available == true ? [] : providers.Select(p => new { p.Name, p.Keywords, level = p.EventLevel.ToString() }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    // CoreCLR reads this DWORD (hexadecimal milliseconds) during runtime initialization, before Main.
    // It cannot be changed by setting an environment variable when a capture starts. Docker sets 32 = 50 ms.
    private static uint? ConfiguredSamplingInterval()
    {
        var value = Environment.GetEnvironmentVariable("DOTNET_EventPipeThreadSamplingRate")
            ?? Environment.GetEnvironmentVariable("COMPlus_EventPipeThreadSamplingRate");
        if (value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true) value = value[2..];
        return uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0
            ? milliseconds : null;
    }

    internal static async Task CopyBoundedAsync(Stream source, string path, CancellationToken ct, long maxBytes = MaxTraceBytes)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new IOException($"The trace exceeded its {maxBytes / 1024 / 1024} MiB limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await output.FlushAsync(ct);
    }

    private static async Task SampleAsync(string path, DateTimeOffset processStartedAt, Stopwatch clock, Func<string> phase, CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        await using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true));
        while (true)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(Measure(process, processStartedAt, clock, phase()), Json));
            await output.FlushAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Measure(process, processStartedAt, clock, phase()), Json));
                break;
            }
        }
    }

    private static Measurement Measure(Process process, DateTimeOffset processStartedAt, Stopwatch clock, string phase)
    {
        process.Refresh();
        var utc = DateTimeOffset.UtcNow;
        return new(utc, clock.Elapsed.TotalSeconds, (utc - processStartedAt).TotalSeconds, phase,
            process.TotalProcessorTime.TotalSeconds, process.UserProcessorTime.TotalSeconds, process.PrivilegedProcessorTime.TotalSeconds,
            process.WorkingSet64, GC.GetTotalAllocatedBytes(), GC.GetTotalMemory(false), GC.CollectionCount(0), GC.CollectionCount(1),
            GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds, ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount);
    }

    private sealed record Measurement(DateTimeOffset Utc, double ElapsedSeconds, double UptimeSeconds, string Phase,
        double CpuSeconds, double UserCpuSeconds, double KernelCpuSeconds, long WorkingSetBytes, long AllocatedBytes,
        long HeapBytes, int Gen0, int Gen1, int Gen2, double GcPauseMilliseconds, int ThreadPoolThreads,
        long PendingWork, long CompletedWork);
}
