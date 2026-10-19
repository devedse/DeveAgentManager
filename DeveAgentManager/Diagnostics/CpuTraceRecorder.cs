using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;

namespace DeveAgentManager.Diagnostics;

public interface ICpuTraceRecorder
{
    Task RecordAsync(string folder, TimeSpan duration, CancellationToken ct);
}

/// <summary>Portable EventPipe collection from this process, with no tools, root access or heap dump.</summary>
public sealed class CpuTraceRecorder : ICpuTraceRecorder
{
    internal const long MaxTraceBytes = 128L * 1024 * 1024;

    public async Task RecordAsync(string folder, TimeSpan duration, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        // GC, loader, JIT, threading and method maps; deliberately excludes exception messages and HTTP payloads.
        EventPipeProvider[] providers = [
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x30019)
        ];
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var session = await new DiagnosticsClient(Environment.ProcessId)
            .StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferMB: 16, connectTimeout.Token);
        using var drain = new CancellationTokenSource(duration + TimeSpan.FromSeconds(30));
        using var metricsStop = new CancellationTokenSource();
        var metrics = SampleAsync(Path.Combine(folder, "process.jsonl"), metricsStop.Token);
        var copy = CopyBoundedAsync(session.EventStream, Path.Combine(folder, "manager.nettrace"), drain.Token);
        try
        {
            var delay = Task.Delay(duration, ct);
            if (await Task.WhenAny(copy, delay) == copy)
            {
                await copy;
                throw new IOException("The runtime ended the trace before the requested duration.");
            }
            await delay;
        }
        finally
        {
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
                // Closing the session interrupts a pending IPC read, including failed/cancelled captures.
                try { await copy; } catch { }
                await metricsStop.CancelAsync();
                await metrics;
            }
        }
        ct.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(folder, "capture.json"), JsonSerializer.Serialize(new
        {
            version = 1, startedAt = started, finishedAt = DateTimeOffset.UtcNow, requestedSeconds = duration.TotalSeconds,
            processId = Environment.ProcessId, processorCount = Environment.ProcessorCount,
            runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            managerVersion = typeof(CpuTraceRecorder).Assembly.GetName().Version?.ToString(),
            method = "EventPipe managed thread samples and runtime GC/JIT events, with method rundown",
            interpretation = "Thread samples include waiting threads and are not exact on-CPU percentages. Use process.jsonl CPU-time deltas to measure CPU consumption. Capture overhead is included.",
            providers = providers.Select(p => new { p.Name, p.Keywords, level = p.EventLevel.ToString() })
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
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

    private static async Task SampleAsync(string path, CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        await using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true));
        var clock = Stopwatch.StartNew();
        while (true)
        {
            process.Refresh();
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, elapsedSeconds = clock.Elapsed.TotalSeconds,
                cpuSeconds = process.TotalProcessorTime.TotalSeconds, userCpuSeconds = process.UserProcessorTime.TotalSeconds,
                kernelCpuSeconds = process.PrivilegedProcessorTime.TotalSeconds, workingSetBytes = process.WorkingSet64,
                allocatedBytes = GC.GetTotalAllocatedBytes(), heapBytes = GC.GetTotalMemory(false),
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,
                threadPoolThreads = ThreadPool.ThreadCount, pendingWork = ThreadPool.PendingWorkItemCount,
                completedWork = ThreadPool.CompletedWorkItemCount
            }));
            await output.FlushAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
