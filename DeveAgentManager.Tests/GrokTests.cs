using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Harnesses.Grok;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class GrokTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public const string Id = "0199f000-0000-7000-8000-000000000001";
    // Recorded ACP envelope shape from the official CLI; all content and identifiers are test-only.
    public const string History = """
        {"timestamp":1792368000,"method":"session/update","params":{"sessionId":"0199f000-0000-7000-8000-000000000001","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"First message"}}}}
        {"timestamp":1792368001,"method":"session/update","params":{"sessionId":"0199f000-0000-7000-8000-000000000001","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello "}}}}
        {"timestamp":1792368002,"method":"session/update","params":{"sessionId":"0199f000-0000-7000-8000-000000000001","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"world 🌱"}}}}
        {"timestamp":1792368003,"method":"session/update","params":{"sessionId":"0199f000-0000-7000-8000-000000000001","update":{"sessionUpdate":"tool_call","toolCallId":"test-call","title":"run_terminal_command","rawInput":{"command":"printf test-only"}}}}
        {"timestamp":1792368004,"method":"session/update","params":{"sessionId":"0199f000-0000-7000-8000-000000000001","update":{"sessionUpdate":"tool_call_update","toolCallId":"test-call","status":"completed","content":[{"type":"content","content":{"type":"text","text":"test-only result"}}]}}}
        {"timestamp":1792368005,"method":"_x.ai/session/update","params":{"sessionId":"0199f000-0000-7000-8000-000000000001","update":{"sessionUpdate":"turn_completed","stop_reason":"end_turn","futureField":"preserve exactly"}}}
        """;

    [Fact]
    public void StreamsDisplayAsMessagesAndPreserveEveryNativeRecord()
    {
        var format = new GrokSessionFormat(); var session = format.Read(History);
        Assert.Equal("Hello world 🌱", session.Entries.Single(e => e.Role == DeveRole.Assistant && e.Content?.FirstOrDefault()?.Type == "text").PlainText());
        Assert.Equal(6, session.Entries.Count);
        Assert.Equal(4, session.Entries.Count(e => e.Kind == DeveEntryKind.Message));
        var output = JsonLines.Parse(new StringReader(format.Write(session, Id, "/workspace"))).ToList();
        Assert.Equal(JsonLines.Parse(new StringReader(History)).Select(j => j.ToJsonString()), output.Select(j => j.ToJsonString()));
        Assert.Equal(ToolKinds.Shell, session.Entries.SelectMany(e => e.Content ?? []).Single(b => b.Type == "toolCall").ToolKind);
    }

    [Fact]
    public void StreamingToolArgumentsAreBackfilledWithoutInventingResults()
    {
        var format = new GrokSessionFormat();
        var lines = JsonLines.Parse(new StringReader(History)).ToList();
        var args = lines[3]["params"]!["update"]!["rawInput"]!.DeepClone();
        lines[3]["params"]!["update"]!.AsObject().Remove("rawInput");
        lines[4]["params"]!["update"]!["rawInput"] = args;
        var session = format.Read(string.Join('\n', lines.Select(l => l.ToJsonString())));
        Assert.Equal("printf test-only", session.Entries.SelectMany(e => e.Content ?? []).Single(b => b.Type == "toolCall").Arguments!["command"]!.GetValue<string>());
        Assert.Single(session.Entries.SelectMany(e => e.Content ?? []), b => b.Type == "toolResult");
    }

    [Fact]
    public void BundleRetainsCompactionCheckpointsAndRejectsMissingOrUnsafeFiles()
    {
        var format = new GrokSessionFormat();
        var checkpoint = "compaction_checkpoints/" + Id + ".json";
        var history = History + "\n" + Checkpoint(checkpoint);
        var session = format.Read(history);
        Assert.Throws<IOException>(() => format.WriteCompanionFiles(session, Id, "/workspace"));
        var files = new Dictionary<string, string> { [checkpoint] = "{\"testOnly\":\"context after compaction 🌱\"}",
            ["summary.json"] = "{\"info\":{\"id\":\"old\",\"cwd\":\"/workspace\"},\"generated_title\":\"Test conversation\"}", ["chat_history.jsonl"] = "{\"type\":\"user\",\"content\":[]}" };
        format.ApplyCompanionFiles(session, files);
        var bundle = format.Export(session, Id, "/new workspace");
        var actual = format.Read(bundle);
        Assert.Equal("Test conversation", actual.Title);
        var restored = format.WriteCompanionFiles(actual, Id, "/new workspace");
        Assert.Equal(files[checkpoint], restored[checkpoint]);
        Assert.Equal("/new workspace", (string?)JsonNode.Parse(restored["summary.json"])?["info"]?["cwd"]);
        Assert.Throws<FormatException>(() => format.ApplyCompanionFiles(session, new Dictionary<string, string> { ["../auth.json"] = "test-only" }));
        Assert.Throws<FormatException>(() => format.CompanionFiles(format.Read(Checkpoint("../../auth.json"))));
    }

    [Fact]
    public async Task MissingReplayCheckpointKeepsThePreviousCommittedLog()
    {
        var root = Directory.CreateTempSubdirectory("dam-grok-checkpoint-");
        try
        {
            var store = new NativeHistoryStore(root.FullName); var format = new GrokSessionFormat(); var id = Guid.NewGuid();
            Task<(DeveSession, bool)> Sync(string text, bool fail)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                return store.SyncAsync(id, format, "/history.jsonl", bytes.Length, Guid.NewGuid().ToString(),
                    async (offset, _, output, ct) => { await output.WriteAsync(bytes.AsMemory((int)offset), ct); return new(true); }, Ct,
                    beforeCommit: (_, _) => fail ? Task.FromException<string?>(new IOException("test-only missing checkpoint")) : Task.FromResult<string?>(null));
            }
            await Sync(History + "\n", false);
            await Assert.ThrowsAsync<IOException>(() => Sync(History + "\n" + Checkpoint("compaction_checkpoints/" + Id + ".json") + "\n", true));
            var actual = await store.LoadAsync(id, new Dictionary<string, ISessionFormat> { ["grok"] = format }, Ct);
            Assert.Equal(format.Read(History).ToJson(), actual!.ToJson());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UnchangedEventLogStillCommitsAChangedCompanionCheckpointWithoutReadingHistory()
    {
        var root = Directory.CreateTempSubdirectory("dam-grok-idle-companion-");
        try
        {
            var store = new NativeHistoryStore(root.FullName); var format = new GrokSessionFormat(); var id = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes(History + "\n");
            var generation = "first"; var reads = 0; var checks = 0;
            Task<(DeveSession Metadata, bool Changed)> Sync() => store.SyncAsync(id, format, "/history.jsonl", bytes.Length, "stable",
                async (offset, _, output, token) => { reads++; await output.WriteAsync(bytes.AsMemory((int)offset), token); return new(true); }, Ct,
                beforeCommit: (_, _) => { checks++; return Task.FromResult<string?>(generation); });
            await Sync(); generation = "second";
            Assert.True((await Sync()).Changed);
            Assert.False((await Sync()).Changed);
            Assert.Equal(1, reads); Assert.Equal(3, checks);
            Assert.Equal("second", (await store.SummaryAsync(id, Ct))!.CompanionManifest);
        }
        finally { root.Delete(true); }
    }

    [Fact(Timeout = 60_000)]
    public async Task ReplayCompanionsTransferOverSshAndCanBeReadWithoutTheRunner()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var root = Directory.CreateTempSubdirectory("dam-grok-companions-");
        try
        {
            var format = new GrokSessionFormat(); var store = new SessionCompanionStore(root.FullName); var id = Guid.NewGuid();
            var checkpoint = "compaction_checkpoints/" + Id + ".json";
            Directory.CreateDirectory(fixture.Root + "/compaction_checkpoints");
            var original = "{\"testOnly\":\"" + new string('x', 100_000) + "\"}";
            await File.WriteAllTextAsync(fixture.Root + "/" + checkpoint, original, Ct);
            var cache = "{\"type\":\"user\",\"testOnly\":\"" + new string('x', 1_000_000) + "\"}\n";
            await File.WriteAllTextAsync(fixture.Root + "/chat_history.jsonl", cache, Ct);
            var metadata = format.Read(History + "\n" + Checkpoint(checkpoint));
            var policy = new NativeHistorySyncPolicy(HistorySyncMode.Sampled);
            var firstGeneration = await store.SyncAsync(fixture.Shell, fixture.Runner, id, format, fixture.Root + "/updates.jsonl", metadata, Ct, policy);
            var size = store.Size(id);
            await store.SyncAsync(fixture.Shell, fixture.Runner, id, format, fixture.Root + "/updates.jsonl", metadata, Ct, policy);
            Assert.Equal(size, store.Size(id));
            const string suffix = "{\"testOnly\":\"new message\"}\n";
            await File.AppendAllTextAsync(fixture.Root + "/chat_history.jsonl", suffix, Ct);
            var appended = await store.SyncAsync(fixture.Shell, fixture.Runner, id, format, fixture.Root + "/updates.jsonl", metadata, Ct, policy);
            Assert.True(await store.HasPendingVerificationAsync(id, appended, Ct)); // The append used bounded samples, not a full-file read.
            await store.ApplyAsync(id, format, metadata, Ct, firstGeneration);
            Assert.Equal(cache, format.WriteCompanionFiles(metadata, Id, "/workspace")["chat_history.jsonl"]);
            var verified = await store.SyncAsync(fixture.Shell, fixture.Runner, id, format, fixture.Root + "/updates.jsonl", metadata, Ct, policy with { Force = true });
            Assert.False(await store.HasPendingVerificationAsync(id, verified, Ct));
            await store.ApplyAsync(id, format, metadata, Ct, verified);
            Assert.Equal(cache + suffix, format.WriteCompanionFiles(metadata, Id, "/workspace")["chat_history.jsonl"]);
            await store.PruneAsync(id, verified, Ct);
            File.Delete(fixture.Root + "/" + checkpoint);
            await new SessionCompanionStore(root.FullName).ApplyAsync(id, format, metadata, Ct, verified);
            Assert.Equal(original, format.WriteCompanionFiles(metadata, Id, "/workspace")[checkpoint]);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("grok-idle", AgentActivityStatus.Ready)]
    [InlineData("grok-working", AgentActivityStatus.Working)]
    public void CapturedOfficialCliControlsEstablishLiveStatus(string name, AgentActivityStatus expected)
    {
        var screen = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", name + ".txt"));
        Assert.Equal(expected, AgentScreenDetector.Detect("grok", screen, DateTime.UtcNow).Status);
    }

    [Fact]
    public void AQuotedSpinnerDoesNotMakeAnIdleComposerBusy()
    {
        var screen = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", "grok-idle.txt"));
        screen = screen.Replace("   Run /doctor", "    ⠸ Waiting for response… 0.8s 0.8s ⇣33 [stop]\n   Run /doctor");
        Assert.Equal(AgentActivityStatus.Ready, AgentScreenDetector.Detect("grok", screen, DateTime.UtcNow).Status);
    }

    [Fact]
    public void RewindPreservesNativeHistoryAndSelectsOnlyTheCurrentBranch()
    {
        var lines = JsonLines.Parse(new StringReader(History)).ToList();
        var first = (JsonObject)lines[0].DeepClone(); first["params"]!["update"]!["content"]!["text"] = "Abandoned branch";
        var rewind = new JsonObject { ["method"] = "_x.ai/session/update", ["params"] = new JsonObject { ["sessionId"] = Id,
            ["update"] = new JsonObject { ["sessionUpdate"] = "rewind_marker", ["target_prompt_index"] = 1 } } };
        var current = (JsonObject)first.DeepClone(); current["params"]!["update"]!["content"]!["text"] = "Current branch";
        var input = History + "\n" + first.ToJsonString() + "\n" + lines[1].ToJsonString() + "\n" + rewind.ToJsonString() + "\n" + current.ToJsonString();
        var format = new GrokSessionFormat(); var full = format.Read(input);
        Assert.Contains(full.Entries, e => e.PlainText() == "Abandoned branch");
        Assert.DoesNotContain(full.ActivePath(), e => e.PlainText() == "Abandoned branch");
        Assert.Contains(full.ActivePath(), e => e.PlainText() == "Current branch");
        var cursor = new SessionReadCursor { MetadataOnly = true };
        foreach (var line in JsonLines.Parse(new StringReader(input)))
        {
            var start = cursor.Session.Entries.Count; format.AppendLines(cursor, [line]); cursor.Compact(start);
        }
        Assert.Equal(full.ActivePath().Where(e => e.Kind != DeveEntryKind.Native).Select(e => e.Id), cursor.Session.ActivePath().Select(e => e.Id));
        Assert.DoesNotContain(cursor.Session.Entries, e => e.Kind == DeveEntryKind.Native);
        Assert.Equal(input.Split('\n').Length, JsonLines.Parse(new StringReader(format.Write(full, Id, "/workspace"))).Count());
    }

    [Fact]
    public async Task RenewalRotatesOnlyInManagerAndNeverExposesRefreshTokenToRunner()
    {
        var secret = new JsonObject { ["https://auth.x.ai::test-client"] = new JsonObject {
            ["key"] = "test-only-old-access", ["refresh_token"] = "test-only-refresh", ["auth_mode"] = "oidc",
            ["create_time"] = "2026-01-01T00:00:00Z", ["expires_at"] = "2026-01-01T01:00:00Z",
            ["oidc_issuer"] = "https://auth.x.ai", ["oidc_client_id"] = "test-client", ["user_id"] = "test-user" } }.ToJsonString();
        var handler = new Tokens(); var harness = new GrokHarness(handler);
        var renewed = await harness.RenewAccountAsync(secret, Ct);
        Assert.NotNull(renewed); Assert.Contains("test-only-new-refresh", renewed);
        Assert.DoesNotContain("refresh_token", harness.RunnerAccountCopy(renewed));
        Assert.Null(await harness.RenewAccountAsync(renewed, Ct));
        Assert.Equal(2, handler.Requests);
        var subscription = harness.BuildAccountSetup("$HOME/test-account", AccountKind.Subscription, renewed);
        Assert.DoesNotContain("test-only-new-refresh", subscription.Files.Single().Content);
        Assert.Contains("unset GROK_AUTH XAI_API_KEY", subscription.Environment);
        var key = harness.BuildAccountSetup("$HOME/test-account", AccountKind.ApiKey, "test-only-key");
        Assert.Contains("export XAI_API_KEY=test-only-key", key.Environment);
        Assert.Equal("{}", key.Files.Single().Content);
    }

    [Fact]
    public void PathsAndModelCatalogMatchTheOfficialCli()
    {
        Assert.Equal("%2Fworkspace%2Fspace%20%F0%9F%8C%B1", GrokSessionFormat.SessionDirectoryName("/workspace/space 🌱"));
        Assert.True(GrokSessionFormat.SessionDirectoryName("/workspace/" + new string('x', 300)).Length < 60);
        var choices = new GrokHarness().ParseModels("Default model: test-model\nAvailable models:\n  * test-model (default)\n  - second-model\n");
        Assert.Equal(new[] { "test-model", "second-model" }, choices.Select(m => m.Id));
        Assert.True(Guid.TryParse(new GrokHarness().NewSessionId(Guid.NewGuid()), out _));
    }

    private static string Checkpoint(string path) => new JsonObject { ["timestamp"] = 1792368010, ["method"] = "_x.ai/session/update",
        ["params"] = new JsonObject { ["sessionId"] = Id, ["update"] = new JsonObject { ["sessionUpdate"] = "compaction_checkpoint", ["checkpoint_file"] = path } } }.ToJsonString();

    private sealed class Tokens : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal("auth.x.ai", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"token_endpoint\":\"https://auth.x.ai/token\"}") };
            Assert.Contains("grant_type=refresh_token", await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test-only-new-access\",\"refresh_token\":\"test-only-new-refresh\",\"expires_in\":3600}") };
        }
    }
}
