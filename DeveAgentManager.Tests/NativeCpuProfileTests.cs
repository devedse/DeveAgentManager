using System.IO.Compression;
using System.Reflection.Emit;
using System.Text.Json;
using DeveAgentManager.Core.Diagnostics;
using DeveAgentManager.Diagnostics;
using DeveAgentManager.Diagnostics.Profiles;
using Google.Protobuf;
using Microsoft.Diagnostics.Tracing;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class NativeCpuProfileTests
{
    [Fact]
    public async Task RuntimeContextIdentifiesCompiledMethodsWithoutSamplingWaitingThreads()
    {
        var folder = Directory.CreateTempSubdirectory("dam-jit-context-").FullName;
        try
        {
            await using (var events = await RuntimeContextTrace.StartAsync(folder, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken))
            {
                Assert.True(events.Available);
                var method = new DynamicMethod("CpuDiagnosticJitProbe", typeof(int), Type.EmptyTypes);
                var il = method.GetILGenerator(); il.Emit(OpCodes.Ldc_I4, 42); il.Emit(OpCodes.Ret);
                var compiled = (Func<int>)method.CreateDelegate(typeof(Func<int>));
                Assert.Equal(42, compiled());
            }
            using var source = new EventPipeEventSource(Path.Combine(folder, "runtime.nettrace"));
            var seen = false; var samples = 0;
            source.Clr.MethodJittingStarted += e => { if (e.MethodName == "CpuDiagnosticJitProbe") seen = true; };
            source.Dynamic.All += e => { if (e.ProviderName == "Microsoft-DotNETCore-SampleProfiler") samples++; };
            source.Process();
            Assert.True(seen, "The name of the method being JIT-compiled must be captured.");
            Assert.Equal(0, samples);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task CpuWeightsRemainDistinctFromWallTimeAndInclusiveFramesAreNotDoubleCounted()
    {
        var folder = Directory.CreateTempSubdirectory("dam-pprof-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "profiles"));
            var profile = new Profile();
            profile.StringTable.Add(["", "cpu", "nanoseconds", "wall", "Hot\"Method", "Parent"]);
            profile.SampleType.Add(new DeveAgentManager.Diagnostics.Profiles.ValueType { Type = 1, Unit = 2 });
            profile.SampleType.Add(new DeveAgentManager.Diagnostics.Profiles.ValueType { Type = 3, Unit = 2 });
            profile.Function.Add(new Function { Id = 1, Name = 4 });
            profile.Function.Add(new Function { Id = 2, Name = 5 });
            profile.Location.Add(new Location { Id = 1, Line = { new Line { FunctionId = 1 } } });
            profile.Location.Add(new Location { Id = 2, Line = { new Line { FunctionId = 2 } } });
            profile.Location.Add(new Location { Id = 3, Address = 123 });
            profile.Sample.Add(new Sample { LocationId = { 1, 2, 2 }, Value = { 2_000_000, 9_000_000_000 } });
            profile.Sample.Add(new Sample { LocationId = { 3 }, Value = { 1_000_000, 0 } });
            await File.WriteAllBytesAsync(Path.Combine(folder, "profiles", "cpu-1.pprof"), profile.ToByteArray(), TestContext.Current.CancellationToken);
            await NativeCpuProfiler.WriteSummaryAsync(folder, TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "cpu-methods.json"), TestContext.Current.CancellationToken));
            Assert.Equal(3_000_000, json.RootElement.GetProperty("sampledCpuNanoseconds").GetInt64());
            Assert.Equal(1_000_000, json.RootElement.GetProperty("unresolvedSelfNanoseconds").GetInt64());
            var parent = json.RootElement.GetProperty("methods").EnumerateArray().Single(x => x.GetProperty("method").GetString() == "Parent");
            Assert.Equal(2_000_000, parent.GetProperty("inclusiveNanoseconds").GetInt64());
            Assert.Equal(0, parent.GetProperty("selfNanoseconds").GetInt64());
            Assert.Contains("\"Hot\"\"Method\"", await File.ReadAllTextAsync(Path.Combine(folder, "cpu-methods.csv"), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task EmptyProfilesAreNotPresentedAsACompletedCpuRanking()
    {
        var folder = Directory.CreateTempSubdirectory("dam-empty-profile-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "profiles"));
            await Assert.ThrowsAsync<IOException>(() => NativeCpuProfiler.WriteSummaryAsync(folder, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void CompressedProfilesHaveAnExpansionLimit()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true)) gzip.Write(new byte[17 * 1024 * 1024]);
        Assert.Throws<IOException>(() => NativeCpuProfiler.ReadProfile(bytes.ToArray()));
    }

    [Fact]
    public async Task OperationCorrelationsDoNotExportArbitraryActivityTagsOrBaggage()
    {
        var folder = Directory.CreateTempSubdirectory("dam-op-privacy-").FullName;
        try
        {
            var path = Path.Combine(folder, "operations.jsonl");
            await using (var writer = new CaptureOperationWriter(path))
            {
                using var activity = CaptureOperations.Start("history.hash", session: Guid.NewGuid(), bytes: 123);
                Assert.NotNull(activity);
                activity.SetTag("sql", "test-only-secret-query");
                activity.SetBaggage("secret", "test-only-private-baggage");
            }
            var data = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("history.hash", data);
            Assert.Contains("123", data);
            Assert.DoesNotContain("test-only-secret-query", data);
            Assert.DoesNotContain("test-only-private-baggage", data);
            Assert.Contains("collector.summary", data);
        }
        finally { Directory.Delete(folder, true); }
    }
}
