using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;

namespace DeveAgentManager.Diagnostics;

/// <summary>Loader/JIT events explain compiler-thread CPU without enabling the wall-clock thread sampler.</summary>
internal sealed class RuntimeContextTrace : IAsyncDisposable
{
    private readonly string _folder;
    private readonly CancellationTokenSource _drain;
    private EventPipeSession? _session;
    private Task? _copy;
    private string? _error;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    public bool Available => _session is not null;

    private RuntimeContextTrace(string folder, TimeSpan duration)
    { _folder = folder; _drain = new(duration + TimeSpan.FromSeconds(30)); }

    internal static async Task<RuntimeContextTrace> StartAsync(string folder, TimeSpan duration, CancellationToken ct)
    {
        var trace = new RuntimeContextTrace(folder, duration);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            trace._session = await new DiagnosticsClient(Environment.ProcessId).StartEventPipeSessionAsync(
                [new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x18)],
                requestRundown: false, circularBufferMB: 8, timeout.Token);
            trace._copy = CpuTraceRecorder.CopyBoundedAsync(trace._session.EventStream,
                Path.Combine(folder, "runtime.nettrace"), trace._drain.Token, 32L * 1024 * 1024);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or DiagnosticsClientException or TimeoutException or OperationCanceledException)
        { trace._error = ex.GetType().Name; }
        catch { trace._drain.Dispose(); throw; }
        return trace;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await _session.StopAsync(timeout.Token);
                await _copy!.WaitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or DiagnosticsClientException or OperationCanceledException)
            { _error = ex.GetType().Name; }
            finally
            {
                _session.Dispose();
                await _drain.CancelAsync();
                try { await _copy!; } catch { }
            }
        }
        _drain.Dispose();
        if (_error is not null) File.Delete(Path.Combine(_folder, "runtime.nettrace"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "runtime-events.json"), JsonSerializer.Serialize(new
        {
            available = Available && _error is null, error = _error, startedAt = _startedAt, finishedAt = DateTimeOffset.UtcNow,
            provider = "Microsoft-Windows-DotNETRuntime",
            keywords = "0x18", level = "Verbose", stackSampling = false, rundown = false,
            interpretation = "Loader and JIT events identify methods being compiled, including compiler-thread work absent from managed CPU stacks. Event durations are elapsed times, not exact per-compilation CPU time. This session never enables the sampled-thread-time provider. Missing context events do not invalidate CPU-clock samples."
        }));
    }
}
