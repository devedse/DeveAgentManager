using System.Text.Json.Nodes;
using DeveAgentManager.Client.Shared;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Harnesses.Pi;
using Xunit;

namespace DeveAgentManager.Tests;

public class SessionTests
{
    // A small pi session with every kind of line pi writes (see pi's docs/session-format.md).
    private static readonly string PiSession = string.Join('\n',
        """{"type":"session","version":3,"id":"orig-id","timestamp":"2026-09-25T10:00:00.000Z","cwd":"/home/remoteman/work/maze"}""",
        """{"type":"message","id":"s1","parentId":null,"timestamp":"2026-09-25T10:00:00.100Z","message":{"role":"system","content":"","sections":{"preamble":"You are pi.","cwd":"/home/remoteman/work/maze"},"toolsAdded":[{"name":"bash"}],"timestamp":1790330400100}}""",
        """{"type":"model_change","id":"m1","parentId":"s1","timestamp":"2026-09-25T10:00:00.200Z","provider":"anthropic","modelId":"claude-opus-4-5"}""",
        """{"type":"session_info","id":"n1","parentId":"m1","timestamp":"2026-09-25T10:00:00.300Z","name":"maze"}""",
        """{"type":"message","id":"u1","parentId":"n1","timestamp":"2026-09-25T10:00:01.000Z","message":{"role":"user","content":[{"type":"text","text":"Make the maze <faster> & smaller"},{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}],"timestamp":1790330401000}}""",
        """{"type":"message","id":"a1","parentId":"u1","timestamp":"2026-09-25T10:00:02.000Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Look at the tests first.","thinkingSignature":"sig=="},{"type":"text","text":"Running the tests."},{"type":"toolCall","id":"toolu_1","name":"bash","arguments":{"command":"dotnet test"}}],"api":"anthropic-messages","provider":"anthropic","model":"claude-opus-4-5","usage":{"input":10,"output":20,"cacheRead":30,"cacheWrite":0,"totalTokens":60,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0.0125}},"stopReason":"toolUse","timestamp":1790330402000}}""",
        """{"type":"message","id":"t1","parentId":"a1","timestamp":"2026-09-25T10:00:03.000Z","message":{"role":"toolResult","toolCallId":"toolu_1","toolName":"bash","content":[{"type":"text","text":"Passed! 18 tests"}],"details":{},"isError":false,"timestamp":1790330403000}}""",
        """{"type":"custom","id":"c1","parentId":"t1","timestamp":"2026-09-25T10:00:03.500Z","customType":"some-extension","data":{"count":42}}""",
        """{"type":"compaction","id":"k1","parentId":"c1","timestamp":"2026-09-25T10:00:04.000Z","summary":"Tests pass.","firstKeptEntryId":"u1","tokensBefore":5000}""",
        """{"type":"message","id":"b1","parentId":"k1","timestamp":"2026-09-25T10:00:05.000Z","message":{"role":"bashExecution","command":"ls","output":"README.md","exitCode":0,"cancelled":false,"truncated":false,"timestamp":1790330405000}}""",
        """{"type":"message","id":"a2","parentId":"b1","timestamp":"2026-09-25T10:00:06.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Done: it is 2x faster."}],"api":"anthropic-messages","provider":"anthropic","model":"claude-opus-4-5","usage":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":3,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"stop","timestamp":1790330406000}}""",
        """{"type":"message","id":"u2","parentId":"a1","timestamp":"2026-09-25T10:00:07.000Z","message":{"role":"user","content":"An abandoned branch","timestamp":1790330407000}}""",
        """{"type":"message","id":"a3","parentId":"a2","timestamp":"2026-09-25T10:00:08.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Anything else?"}],"api":"anthropic-messages","provider":"anthropic","model":"claude-opus-4-5","usage":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":3,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"stop","timestamp":1790330408000}}""") + "\n";

    private readonly PiSessionFormat _pi = new();

    [Fact]
    public void Read_MapsPiLinesToNeutralEntries()
    {
        var session = _pi.Read(PiSession);

        Assert.Equal("orig-id", session.Sources.Single().SessionId);
        Assert.Equal("/home/remoteman/work/maze", session.Cwd);
        Assert.Equal("maze", session.Title);
        Assert.Equal("a3", session.LeafId);
        Assert.Equal(1, session.OtherBranchCount());

        var assistant = session.Entries.Single(e => e.Id == "a1");
        Assert.Equal(DeveRole.Assistant, assistant.Role);
        Assert.Equal(["thinking", "text", "toolCall"], assistant.Content!.Select(b => b.Type));
        Assert.Equal("sig==", assistant.Content![0].Signature!.Value);
        Assert.Equal("anthropic", assistant.Content![0].Signature!.Provider);
        Assert.Equal(ToolKinds.Shell, assistant.Content![2].ToolKind);
        Assert.Equal(0.0125m, assistant.Usage!.CostUsd);

        var result = session.Entries.Single(e => e.Id == "t1").Content!.Single();
        Assert.Equal(("toolResult", "toolu_1", false), (result.Type, result.ToolCallId, result.IsError));
        Assert.Equal(DeveEntryKind.Native, session.Entries.Single(e => e.Id == "c1").Kind);
        Assert.Equal(("compaction", "u1"), (session.Entries.Single(e => e.Id == "k1").SummaryKind, session.Entries.Single(e => e.Id == "k1").FirstKeptId));
        Assert.Equal("shell", session.Entries.Single(e => e.Id == "b1").Event);

        // The active path skips the abandoned branch (u2) and ends at the leaf.
        Assert.Equal(["s1", "m1", "n1", "u1", "a1", "t1", "c1", "k1", "b1", "a2", "a3"], session.ActivePath().Select(e => e.Id));
    }

    [Fact]
    public void Write_IsLosslessForPiAndOnlyChangesTheHeader()
    {
        // Through the stored JSON form, like the Manager does.
        var session = DeveSession.FromJson(_pi.Read(PiSession).ToJson());
        var written = _pi.Write(session, "new-id", "/home/remoteman/work/other");

        var original = PiSession.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(original.Length, lines.Length);

        var header = JsonNode.Parse(lines[0])!;
        Assert.Equal(("new-id", "/home/remoteman/work/other"), (header["id"]!.GetValue<string>(), header["cwd"]!.GetValue<string>()));
        for (var i = 1; i < original.Length; i++)
        {
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original[i]), JsonNode.Parse(lines[i])), $"Line {i} changed: {lines[i]}");
        }

        Assert.Contains("<faster> & smaller", written); // no \u003C escaping in the file
    }

    [Fact]
    public void Write_FromNeutralFieldsGivesAValidPiSession()
    {
        // As if the session came from another harness: no pi lines to copy.
        var session = _pi.Read(PiSession);
        foreach (var entry in session.Entries)
        {
            entry.Native = null;
            entry.Harness = "other";
        }

        var back = _pi.Read(_pi.Write(session, "x", "/w"));

        // System prompts and other harnesses' extension data are not carried over; the conversation is.
        Assert.Equal(["m1", "n1", "u1", "a1", "t1", "k1", "b1", "a2", "a3"], back.ActivePath().Select(e => e.Id));
        var assistant = back.Entries.Single(e => e.Id == "a1");
        Assert.Null(assistant.Content![0].Signature); // foreign signatures are dropped, the thinking text stays
        Assert.Equal("Look at the tests first.", assistant.Content![0].Text);
        Assert.Equal("dotnet test", assistant.Content![2].Arguments!["command"]!.GetValue<string>());
        Assert.Equal("Passed! 18 tests", back.Entries.Single(e => e.Id == "t1").Content!.Single().Content!.Single().Text);
        Assert.Equal("m1", back.Entries.Single(e => e.Id == "m1").Id);
        Assert.Null(back.Entries.Single(e => e.Id == "m1").ParentId); // re-attached past the dropped system prompt
    }

    [Fact]
    public void SessionPaths_MatchPi()
    {
        Assert.Equal("--home-remoteman-work-maze--", PiSessionFormat.SessionDirectoryName("/home/remoteman/work/maze"));
        Assert.Equal("--C--XGitPrivate-DeveAgentManager--", PiSessionFormat.SessionDirectoryName(@"C:\XGitPrivate\DeveAgentManager"));
        Assert.Equal("~/.pi/agent/sessions/--w--/2026-09-25T10-00-00-000Z_abc.jsonl", _pi.GetPath("abc", "/w", new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ParseLocated_ReadsSizeAndPathPerSession()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var located = SessionService.ParseLocated($"__DAM_SESSION__ {a:N}\n1234 /home/r/.pi/agent/sessions/--x--/t_1.jsonl\n__DAM_SESSION__ {b:N}\n");
        Assert.Equal((1234L, "/home/r/.pi/agent/sessions/--x--/t_1.jsonl"), located[a]);
        Assert.False(located.ContainsKey(b));
    }

    [Theory]
    [InlineData("https://github.com/devedse", true)]
    [InlineData("docs/readme.md", true)]
    [InlineData("#section", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" JavaScript:alert(1)", false)]
    [InlineData("data:text/html;base64,xx", false)]
    public void Markdown_OnlyKeepsSafeLinks(string url, bool safe) => Assert.Equal(safe, MarkdownRenderer.IsSafe(url));

    [Fact]
    public void Markdown_DoesNotRenderHtmlOrScriptLinks()
    {
        var html = MarkdownRenderer.ToHtml("<script>alert(1)</script> [x](javascript:alert(1))").Value;
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("javascript:", html);
    }
}
