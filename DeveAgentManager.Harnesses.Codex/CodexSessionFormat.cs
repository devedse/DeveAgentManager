using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using static DeveAgentManager.Core.Sessions.JsonLines;

namespace DeveAgentManager.Harnesses.Codex;

/// <summary>
/// Codex's session files (~/.codex/sessions/YYYY/MM/DD/rollout-&lt;time&gt;-&lt;uuid&gt;.jsonl, one
/// <c>{timestamp, type, payload}</c> per line) to and from DeveSession. Every line is kept in
/// <see cref="DeveEntry.Native"/>, so a Codex session written back is the same session with its id and folder updated.
/// The history is linear, so each entry's parent is the one before it. Messages from other harnesses are written as
/// plain user and assistant text (see <see cref="SessionFlattener"/>), which <c>codex resume</c> continues like its own.
/// </summary>
public sealed class CodexSessionFormat : ISessionFormat
{
    /// <summary>User messages that are context Codex adds itself (environment, AGENTS.md), not something the user typed.</summary>
    private static readonly string[] ContextPrefixes = ["<environment_context>", "<user_instructions>", "# AGENTS.md instructions", "<INSTRUCTIONS>", "<permissions instructions>"];

    public string HarnessId => CodexHarness.HarnessId;

    public string LocateCommand(string sessionId) =>
        $"f=$(ls -1t \"$HOME\"/.codex/sessions/*/*/*/rollout-*-{ShellQuote.Quote(sessionId)}.jsonl \"$HOME\"/.deveagentmanager/agent-runtime/*/*/codex/sessions/*/*/*/rollout-*-{ShellQuote.Quote(sessionId)}.jsonl 2>/dev/null | head -n1); " +
        "if [ -n \"$f\" ]; then printf '%s %s\\n' \"$(stat -L -c %s \"$f\")\" \"$f\"; fi";

    public string GetPath(string sessionId, string workingDirectory, DateTime createdAt)
    {
        var time = createdAt.ToUniversalTime();
        return $"~/.codex/sessions/{time:yyyy}/{time:MM}/{time:dd}/rollout-{time:yyyy-MM-ddTHH-mm-ss}-{sessionId}.jsonl";
    }

    public bool SupportsIncrementalRead => true;
    public DeveSession Read(string content) { using var reader = new StringReader(content); return Read(reader); }
    public DeveSession Read(TextReader reader)
    {
        var cursor = new SessionReadCursor();
        AppendLines(cursor, JsonLines.Parse(reader));
        return cursor.Session;
    }

    public void AppendLines(SessionReadCursor cursor, IEnumerable<JsonObject> lines)
    {
        var session = cursor.Session;
        string? previous = session.LeafId;
        var index = cursor.LineCount;
        foreach (var line in lines)
        {
            index++;
            var timestamp = Time(line["timestamp"]);
            var payload = line["payload"] as JsonObject;
            if (timestamp is { } time)
            {
                if (session.CreatedAt == default)
                {
                    session.CreatedAt = time;
                }

                session.UpdatedAt = time;
            }

            if (Str(line, "type") == "session_meta" && payload is not null)
            {
                session.Sources.Add(new DeveSource { Harness = HarnessId, SessionId = Str(payload, "id"), Header = line });
                session.Cwd = Str(payload, "cwd");
                session.CreatedAt = Time(payload["timestamp"]) ?? session.CreatedAt;
                continue;
            }

            var entry = new DeveEntry
            {
                Id = $"codex-{index}",
                ParentId = previous,
                Timestamp = timestamp ?? session.UpdatedAt,
                Harness = HarnessId,
                Native = line,
                Kind = DeveEntryKind.Native,
            };

            switch (Str(line, "type"))
            {
                case "response_item" when payload is not null:
                    ReadResponseItem(entry, payload);
                    break;
                case "compacted" when payload is not null:
                    entry.Kind = DeveEntryKind.Summary;
                    entry.SummaryKind = "compaction";
                    entry.Text = Str(payload, "message");
                    break;
                // event_msg (UI events: the user's and agent's messages again, token counts), turn_context and others
                // are the harness's own bookkeeping.
            }

            session.Entries.Add(entry);
            previous = entry.Id;
        }

        session.LeafId = previous;
        session.Title = session.FirstPrompt();
        cursor.LineCount = index;
    }

    private static void ReadResponseItem(DeveEntry entry, JsonObject item)
    {
        switch (Str(item, "type"))
        {
            case "message":
                var role = Str(item, "role");
                var blocks = ReadContent(item["content"]);
                var text = string.Join("\n", blocks.Where(b => b.Type == "text").Select(b => b.Text));
                if (role == "user" && ContextPrefixes.Any(p => text.TrimStart().StartsWith(p, StringComparison.Ordinal)))
                {
                    return;
                }

                entry.Kind = DeveEntryKind.Message;
                entry.Role = role switch
                {
                    "user" => DeveRole.User,
                    "assistant" => DeveRole.Assistant,
                    _ => DeveRole.System,
                };
                entry.Content = blocks;
                if (entry.Role == DeveRole.Assistant)
                {
                    entry.Model = new DeveModel { Provider = "openai" };
                }

                break;
            case "reasoning":
                var summary = string.Join("\n\n", (item["summary"] as JsonArray ?? []).OfType<JsonObject>().Select(s => Str(s, "text")).Where(t => !string.IsNullOrEmpty(t)));
                entry.Kind = DeveEntryKind.Message;
                entry.Role = DeveRole.Assistant;
                entry.Model = new DeveModel { Provider = "openai" };
                // The reasoning itself is encrypted for OpenAI; the readable part is its summary.
                entry.Content = [new DeveBlock { Type = "thinking", Text = summary, Redacted = summary.Length == 0 ? true : null }];
                break;
            case "function_call":
                AddCall(entry, Str(item, "call_id"), Str(item, "name"), ParseArguments(Str(item, "arguments")));
                break;
            case "custom_tool_call":
                AddCall(entry, Str(item, "call_id"), Str(item, "name"), new JsonObject { ["input"] = Str(item, "input") });
                break;
            case "local_shell_call":
                var command = (item["action"] as JsonObject)?["command"];
                var joined = command is JsonArray parts ? string.Join(' ', parts.Select(p => p?.ToString())) : command?.ToString();
                AddCall(entry, Str(item, "call_id") ?? Str(item, "id"), "local_shell", new JsonObject { ["command"] = joined });
                break;
            case "web_search_call":
                AddCall(entry, Str(item, "id"), "web_search", (item["action"] as JsonObject)?.DeepClone() as JsonObject ?? []);
                break;
            case "function_call_output" or "custom_tool_call_output":
                entry.Kind = DeveEntryKind.Message;
                entry.Role = DeveRole.Tool;
                entry.Content =
                [
                    new DeveBlock
                    {
                        Type = "toolResult",
                        ToolCallId = Str(item, "call_id"),
                        ToolName = Str(item, "name"),
                        Content = ReadOutput(item["output"]),
                    },
                ];
                break;
        }
    }

    private static void AddCall(DeveEntry entry, string? callId, string? name, JsonNode? arguments)
    {
        entry.Kind = DeveEntryKind.Message;
        entry.Role = DeveRole.Assistant;
        entry.Model = new DeveModel { Provider = "openai" };
        entry.Content = [new DeveBlock { Type = "toolCall", Id = callId, Name = name, ToolKind = ToolKinds.For(name), Arguments = arguments }];
    }

    private static JsonNode? ParseArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments);
        }
        catch (JsonException)
        {
            return new JsonObject { ["input"] = arguments };
        }
    }

    private static List<DeveBlock> ReadContent(JsonNode? content)
    {
        var blocks = new List<DeveBlock>();
        foreach (var block in content as JsonArray ?? [])
        {
            if (block is not JsonObject item)
            {
                continue;
            }

            switch (Str(item, "type"))
            {
                case "input_text" or "output_text" or "text":
                    blocks.Add(DeveBlock.OfText(Str(item, "text") ?? ""));
                    break;
                case "input_image" when Str(item, "image_url") is { } url && url.StartsWith("data:", StringComparison.Ordinal) && url.IndexOf(";base64,", StringComparison.Ordinal) is var marker and > 0:
                    blocks.Add(new DeveBlock { Type = "image", MimeType = url[5..marker], Data = url[(marker + 8)..] });
                    break;
            }
        }

        return blocks;
    }

    /// <summary>A tool's output: a string, a list of content items, or an object with <c>content</c>.</summary>
    private static List<DeveBlock> ReadOutput(JsonNode? output) => output switch
    {
        JsonValue text when text.GetValueKind() == JsonValueKind.String => [DeveBlock.OfText(text.GetValue<string>())],
        JsonArray array => ReadContent(array),
        JsonObject item when item["content"] is JsonValue inner && inner.GetValueKind() == JsonValueKind.String => [DeveBlock.OfText(inner.GetValue<string>())],
        JsonObject item when item["content"] is JsonArray inner => ReadContent(inner),
        _ => [],
    };

    public string Write(DeveSession session, string sessionId, string workingDirectory)
    {
        var output = new StringBuilder();
        var header = session.Sources.FirstOrDefault(s => s.Harness == HarnessId)?.Header?.DeepClone() as JsonObject
            ?? new JsonObject
            {
                ["timestamp"] = Iso(session.CreatedAt == default ? DateTime.UtcNow : session.CreatedAt),
                ["type"] = "session_meta",
                ["payload"] = new JsonObject
                {
                    ["timestamp"] = Iso(session.CreatedAt == default ? DateTime.UtcNow : session.CreatedAt),
                    ["originator"] = "codex_cli_rs",
                    ["cli_version"] = CodexHarness.WrittenVersion,
                    ["source"] = "cli",
                    ["model_provider"] = "openai",
                },
            };
        if (header["payload"] is JsonObject meta)
        {
            meta["id"] = sessionId;
            meta["cwd"] = workingDirectory;
            if (meta.ContainsKey("session_id"))
            {
                meta["session_id"] = sessionId;
            }

            // A moved session is its own thread now, not a fork of the original.
            meta.Remove("forked_from_id");
            meta.Remove("parent_thread_id");
        }

        Append(output, header);
        foreach (var entry in session.Entries)
        {
            if (entry.Harness == HarnessId && entry.Native is not null)
            {
                Append(output, (JsonObject)entry.Native.DeepClone());
                continue;
            }

            foreach (var line in WriteForeign(entry))
            {
                Append(output, line);
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Codex lines for a user or assistant message from another harness: the message the model sees
    /// (<c>response_item</c>) and the one the terminal shows when resuming (<c>event_msg</c>).
    /// </summary>
    private static IEnumerable<JsonObject> WriteForeign(DeveEntry entry)
    {
        if (entry.Kind != DeveEntryKind.Message || entry.Role is not (DeveRole.User or DeveRole.Assistant))
        {
            yield break;
        }

        var user = entry.Role == DeveRole.User;
        var text = string.Join("\n\n", (entry.Content ?? []).Where(b => b.Type == "text" && !string.IsNullOrEmpty(b.Text)).Select(b => b.Text));
        var images = user
            ? (entry.Content ?? []).Where(b => b.Type == "image" && !string.IsNullOrEmpty(b.Data)).Select(b => $"data:{b.MimeType ?? "image/png"};base64,{b.Data}").ToList()
            : [];
        if (text.Length == 0 && images.Count == 0)
        {
            yield break;
        }

        var timestamp = Iso(entry.Timestamp == default ? DateTime.UtcNow : entry.Timestamp);
        var content = new JsonArray();
        if (text.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = user ? "input_text" : "output_text", ["text"] = text });
        }

        foreach (var image in images)
        {
            content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image });
        }

        yield return new JsonObject
        {
            ["timestamp"] = timestamp,
            ["type"] = "response_item",
            ["payload"] = new JsonObject { ["type"] = "message", ["role"] = user ? "user" : "assistant", ["content"] = content },
        };

        var shown = new JsonObject { ["type"] = user ? "user_message" : "agent_message", ["message"] = text };
        if (user && images.Count > 0)
        {
            shown["images"] = new JsonArray(images.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        }

        yield return new JsonObject { ["timestamp"] = timestamp, ["type"] = "event_msg", ["payload"] = shown };
    }
}
