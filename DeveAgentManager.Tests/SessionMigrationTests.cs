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
    public static IEnumerable<object[]> Routes() =>
        from source in new[] { "claude", "codex", "pi", "grok" }
        from target in new[] { "claude", "codex", "pi", "grok" }
        where source != target select new object[] { source, target };

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
        Assert.Throws<FormatException>(() => SessionMigration.ValidateWritten(migrated, changed));
        changed = DeveSession.FromJson(migrated.ToJson());
        changed.Entries.RemoveAll(e => e.Role == DeveRole.Tool);
        Assert.Throws<FormatException>(() => SessionMigration.ValidateWritten(migrated, changed));
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
