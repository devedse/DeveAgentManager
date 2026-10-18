using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Harnesses.Pi;

/// <summary>
/// pi's session files (JSONL, see pi's docs/session-format.md) to and from DeveSession. Lines are kept verbatim in
/// <see cref="DeveEntry.Native"/>, so a pi session written back is the same session; only the header's id and cwd change.
/// </summary>
public sealed class PiSessionFormat : ISessionFormat
{
    private static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string HarnessId => PiHarness.HarnessId;

    public string LocateCommand(string sessionId) =>
        $"f=$(ls -1 \"$HOME\"/.pi/agent/sessions/*/*_{ShellQuote.Quote(sessionId)}.jsonl 2>/dev/null | head -n1); " +
        "if [ -n \"$f\" ]; then printf '%s %s\\n' \"$(stat -L -c %s \"$f\")\" \"$f\"; fi";

    public string GetPath(string sessionId, string workingDirectory, DateTime createdAt) =>
        $"~/.pi/agent/sessions/{SessionDirectoryName(workingDirectory)}/{createdAt.ToUniversalTime():yyyy-MM-ddTHH-mm-ss-fff}Z_{sessionId}.jsonl";

    /// <summary>pi's folder name for a working directory: "/home/a/b" becomes "--home-a-b--".</summary>
    public static string SessionDirectoryName(string workingDirectory)
    {
        var path = workingDirectory.Length > 0 && workingDirectory[0] is '/' or '\\' ? workingDirectory[1..] : workingDirectory;
        return "--" + path.Replace('/', '-').Replace('\\', '-').Replace(':', '-') + "--";
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
        DateTime? lastTimestamp = session.UpdatedAt == default ? null : session.UpdatedAt;
        foreach (var item in lines)
        {
            if (Str(item, "type") == "session")
            {
                session.Sources.Add(new DeveSource { Harness = HarnessId, SessionId = Str(item, "id"), Header = item });
                session.Cwd = Str(item, "cwd");
                session.CreatedAt = Time(item["timestamp"]) ?? DateTime.UtcNow;
                continue;
            }

            var entry = ReadEntry(item);
            session.Entries.Add(entry);
            lastTimestamp = entry.Timestamp;
            if (entry is { Kind: DeveEntryKind.Event, Event: "title" } && entry.Text is { Length: > 0 } name)
            {
                session.Title = name;
            }
        }

        session.LeafId = session.Entries.LastOrDefault()?.Id;
        session.UpdatedAt = lastTimestamp ?? session.CreatedAt;
        session.Title ??= session.FirstPrompt();
    }

    public string Write(DeveSession session, string sessionId, string workingDirectory)
    {
        var output = new StringBuilder();
        var header = session.Sources.FirstOrDefault(s => s.Harness == HarnessId)?.Header?.DeepClone() as JsonObject
            ?? new JsonObject { ["type"] = "session", ["version"] = 3, ["timestamp"] = Iso(session.CreatedAt) };
        header["id"] = sessionId;
        header["cwd"] = workingDirectory;
        header.Remove("parentSession");
        AppendLine(output, header);

        // Entries that can't be written (another harness's system prompt or UI state) are skipped; their children are
        // attached to the skipped entry's parent so the tree stays connected.
        var replaced = new Dictionary<string, string?>();
        string? Parent(string? id)
        {
            while (id is not null && replaced.TryGetValue(id, out var parent))
            {
                id = parent;
            }

            return id;
        }

        foreach (var entry in session.Entries)
        {
            if (entry.Harness == HarnessId && entry.Native is not null)
            {
                var native = (JsonObject)entry.Native.DeepClone();
                if (native["parentId"] is JsonValue && Parent(Str(native, "parentId")) is var parentId)
                {
                    native["parentId"] = parentId;
                }

                AppendLine(output, native);
                continue;
            }

            var lines = WriteForeign(entry, Parent(entry.ParentId)).ToList();
            if (lines.Count == 0)
            {
                replaced[entry.Id] = entry.ParentId;
                continue;
            }

            foreach (var line in lines)
            {
                AppendLine(output, line);
            }

            // Children attach to the last line written for this entry.
            var last = Str(lines[^1], "id")!;
            if (last != entry.Id)
            {
                replaced[entry.Id] = last;
            }
        }

        return output.ToString();
    }

    private DeveEntry ReadEntry(JsonObject item)
    {
        var entry = new DeveEntry
        {
            Id = Str(item, "id") ?? Guid.NewGuid().ToString("N")[..8],
            ParentId = Str(item, "parentId"),
            Timestamp = Time(item["timestamp"]) ?? DateTime.UtcNow,
            Harness = HarnessId,
            Native = item,
            Kind = DeveEntryKind.Native,
        };

        switch (Str(item, "type"))
        {
            case "message" when item["message"] is JsonObject message:
                ReadMessage(entry, message);
                break;
            case "compaction":
                entry.Kind = DeveEntryKind.Summary;
                entry.SummaryKind = "compaction";
                entry.Text = Str(item, "summary");
                entry.FirstKeptId = Str(item, "firstKeptEntryId");
                break;
            case "branch_summary":
                entry.Kind = DeveEntryKind.Summary;
                entry.SummaryKind = "branch";
                entry.Text = Str(item, "summary");
                break;
            case "model_change":
                entry.Kind = DeveEntryKind.Event;
                entry.Event = "model_change";
                entry.Model = new DeveModel { Provider = Str(item, "provider"), Id = Str(item, "modelId") };
                entry.Text = $"{Str(item, "provider")}/{Str(item, "modelId")}";
                break;
            case "thinking_level_change":
                entry.Kind = DeveEntryKind.Event;
                entry.Event = "thinking_level";
                entry.Text = Str(item, "thinkingLevel");
                break;
            case "session_info":
                entry.Kind = DeveEntryKind.Event;
                entry.Event = "title";
                entry.Text = Str(item, "name");
                break;
            case "label":
                entry.Kind = DeveEntryKind.Event;
                entry.Event = "label";
                entry.Text = Str(item, "label");
                entry.Data = new JsonObject { ["targetId"] = Str(item, "targetId") };
                break;
            case "custom_message":
                entry.Kind = DeveEntryKind.Event;
                entry.Event = "extension_message";
                entry.Text = TextOf(item["content"]);
                entry.Data = new JsonObject { ["customType"] = Str(item, "customType"), ["display"] = item["display"]?.DeepClone() };
                break;
            // custom, usage, context_edit and unknown types stay native-only.
        }

        return entry;
    }

    private void ReadMessage(DeveEntry entry, JsonObject message)
    {
        entry.Kind = DeveEntryKind.Message;
        switch (Str(message, "role"))
        {
            case "system":
                entry.Role = DeveRole.System;
                var sections = message["sections"] as JsonObject;
                var text = new StringBuilder(TextOf(message["content"]));
                foreach (var (name, value) in sections ?? [])
                {
                    if (value is JsonValue && value.GetValueKind() == JsonValueKind.String)
                    {
                        text.Append(text.Length > 0 ? "\n\n" : "").Append(value.GetValue<string>());
                    }
                }

                entry.Content = [DeveBlock.OfText(text.ToString())];
                break;
            case "user":
                entry.Role = DeveRole.User;
                entry.Content = ReadBlocks(message["content"], null);
                break;
            case "assistant":
                entry.Role = DeveRole.Assistant;
                entry.Model = new DeveModel { Provider = Str(message, "provider"), Id = Str(message, "model") };
                entry.Content = ReadBlocks(message["content"], entry.Model.Provider);
                entry.Usage = ReadUsage(message["usage"] as JsonObject);
                entry.StopReason = Str(message, "stopReason");
                entry.Error = Str(message, "errorMessage");
                break;
            case "toolResult":
                entry.Role = DeveRole.Tool;
                entry.Content =
                [
                    new DeveBlock
                    {
                        Type = "toolResult",
                        ToolCallId = Str(message, "toolCallId"),
                        ToolName = Str(message, "toolName"),
                        IsError = message["isError"] is JsonValue error && error.GetValueKind() == JsonValueKind.True,
                        Content = ReadBlocks(message["content"], null),
                    },
                ];
                break;
            case "bashExecution":
                entry.Kind = DeveEntryKind.Event;
                entry.Role = null;
                entry.Event = "shell";
                entry.Text = Str(message, "command");
                entry.Data = new JsonObject
                {
                    ["command"] = Str(message, "command"),
                    ["output"] = Str(message, "output"),
                    ["exitCode"] = message["exitCode"]?.DeepClone(),
                };
                break;
            case "branchSummary" or "compactionSummary":
                entry.Kind = DeveEntryKind.Summary;
                entry.Role = null;
                entry.SummaryKind = Str(message, "role") == "branchSummary" ? "branch" : "compaction";
                entry.Text = Str(message, "summary");
                break;
            default:
                // "custom" (extension) and unknown roles: context the model saw, shown as extension messages.
                entry.Kind = DeveEntryKind.Event;
                entry.Role = null;
                entry.Event = "extension_message";
                entry.Text = TextOf(message["content"]);
                break;
        }
    }

    private List<DeveBlock> ReadBlocks(JsonNode? content, string? provider)
    {
        if (content is JsonValue && content.GetValueKind() == JsonValueKind.String)
        {
            return [DeveBlock.OfText(content.GetValue<string>())];
        }

        var blocks = new List<DeveBlock>();
        foreach (var node in content as JsonArray ?? [])
        {
            if (node is not JsonObject block)
            {
                continue;
            }

            switch (Str(block, "type"))
            {
                case "text":
                    blocks.Add(DeveBlock.OfText(Str(block, "text") ?? ""));
                    break;
                case "image":
                    blocks.Add(new DeveBlock { Type = "image", MimeType = Str(block, "mimeType"), Data = Str(block, "data") });
                    break;
                case "thinking":
                    var signature = Str(block, "thinkingSignature");
                    blocks.Add(new DeveBlock
                    {
                        Type = "thinking",
                        Text = Str(block, "thinking"),
                        Redacted = block["redacted"] is JsonValue redacted && redacted.GetValueKind() == JsonValueKind.True ? true : null,
                        Signature = signature is null ? null : new DeveSignature { Harness = HarnessId, Provider = provider, Value = signature },
                    });
                    break;
                case "toolCall":
                    var name = Str(block, "name");
                    blocks.Add(new DeveBlock
                    {
                        Type = "toolCall",
                        Id = Str(block, "id"),
                        Name = name,
                        ToolKind = ToolKinds.For(name),
                        Arguments = block["arguments"]?.DeepClone(),
                    });
                    break;
            }
        }

        return blocks;
    }

    private static DeveUsage? ReadUsage(JsonObject? usage) => usage is null ? null : new DeveUsage
    {
        Input = Long(usage["input"]),
        Output = Long(usage["output"]),
        CacheRead = Long(usage["cacheRead"]),
        CacheWrite = Long(usage["cacheWrite"]),
        Reasoning = Long(usage["reasoning"]),
        CostUsd = usage["cost"] is JsonObject cost && cost["total"] is JsonValue total && total.TryGetValue<decimal>(out var value) ? value : 0,
    };

    /// <summary>pi lines for an entry that didn't come from pi (another harness, or edited in the Manager).</summary>
    private IEnumerable<JsonObject> WriteForeign(DeveEntry entry, string? parentId)
    {
        var timestamp = Iso(entry.Timestamp);
        var milliseconds = new DateTimeOffset(DateTime.SpecifyKind(entry.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        JsonObject Line(string type, string id, string? parent) => new() { ["type"] = type, ["id"] = id, ["parentId"] = parent, ["timestamp"] = timestamp };

        switch (entry)
        {
            case { Kind: DeveEntryKind.Message, Role: DeveRole.User }:
                var user = Line("message", entry.Id, parentId);
                user["message"] = new JsonObject { ["role"] = "user", ["content"] = WriteBlocks(entry.Content, forUser: true), ["timestamp"] = milliseconds };
                yield return user;
                break;
            case { Kind: DeveEntryKind.Message, Role: DeveRole.Assistant }:
                var assistant = Line("message", entry.Id, parentId);
                assistant["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = WriteBlocks(entry.Content, forUser: false),
                    // Unknown api/provider: pi treats signatures as foreign and sends thinking as plain text.
                    ["api"] = "unknown",
                    ["provider"] = entry.Model?.Provider ?? "unknown",
                    ["model"] = entry.Model?.Id ?? "unknown",
                    ["usage"] = new JsonObject
                    {
                        ["input"] = entry.Usage?.Input ?? 0, ["output"] = entry.Usage?.Output ?? 0,
                        ["cacheRead"] = entry.Usage?.CacheRead ?? 0, ["cacheWrite"] = entry.Usage?.CacheWrite ?? 0,
                        ["totalTokens"] = (entry.Usage?.Input ?? 0) + (entry.Usage?.Output ?? 0) + (entry.Usage?.CacheRead ?? 0) + (entry.Usage?.CacheWrite ?? 0),
                        ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = entry.Usage?.CostUsd ?? 0 },
                    },
                    ["stopReason"] = entry.StopReason ?? (entry.Content?.Any(b => b.Type == "toolCall") == true ? "toolUse" : "stop"),
                    ["timestamp"] = milliseconds,
                };
                yield return assistant;
                break;
            case { Kind: DeveEntryKind.Message, Role: DeveRole.Tool }:
                // pi has one message per tool result; Claude Code puts several in one message.
                var results = (entry.Content ?? []).Where(b => b.Type == "toolResult").ToList();
                var parent = parentId;
                for (var i = 0; i < results.Count; i++)
                {
                    var id = i == 0 ? entry.Id : $"{entry.Id}-{i}";
                    var result = Line("message", id, parent);
                    result["message"] = new JsonObject
                    {
                        ["role"] = "toolResult",
                        ["toolCallId"] = results[i].ToolCallId,
                        ["toolName"] = results[i].ToolName,
                        ["content"] = WriteBlocks(results[i].Content, forUser: true),
                        ["isError"] = results[i].IsError ?? false,
                        ["timestamp"] = milliseconds,
                    };
                    parent = id;
                    yield return result;
                }

                break;
            case { Kind: DeveEntryKind.Summary, SummaryKind: "branch" }:
                var branch = Line("branch_summary", entry.Id, parentId);
                branch["fromId"] = parentId;
                branch["summary"] = entry.Text ?? "";
                yield return branch;
                break;
            case { Kind: DeveEntryKind.Summary }:
                var compaction = Line("compaction", entry.Id, parentId);
                compaction["summary"] = entry.Text ?? "";
                compaction["firstKeptEntryId"] = entry.FirstKeptId ?? entry.Id;
                compaction["tokensBefore"] = 0;
                yield return compaction;
                break;
            case { Kind: DeveEntryKind.Event, Event: "model_change" }:
                var model = Line("model_change", entry.Id, parentId);
                model["provider"] = entry.Model?.Provider;
                model["modelId"] = entry.Model?.Id;
                yield return model;
                break;
            case { Kind: DeveEntryKind.Event, Event: "title" }:
                var info = Line("session_info", entry.Id, parentId);
                info["name"] = entry.Text;
                yield return info;
                break;
            case { Kind: DeveEntryKind.Event, Event: "shell" }:
                var shell = Line("message", entry.Id, parentId);
                shell["message"] = new JsonObject
                {
                    ["role"] = "bashExecution",
                    ["command"] = entry.Data?["command"]?.DeepClone() ?? entry.Text,
                    ["output"] = entry.Data?["output"]?.DeepClone() ?? "",
                    ["exitCode"] = entry.Data?["exitCode"]?.DeepClone(),
                    ["cancelled"] = false,
                    ["truncated"] = false,
                    ["timestamp"] = milliseconds,
                };
                yield return shell;
                break;
            // System prompts, other harnesses' UI state and unknown events are not carried over.
        }
    }

    private static JsonArray WriteBlocks(List<DeveBlock>? blocks, bool forUser)
    {
        var array = new JsonArray();
        foreach (var block in blocks ?? [])
        {
            switch (block.Type)
            {
                case "text":
                    array.Add(new JsonObject { ["type"] = "text", ["text"] = block.Text ?? "" });
                    break;
                case "image":
                    array.Add(new JsonObject { ["type"] = "image", ["data"] = block.Data, ["mimeType"] = block.MimeType });
                    break;
                case "thinking" when !forUser && !string.IsNullOrEmpty(block.Text):
                    // Signatures are only valid for the provider that made them, so a foreign one is not passed on.
                    array.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = block.Text });
                    break;
                case "toolCall" when !forUser:
                    array.Add(new JsonObject
                    {
                        ["type"] = "toolCall",
                        ["id"] = block.Id,
                        ["name"] = block.Name,
                        ["arguments"] = block.Arguments?.DeepClone() ?? new JsonObject(),
                    });
                    break;
            }
        }

        return array;
    }

    private static void AppendLine(StringBuilder output, JsonObject line) => output.Append(line.ToJsonString(LineOptions)).Append('\n');

    private static string? Str(JsonObject item, string name) =>
        item[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static long Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonArray array => string.Join("\n", array.OfType<JsonObject>().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text"))),
        _ => "",
    };

    private static DateTime? Time(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
        && DateTime.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    private static string Iso(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
