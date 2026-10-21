using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Sessions;
using Xunit;

namespace DeveAgentManager.Tests;

public class SampledHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly NativeHistorySyncPolicy Sampled = new(HistorySyncMode.Sampled);
    private const string Extra = "{\"type\":\"queue-operation\",\"id\":\"extra\",\"timestamp\":\"2026-10-18T12:00:00Z\"}\n";

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("pi")]
    public async Task AppendsReadBoundedSamplesWithoutHashingAndPreserveFullNativeHistory(string harness)
    {
        using var rig = new Rig(harness);
        await rig.Sync();
        rig.Text += Extra;
        var hashes = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DeveAgentManager.Core.Diagnostics.CaptureOperations.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => { if (a.GetTagItem("session")?.ToString() == rig.Id.ToString("N") && a.OperationName.Contains("hash")) hashes.Add(a.OperationName); }
        };
        ActivitySource.AddActivityListener(listener);
        var result = await rig.Sync();
        Assert.Equal(0, rig.FullCalls);
        Assert.Equal(4096 + 65536, rig.SampleBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(Extra), rig.Transferred);
        Assert.Empty(hashes);
        Assert.Equal(rig.Text, await rig.Raw());
        Assert.Equal("", (await rig.State()).Sha256);
        var format = NativeHistoryStoreTests.Format(harness);
        var expected = format.Read(rig.Text);
        Assert.Equal(expected.ActivePath().Select(e => e.Id), result.Metadata.ActivePath().Select(e => e.Id));
        Assert.Equal(SessionService.ReplyPreview(expected), SessionService.ReplyPreview(result.Metadata));
        var loaded = await rig.Store.LoadAsync(rig.Id, new Dictionary<string, DeveAgentManager.Core.Abstractions.ISessionFormat> { [harness] = format }, Ct);
        Assert.Equal(expected.ToJson(), loaded!.ToJson());
        await rig.Sync(new(HistorySyncMode.Sampled, Force: true));
        Assert.Equal(1, rig.FullCalls);
        Assert.Equal(0, rig.Transferred);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rig.Text))), (await rig.State()).Sha256);
    }

    [Theory]
    [InlineData("force")]
    [InlineData("idle")]
    [InlineData("interval")]
    [InlineData("restart")]
    [InlineData("rollback")]
    [InlineData("shadow")]
    public async Task MiddleEditsMissedBySamplesAreRebuiltByTheNextRequiredAudit(string trigger)
    {
        using var rig = new Rig("claude");
        await rig.Sync();
        var previous = rig.Text;
        rig.Text = rig.Text[..90000] + 'B' + rig.Text[90001..] + Extra;
        await rig.Sync();
        Assert.Equal(0, rig.FullCalls);
        Assert.Equal(previous + Extra, await rig.Raw()); // Precisely the accepted delayed-detection tradeoff.
        var policy = Sampled;
        if (trigger == "force") policy = policy with { Force = true };
        if (trigger == "idle") policy = policy with { Idle = true };
        if (trigger == "interval") rig.Clock.Advance(NativeHistoryStore.AuditInterval);
        if (trigger == "restart") rig.Restart();
        if (trigger == "rollback") policy = new(HistorySyncMode.Full);
        if (trigger == "shadow") policy = new(HistorySyncMode.Shadow);
        await rig.Sync(policy);
        Assert.Equal(2, rig.FullCalls); // Mismatch followed by full snapshot rebuild.
        Assert.Equal(rig.Text, await rig.Raw());
        Assert.NotEmpty((await rig.State()).Sha256);
        await rig.Sync(policy with { Force = false });
        Assert.Equal(0, rig.FullCalls); // An unchanged, audited idle session stays cheap.
    }

    [Theory]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("identity")]
    [InlineData("same-size")]
    [InlineData("truncate")]
    [InlineData("source-changing")]
    public async Task SuspiciousChangesNeverUseTheSampledAppendPath(string change)
    {
        using var rig = new Rig("claude");
        await rig.Sync();
        rig.Text = change switch
        {
            "head" or "same-size" => rig.Text.Replace("hello there", "HELLO THERE", StringComparison.Ordinal),
            "tail" => rig.Text[..^20] + rig.Text[^20..].Replace('A', 'B'),
            "truncate" => NativeHistoryStoreTests.Sample("claude"),
            _ => rig.Text
        };
        if (change is not ("same-size" or "truncate")) rig.Text += Extra;
        if (change == "identity") rig.Identity = "10:999";
        rig.SampleMatches = change != "source-changing";
        await rig.Sync();
        Assert.True(rig.FullCalls > 0);
        Assert.Equal(rig.Text, await rig.Raw());
        Assert.NotEmpty((await rig.State()).Sha256);
    }

    [Fact]
    public async Task AuditsReadTheCommittedPrefixOnce_AndHashTheAssembledFileEvenAfterAnUncommittedSuffix()
    {
        using var rig = new Rig("claude");
        await rig.Sync(new(HistorySyncMode.Full));
        var committed = Encoding.UTF8.GetByteCount(rig.Text);
        // A failed earlier sync can leave bytes past the committed length; the next audit truncates them.
        var state = await rig.State();
        await File.AppendAllTextAsync(Path.Combine(rig.Folder, "native", rig.Id.ToString("N"), state.File), "test-only uncommitted suffix", Ct);
        rig.Text += Extra;
        var hashed = new Dictionary<string, long>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DeveAgentManager.Core.Diagnostics.CaptureOperations.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { if (a.GetTagItem("session")?.ToString() == rig.Id.ToString("N") && a.OperationName.StartsWith("history.hash")) hashed[a.OperationName] = (long)a.GetTagItem("bytes")!; }
        };
        ActivitySource.AddActivityListener(listener);
        await rig.Sync(new(HistorySyncMode.Full)); // The store throws if its hash differs from the Runner's snapshot.
        Assert.Equal(committed, hashed["history.hash-prefix"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(Extra), hashed["history.hash"]); // Only the appended bytes are read again.
        Assert.Equal(rig.Text, await rig.Raw());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rig.Text))), (await rig.State()).Sha256);
        var raw = await File.ReadAllBytesAsync(Path.Combine(rig.Folder, "native", rig.Id.ToString("N"), (await rig.State()).File), Ct);
        Assert.Equal(Encoding.UTF8.GetByteCount(rig.Text), raw.Length);
    }

    [Fact]
    public async Task WholeCopiesAreLoggedWithWhy_WhenTheRunnersHistoryNoLongerMatchesOrShrank()
    {
        var logger = new CapturedLogger<NativeHistoryStore>();
        using var rig = new Rig("claude") { Logger = logger };
        await rig.Sync(new(HistorySyncMode.Full));
        Assert.Empty(logger.Entries); // A small first copy is routine.

        // An edit before the saved part: the new part can't simply be appended.
        rig.Text = rig.Text.Replace("hello there", "HELLO THERE", StringComparison.Ordinal) + Extra;
        await rig.Sync(new(HistorySyncMode.Full));
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("no longer matches the saved copy before") && e.Message.Contains(rig.Id.ToString()));

        // Shorter than what was saved: replaced or shortened on the Runner.
        rig.Text = NativeHistoryStoreTests.Sample("claude");
        await rig.Sync(new(HistorySyncMode.Full));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Copying the whole history of session") && e.Message.Contains("so it was replaced or shortened"));
        Assert.Equal(rig.Text, await rig.Raw());
    }

    [Fact]
    public async Task ShadowAndFullModesNeverPublishUnverifiedAppendsAndFailuresKeepTheCheckpoint()
    {
        using var rig = new Rig("claude");
        await rig.Sync();
        rig.Text = rig.Text[..90000] + 'B' + rig.Text[90001..] + Extra;
        await rig.Sync(new(HistorySyncMode.Shadow));
        Assert.Equal(69632, rig.SampleBytes);
        Assert.Equal(2, rig.FullCalls);
        Assert.Equal(rig.Text, await rig.Raw());
        rig.Text += Extra;
        await rig.Sync(new(HistorySyncMode.Full));
        Assert.Equal(0, rig.SampleBytes);
        Assert.NotEmpty((await rig.State()).Sha256);
        var checkpoint = await rig.Raw();
        rig.Text += Extra;
        rig.Interrupt = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Sync());
        Assert.Equal(checkpoint, await rig.Raw());
        rig.Interrupt = false;
        await rig.Sync();
        Assert.Equal(rig.Text, await rig.Raw());
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Rig(string harness) : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("dam-sampled-history-").FullName;
        public Guid Id { get; } = Guid.NewGuid();
        public TestClock Clock { get; } = new();
        public Microsoft.Extensions.Logging.ILogger? Logger;
        public NativeHistoryStore Store => store ??= new(Folder, Clock, Logger);
        private NativeHistoryStore? store;
        public string Text = NativeHistoryStoreTests.Sample(harness) + "\n{\"type\":\"padding\",\"id\":\"padding\",\"timestamp\":\"2026-10-18T12:00:00Z\",\"payload\":\"" + new string('A', 192 * 1024) + "\"}\n";
        public string Identity = "10:100";
        private int revision;
        private string? previousText, previousIdentity;
        public int FullCalls, SampleBytes;
        public long Transferred;
        public bool SampleMatches = true, Interrupt;
        public void Restart() => store = null;
        public async Task<NativeHistoryStore.State> State() => (await Store.SummaryAsync(Id, Ct))!;
        public async Task<string> Raw()
        {
            var state = await State();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Folder, "native", Id.ToString("N"), state.File), Ct);
            return Encoding.UTF8.GetString(bytes.AsSpan(0, (int)state.Bytes));
        }
        public Task<(DeveSession Metadata, bool Changed)> Sync(NativeHistorySyncPolicy? policy = null)
        {
            var bytes = Encoding.UTF8.GetBytes(Text);
            if (Text != previousText || Identity != previousIdentity) revision++;
            previousText = Text; previousIdentity = Identity;
            FullCalls = SampleBytes = 0; Transferred = 0;
            return Store.SyncAsync(Id, NativeHistoryStoreTests.Format(harness), "/history.jsonl", bytes.Length, $"{Identity}:revision-{revision}",
                async (offset, hash, output, ct) =>
                {
                    FullCalls++;
                    if (hash is not null && hash != Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, (int)offset)))) return new(false);
                    await output.WriteAsync(bytes.AsMemory((int)offset), ct);
                    Transferred += bytes.Length - offset;
                    return new(true, Convert.ToHexString(SHA256.HashData(bytes)));
                }, Ct, policy ?? Sampled, async (offset, samples, output, ct) =>
                {
                    SampleBytes = samples.Sum(s => s.Bytes.Length);
                    if (!SampleMatches || samples.Any(s => !bytes.AsSpan((int)s.Offset, s.Bytes.Length).SequenceEqual(s.Bytes))) return false;
                    await output.WriteAsync(bytes.AsMemory((int)offset), ct);
                    if (Interrupt) throw new IOException("test-only interrupted append");
                    Transferred += bytes.Length - offset;
                    return true;
                });
        }
        public void Dispose() => Directory.Delete(Folder, true);
    }
}
