using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using DeveAgentManager.Harnesses.Grok;
using Xunit;

namespace DeveAgentManager.Tests;

public class SessionMigrationTests
{
    // Codex can emit empty text alongside other text/images, or as the entire output of a completed call.
    // The JavaScript is historical input only; these regressions never execute it.
    public const string CodexWithEmptyToolText = """
        {"timestamp":"2026-10-26T12:00:00Z","type":"session_meta","payload":{"id":"empty-output-source","cwd":"/workspace","timestamp":"2026-10-26T12:00:00Z"}}
        {"timestamp":"2026-10-26T12:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Inspect the output"}]}}
        {"timestamp":"2026-10-26T12:00:02Z","type":"response_item","payload":{"type":"custom_tool_call","call_id":"mixed","name":"exec","namespace":"functions","input":"text('historical only');"}}
        {"timestamp":"2026-10-26T12:00:02Z","type":"response_item","payload":{"type":"function_call","call_id":"empty","name":"read_file","arguments":"{\"path\":\"/workspace/empty.txt\"}"}}
        {"timestamp":"2026-10-26T12:00:02Z","type":"response_item","payload":{"type":"function_call","call_id":"absent","name":"read_file","arguments":"{\"path\":\"/workspace/absent.txt\"}"}}
        {"timestamp":"2026-10-26T12:00:03Z","type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"mixed","output":[{"type":"input_text","text":""},{"type":"input_text","text":"Original output"},{"type":"input_text","text":""},{"type":"input_image","image_url":"data:image/png;base64,aGlzdG9yaWNhbA=="},{"type":"input_text","text":""},{"type":"input_text","text":""}]}}
        {"timestamp":"2026-10-26T12:00:03Z","type":"response_item","payload":{"type":"function_call_output","call_id":"empty","output":""}}
        {"timestamp":"2026-10-26T12:00:03Z","type":"response_item","payload":{"type":"function_call_output","call_id":"absent","output":[]}}
        {"timestamp":"2026-10-26T12:00:04Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Continue without repeating the tools"}]}}
        """;

    [Theory]
    [InlineData("claude")]
    [InlineData("pi")]
    [InlineData("grok")]
    public void EmptyToolTextSurvivesConversionAndRepeatedSwitches(string target)
    {
        var source = Format("codex").Read(CodexWithEmptyToolText);
        var original = source.ToJson();
        foreach (var to in new[] { target, "codex", "claude" })
        {
            var before = source.ToJson();
            var migrated = SessionMigration.Prepare(source, "source", to, to);
            Assert.Equal(before, source.ToJson());
            var written = Format(to).Write(migrated, "empty-output-target", "/workspace");
            var actual = Format(to).Read(written);
            SessionMigration.ValidateWritten(migrated, actual);
            var results = actual.ActivePath().SelectMany(e => e.Content ?? []).Where(b => b.Type == "toolResult").ToList();
            Assert.Equal(["text", "text", "text", "image", "text", "text"], results[0].Content!.Select(b => b.Type));
            Assert.Equal(["", "Original output", "", null, "", ""], results[0].Content!.Select(b => b.Text));
            Assert.Equal("", Assert.Single(results[1].Content!).Text);
            Assert.Empty(results[2].Content!);
            Assert.Equal(3, migrated.Migration!.PreservedCalls);
            Assert.Equal(0, migrated.Migration.ArchivedExchanges);
            if (to == "claude")
            {
                var lines = JsonLines.Parse(written).ToList();
                var nativeResults = lines.SelectMany(l => (l["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>())
                    .Where(b => b["type"]?.GetValue<string>() == "tool_result").ToList();
                // Model input stays valid; the envelope carries exact empty-block positions for lossless reads.
                Assert.DoesNotContain(nativeResults.SelectMany(r => (r["content"] as JsonArray ?? []).OfType<JsonObject>()),
                    b => b["type"]?.GetValue<string>() == "text" && b["text"]?.GetValue<string>() == "");
                Assert.Empty(nativeResults[1]["content"]!.AsArray());
                Assert.Empty(nativeResults[2]["content"]!.AsArray());
            }
            source = actual;
        }
        Assert.Equal(original, Format("codex").Read(CodexWithEmptyToolText).ToJson());
    }

    [Fact]
    public void EmptyOutputHintsDoNotHideChangedOrLostNonemptyOutput()
    {
        var expected = SessionMigration.Prepare(Format("codex").Read(CodexWithEmptyToolText), "Codex", "Claude Code", "claude");
        var written = Format("claude").Write(expected, "target", "/workspace");
        var changed = Format("claude").Read(written.Replace("Original output", "Changed output", StringComparison.Ordinal));
        var error = Assert.Throws<SessionMigrationException>(() => SessionMigration.ValidateWritten(expected, changed));
        Assert.Contains("output content", error.Message);
        Assert.Contains("tool result 4 (exec)", error.Message);
        Assert.DoesNotContain("Changed output", error.Message);
        var missingHint = JsonLines.Parse(written).ToList();
        foreach (var line in missingHint)
            foreach (var hint in (line["damToolHistory"] as JsonArray ?? []).OfType<JsonObject>()) hint.Remove("emptyResultTextPositions");
        changed = Format("claude").Read(string.Join('\n', missingHint.Select(l => l.ToJsonString())));
        Assert.Throws<SessionMigrationException>(() => SessionMigration.ValidateWritten(expected, changed));
    }

    [Theory]
    [InlineData("[-1]")]
    [InlineData("[2]")]
    [InlineData("[0,0]")]
    [InlineData("[0,\"1\"]")]
    public void InvalidEmptyOutputPositionsAreRejected(string positions)
    {
        var result = Result("id", "kept");
        var entry = new DeveEntry { Content = [result] };
        var metadata = new JsonObject { ["damToolHistory"] = new JsonArray(new JsonObject
        {
            ["id"] = "id", ["emptyResultTextPositions"] = JsonNode.Parse(positions),
        }) };
        Assert.Throws<FormatException>(() => SessionToolMetadata.Read(entry, metadata));
    }

    public static IEnumerable<object[]> Routes() =>
        from source in new[] { "claude", "codex", "pi", "grok" }
        from target in new[] { "claude", "codex", "pi", "grok" }
        where source != target select new object[] { source, target };

    // Converting record by record must give exactly what converting the loaded history gives: generated native histories
    // with abandoned branches, images over the limits, broken and oversized tool exchanges, summaries and small budgets.
    [Theory]
    [MemberData(nameof(StreamingCases))]
    public void StreamingConversionEqualsWholeConversion(string harness, int seed)
    {
        var (text, budget, images) = GeneratedCase(harness, seed);
        var format = Format(harness);
        var loaded = format.Read(text);
        var target = harness == "claude" ? "codex" : "claude";
        string Run(Func<DeveSession?> prepare)
        {
            try { return Normalized(prepare() ?? throw new InvalidOperationException("Not streamed.")); }
            catch (InvalidOperationException e) { return "refused: " + e.Message; }
        }
        var whole = Run(() => SessionMigration.Prepare(format.Read(text), "From", "To", target, budget, images));
        var streamed = Run(() => SessionMigration.Prepare(() => new StringReader(text), format, loaded.Title, "From", "To", target, budget, images));
        Assert.Equal(whole, streamed);
    }

    public static IEnumerable<object[]> StreamingCases() =>
        Enumerable.Range(1, 40).SelectMany(seed => new[] { new object[] { "claude", seed }, ["codex", seed] });

    private static (string Text, int Budget, int Images) GeneratedCase(string harness, int seed)
    {
        var random = new Random(seed);
        var text = harness == "claude" ? GeneratedClaude(random) : GeneratedCodex(random);
        return (text, random.Next(2) == 0 ? SessionMigration.DefaultMaxCharacters : random.Next(2_000, 30_000), random.Next(0, 4));
    }

    [Fact]
    public void GeneratedHistoriesCoverTheConversionCases()
    {
        var reasons = new HashSet<string>();
        var (refused, branched) = (0, 0);
        foreach (var (harness, seed) in StreamingCases().Select(c => ((string)c[0], (int)c[1])))
        {
            var (text, budget, images) = GeneratedCase(harness, seed);
            var loaded = Format(harness).Read(text);
            if (loaded.ActivePath().Count < loaded.Entries.Count(e => e.Kind != DeveEntryKind.Native)) branched++;
            try { reasons.UnionWith(SessionMigration.Prepare(loaded, "From", "To", harness == "claude" ? "codex" : "claude", budget, images).Migration!.Issues.Select(i => i.Reason)); }
            catch (InvalidOperationException) { refused++; }
        }
        Assert.Superset(new HashSet<string> { "context-budget", "image-limit", "incomplete-exchange", "unsupported-call", "orphan-result", "oversized-exchange" }, reasons);
        Assert.InRange(refused, 1, 20);
        Assert.True(branched > 0, "Some generated Claude histories have abandoned branches.");
    }

    [Fact]
    public void StreamingFallsBackWhenEntriesChangeLaterOrTheBranchIsOutOfFileOrder()
    {
        // Grok merges message chunks into earlier entries: it is loaded whole.
        Assert.Null(SessionMigration.Prepare(() => new StringReader(GrokTests.History), Format("grok"), null, "Grok", "Codex", "codex"));
        // A Claude record whose parent comes later in the file.
        var lines = GeneratedClaude(new Random(7)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        (lines[1], lines[2]) = (lines[2], lines[1]);
        Assert.Null(SessionMigration.Prepare(() => new StringReader(string.Join('\n', lines)), Format("claude"), null, "Claude Code", "Codex", "codex"));
        // Nothing to convert.
        Assert.Empty(SessionMigration.Prepare(() => new StringReader(""), Format("codex"), null, "Codex", "Claude Code", "claude")!.Entries);
    }

    private static string Normalized(DeveSession migrated)
    {
        var ids = new Dictionary<string, string>();
        string Map(string id) => ids.TryGetValue(id, out var mapped) ? mapped : ids[id] = "e" + ids.Count;
        foreach (var entry in migrated.Entries) { entry.Id = Map(entry.Id); if (entry.ParentId is not null) entry.ParentId = Map(entry.ParentId); }
        if (migrated.LeafId is not null) migrated.LeafId = Map(migrated.LeafId);
        if (migrated.Migration is not null) migrated.Migration.CreatedAt = default;
        return migrated.ToJson();
    }

    private static string Words(Random random, int count) => string.Join(' ', Enumerable.Range(0, count).Select(_ => "word" + random.Next(1000)));
    private static string ImageData(Random random) => Convert.ToBase64String(Enumerable.Range(0, random.Next(0, 3) == 0 ? 0 : random.Next(10, 4_000)).Select(i => (byte)i).ToArray());

    // Claude Code lines; every fifth turn starts from an earlier record, leaving the records after it as an abandoned branch.
    private static string GeneratedClaude(Random random)
    {
        var lines = new List<JsonObject>();
        var uuids = new List<string>();
        string? parent = null;
        var time = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        void Add(string type, JsonNode content, bool summary = false)
        {
            var uuid = Guid.NewGuid().ToString();
            var line = new JsonObject { ["type"] = type, ["uuid"] = uuid, ["parentUuid"] = parent, ["sessionId"] = "generated", ["cwd"] = "/workspace",
                ["timestamp"] = (time = time.AddSeconds(1)).ToString("O"), ["message"] = new JsonObject { ["role"] = type, ["content"] = content } };
            if (summary) line["isCompactSummary"] = true;
            lines.Add(line); uuids.Add(uuid); parent = uuid;
        }
        for (var turn = 0; turn < random.Next(4, 30); turn++)
        {
            if (turn % 5 == 4 && uuids.Count > 2) parent = uuids[random.Next(uuids.Count - 1)];
            if (random.Next(12) == 0) { Add("user", Words(random, 40), summary: true); continue; }
            var user = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Words(random, random.Next(1, 60)) });
            for (var i = random.Next(0, 3); i > 0; i--)
                user.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = ImageData(random) } });
            Add("user", user);
            if (random.Next(8) == 0) // Output whose call isn't in the history.
                Add("user", new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "toolu_stray", ["content"] = Words(random, 30) }));
            for (var step = random.Next(0, 4); step > 0; step--)
            {
                var calls = Enumerable.Range(0, random.Next(1, 3)).Select(_ => "toolu_" + random.Next(100_000)).ToList();
                var assistant = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Words(random, random.Next(0, 20)) });
                foreach (var id in calls)
                    assistant.Add(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = random.Next(10) == 0 ? "not valid!" : "Bash",
                        ["input"] = new JsonObject { ["command"] = Words(random, random.Next(1, 10)) } });
                Add("assistant", assistant);
                var results = new JsonArray();
                foreach (var id in calls.Where(_ => random.Next(8) != 0))
                {
                    var output = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Words(random, random.Next(0, 2) == 0 ? 20 : random.Next(500, 40_000)) });
                    if (random.Next(5) == 0)
                        output.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = ImageData(random) } });
                    results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = random.Next(15) == 0 ? "toolu_orphan" : id, ["content"] = output, ["is_error"] = random.Next(6) == 0 });
                }
                if (results.Count > 0) Add("user", results);
            }
            Add("assistant", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Words(random, random.Next(1, 80)) }));
        }
        return string.Concat(lines.Select(l => l.ToJsonString() + "\n"));
    }

    // Codex rollout lines: messages, parallel function and custom calls, outputs (some missing) and compactions.
    private static string GeneratedCodex(Random random)
    {
        var lines = new List<JsonObject>();
        var time = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        void Add(string type, JsonObject payload) => lines.Add(new JsonObject { ["timestamp"] = (time = time.AddSeconds(1)).ToString("O"), ["type"] = type, ["payload"] = payload });
        Add("session_meta", new JsonObject { ["id"] = "generated", ["cwd"] = "/workspace", ["timestamp"] = time.ToString("O") });
        for (var turn = 0; turn < random.Next(4, 30); turn++)
        {
            if (random.Next(12) == 0) { Add("compacted", new JsonObject { ["message"] = Words(random, 50) }); continue; }
            var user = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = Words(random, random.Next(1, 60)) });
            for (var i = random.Next(0, 3); i > 0; i--) user.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/png;base64," + ImageData(random) });
            Add("response_item", new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = user });
            if (random.Next(8) == 0) // Output whose call isn't in the history.
                Add("response_item", new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call_stray", ["output"] = Words(random, 30) });
            for (var step = random.Next(0, 4); step > 0; step--)
            {
                var calls = Enumerable.Range(0, random.Next(1, 3)).Select(_ => "call_" + random.Next(100_000)).ToList();
                foreach (var id in calls)
                    Add("response_item", random.Next(3) == 0
                        ? new JsonObject { ["type"] = "custom_tool_call", ["call_id"] = id, ["name"] = "exec", ["input"] = Words(random, random.Next(1, 10)) }
                        : new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = "shell", ["arguments"] = new JsonObject { ["cmd"] = Words(random, 5) }.ToJsonString() });
                foreach (var id in calls.Where(_ => random.Next(8) != 0))
                    Add("response_item", new JsonObject { ["type"] = "function_call_output", ["call_id"] = id,
                        ["output"] = Words(random, random.Next(0, 2) == 0 ? 20 : random.Next(500, 40_000)) });
            }
            Add("response_item", new JsonObject { ["type"] = "message", ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = Words(random, random.Next(1, 80)) }) });
        }
        return string.Concat(lines.Select(l => l.ToJsonString() + "\n"));
    }

    public static ISessionFormat Format(string id) => id switch
    {
        "claude" => new ClaudeSessionFormat(), "codex" => new CodexSessionFormat(), "pi" => new PiSessionFormat(), "grok" => new GrokSessionFormat(),
        _ => throw new ArgumentException(id),
    };

    [Theory, MemberData(nameof(Routes))]
    public void CompletedExchangesRemainStructured_WithArgumentsErrorsImagesAndProvenance(string from, string to)
    {
        var sourceFormat = Format(from); var targetFormat = Format(to);
        var source = sourceFormat.Read(sourceFormat.Write(Sample(from), Guid.NewGuid().ToString(), "/workspace"));
        var original = source.ToJson();
        var migrated = SessionMigration.Prepare(source, from, to, to);
        var written = targetFormat.Write(migrated, Guid.NewGuid().ToString(), "/workspace");
        var actual = targetFormat.Read(written);
        SessionMigration.ValidateWritten(migrated, actual);
        Assert.Equal(original, source.ToJson());
        Assert.Equal(2, Calls(actual).Count);
        Assert.Equal(2, migrated.Migration!.PreservedCalls);
        Assert.Equal(0, migrated.Migration.ArchivedExchanges);
        Assert.Contains(actual.ActivePath().SelectMany(e => e.Content ?? []), b => b is { Type: "toolResult", IsError: true });
        Assert.DoesNotContain("▸ ran", string.Join('\n', actual.Entries.Select(e => e.PlainText())));
        Assert.DoesNotContain("PRIVATE REASONING", written);
        Assert.DoesNotContain("source-system-instructions", written);
        Assert.Contains("fresh user request", written);
        Assert.Contains("historical-result-image", written);

        // A second switch must not lose the opaque call encoding or stack transfer notices.
        var third = new[] { "claude", "codex", "pi", "grok" }.First(h => h != from && h != to);
        var again = SessionMigration.Prepare(actual, to, third, third);
        var final = Format(third).Read(Format(third).Write(again, Guid.NewGuid().ToString(), "/workspace"));
        SessionMigration.ValidateWritten(again, final);
        Assert.Equal(2, Calls(final).Count);
        Assert.Single(final.ActivePath().SelectMany(e => e.Content ?? []), b => b.Text?.Contains("[DAM session migration v1]", StringComparison.Ordinal) == true);
        if (from == "codex")
        {
            var script = Assert.Single(Calls(final), b => b.Name == "exec");
            Assert.Equal("custom", script.CallType);
            Assert.Equal("functions", script.Namespace);
            Assert.Contains("if (false)", script.Arguments!["input"]!.GetValue<string>());
        }
    }

    [Fact]
    public void AssistantClaimsAreNeverPromotedToToolRecords()
    {
        var source = new DeveSession();
        Add(source, DeveRole.User, DeveBlock.OfText("Share a file."));
        const string claim = "▸ ran `dam-tools share-file invented.html`\noutput:\nFile shared successfully.";
        Add(source, DeveRole.Assistant, DeveBlock.OfText(claim));
        foreach (var to in new[] { "claude", "codex", "pi", "grok" })
        {
            var converted = SessionMigration.Prepare(source, "source", to, to);
            Assert.Empty(Calls(converted));
            Assert.Contains(converted.Entries, e => e.Role == DeveRole.Assistant && e.PlainText() == claim);
            Assert.Equal(0, converted.Migration!.PreservedCalls);
        }
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("orphan-result")]
    [InlineData("duplicate-call")]
    [InlineData("duplicate-result")]
    [InlineData("unsupported-name")]
    [InlineData("malformed-arguments")]
    public void BrokenExchangesAreArchivalData_WithoutInventedResults(string failure)
    {
        var source = new DeveSession();
        Add(source, DeveRole.User, DeveBlock.OfText("Perform an operation"));
        var call = Call("c1", "Bash", new JsonObject { ["command"] = "printf historical" });
        if (failure == "unsupported-name") call.Name = "tool.with.invalid.name";
        if (failure == "malformed-arguments") call.Arguments = new JsonArray("broken");
        if (failure != "orphan-result") Add(source, DeveRole.Assistant, call);
        if (failure == "duplicate-call") Add(source, DeveRole.Assistant, Call("c1", "Read", new JsonObject()));
        if (failure != "missing-result") Add(source, DeveRole.Tool, Result("c1", "original actual output"));
        if (failure == "duplicate-result") Add(source, DeveRole.Tool, Result("c1", "duplicate output"));
        var migrated = SessionMigration.Prepare(source, "source", "claude", "claude");
        Assert.Empty(Calls(migrated));
        Assert.DoesNotContain(migrated.Entries.SelectMany(e => e.Content ?? []), b => b.Type == "toolResult");
        Assert.Equal(1, migrated.Migration!.ArchivedExchanges);
        Assert.Contains(migrated.Entries, e => e.Role == DeveRole.User && e.PlainText().Contains("[DAM historical record:", StringComparison.Ordinal));
        Assert.DoesNotContain("No result provided", migrated.ToJson());
        SessionMigration.Validate(migrated);
    }

    [Fact]
    public void MixedClaudeResultAndNewUserMessageKeepTheirOrder()
    {
        var sample = Sample("claude");
        var result = sample.Entries.Single(e => e.Role == DeveRole.Tool);
        result.Role = DeveRole.User;
        result.Content!.Add(DeveBlock.OfText("Next user request in the result record"));
        var migrated = SessionMigration.Prepare(sample, "claude", "codex", "codex");
        SessionMigration.ValidateWritten(migrated, Format("codex").Read(Format("codex").Write(migrated, "test-id", "/workspace")));
        var messages = migrated.ActivePath();
        Assert.True(messages.FindIndex(e => e.Role == DeveRole.Tool) < messages.FindIndex(e => e.PlainText().Contains("Next user request", StringComparison.Ordinal)));
    }

    [Fact]
    public void BudgetKeepsTheLatestRequestAndWholeExchanges_AndReportsAbbreviation()
    {
        var source = new DeveSession();
        for (var i = 0; i < 15; i++)
        {
            Add(source, DeveRole.User, DeveBlock.OfText("user request " + i));
            Add(source, DeveRole.Assistant, Call("reused_id", "Bash", new JsonObject { ["command"] = "printf test" }));
            Add(source, DeveRole.Tool, Result("reused_id", new string('x', i == 14 ? 50_000 : 1_000)));
        }
        var migrated = SessionMigration.Prepare(source, "source", "claude", "claude", maxCharacters: 8_000);
        SessionMigration.Validate(migrated);
        var text = string.Join('\n', migrated.Entries.Select(e => e.PlainText()));
        Assert.Contains("user request 14", text);
        Assert.Contains("oversized-exchange", text);
        Assert.True(migrated.Migration!.OmittedExchanges > 0);
        Assert.Equal(1, migrated.Migration.ArchivedExchanges);
        var ids = Calls(migrated).Select(b => b.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.DoesNotContain(migrated.Entries.SelectMany(e => e.Content ?? []), b => b.Type == "toolResult" && b.Content?.Any(c => c.Text?.Length == 50_000) == true);
    }

    [Fact]
    public void LatestUserInstructionsAreNotSilentlyTruncated()
    {
        var source = new DeveSession();
        Add(source, DeveRole.User, DeveBlock.OfText(new string('u', 10_000)));
        Assert.Throws<InvalidOperationException>(() => SessionMigration.Prepare(source, "source", "claude", "claude", 3_000));
    }

    [Fact]
    public void RecentImagesAndToolImagesShareTheBudget_AndOriginalsRemainIntact()
    {
        var source = Sample("pi");
        Add(source, DeveRole.User, Image("newest"));
        var before = source.ToJson();
        var migrated = SessionMigration.Prepare(source, "pi", "claude", "claude", maxImages: 1);
        Assert.Equal(before, source.ToJson());
        Assert.Equal(2, migrated.Migration!.OmittedImages);
        var actual = Format("claude").Read(Format("claude").Write(migrated, "test", "/workspace"));
        SessionMigration.ValidateWritten(migrated, actual);
        Assert.Contains(actual.Entries.SelectMany(e => e.Content ?? []), b => b.Type == "image" && b.Data == "newest");
    }

    [Fact]
    public void ActiveBranchOnly_AndOriginalAbandonedBranchIsUntouched()
    {
        var source = Sample("pi");
        var leaf = source.LeafId;
        Add(source, DeveRole.Assistant, Call("abandoned", "Bash", new JsonObject { ["command"] = "wrong branch" }));
        source.LeafId = leaf;
        var migrated = SessionMigration.Prepare(source, "pi", "claude", "claude");
        Assert.Equal(2, Calls(migrated).Count);
        Assert.DoesNotContain("wrong branch", migrated.ToJson());
        Assert.Contains("wrong branch", source.ToJson());
    }

    [Fact]
    public void ValidatorRejectsSilentWriterLossAndChangedArguments()
    {
        var migrated = SessionMigration.Prepare(Sample("pi"), "pi", "claude", "claude");
        var changed = DeveSession.FromJson(migrated.ToJson());
        Calls(changed)[0].Arguments!["command"] = "different command";
        var error = Assert.Throws<SessionMigrationException>(() => SessionMigration.ValidateWritten(migrated, changed));
        Assert.Contains("arguments", error.Message);
        Assert.Contains("tool call 1 (", error.Message);
        Assert.Contains("not installed", error.Message);
        Assert.DoesNotContain("different command", error.Message);
        changed = DeveSession.FromJson(migrated.ToJson());
        changed.Entries.RemoveAll(e => e.Role == DeveRole.Tool);
        Assert.Throws<SessionMigrationException>(() => SessionMigration.ValidateWritten(migrated, changed));
    }

    [Fact]
    public void MalformedNativeFunctionArgumentsCannotBecomeAValidHistoricalCall()
    {
        var source = Format("codex").Read(HarnessFormatTests.CodexSession.Replace("{\\\"cmd\\\":\\\"dotnet test\\\"}", "not-json", StringComparison.Ordinal));
        var migrated = SessionMigration.Prepare(source, "codex", "claude", "claude");
        Assert.Empty(Calls(migrated));
        Assert.Equal(1, migrated.Migration!.ArchivedExchanges);
        Assert.Contains(migrated.Migration.Issues, i => i.Reason == "unsupported-call");
    }

    [Fact]
    public void ImageOnlyLatestUserMessageIsProtected()
    {
        var source = new DeveSession();
        Add(source, DeveRole.User, DeveBlock.OfText("older request"));
        Add(source, DeveRole.Assistant, DeveBlock.OfText(new string('x', 40_000)));
        Add(source, DeveRole.User, Image("latest-image"));
        var migrated = SessionMigration.Prepare(source, "source", "claude", "claude", maxCharacters: 10_000);
        Assert.Contains(migrated.Entries.SelectMany(e => e.Content ?? []), b => b.Type == "image" && b.Data == "latest-image");
        Assert.True(migrated.Migration!.OmittedExchanges > 0);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("pi")]
    public void ParallelCallsWithInterleavedCommentaryKeepTheirOrder(string target)
    {
        var source = new DeveSession();
        Add(source, DeveRole.User, DeveBlock.OfText("Perform both operations"));
        Add(source, DeveRole.Assistant, Call("a", "Bash", new JsonObject()), DeveBlock.OfText("Commentary between calls"));
        Add(source, DeveRole.Assistant, Call("b", "Read", new JsonObject()), DeveBlock.OfText("Commentary after calls"));
        Add(source, DeveRole.Tool, Result("a", "first"));
        Add(source, DeveRole.Tool, Result("b", "second"));
        var migrated = SessionMigration.Prepare(source, "source", target, target);
        var format = Format(target);
        var actual = format.Read(format.Write(migrated, "test-session", "/workspace"));
        SessionMigration.ValidateWritten(migrated, actual);
        Assert.Equal(2, Calls(actual).Count);
        var sequence = actual.ActivePath().SelectMany(e => e.Content ?? []).Where(b => b.Type == "toolCall" || b.Text?.StartsWith("Commentary", StringComparison.Ordinal) == true)
            .Select(b => b.Name ?? b.Text).ToArray();
        Assert.Equal(["Bash", "Commentary between calls", "Read", "Commentary after calls"], sequence);
    }

    public static DeveSession Sample(string harness)
    {
        var session = new DeveSession { CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Cwd = "/workspace" };
        Add(session, DeveRole.System, DeveBlock.OfText("source-system-instructions"));
        Add(session, DeveRole.User, DeveBlock.OfText("Earlier user request"), Image("historical-user-image"));
        var call = harness == "codex"
            ? Call("call_one", "exec", new JsonObject { ["input"] = "if (false) { await tools.exec_command({cmd:'never run this'}); } text('old');" })
            : Call("call_one", harness == "claude" ? "Bash" : "bash", new JsonObject { ["command"] = "printf historical >> historical.txt", ["timeout"] = 1000 });
        if (harness == "codex") { call.CallType = "custom"; call.Namespace = "functions"; }
        Add(session, DeveRole.Assistant, new DeveBlock { Type = "thinking", Text = "PRIVATE REASONING", Signature = new DeveSignature { Value = "private-signature" } },
            DeveBlock.OfText("Running the requested operations."), call, Call("call_two", "Read", new JsonObject { ["file_path"] = "/workspace/test.txt" }));
        var result = Result("call_one", "Original output; a process is still running with id 42.");
        result.Content!.Add(Image("historical-result-image"));
        result.CallType = call.CallType; result.Namespace = call.Namespace;
        var error = Result("call_two", "File not found"); error.IsError = true;
        Add(session, DeveRole.Tool, result, error);
        Add(session, DeveRole.Assistant, DeveBlock.OfText("The first command started; the read failed."));
        Add(session, DeveRole.User, DeveBlock.OfText("fresh user request"));
        return session;
    }

    private static DeveBlock Image(string data) => new() { Type = "image", MimeType = "image/png", Data = data };
    private static DeveBlock Call(string id, string name, JsonObject args) => new() { Type = "toolCall", Id = id, Name = name, ToolKind = ToolKinds.For(name), Arguments = args };
    private static DeveBlock Result(string id, string text) => new() { Type = "toolResult", ToolCallId = id, Content = [DeveBlock.OfText(text)] };
    private static List<DeveBlock> Calls(DeveSession session) => session.ActivePath().SelectMany(e => e.Content ?? []).Where(b => b.Type == "toolCall").ToList();
    private static void Add(DeveSession session, DeveRole role, params DeveBlock[] blocks)
    {
        var id = Guid.NewGuid().ToString("N");
        session.Entries.Add(new DeveEntry { Id = id, ParentId = session.LeafId, Timestamp = DateTime.UtcNow, Kind = DeveEntryKind.Message, Role = role, Content = [.. blocks] });
        session.LeafId = id;
    }
}
