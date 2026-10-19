using System.IO.Compression;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class ManagerCpuTraceTests
{
    [Fact]
    public async Task RealCaptureHasReadableManagedSamplesAndProcessMeasurements()
    {
        var folder = Directory.CreateTempSubdirectory("dam-cpu-real-").FullName;
        try
        {
            await new CpuTraceRecorder().RecordAsync(folder, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            using var source = new EventPipeEventSource(Path.Combine(folder, "manager.nettrace"));
            var samples = 0;
            source.Dynamic.All += e => { if (e.ProviderName == "Microsoft-DotNETCore-SampleProfiler") samples++; };
            source.Process();
            Assert.True(samples > 0, "The trace must contain real managed thread samples.");
            var lines = await File.ReadAllLinesAsync(Path.Combine(folder, "process.jsonl"), TestContext.Current.CancellationToken);
            Assert.True(lines.Length >= 2);
            using var first = JsonDocument.Parse(lines[0]);
            using var last = JsonDocument.Parse(lines[^1]);
            Assert.True(last.RootElement.GetProperty("elapsedSeconds").GetDouble() > first.RootElement.GetProperty("elapsedSeconds").GetDouble());
            Assert.True(last.RootElement.GetProperty("cpuSeconds").GetDouble() >= first.RootElement.GetProperty("cpuSeconds").GetDouble());
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "capture.json"), TestContext.Current.CancellationToken));
            Assert.Equal(Environment.ProcessId, manifest.RootElement.GetProperty("processId").GetInt32());
            Assert.Contains("waiting threads", manifest.RootElement.GetProperty("interpretation").GetString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task CaptureOwnershipCancellationFailureRetentionAndRestartAreDurable()
    {
        var folder = Directory.CreateTempSubdirectory("dam-cpu-state-").FullName;
        var recorder = new ControlledCpuRecorder();
        var options = new ManagerOptions { DataDirectory = folder };
        using var service = new ManagerCpuTraces(options, recorder, NullLogger<ManagerCpuTraces>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Empty(service.Status().Captures);
            var canceled = service.Start();
            Assert.Throws<InvalidOperationException>(() => service.Start());
            Assert.Throws<KeyNotFoundException>(() => service.Download(canceled.Id));
            Assert.Throws<InvalidOperationException>(() => service.Delete(canceled.Id));
            service.Cancel(canceled.Id);
            await WaitAsync(() => service.Status().Active is null);
            Assert.Equal("canceled", service.Status().Captures.Single().State);

            recorder.Fail = true;
            var failed = service.Start(); recorder.Release.Release();
            await WaitAsync(() => service.Status().Active is null);
            Assert.Equal("failed", service.Status().Captures.Single(c => c.Id == failed.Id).State);
            Assert.Throws<KeyNotFoundException>(() => service.Download(failed.Id));
            recorder.Fail = false;
            ManagerCpuTrace? ready = null;
            for (var i = 0; i < 4; i++)
            {
                ready = service.Start(); recorder.Release.Release();
                await WaitAsync(() => service.Status().Active is null);
                Assert.Equal("ready", service.Status().Captures[0].State);
            }
            Assert.Equal(3, service.Status().Captures.Count);
            await using (var download = service.Download(ready!.Id))
            using (var zip = new ZipArchive(download))
                Assert.Equal(new[] { "manager.nettrace", "process.jsonl", "capture.json" }, zip.Entries.Select(e => e.FullName));
            Assert.False(File.Exists(Path.Combine(folder, "diagnostics", "cpu", ready.Id.ToString("N"), "manager.nettrace")));
            service.Delete(ready.Id);
            Assert.Throws<KeyNotFoundException>(() => service.Download(ready.Id));
            var interrupted = service.Start();
            await service.StopAsync(TestContext.Current.CancellationToken);
            using var resumed = new ManagerCpuTraces(options, recorder, NullLogger<ManagerCpuTraces>.Instance);
            Assert.Null(resumed.Status().Active);
            Assert.Equal("interrupted", resumed.Status().Captures.Single(c => c.Id == interrupted.Id).State);
            Assert.Equal(3, resumed.Status().Captures.Count);
        }
        finally { await service.StopAsync(CancellationToken.None); Directory.Delete(folder, true); }
    }

    internal static async Task WaitAsync(Func<bool> complete)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!complete()) await Task.Delay(20, timeout.Token);
    }
}

internal sealed class ControlledCpuRecorder : ICpuTraceRecorder
{
    public SemaphoreSlim Release { get; } = new(0);
    public bool Fail { get; set; }
    public async Task RecordAsync(string folder, TimeSpan duration, CancellationToken ct)
    {
        await Release.WaitAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "manager.nettrace"), "test-only trace", ct);
        if (Fail) throw new IOException("Test-only storage failure");
        await File.WriteAllTextAsync(Path.Combine(folder, "process.jsonl"), "test-only measurements", ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "capture.json"), "{}", ct);
    }
}
