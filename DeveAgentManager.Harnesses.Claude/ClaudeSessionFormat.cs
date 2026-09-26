using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using static DeveAgentManager.Core.Sessions.JsonLines;

namespace DeveAgentManager.Harnesses.Claude;

/// <summary>
/// Claude Code's session files (~/.claude/projects/&lt;folder&gt;/&lt;uuid&gt;.jsonl) to and from DeveSession. Every line
/// is kept in <see cref="DeveEntry.Native"/>, so a Claude session written back is the same session with its id and
/// folder updated. Messages from other harnesses are written as plain user and assistant text (see
/// <see cref="SessionFlattener"/>), which Claude Code resumes like its own.
/// </summary>
public sealed class ClaudeSessionFormat : ISessionFormat
{
    /// <summary>The Claude Code version written into converted lines (any recent version reads them).</summary>
    private const string WrittenVersion = "2.1.283";

    private const string FallbackModel = "claude-sonnet-4-5";

    public string HarnessId => ClaudeHarness.HarnessId;

    public string LocateCommand(string sessionId) =>
        $"f=$(ls -1 \"$HOME\"/.claude/projects/*/{ShellQuote.Quote(sessionId)}.jsonl 2>/dev/null | head -n1); " +
        "if [ -n \"$f\" ]; then printf '%s %s\\n' \"$(stat -c %s \"$f\")\" \"$f\"; fi";

    public string GetPath(string sessionId, string workingDirectory, DateTime createdAt) =>
        $"~/.claude/projects/{ProjectDirectoryName(workingDirectory)}/{sessionId}.jsonl";

    /// <summary>Claude Code's folder name for a working directory: every character but letters and digits becomes '-'.</summary>
    public static string ProjectDirectoryName(string workingDirectory) =>
        new(workingDirectory.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    public DeveSession Read(string content)
    {
        var session = new DeveSession();
        string? customTitle = null;
        string? autoTitle = null;
        string? lastTreeEntry = null;
        var index = 0;
        foreach (var line in Parse(content))
        {
            index++;
            var uuid = Str(line, "uuid");
            var timestamp = Time(line["timestamp"]);
            if (session.Sources.Count == 0 && Str(line, "sessionId") is { } sessionId)
            {
                session.Sources.Add(new DeveSource { Harness = HarnessId, SessionId = sessionId });
            }

            if (session.Cwd is null && Str(line, "cwd") is { } cwd)
            {
                session.Cwd = cwd;
            }

            if (timestamp is { } time)
            {
                if (session.CreatedAt == default)
                {
                    session.CreatedAt = time;
                }

                session.UpdatedAt = time;
            }

            var entry = new DeveEntry
            {
                Id = uuid ?? $"claude-line-{index}",
                ParentId = uuid is null ? null : Str(line, "parentUuid"),
                Timestamp = timestamp ?? session.UpdatedAt,
                Harness = HarnessId,
                Native = line,
                Kind = DeveEntryKind.Native,
            };

            switch (Str(line, "type"))
            {
                case "user" when !True(line, "isMeta") && line["message"] is JsonObject message:
                    ReadUser(entry, message, True(line, "isCompactSummary"));
                    break;
                case "assistant" when line["message"] is JsonObject message:
                    ReadAssistant(entry, message);
                    break;
                case "summary":
                    // Claude Code's automatic title for the conversation.
                    autoTitle = Str(line, "summary") ?? autoTitle;
                    break;
                case "custom-title":
                    customTitle = Str(line, "customTitle") ?? customTitle;
                    break;
            }

            session.Entries.Add(entry);
            if (uuid is not null)
            {
                lastTreeEntry = uuid;
            }
        }

        session.LeafId = lastTreeEntry;
        session.Title = customTitle ?? autoTitle ?? session.FirstPrompt();
        return session;
    }

    private static void ReadUser(DeveEntry entry, JsonObject message, bool compactSummary)
    {
        var blocks = message["content"] switch
        {
            JsonValue text when text.GetValueKind() == JsonValueKind.String => [DeveBlock.OfText(text.GetValue<string>())],
            JsonArray array => ReadBlocks(array),
            _ => [],
        };

        if (compactSummary)
        {
            entry.Kind = DeveEntryKind.Summary;
            entry.SummaryKind = "compaction";
            entry.Text = string.Join("\n", blocks.Where(b => b.Type == "text").Select(b => b.Text));
            return;
        }

        entry.Kind = DeveEntryKind.Message;
        // Tool results travel in user messages; on their own they are the tool's side of the conversation.
        entry.Role = blocks.Count > 0 && blocks.All(b => b.Type == "toolResult") ? DeveRole.Tool : DeveRole.User;
        entry.Content = blocks;
    }

    private static void ReadAssistant(DeveEntry entry, JsonObject message)
    {
        entry.Kind = DeveEntryKind.Message;
        entry.Role = DeveRole.Assistant;
        var model = Str(message, "model");
        entry.Model = new DeveModel { Provider = "anthropic", Id = model };
        entry.Content = message["content"] is JsonArray array ? ReadBlocks(array) : [];
        entry.StopReason = Str(message, "stop_reason");
        if (message["usage"] is JsonObject usage)
        {
            entry.Usage = new DeveUsage
            {
                Input = Long(usage["input_tokens"]),
                Output = Long(usage["output_tokens"]),
                CacheRead = Long(usage["cache_read_input_tokens"]),
                CacheWrite = Long(usage["cache_creation_input_tokens"]),
            };
        }

        if (model == "<synthetic>")
        {
            // Claude Code's own messages, e.g. an API error shown as the answer.
            entry.Error = string.Join("\n", entry.Content.Where(b => b.Type == "text").Select(b => b.Text));
        }
    }

    private static List<DeveBlock> ReadBlocks(JsonArray array)
    {
        var blocks = new List<DeveBlock>();
        foreach (var block in array.OfType<JsonObject>())
        {
            switch (Str(block, "type"))
            {
                case "text":
                    blocks.Add(DeveBlock.OfText(Str(block, "text") ?? ""));
                    break;
                case "image" when block["source"] is JsonObject source:
                    blocks.Add(new DeveBlock { Type = "image", MimeType = Str(source, "media_type"), Data = Str(source, "data") });
                    break;
                case "thinking":
                    var signature = Str(block, "signature");
                    blocks.Add(new DeveBlock
                    {
                        Type = "thinking",
                        Text = Str(block, "thinking"),
                        Signature = string.IsNullOrEmpty(signature) ? null : new DeveSignature { Harness = ClaudeHarness.HarnessId, Provider = "anthropic", Value = signature },
                    });
                    break;
                case "redacted_thinking":
                    blocks.Add(new DeveBlock { Type = "thinking", Redacted = true });
                    break;
                case "tool_use":
                    var name = Str(block, "name");
                    blocks.Add(new DeveBlock
                    {
                        Type = "toolCall",
                        Id = Str(block, "id"),
                        Name = name,
                        ToolKind = ToolKinds.For(name),
                        Arguments = block["input"]?.DeepClone(),
                    });
                    break;
                case "tool_result":
                    blocks.Add(new DeveBlock
                    {
                        Type = "toolResult",
                        ToolCallId = Str(block, "tool_use_id"),
                        IsError = True(block, "is_error"),
                        Content = block["content"] switch
                        {
                            JsonValue text when text.GetValueKind() == JsonValueKind.String => [DeveBlock.OfText(text.GetValue<string>())],
                            JsonArray inner => ReadBlocks(inner),
                            _ => [],
                        },
                    });
                    break;
            }
        }

        return blocks;
    }

    public string Write(DeveSession session, string sessionId, string workingDirectory)
    {
        var output = new StringBuilder();
        // Children of entries that are not written (another harness's extras) attach to the last line written before.
        var written = new Dictionary<string, string>();
        string? previous = null;
        foreach (var entry in session.Entries)
        {
            if (entry.Harness == HarnessId && entry.Native is not null)
            {
                var native = (JsonObject)entry.Native.DeepClone();
                if (native.ContainsKey("sessionId"))
                {
                    native["sessionId"] = sessionId;
                }

                if (native.ContainsKey("cwd"))
                {
                    native["cwd"] = workingDirectory;
                }

                Append(output, native);
                if (Str(native, "uuid") is { } uuid)
                {
                    written[entry.Id] = uuid;
                    previous = uuid;
                }

                continue;
            }

            var line = WriteForeign(entry, sessionId, workingDirectory);
            if (line is null)
            {
                continue;
            }

            var parent = entry.ParentId is not null && written.TryGetValue(entry.ParentId, out var parentUuid) ? parentUuid : previous;
            line["parentUuid"] = parent;
            var id = Str(line, "uuid")!;
            written[entry.Id] = id;
            previous = id;
            Append(output, line);
        }

        return output.ToString();
    }

    /// <summary>A Claude line for a user or assistant message from another harness (text and images).</summary>
    private static JsonObject? WriteForeign(DeveEntry entry, string sessionId, string workingDirectory)
    {
        if (entry.Kind != DeveEntryKind.Message || entry.Role is not (DeveRole.User or DeveRole.Assistant))
        {
            return null;
        }

        var content = new JsonArray();
        foreach (var block in entry.Content ?? [])
        {
            if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = block.Text });
            }
            else if (block.Type == "image" && entry.Role == DeveRole.User && !string.IsNullOrEmpty(block.Data))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = block.MimeType ?? "image/png", ["data"] = block.Data },
                });
            }
        }

        if (content.Count == 0)
        {
            return null;
        }

        var line = new JsonObject
        {
            ["parentUuid"] = null,
            ["isSidechain"] = false,
            ["userType"] = "external",
            ["cwd"] = workingDirectory,
            ["sessionId"] = sessionId,
            ["version"] = WrittenVersion,
            ["gitBranch"] = "",
            ["type"] = entry.Role == DeveRole.User ? "user" : "assistant",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["timestamp"] = Iso(entry.Timestamp == default ? DateTime.UtcNow : entry.Timestamp),
        };

        if (entry.Role == DeveRole.User)
        {
            line["message"] = new JsonObject { ["role"] = "user", ["content"] = content };
        }
        else
        {
            var model = entry.Model?.Provider == "anthropic" && entry.Model.Id is { Length: > 0 } id && id != "<synthetic>" ? id : FallbackModel;
            line["message"] = new JsonObject
            {
                ["id"] = "msg_dam_" + Guid.NewGuid().ToString("N"),
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = content,
                ["stop_reason"] = "end_turn",
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
            };
        }

        return line;
    }
}
