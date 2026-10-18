using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using Xunit;

namespace DeveAgentManager.Tests;

public class NativeHistoryStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static ISessionFormat Format(string harness) => harness switch { "claude" => new ClaudeSessionFormat(), "codex" => new CodexSessionFormat(), _ => new PiSessionFormat() };
    internal static string Sample(string harness) => harness switch { "claude" => HarnessFormatTests.ClaudeSession, "codex" => HarnessFormatTests.CodexSession, _ => SessionTests.PiSession };

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("pi")]
    public async Task EveryAppendAndRestartPreservesFullNativeRecordsAndMetadata(string harness)
    {
        using var rig = new Rig(harness);
        var full = "";
        foreach (var line in Sample(harness).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var oldBytes = Encoding.UTF8.GetByteCount(full);
            full += line + "\n";
            var metadata = await rig.Sync(full);
            Assert.Equal(Encoding.UTF8.GetByteCount(full) - oldBytes, rig.Transferred);
            var expected = rig.Format.Read(full);
            await rig.AssertFull(expected);
            AssertMetadata(expected, metadata);
            var summary = await rig.Store.SummaryAsync(rig.Id, Ct);
            Assert.Equal(SessionService.ReplyPreview(expected), summary!.ReplyPreview);
            rig.Restart();
        }
        Assert.True(await rig.Store.IsCurrentAsync(rig.Id, harness, "/history.jsonl", Encoding.UTF8.GetByteCount(full), rig.Stamp, Ct));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("pi")]
    public async Task RewritesTruncationAndPathChangesRebuildWithoutCombiningUnrelatedHistories(string harness)
    {
        using var rig = new Rig(harness);
        var original = Sample(harness) + "\n";
        await rig.Sync(original);
        // Same-length edit in the old prefix, not just in the tail.
        var changed = original.Replace("message", "messagE", StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        await rig.Sync(changed);
        Assert.Equal(Encoding.UTF8.GetByteCount(changed), rig.Transferred);
        await rig.AssertFull(rig.Format.Read(changed));
        var truncated = changed[..(changed.IndexOf('\n') + 1)];
        await rig.Sync(truncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(truncated), rig.Transferred);
        await rig.AssertFull(rig.Format.Read(truncated));
        await rig.Sync(original, "/replacement.jsonl");
        Assert.Equal(Encoding.UTF8.GetByteCount(original), rig.Transferred);
        await rig.AssertFull(rig.Format.Read(original));
        Assert.Single(Directory.GetFiles(rig.Folder, "*.jsonl", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(rig.Folder, "*.index.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PartialUnicodeLineAndValidUnterminatedLineAreReparsedWhenCompleted()
    {
        using var rig = new Rig("claude");
        var initial = """{"type":"user","uuid":"u","sessionId":"s","message":{"content":"hi"}}""";
        await rig.Sync(initial);
        await rig.AssertFull(rig.Format.Read(initial));
        var partial = initial + "\n{\"type\":\"assistant\",\"uuid\":\"a\",\"parentUuid\":\"u\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"こんにちは";
        await rig.Sync(partial);
        var complete = partial + " 🌱\"}]}}\n";
        var metadata = await rig.Sync(complete);
        await rig.AssertFull(rig.Format.Read(complete));
        AssertMetadata(rig.Format.Read(complete), metadata);
    }

    [Fact]
    public async Task InterruptedTransferAndUncommittedSuffixNeverReplaceLastGoodHistory()
    {
        using var rig = new Rig("claude");
        var initial = Sample("claude") + "\n";
        await rig.Sync(initial);
        var expected = rig.Format.Read(initial);
        await Assert.ThrowsAsync<IOException>(() => rig.Store.SyncAsync(rig.Id, rig.Format, "/history.jsonl", 99999, "next",
            async (_, _, stream, ct) => { await stream.WriteAsync("partial"u8.ToArray(), ct); throw new IOException("test-only interrupted transport"); }, Ct));
        rig.Restart();
        await rig.AssertFull(expected);
        // Model a crash after appending bytes but before publishing the checkpoint.
        var native = Directory.GetFiles(rig.Folder, "*.jsonl", SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(native, "\n{\"type\":\"user\",\"uuid\":\"uncommitted\"}\n", Ct);
        await rig.AssertFull(expected);
        var next = initial + "{\"type\":\"user\",\"uuid\":\"next\",\"message\":{\"content\":\"next message\"}}\n";
        await rig.Sync(next);
        await rig.AssertFull(rig.Format.Read(next));
    }

    [Fact]
    public async Task IndexDoesNotRetainImageToolOrNativePayloadsAndSummaryDoesNotLoadIndex()
    {
        using var rig = new Rig("claude");
        var payload = new string('A', 2 * 1024 * 1024);
        var content = "{\"type\":\"user\",\"uuid\":\"u\",\"sessionId\":\"s\",\"message\":{\"content\":[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + payload + "\"}},{\"type\":\"text\",\"text\":\"image prompt\"}]}}\n";
        await rig.Sync(content);
        var index = Directory.GetFiles(rig.Folder, "*.index.json", SearchOption.AllDirectories).Single();
        Assert.True(new FileInfo(index).Length < 8192);
        await rig.AssertFull(rig.Format.Read(content));
        File.Delete(index); // Summary and full history must not depend on this rebuildable index.
        Assert.NotNull(await rig.Store.SummaryAsync(rig.Id, Ct));
        await rig.AssertFull(rig.Format.Read(content));
    }

    [Fact]
    public async Task MissingIndexRebuildsOnNextAppendAndFailedParsingKeepsPreviousCheckpoint()
    {
        using var rig = new Rig("claude");
        var initial = Sample("claude");
        await rig.Sync(initial);
        File.Delete(Directory.GetFiles(rig.Folder, "*.index.json", SearchOption.AllDirectories).Single());
        var added = "{\"type\":\"assistant\",\"uuid\":\"new\",\"parentUuid\":\"a2\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"new reply\"}]}}\n";
        var combined = initial + added;
        AssertMetadata(rig.Format.Read(combined), await rig.Sync(combined));
        Assert.Equal(Encoding.UTF8.GetByteCount(added), rig.Transferred);
        var pending = combined + "{\"type\":\"queue-operation\",\"content\":\"pending\"}\n";
        var bytes = Encoding.UTF8.GetBytes(pending);
        await Assert.ThrowsAsync<IOException>(() => rig.Store.SyncAsync(rig.Id, new ParseFailure(rig.Format), "/history.jsonl", bytes.Length, "failure",
            async (offset, _, output, ct) => { await output.WriteAsync(bytes.AsMemory((int)offset), ct); return new NativeHistoryTransfer(true); }, Ct));
        rig.Restart();
        await rig.AssertFull(rig.Format.Read(combined));
        await rig.Sync(pending);
        await rig.AssertFull(rig.Format.Read(pending));
    }

    [Fact]
    public async Task Utf8CharacterSplitAcrossTransfersIsRecoveredWithoutDuplicateEntries()
    {
        using var rig = new Rig("claude");
        var initial = Sample("claude");
        var extra = """{"type":"assistant","uuid":"unicode","parentUuid":"a2","timestamp":"2026-10-18T12:00:00Z","message":{"content":[{"type":"text","text":"Hello 🌱"}]}}""" + "\n";
        var all = Encoding.UTF8.GetBytes(initial + extra);
        var cut = all.AsSpan().IndexOf("🌱"u8) + 2;
        async Task SyncBytes(byte[] bytes, string stamp)
        {
            await rig.Store.SyncAsync(rig.Id, rig.Format, "/history.jsonl", bytes.Length, stamp,
                async (offset, hash, output, ct) =>
                {
                    if (hash is not null && Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, (int)offset))) != hash)
                        return new NativeHistoryTransfer(false);
                    await output.WriteAsync(bytes.AsMemory((int)offset), ct);
                    return new NativeHistoryTransfer(true, Convert.ToHexString(SHA256.HashData(bytes)));
                }, Ct);
        }
        await SyncBytes(all[..cut], "partial");
        rig.Restart();
        await rig.AssertFull(rig.Format.Read(initial));
        await SyncBytes(all, "complete");
        await rig.AssertFull(rig.Format.Read(initial + extra));
    }

    [Theory]
    [InlineData("/history.jsonl")]
    [InlineData("/replacement.jsonl")]
    public async Task SnapshotChecksumMismatchCannotPublishMixedVersions(string sourcePath)
    {
        using var rig = new Rig("claude");
        var initial = Sample("claude");
        await rig.Sync(initial);
        var pending = Encoding.UTF8.GetBytes(initial + "{\"type\":\"queue-operation\",\"content\":\"pending\"}\n");
        await Assert.ThrowsAsync<IOException>(() => rig.Store.SyncAsync(rig.Id, rig.Format, sourcePath, pending.Length, "mixed",
            async (offset, _, output, ct) =>
            {
                await output.WriteAsync(pending.AsMemory((int)offset), ct);
                return new NativeHistoryTransfer(true, new string('0', 64));
            }, Ct));
        rig.Restart();
        await rig.AssertFull(rig.Format.Read(initial));
        Assert.Single(Directory.GetFiles(rig.Folder, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SummariesRemainReadableDuringTransferAndBranchPreviewsFollowTheActiveTree()
    {
        using var rig = new Rig("claude");
        var initial = Sample("claude");
        await rig.Sync(initial);
        var branch = """{"type":"assistant","uuid":"branch","parentUuid":"u1","timestamp":"2026-10-18T12:00:00Z","message":{"content":[{"type":"text","text":"A different branch"}]}}""" + "\n"
            + """{"type":"custom-title","customTitle":"Pinned native title"}""" + "\n"
            + """{"type":"summary","summary":"Later automatic title"}""" + "\n";
        var pending = Encoding.UTF8.GetBytes(initial + branch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = rig.Store.SyncAsync(rig.Id, rig.Format, "/history.jsonl", pending.Length, "branch", async (offset, _, output, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct);
            await output.WriteAsync(pending.AsMemory((int)offset), ct); return new NativeHistoryTransfer(true);
        }, Ct);
        await entered.Task.WaitAsync(Ct);
        try
        {
            var summary = await rig.Store.SummaryAsync(rig.Id, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
            Assert.Equal(SessionService.ReplyPreview(rig.Format.Read(initial)), summary!.ReplyPreview);
        }
        finally { release.SetResult(); }
        var result = await sync;
        Assert.Equal("A different branch", SessionService.ReplyPreview(result.Metadata));
        Assert.Equal("Pinned native title", result.Metadata.Title);
        AssertMetadata(rig.Format.Read(initial + branch), result.Metadata);
    }

    private sealed class ParseFailure(ISessionFormat inner) : ISessionFormat
    {
        public string HarnessId => inner.HarnessId;
        public string LocateCommand(string id) => inner.LocateCommand(id);
        public string GetPath(string id, string cwd, DateTime at) => inner.GetPath(id, cwd, at);
        public DeveSession Read(string content) => inner.Read(content);
        public string Write(DeveSession session, string id, string cwd) => inner.Write(session, id, cwd);
        public bool SupportsIncrementalRead => true;
        public void AppendLines(SessionReadCursor cursor, IEnumerable<JsonObject> lines) => throw new IOException("test-only parse failure");
    }

    private static void AssertMetadata(DeveSession expected, DeveSession actual)
    {
        var original = new StoredSession(); var compact = new StoredSession();
        SessionService.Apply(original, expected, null); SessionService.Apply(compact, actual, null);
        Assert.Equal((original.Title, original.MessageCount, original.WorkingDirectory),
            (compact.Title, compact.MessageCount, compact.WorkingDirectory));
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(SessionService.ReplyPreview(expected), SessionService.ReplyPreview(actual));
        var a = new Agent(); var b = new Agent();
        SessionService.UpdateLastAssistantMessage(a, expected); SessionService.UpdateLastAssistantMessage(b, actual);
        Assert.Equal(a.LastAssistantMessageAt, b.LastAssistantMessageAt);
        Assert.Equal(expected.ActivePath().Select(e => e.Id), actual.ActivePath().Select(e => e.Id));
    }

    private sealed class Rig(string harness) : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("dam-native-history-").FullName;
        public Guid Id { get; } = Guid.NewGuid();
        public ISessionFormat Format { get; } = NativeHistoryStoreTests.Format(harness);
        public NativeHistoryStore Store => store ??= new(Folder);
        private NativeHistoryStore? store;
        public long Transferred { get; private set; }
        private int revision;
        public string Stamp => revision.ToString();
        public void Restart() => store = null;
        public async Task<DeveSession> Sync(string text, string path = "/history.jsonl")
        {
            var bytes = Encoding.UTF8.GetBytes(text); revision++; Transferred = 0;
            var result = await Store.SyncAsync(Id, Format, path, bytes.Length, Stamp, async (offset, hash, output, ct) =>
            {
                if (hash is not null && Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, (int)offset))) != hash) return new NativeHistoryTransfer(false);
                await output.WriteAsync(bytes.AsMemory((int)offset), ct);
                Transferred += bytes.Length - offset;
                return new NativeHistoryTransfer(true);
            }, Ct);
            return result.Metadata;
        }
        public async Task AssertFull(DeveSession expected)
        {
            var actual = await Store.LoadAsync(Id, new Dictionary<string, ISessionFormat> { [harness] = Format }, Ct);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.ToJson()), JsonNode.Parse(actual!.ToJson())), "Full history changed.");
            // Same-harness replay and cross-harness conversions see the original payloads.
            var fixedTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (expected.CreatedAt == default) expected.CreatedAt = actual.CreatedAt = fixedTime;
            foreach (var entry in expected.Entries.Where(e => e.Timestamp == default)) entry.Timestamp = fixedTime;
            foreach (var entry in actual.Entries.Where(e => e.Timestamp == default)) entry.Timestamp = fixedTime;
            static string StableIds(string text) => System.Text.RegularExpressions.Regex.Replace(text,
                @"(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|msg_dam_[0-9a-f]{32})", "generated-id");
            foreach (var format in new ISessionFormat[] { new ClaudeSessionFormat(), new CodexSessionFormat(), new PiSessionFormat() })
                Assert.Equal(StableIds(format.Write(expected, "restored", "/workspace")), StableIds(format.Write(actual, "restored", "/workspace")));
        }
        public void Dispose() => Directory.Delete(Folder, true);
    }
}
