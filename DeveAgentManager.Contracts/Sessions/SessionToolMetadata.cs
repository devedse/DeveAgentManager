using System.Text.Json.Nodes;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// DAM replay hints live on the native JSONL envelope, outside the messages sent to a model. This retains native
/// call encoding across repeated switches without injecting provider-specific fields into tool arguments.
/// </summary>
public static class SessionToolMetadata
{
    public static void Write(DeveEntry entry, JsonObject line, bool omittedEmptyResultText = false)
    {
        if (entry.MigrationKind is not null) line["damHistoryKind"] = entry.MigrationKind;
        var hints = new JsonArray();
        foreach (var block in entry.Content ?? [])
        {
            if (block.Type is not ("toolCall" or "toolResult")) continue;
            var hint = new JsonObject
            {
                ["id"] = block.Id ?? block.ToolCallId, ["name"] = block.Name ?? block.ToolName,
                ["callType"] = block.CallType, ["namespace"] = block.Namespace, ["isError"] = block.IsError,
            };
            // Claude omits empty text from model input. Keep only its positions, never a second copy of output.
            if (omittedEmptyResultText && block.Type == "toolResult" && block.Content is { } content)
            {
                var positions = content.Select((part, index) => (part, index))
                    .Where(p => p.part.Type == "text" && p.part.Text == "").Select(p => p.index).ToArray();
                if (positions.Length > 0) hint["emptyResultTextPositions"] = new JsonArray(positions.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
            }
            hints.Add(hint);
        }
        if (hints.Count > 0) line["damToolHistory"] = hints;
    }

    public static void Read(DeveEntry entry, JsonObject line)
    {
        entry.MigrationKind = line["damHistoryKind"]?.GetValue<string>();
        if (line["damToolHistory"] is not JsonArray hints) return;
        foreach (var block in entry.Content ?? [])
        {
            if (block.Type is not ("toolCall" or "toolResult")) continue;
            var id = block.Id ?? block.ToolCallId;
            var hint = hints.OfType<JsonObject>().FirstOrDefault(h => h["id"]?.GetValue<string>() == id);
            if (hint is null) continue;
            block.CallType = hint["callType"]?.GetValue<string>();
            block.Namespace = hint["namespace"]?.GetValue<string>();
            if (block.Type == "toolResult")
            {
                block.ToolName = hint["name"]?.GetValue<string>();
                block.IsError = hint["isError"]?.GetValue<bool>();
                if (hint["emptyResultTextPositions"] is JsonArray positions)
                {
                    var content = block.Content ??= [];
                    var previous = -1;
                    foreach (var position in positions)
                    {
                        if (position is not JsonValue value || !value.TryGetValue<int>(out var index) || index <= previous || index > content.Count)
                            throw new FormatException("Invalid historical empty tool-output position.");
                        content.Insert(index, DeveBlock.OfText(""));
                        previous = index;
                    }
                }
            }
        }
    }
}
