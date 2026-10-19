using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;

namespace DeveAgentManager.Diagnostics;

public interface ICpuTraceRecorder
{
    Task RecordAsync(string folder, CpuTracePlan plan, Action<string, int> phaseChanged, CancellationToken ct);
}

/// <summary>EventPipe sampling bracketed by process measurements with no profiler attached.</summary>
public sealed class CpuTraceRecorder : ICpuTraceRecorder
{
    internal const long MaxTraceBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
        // GC, loader, JIT, threading and method maps; excludes exception messages and HTTP payloads.
        EventPipeProvider[] providers = [
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x30019)
        ];
        try
        {
            await Task.Delay(plan.Before, ct);
            ChangePhase("profiler-starting", 10);
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
            ct.ThrowIfCancellationRequested();
            // Rundown and IPC shutdown have finished: these measurements exclude our stack profiler.
            ChangePhase("baseline-after", (int)plan.After.TotalSeconds);
            await Task.Delay(plan.After, ct);
            ChangePhase("complete", 0);
        }
        finally
        {
            await metricsStop.CancelAsync();
            await metrics;
        }
        ct.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(folder, "capture.json"), JsonSerializer.Serialize(new
        {
            version = 2, startedAt = started, finishedAt = DateTimeOffset.UtcNow, processStartedAt,
            requestedSeconds = plan.DurationSeconds, profilingSeconds = plan.Profiling.TotalSeconds,
            baselineBeforeSeconds = plan.Before.TotalSeconds, baselineAfterSeconds = plan.After.TotalSeconds,
            processId = Environment.ProcessId, processorCount = Environment.ProcessorCount,
            runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            managerVersion = typeof(CpuTraceRecorder).Assembly.GetName().Version?.ToString(),
            configuredSamplingIntervalMilliseconds = ConfiguredSamplingInterval(),
            method = "Unprofiled baselines before and after EventPipe managed thread samples, with runtime events and method rundown",
            interpretation = "Thread samples include waiting threads and are not exact on-CPU percentages. Compare process CPU-time deltas and phase summaries. Baselines exclude this collector's profiler; other workloads and external profilers can still affect them. Capture and rundown overhead are labeled separately. Startup workloads change over time, so phase differences are not pure overhead measurements.",
            phases, providers = providers.Select(p => new { p.Name, p.Keywords, level = p.EventLevel.ToString() })
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

    internal static async Task CopyBoundedAsync(Stream source, string path, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxTraceBytes) throw new IOException("The trace exceeded its 128 MiB limit.");
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
