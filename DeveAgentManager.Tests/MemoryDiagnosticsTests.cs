using System.IO.Compression;
using System.Text.Json;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class MemoryDiagnosticsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dam-memory-test-").FullName;
    private MemoryDiagnostics Create(bool enabled = true) => new(new ManagerOptions { DataDirectory = _folder, MemoryDiagnosticsEnabled = enabled }, NullLogger<MemoryDiagnostics>.Instance);

    [Theory]
    [InlineData(4096L, 900L, 1024L, 124L)]
    [InlineData(100L, 0L, 1024L, 100L)]
    [InlineData(null, 900L, 1024L, 124L)]
    [InlineData(4096L, null, null, 4096L)]
    [InlineData(4096L, 1100L, 1024L, 0L)]
    public void CaptureHeadroomRespectsContainerAndHostLimits(long? system, long? used, long? limit, long expected) =>
        Assert.Equal(expected, MemoryDiagnostics.EffectiveHeadroom(system, used, limit));

    [Fact]
    public void RetentionAlwaysKeepsCurrentRunEvenIfOlderDirectoriesHaveFutureDates()
    {
        var root = Path.Combine(_folder, "diagnostics", "memory");
        Directory.CreateDirectory(root);
        for (var i = 0; i < 5; i++) Directory.CreateDirectory(Path.Combine(root, $"run-99999999-{i}"));
        using var capture = Create();
        Assert.True(Directory.Exists(capture.RunDirectory));
        Assert.Equal(3, Directory.EnumerateDirectories(root, "run-*").Count());
    }

    [Fact]
    public void DisabledCreatesNoFilesAndNoOperationScopes()
    {
        using var capture = Create(false);
        Assert.Null(capture.Begin("history-download", Guid.NewGuid()));
        capture.Archive();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("true", null, true)]
    [InlineData("false", "true", false)]
    [InlineData(null, "true", true)]
    public void ContainersCaptureAutomaticallyWithAnExplicitOptOut(string? setting, string? container, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["DATA_DIR"] = _folder, ["MEMORY_DIAGNOSTICS_ENABLED"] = setting, ["DOTNET_RUNNING_IN_CONTAINER"] = container
        }).Build();
        Assert.Equal(expected, ManagerOptions.FromConfiguration(config, _folder).MemoryDiagnosticsEnabled);
    }

    [Fact]
    public void SamplesAndNestedOperationsAreBoundedAndUseOpaqueSessionTags()
    {
        using var capture = new MemoryDiagnostics(new ManagerOptions { DataDirectory = _folder, MemoryDiagnosticsEnabled = true }, NullLogger<MemoryDiagnostics>.Instance) { LogLimit = 4096 };
        var id = Guid.NewGuid();
        using (var outer = capture.Begin("session-sync", id, 1234, "claude"))
        {
            using (var inner = capture.Begin("history-download", id, 1234, "claude")) inner!.Complete();
            capture.Sample();
            outer!.Complete();
        }
        var path = Path.Combine(capture.RunDirectory, "samples.jsonl");
        var rows = File.ReadLines(path).Select(s => JsonDocument.Parse(s)).ToArray();
        var operations = rows.Where(r => r.RootElement.GetProperty("kind").GetString() == "operation").Select(r => r.RootElement).ToArray();
        Assert.Equal(4, operations.Length);
        Assert.Equal(operations[0].GetProperty("operation").GetInt64(), operations[1].GetProperty("parent").GetInt64());
        Assert.True(operations[^1].GetProperty("success").GetBoolean());
        Assert.DoesNotContain(id.ToString(), File.ReadAllText(path));
        Assert.DoesNotContain(id.ToString("N"), File.ReadAllText(path));
        foreach (var row in rows) row.Dispose();
        for (var i = 0; i < 40; i++) capture.Sample();
        capture.Archive();
        Assert.InRange(new FileInfo(path).Length, 1, 8192);
        Assert.InRange(new FileInfo(path + ".previous").Length, 1, 8192);
        using var zip = ZipFile.OpenRead(capture.BundlePath);
        Assert.Contains(zip.Entries, entry => entry.FullName == "samples.jsonl");
        Assert.Contains(zip.Entries, entry => entry.FullName == "samples.previous.jsonl");
    }

    [Fact]
    public async Task AutomaticCaptureProducesAnAnalyzableAllocationTraceAndBundle()
    {
        using var capture = new MemoryDiagnostics(new ManagerOptions { DataDirectory = _folder, MemoryDiagnosticsEnabled = true }, NullLogger<MemoryDiagnostics>.Instance) { TraceDuration = TimeSpan.FromSeconds(2) };
        var collecting = capture.CaptureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        using (var operation = capture.Begin("native-history-parse", Guid.NewGuid(), 12345, "codex"))
        {
            for (var i = 0; i < 100; i++)
            {
                var allocation = new byte[256 * 1024];
                allocation[0] = 1;
                await Task.Delay(10, TestContext.Current.CancellationToken);
                GC.KeepAlive(allocation);
            }
            operation!.Complete();
        }
        await collecting;
        var trace = Assert.Single(Directory.GetFiles(capture.RunDirectory, "allocations-*.nettrace"));
        Assert.DoesNotContain("partial", trace);
        var converted = TraceLog.CreateFromEventPipeDataFile(trace);
        using var parsed = new TraceLog(converted);
        var allocations = 0;
        var withStacks = 0;
        var markers = 0;
        foreach (var entry in parsed.Events)
        {
            if (entry is GCAllocationTickTraceData allocation)
            {
                allocations++;
                if (allocation.CallStack() is not null) withStacks++;
            }
            if (entry.ProviderName == "DeveAgentManager-Memory" && entry.EventName.Contains("Operation")) markers++;
        }
        Assert.True(allocations > 0, "The trace must contain sampled allocations.");
        Assert.True(withStacks > 0, "Allocations must have call stacks.");
        Assert.True(markers > 0, "Operation markers must be present in the same timeline.");
        using var bundle = ZipFile.OpenRead(capture.BundlePath);
        Assert.Contains(bundle.Entries, e => e.FullName == "allocations-1.nettrace");
        Assert.Contains(bundle.Entries, e => e.FullName == "samples.jsonl");
        Assert.Contains(bundle.Entries, e => e.FullName == "manifest.json");
    }

    [Fact]
    public async Task TraceSizeLimitRetainsAndLabelsPartialEvidence()
    {
        using var capture = new MemoryDiagnostics(new ManagerOptions { DataDirectory = _folder, MemoryDiagnosticsEnabled = true }, NullLogger<MemoryDiagnostics>.Instance)
            { TraceDuration = TimeSpan.FromMilliseconds(200), TraceLimit = 4096 };
        await capture.CaptureAsync(TestContext.Current.CancellationToken);
        var trace = Assert.Single(Directory.GetFiles(capture.RunDirectory, "*.nettrace"));
        Assert.EndsWith(".partial.nettrace", trace);
        Assert.InRange(new FileInfo(trace).Length, 1, 4096);
        Assert.Contains("size-limit", File.ReadAllText(Path.Combine(capture.RunDirectory, "samples.jsonl")));
        using var bundle = ZipFile.OpenRead(capture.BundlePath);
        Assert.Contains(bundle.Entries, e => e.FullName == "allocations-1.partial.nettrace");
    }

    [Fact]
    public void RestartBundleIncludesPreviousSamplesAndPartialTrace()
    {
        using var previous = Create();
        previous.Sample();
        File.WriteAllBytes(Path.Combine(previous.RunDirectory, "allocations-1.partial.nettrace"), [1, 2, 3]);
        using var current = Create();
        current.Archive();
        using var bundle = ZipFile.OpenRead(current.BundlePath);
        Assert.Contains(bundle.Entries, e => e.FullName == "previous-run/allocations-1.partial.nettrace");
        Assert.Contains(bundle.Entries, e => e.FullName == "previous-run/samples.jsonl");
        Assert.Contains(bundle.Entries, e => e.FullName == "previous-run/manifest.json");
    }

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }
}
