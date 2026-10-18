using System.Text.Json.Nodes;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Claude Code and Codex session files, and moving a conversation between harnesses.</summary>
public class HarnessFormatTests
{
    // Lines as Claude Code 2.1 writes them (shortened), plus a tool call and its result.
    internal static readonly string ClaudeSession = string.Join('\n',
        """{"type":"queue-operation","operation":"enqueue","timestamp":"2026-09-26T09:48:16.975Z","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685","content":"hello there"}""",
        """{"parentUuid":null,"isSidechain":false,"type":"user","message":{"role":"user","content":"hello there"},"uuid":"u1","timestamp":"2026-09-26T09:48:17.054Z","userType":"external","cwd":"/home/remoteman/work/maze","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685","version":"2.1.283","gitBranch":"HEAD"}""",
        """{"parentUuid":"u1","isSidechain":false,"attachment":{"type":"date","date":"2026-09-26"},"type":"attachment","uuid":"at1","timestamp":"2026-09-26T09:48:17.136Z","cwd":"/home/remoteman/work/maze","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685"}""",
        """{"parentUuid":"at1","isSidechain":false,"type":"assistant","uuid":"a1","timestamp":"2026-09-26T09:48:19.000Z","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5","content":[{"type":"thinking","thinking":"Check the tests.","signature":"sig=="},{"type":"text","text":"Running the tests."},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"dotnet test"}}],"stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":30}},"cwd":"/home/remoteman/work/maze","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685"}""",
        """{"parentUuid":"a1","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Passed! 18 tests","is_error":false}]},"toolUseResult":{"stdout":"Passed! 18 tests"},"uuid":"t1","timestamp":"2026-09-26T09:48:25.000Z","cwd":"/home/remoteman/work/maze","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685"}""",
        """{"parentUuid":"t1","isSidechain":false,"type":"assistant","uuid":"a2","timestamp":"2026-09-26T09:48:30.000Z","message":{"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-5","content":[{"type":"text","text":"All 18 tests pass."}],"stop_reason":"end_turn"},"cwd":"/home/remoteman/work/maze","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685"}""",
        """{"type":"summary","summary":"Run the maze tests","leafUuid":"a2"}""",
        """{"type":"last-prompt","lastPrompt":"hello there","leafUuid":"u1","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685"}""",
        """{"type":"cost-state","sessionId":"7294817d-8c1d-4058-a31a-d8ee9a43f685","totalCostUSD":0}""") + "\n";

    // Lines as Codex 0.157 writes them (shortened), plus a tool call and its output.
    internal static readonly string CodexSession = string.Join('\n',
        """{"timestamp":"2026-09-26T09:00:00.000Z","type":"session_meta","payload":{"id":"0199aaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee","timestamp":"2026-09-26T09:00:00.000Z","cwd":"/home/remoteman/work/maze","originator":"codex_cli_rs","cli_version":"0.157.1","source":"cli","model_provider":"openai"}}""",
        """{"timestamp":"2026-09-26T09:00:01.000Z","type":"response_item","payload":{"type":"message","id":"msg_d","role":"developer","content":[{"type":"input_text","text":"<skills_instructions>..."}]}}""",
        """{"timestamp":"2026-09-26T09:00:01.000Z","type":"response_item","payload":{"type":"message","id":"msg_e","role":"user","content":[{"type":"input_text","text":"<environment_context>\n  <cwd>/home/remoteman/work/maze</cwd>\n</environment_context>"}]}}""",
        """{"timestamp":"2026-09-26T09:00:02.000Z","type":"turn_context","payload":{"cwd":"/home/remoteman/work/maze","model":"gpt-6"}}""",
        """{"timestamp":"2026-09-26T09:00:02.000Z","type":"response_item","payload":{"type":"message","id":"msg_u","role":"user","content":[{"type":"input_text","text":"make the maze faster"}]}}""",
        """{"timestamp":"2026-09-26T09:00:02.000Z","type":"event_msg","payload":{"type":"user_message","message":"make the maze faster","images":[]}}""",
        """{"timestamp":"2026-09-26T09:00:03.000Z","type":"response_item","payload":{"type":"reasoning","summary":[{"type":"summary_text","text":"Profile first."}],"encrypted_content":"gAAAA..."}}""",
        """{"timestamp":"2026-09-26T09:00:04.000Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"dotnet test\"}","call_id":"call_1"}}""",
        """{"timestamp":"2026-09-26T09:00:09.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_1","output":"Passed! 18 tests"}}""",
        """{"timestamp":"2026-09-26T09:00:10.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Tests pass; now profiling."}]}}""",
        """{"timestamp":"2026-09-26T09:00:10.000Z","type":"event_msg","payload":{"type":"token_count","info":null}}""") + "\n";

    private readonly ClaudeSessionFormat _claude = new();
    private readonly CodexSessionFormat _codex = new();

    [Fact]
    public void Claude_Read_MapsMessagesToolsAndTitle()
    {
        var session = _claude.Read(ClaudeSession);

        Assert.Equal("7294817d-8c1d-4058-a31a-d8ee9a43f685", session.Sources.Single().SessionId);
        Assert.Equal("/home/remoteman/work/maze", session.Cwd);
        Assert.Equal("Run the maze tests", session.Title);
        Assert.Equal("a2", session.LeafId);

        var assistant = session.Entries.Single(e => e.Id == "a1");
        Assert.Equal(["thinking", "text", "toolCall"], assistant.Content!.Select(b => b.Type));
        Assert.Equal(("Bash", ToolKinds.Shell), (assistant.Content![2].Name, assistant.Content![2].ToolKind));
        Assert.Equal(30, assistant.Usage!.CacheRead);

        var result = session.Entries.Single(e => e.Id == "t1");
        Assert.Equal(DeveRole.Tool, result.Role);
        Assert.Equal("Passed! 18 tests", result.Content!.Single().Content!.Single().Text);

        // Attachments and bookkeeping are kept, but aren't conversation.
        Assert.Equal(DeveEntryKind.Native, session.Entries.Single(e => e.Id == "at1").Kind);
        Assert.Equal(["u1", "at1", "a1", "t1", "a2"], session.ActivePath().Select(e => e.Id));
    }

    [Fact]
    public void Claude_Write_IsLosslessForClaude_WithTheNewIdAndFolder()
    {
        var written = _claude.Write(_claude.Read(ClaudeSession), "11111111-2222-3333-4444-555555555555", "/home/remoteman/work/other");
        Assert.Equal(ClaudeSession.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, written.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("7294817d", written);
        Assert.DoesNotContain("/home/remoteman/work/maze", written);

        var again = _claude.Read(written);
        Assert.Equal(["u1", "at1", "a1", "t1", "a2"], again.ActivePath().Select(e => e.Id));
        Assert.Equal("All 18 tests pass.", again.Entries.Single(e => e.Id == "a2").PlainText());
    }

    [Fact]
    public void Codex_Read_HidesContext_MapsReasoningAndTools()
    {
        var session = _codex.Read(CodexSession);

        Assert.Equal("0199aaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee", session.Sources.Single().SessionId);
        Assert.Equal("/home/remoteman/work/maze", session.Cwd);
        Assert.Equal("make the maze faster", session.Title);

        var messages = session.ActivePath().Where(e => e.Kind == DeveEntryKind.Message && e.Role != DeveRole.System).ToList();
        Assert.Equal([DeveRole.User, DeveRole.Assistant, DeveRole.Assistant, DeveRole.Tool, DeveRole.Assistant], messages.Select(m => m.Role!.Value));
        Assert.Equal("Profile first.", messages[1].Content!.Single().Text);
        Assert.Equal(("exec_command", ToolKinds.Shell, "dotnet test"), (messages[2].Content![0].Name, messages[2].Content![0].ToolKind, messages[2].Content![0].Arguments!["cmd"]!.ToString()));
        Assert.Equal("Passed! 18 tests", messages[3].Content!.Single().Content!.Single().Text);
    }

    [Fact]
    public void Codex_Write_IsLosslessForCodex_WithTheNewIdAndFolder()
    {
        var written = _codex.Write(_codex.Read(CodexSession), "0199ffff-0000-7000-8000-000000000001", "/home/remoteman/work/other");
        var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CodexSession.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, lines.Length);
        Assert.Contains("\"id\":\"0199ffff-0000-7000-8000-000000000001\"", lines[0]);
        Assert.Contains("\"cwd\":\"/home/remoteman/work/other\"", lines[0]);
        Assert.Equal(CodexSession.Split('\n')[4], lines[4]);
    }

    [Fact]
    public void Flatten_PiToClaude_TextOnly_AlternatingAndResumable()
    {
        var pi = new PiSessionFormat().Read(PiSample);
        var flat = SessionFlattener.Flatten(pi, "pi", "Claude Code");

        var roles = flat.Entries.Select(e => e.Role!.Value).ToList();
        Assert.Equal(DeveRole.User, roles[0]);
        Assert.True(roles.Zip(roles.Skip(1)).All(pair => pair.First != pair.Second), "user and assistant alternate");
        Assert.All(flat.Entries, e => Assert.All(e.Content!, b => Assert.Contains(b.Type, new[] { "text", "image" })));

        var text = string.Join("\n", flat.Entries.Select(e => e.PlainText()));
        Assert.Contains("moved from pi to Claude Code", text);
        Assert.Contains("Make the maze faster", text);
        Assert.Contains("▸ ran `dotnet test`", text);
        Assert.Contains("Passed! 18 tests", text);
        Assert.DoesNotContain("Look at the tests first", text); // thinking stays behind
        Assert.Contains(flat.Entries[0].Content!, b => b.Type == "image");

        // Claude Code reads what we write back as a normal conversation of the same messages.
        var claude = _claude.Read(_claude.Write(flat, "11111111-2222-3333-4444-555555555555", "/home/remoteman/work/maze"));
        Assert.Equal(roles, claude.ActivePath().Select(e => e.Role!.Value));
        Assert.Contains("▸ ran `dotnet test`", string.Join("\n", claude.Entries.Select(e => e.PlainText())));
    }

    [Fact]
    public void Flatten_ClaudeToCodex_RoundTripsThroughCodex()
    {
        var flat = SessionFlattener.Flatten(_claude.Read(ClaudeSession), "Claude Code", "Codex");
        var codex = _codex.Read(_codex.Write(flat, "0199ffff-0000-7000-8000-000000000002", "/home/remoteman/work/maze"));

        var messages = codex.ActivePath().Where(e => e.Kind == DeveEntryKind.Message).ToList();
        Assert.Equal([DeveRole.User, DeveRole.Assistant], messages.Select(m => m.Role!.Value));
        Assert.Contains("hello there", messages[0].PlainText());
        Assert.Contains("▸ ran `dotnet test`", messages[1].PlainText());
        Assert.Contains("All 18 tests pass.", messages[1].PlainText());
    }

    [Fact]
    public void Flatten_Twice_KeepsOneNote()
    {
        var once = SessionFlattener.Flatten(new PiSessionFormat().Read(PiSample), "pi", "Codex");
        var twice = SessionFlattener.Flatten(once, "Codex", "Claude Code");

        var first = twice.Entries[0].PlainText();
        Assert.Contains("moved from Codex to Claude Code", first);
        Assert.DoesNotContain("moved from pi to Codex", first);
        Assert.Contains("Make the maze faster", first);
    }

    [Fact]
    public void Flatten_KeepsTheMostRecentPart_WhenTooLong()
    {
        var pi = new PiSessionFormat().Read(PiSample);
        var flat = SessionFlattener.Flatten(pi, "pi", "Codex", maxCharacters: 60);

        Assert.Equal(DeveRole.User, flat.Entries[0].Role);
        Assert.Contains("earlier messages were left out", flat.Entries[0].PlainText());
        Assert.Contains("Anything else?", string.Join("\n", flat.Entries.Select(e => e.PlainText())));
    }

    [Fact]
    public void Flatten_CodexCodeMode_ShowsCommandsAndTheirOutput()
    {
        // Codex's code mode: one "exec" call runs JavaScript that calls the real tools and prints their JSON results.
        const string script = """
            const patch="*** Begin Patch\n*** Update File: /w/maze/Maze.cs\n@@\n-a\n+b\n*** Add File: /w/maze/New.cs\n+x\n*** End Patch";
            text(await tools.apply_patch(patch));
            const r=await Promise.allSettled([tools.exec_command({cmd:"git status --short && grep -n 'it\u0027s' a.txt","max_output_tokens":1000}),
              tools.exec_command({cmd:'dotnet test'}),tools.write_stdin({session_id:5,chars:""})]);
            r.forEach((x,i)=>text(JSON.stringify({i,result:x})));
            """;
        var outputs = new JsonArray(
            new JsonObject { ["type"] = "input_text", ["text"] = "Script completed\nWall time 1.2 seconds\nOutput:\n" },
            new JsonObject { ["type"] = "input_text", ["text"] = "{}" },
            new JsonObject { ["type"] = "input_text", ["text"] = """{"i":0,"result":{"status":"fulfilled","value":{"chunk_id":"a1","wall_time_seconds":0.1,"exit_code":0,"original_token_count":3,"output":" M Maze.cs\n"}}}""" },
            new JsonObject { ["type"] = "input_text", ["text"] = """{"i":1,"result":{"status":"fulfilled","value":{"chunk_id":"a2","wall_time_seconds":4.0,"exit_code":1,"original_token_count":3,"output":"Failed! 1 test"}}}""" },
            new JsonObject { ["type"] = "input_text", ["text"] = """{"i":2,"result":{"status":"fulfilled","value":{"chunk_id":"a3","wall_time_seconds":1.0,"session_id":5,"original_token_count":0,"output":""}}}""" });
        string Line(JsonObject payload) => new JsonObject { ["timestamp"] = "2026-09-26T09:00:04.000Z", ["type"] = "response_item", ["payload"] = payload }.ToJsonString();
        var session = string.Join('\n',
            CodexSession.Split('\n').Take(5).Append(Line(new JsonObject { ["type"] = "custom_tool_call", ["call_id"] = "call_2", ["name"] = "exec", ["input"] = script }))
                .Append(Line(new JsonObject { ["type"] = "custom_tool_call_output", ["call_id"] = "call_2", ["output"] = outputs }))) + "\n";

        var text = SessionFlattener.Flatten(_codex.Read(session), "Codex", "Claude Code").Entries[1].PlainText();

        Assert.Contains("▸ edited /w/maze/Maze.cs\n▸ created /w/maze/New.cs\n▸ ran `git status --short && grep -n 'it's' a.txt`\n▸ ran `dotnet test`\n▸ waited for a running command", text);
        Assert.Contains(" M Maze.cs\n\n(exit code 1)\nFailed! 1 test\n\n(still running)", text);
        Assert.Contains("output:\n```\n M Maze.cs", text);
        Assert.DoesNotContain("Script completed", text);
        Assert.DoesNotContain("chunk_id", text);
        Assert.DoesNotContain("\\u00", text);
    }

    [Fact]
    public void Flatten_OtherTools_AreReadableJson()
    {
        var session = _codex.Read(CodexSession.Replace(
            """{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"dotnet test\"}","call_id":"call_1"}""",
            """{"type":"function_call","name":"request_user_input","arguments":"{\"title\":\"Use <b> & 'quotes'?\"}","call_id":"call_1"}"""));

        var text = SessionFlattener.Flatten(session, "Codex", "Claude Code").Entries[1].PlainText();

        Assert.Contains("""▸ request_user_input {"title":"Use <b> & 'quotes'?"}""", text);
    }

    [Fact]
    public void Flatten_Screenshots_DoNotPushOutText_AndOnlyRecentOnesAreKept()
    {
        var session = new DeveSession { CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        for (var i = 0; i < 25; i++)
        {
            // A phone screenshot is about half a megabyte of base64.
            Add(DeveRole.User, DeveBlock.OfText($"screenshot {i}"), new DeveBlock { Type = "image", MimeType = "image/jpeg", Data = new string('A', 500_000) });
            Add(DeveRole.Assistant, DeveBlock.OfText($"reply {i}"));
        }

        var flat = SessionFlattener.Flatten(session, "Codex", "Claude Code");

        Assert.Contains("screenshot 0", flat.Entries[0].PlainText());
        Assert.Contains("(An image was left out.)", flat.Entries[0].PlainText());
        Assert.DoesNotContain("left out to fit", flat.Entries[0].PlainText());
        Assert.Equal(20, flat.Entries.Sum(e => e.Content!.Count(b => b.Type == "image")));
        Assert.Single(flat.Entries[^2].Content!, b => b.Type == "image");

        void Add(DeveRole role, params DeveBlock[] content)
        {
            var id = Guid.NewGuid().ToString("N");
            session.Entries.Add(new DeveEntry { Id = id, ParentId = session.LeafId, Timestamp = DateTime.UtcNow, Kind = DeveEntryKind.Message, Role = role, Content = [.. content] });
            session.LeafId = id;
        }
    }

    [Fact]
    public void Paths_MatchWhereTheHarnessesLook()
    {
        Assert.Equal("~/.claude/projects/-home-remoteman-work-my-app/abc.jsonl", _claude.GetPath("abc", "/home/remoteman/work/my.app", DateTime.UtcNow));
        Assert.Equal("~/.codex/sessions/2026/09/26/rollout-2026-09-26T09-10-00-abc.jsonl",
            _codex.GetPath("abc", "/x", new DateTime(2026, 9, 26, 9, 10, 0, DateTimeKind.Utc)));
    }

    private static readonly string PiSample = string.Join('\n',
        """{"type":"session","version":3,"id":"orig-id","timestamp":"2026-09-25T10:00:00.000Z","cwd":"/home/remoteman/work/maze"}""",
        """{"type":"message","id":"u1","parentId":null,"timestamp":"2026-09-25T10:00:01.000Z","message":{"role":"user","content":[{"type":"text","text":"Make the maze faster"},{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}],"timestamp":1790330401000}}""",
        """{"type":"message","id":"a1","parentId":"u1","timestamp":"2026-09-25T10:00:02.000Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Look at the tests first.","thinkingSignature":"sig=="},{"type":"text","text":"Running the tests."},{"type":"toolCall","id":"toolu_1","name":"bash","arguments":{"command":"dotnet test"}}],"provider":"anthropic","model":"claude-opus-4-5","stopReason":"toolUse","timestamp":1790330402000}}""",
        """{"type":"message","id":"t1","parentId":"a1","timestamp":"2026-09-25T10:00:03.000Z","message":{"role":"toolResult","toolCallId":"toolu_1","toolName":"bash","content":[{"type":"text","text":"Passed! 18 tests"}],"isError":false,"timestamp":1790330403000}}""",
        """{"type":"message","id":"a2","parentId":"t1","timestamp":"2026-09-25T10:00:06.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Done: it is 2x faster."}],"provider":"anthropic","model":"claude-opus-4-5","stopReason":"stop","timestamp":1790330406000}}""",
        """{"type":"message","id":"u2","parentId":"a2","timestamp":"2026-09-25T10:00:07.000Z","message":{"role":"user","content":"Thanks","timestamp":1790330407000}}""",
        """{"type":"message","id":"a3","parentId":"u2","timestamp":"2026-09-25T10:00:08.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Anything else?"}],"provider":"anthropic","model":"claude-opus-4-5","stopReason":"stop","timestamp":1790330408000}}""") + "\n";
}
