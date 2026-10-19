using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Harnesses.Grok;

/// <summary>The authoritative ACP updates.jsonl stream, including xAI replay extensions.</summary>
public sealed class GrokSessionFormat : ISessionFormat
{
    public string HarnessId => GrokHarness.HarnessId;
    public bool SupportsIncrementalRead => true;
    public bool HasCompanionFiles => true;
    public string LocateCommand(string sessionId) =>
        $"f=$(ls -1 \"$HOME\"/.deveagentmanager/agent-runtime/*/*/grok/sessions/*/{Guid.Parse(sessionId):D}/updates.jsonl \"$HOME\"/.grok/sessions/*/{Guid.Parse(sessionId):D}/updates.jsonl 2>/dev/null | head -n1); "
        + "if [ -n \"$f\" ]; then printf '%s %s\\n' \"$(stat -L -c %s \"$f\")\" \"$f\"; fi";

    public string GetPath(string sessionId, string workingDirectory, DateTime createdAt) =>
        $"~/.grok/sessions/{SessionDirectoryName(workingDirectory)}/{Guid.Parse(sessionId):D}/updates.jsonl";

    public static string SessionDirectoryName(string cwd)
    {
        var encoded = Uri.EscapeDataString(cwd);
        if (encoded.Length <= 255) return encoded;
        var leaf = cwd.TrimEnd('/').Split('/').LastOrDefault() ?? "workspace";
        var slug = System.Text.RegularExpressions.Regex.Replace(leaf.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40];
        return (slug.Length == 0 ? "workspace" : slug) + "-" + Blake3.Hasher.Hash(Encoding.UTF8.GetBytes(cwd)).ToString()[..16];
    }

    public DeveSession Read(string content)
    {
        if (content.TrimStart().StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(content) is JsonObject bundle && (string?)bundle["format"] == "grok-session")
                {
                    if ((int?)bundle["version"] != 1) throw new FormatException("Unsupported Grok session bundle version.");
                    var session = Read((string?)bundle["history"] ?? "");
                    ApplyCompanionFiles(session, (bundle["files"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()));
                    return session;
                }
            }
            catch (System.Text.Json.JsonException) { } // Native JSONL has multiple JSON roots.
        }
        using var reader = new StringReader(content); return Read(reader);
    }
    public DeveSession Read(TextReader reader)
    {
        var cursor = new SessionReadCursor();
        AppendLines(cursor, JsonLines.Parse(reader));
        return cursor.Session;
    }

    public void AppendLines(SessionReadCursor cursor, IEnumerable<JsonObject> lines)
    {
        var session = cursor.Session;
        foreach (var line in lines)
        {
            var parameters = line["params"] as JsonObject ?? line;
            if (parameters["update"] is not JsonObject update || (string?)parameters["sessionId"] is not { Length: > 0 } id) continue;
            var type = (string?)update["sessionUpdate"];
            if (type is null) continue;
            if (session.Sources.Count == 0) session.Sources.Add(new() { Harness = HarnessId, SessionId = id });
            var index = cursor.LineCount++;
            var timestamp = Timestamp(line, parameters);
            var entry = new DeveEntry { Id = "grok-" + index, ParentId = session.LeafId,
                Timestamp = timestamp, Harness = HarnessId, Kind = DeveEntryKind.Native, Native = line };
            if (session.CreatedAt == default) session.CreatedAt = timestamp;
            var role = type switch { "user_message_chunk" => DeveRole.User, "agent_message_chunk" or "agent_thought_chunk" => DeveRole.Assistant, _ => (DeveRole?)null };
            if (role is not null && update["content"] is JsonObject content)
            {
                var block = ReadBlock(content);
                if (type == "agent_thought_chunk" && block.Type == "text") block.Type = "thinking";
                // Each native line remains in the tree. Text chunks belonging to one message are displayed together.
                var key = role + ":" + ((string?)parameters["_meta"]?["damMessageId"] ?? "")
                    + (role == DeveRole.User ? ":" + update["_meta"]?["promptIndex"]?.ToJsonString() : "");
                DeveEntry? previous = null;
                if (cursor.ParserState.GetValueOrDefault("message-key") == key
                    && int.TryParse(cursor.ParserState.GetValueOrDefault("message-index"), out var previousIndex)
                    && previousIndex < session.Entries.Count) previous = session.Entries[previousIndex];
                if (previous is not null)
                {
                    if (previous.Content is { } blocks)
                    {
                        if (blocks.LastOrDefault() is { } last && last.Type == block.Type && block.Type is "text" or "thinking") last.Text += block.Text;
                        else blocks.Add(block);
                    }
                    else previous.Text = SessionReadCursor.Snippet([(previous.Text ?? "")
                        + (cursor.ParserState.GetValueOrDefault("message-space") == "1" ? " " : "") + (block.Text ?? "")], role == DeveRole.User ? 80 : 281);
                    previous.Timestamp = timestamp;
                }
                else
                {
                    entry.Kind = DeveEntryKind.Message; entry.Role = role; entry.Content = [block];
                    cursor.ParserState["message-key"] = key;
                    cursor.ParserState["message-index"] = session.Entries.Count.ToString(CultureInfo.InvariantCulture);
                    if (role == DeveRole.User)
                    {
                        var prompt = (int?)update["_meta"]?["promptIndex"]
                            ?? (int.TryParse(cursor.ParserState.GetValueOrDefault("next-prompt"), out var next) ? next : 0);
                        cursor.ParserState["prompt:" + prompt] = entry.ParentId ?? "";
                        cursor.ParserState["next-prompt"] = (prompt + 1).ToString(CultureInfo.InvariantCulture);
                    }
                }
                if (block.Text is { Length: > 0 } chunk) cursor.ParserState["message-space"] = char.IsWhiteSpace(chunk[^1]) ? "1" : "0";
            }
            else
            {
                cursor.ParserState.Remove("message-key");
                if (type == "tool_call")
                {
                    var name = (string?)update["title"] ?? "tool";
                    entry.Kind = DeveEntryKind.Message; entry.Role = DeveRole.Assistant;
                    entry.Content = [new() { Type = "toolCall", Id = (string?)update["toolCallId"], Name = name,
                        ToolKind = ToolKinds.For(name), Arguments = update["rawInput"]?.DeepClone(), CallType = "function" }];
                    if ((string?)update["toolCallId"] is { } callId) cursor.ParserState["tool:" + callId] = session.Entries.Count.ToString(CultureInfo.InvariantCulture);
                }
                else if (type == "tool_call_update")
                {
                    var callId = (string?)update["toolCallId"] ?? "";
                    if (int.TryParse(cursor.ParserState.GetValueOrDefault("tool:" + callId), out var callIndex)
                        && callIndex < session.Entries.Count && session.Entries[callIndex].Content?.FirstOrDefault() is { } call
                        && call.Arguments is null && update["rawInput"] is { } arguments) call.Arguments = arguments.DeepClone();
                    if ((string?)update["status"] is "completed" or "failed" && !cursor.ParserState.ContainsKey("done:" + callId))
                    {
                        entry.Kind = DeveEntryKind.Message; entry.Role = DeveRole.Tool;
                        entry.Content = [new() { Type = "toolResult", ToolCallId = callId,
                            IsError = (string?)update["status"] == "failed", Content = ReadToolContent(update) }];
                        cursor.ParserState.Remove("tool:" + callId);
                        cursor.ParserState["done:" + callId] = "1";
                    }
                }
                else if (type == "compaction_checkpoint")
                {
                    entry.Kind = DeveEntryKind.Event; entry.Event = "native-checkpoint";
                    entry.Text = (string?)update["checkpoint_file"];
                }
                else if (type == "rewind_marker" && (int?)update["target_prompt_index"] is { } target)
                {
                    if (cursor.ParserState.TryGetValue("prompt:" + target, out var parent)) entry.ParentId = parent.Length == 0 ? null : parent;
                    else if (target == 0) entry.ParentId = null;
                    entry.Kind = DeveEntryKind.Event; entry.Event = "rewind"; entry.Text = target.ToString(CultureInfo.InvariantCulture);
                    foreach (var key in cursor.ParserState.Keys.Where(k => k.StartsWith("prompt:", StringComparison.Ordinal)
                        && int.TryParse(k[7..], out var prompt) && prompt >= target).ToArray()) cursor.ParserState.Remove(key);
                    cursor.ParserState["next-prompt"] = target.ToString(CultureInfo.InvariantCulture);
                }
                else if (type == "session_info_update" && (string?)update["title"] is { Length: > 0 } title)
                { cursor.CustomTitle = title; }
                else if (type == "session_summary_generated" && (string?)update["session_summary"] is { Length: > 0 } generated)
                { cursor.AutoTitle = generated; }
            }
            SessionToolMetadata.Read(entry, line);
            if (!cursor.MetadataOnly || entry.Kind != DeveEntryKind.Native)
            {
                session.Entries.Add(entry);
                session.LeafId = entry.Id;
            }
            session.UpdatedAt = timestamp;
        }
        session.Title = cursor.CustomTitle ?? cursor.AutoTitle ?? session.FirstPrompt();
    }

    public string Write(DeveSession session, string sessionId, string workingDirectory)
    {
        var output = new StringBuilder();
        foreach (var entry in session.Entries)
        {
            if (entry.Harness == HarnessId && entry.Native is not null)
            {
                var native = (JsonObject)entry.Native.DeepClone();
                var parameters = native["params"] as JsonObject ?? native;
                parameters["sessionId"] = sessionId;
                Append(output, native);
                continue;
            }
            if (entry.Kind != DeveEntryKind.Message || entry.Role is null or DeveRole.System) continue;
            foreach (var block in entry.Content ?? [])
            {
                JsonObject? update = block.Type switch
                {
                    "text" or "image" when entry.Role is DeveRole.User or DeveRole.Assistant => new()
                    {
                        ["sessionUpdate"] = entry.Role == DeveRole.User ? "user_message_chunk" : "agent_message_chunk",
                        ["content"] = WriteBlock(block),
                    },
                    "toolCall" => new() { ["sessionUpdate"] = "tool_call", ["toolCallId"] = block.Id, ["title"] = block.Name,
                        ["kind"] = "other", ["status"] = "pending", ["rawInput"] = block.Arguments?.DeepClone() },
                    "toolResult" => new() { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = block.ToolCallId,
                        ["status"] = block.IsError == true ? "failed" : "completed",
                        ["content"] = new JsonArray((block.Content ?? []).Select(b => (JsonNode)new JsonObject { ["type"] = "content", ["content"] = WriteBlock(b) }).ToArray()) },
                    _ => null,
                };
                if (update is null) continue;
                var line = new JsonObject { ["timestamp"] = new DateTimeOffset(DateTime.SpecifyKind(entry.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                    ["method"] = "session/update", ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update,
                        ["_meta"] = new JsonObject { ["damMessageId"] = entry.Id } } };
                SessionToolMetadata.Write(new DeveEntry { Content = [block], MigrationKind = entry.MigrationKind }, line);
                Append(output, line);
            }
        }
        return output.ToString();
    }

    public IReadOnlyList<SessionCompanionFile> CompanionFiles(DeveSession metadata)
    {
        List<SessionCompanionFile> files = [new("summary.json"), new("system_prompt.txt"), new("prompt_context.json"), new("tool_definitions.json"),
            new("chat_history.jsonl", Required: metadata.Entries.Any(e => e.Event == "native-checkpoint"), Incremental: true)];
        foreach (var entry in metadata.Entries.Where(e => e.Event == "native-checkpoint"))
        {
            if (entry.Text is not { } path || !ValidCompanion(path)) throw new FormatException("Invalid Grok compaction checkpoint path.");
            if (files.All(f => f.Path != path)) files.Add(new(path, Required: true, Immutable: true));
        }
        return files;
    }

    private static bool ValidCompanion(string path) => path is "summary.json" or "system_prompt.txt" or "prompt_context.json" or "tool_definitions.json" or "chat_history.jsonl"
        || path.StartsWith("compaction_checkpoints/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)
            && Guid.TryParseExact(path["compaction_checkpoints/".Length..^5], "D", out _);

    public void ApplyCompanionFiles(DeveSession session, IReadOnlyDictionary<string, string> contents)
    {
        if (session.Sources.FirstOrDefault(s => s.Harness == HarnessId) is not { } source) return;
        var files = new JsonObject();
        foreach (var (path, content) in contents)
        {
            if (!ValidCompanion(path)) throw new FormatException("Unsupported Grok companion file.");
            files[path] = content;
        }
        foreach (var file in CompanionFiles(session).Where(f => f.Required))
            if (!contents.ContainsKey(file.Path)) throw new IOException("The Grok session is missing a compaction checkpoint.");
        source.Header = new JsonObject { ["files"] = files };
        if (contents.TryGetValue("summary.json", out var text) && JsonNode.Parse(text) is JsonObject summary)
        {
            session.Cwd = (string?)summary["info"]?["cwd"];
            session.Title = (string?)summary["manual_title"] ?? (string?)summary["generated_title"] ?? session.Title;
        }
    }

    public IReadOnlyDictionary<string, string> WriteCompanionFiles(DeveSession session, string sessionId, string workingDirectory)
    {
        var files = (session.Sources.FirstOrDefault(s => s.Harness == HarnessId)?.Header?["files"] as JsonObject ?? [])
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
        foreach (var path in files.Keys) if (!ValidCompanion(path)) throw new FormatException("Unsupported Grok companion file.");
        foreach (var file in CompanionFiles(session).Where(f => f.Required))
            if (!files.ContainsKey(file.Path)) throw new IOException("The Grok session is missing a compaction checkpoint.");
        var summary = files.TryGetValue("summary.json", out var original) ? JsonNode.Parse(original)!.AsObject() : new JsonObject
        {
            ["created_at"] = session.CreatedAt.ToUniversalTime().ToString("O"), ["updated_at"] = session.UpdatedAt.ToUniversalTime().ToString("O"),
            ["num_messages"] = session.Entries.Count, ["num_chat_messages"] = 0, ["chat_format_version"] = 1, ["current_model_id"] = "",
        };
        summary["info"] = new JsonObject { ["id"] = sessionId, ["cwd"] = workingDirectory };
        summary["session_summary"] = session.Title ?? session.FirstPrompt() ?? "Conversation";
        foreach (var key in new[] { "agent_id", "attempt_id", "parent_session_id", "grok_home", "request_id" }) summary.Remove(key);
        files["summary.json"] = summary.ToJsonString();
        return files;
    }

    public string ExportExtension => "json";
    public string ExportContentType => "application/json";
    public string Export(DeveSession session, string sessionId, string workingDirectory) => new JsonObject
    {
        ["format"] = "grok-session", ["version"] = 1, ["history"] = Write(session, sessionId, workingDirectory),
        ["files"] = new JsonObject(WriteCompanionFiles(session, sessionId, workingDirectory).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))),
    }.ToJsonString();

    private static void Append(StringBuilder output, JsonObject line) => output.AppendLine(line.ToJsonString());
    private static DateTime Timestamp(JsonObject line, JsonObject parameters)
    {
        if ((long?)parameters["_meta"]?["agentTimestampMs"] is { } ms) return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return (long?)line["timestamp"] is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UnixEpoch;
    }
    private static DeveBlock ReadBlock(JsonObject content) => (string?)content["type"] == "image"
        ? new() { Type = "image", MimeType = (string?)content["mimeType"], Data = (string?)content["data"] }
        : DeveBlock.OfText((string?)content["text"] ?? content.ToJsonString());
    private static JsonObject WriteBlock(DeveBlock block) => block.Type == "image"
        ? new() { ["type"] = "image", ["mimeType"] = block.MimeType ?? "image/png", ["data"] = block.Data,
            ["uri"] = "data:" + (block.MimeType ?? "image/png") + ";base64," + block.Data }
        : new() { ["type"] = "text", ["text"] = block.Text ?? "" };
    private static List<DeveBlock> ReadToolContent(JsonObject update)
    {
        var blocks = (update["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => c["content"] is JsonObject).Select(c => ReadBlock((JsonObject)c["content"]!)).ToList();
        if (blocks.Count == 0 && update["rawOutput"] is { } raw) blocks.Add(DeveBlock.OfText(raw.ToJsonString()));
        return blocks;
    }
}
