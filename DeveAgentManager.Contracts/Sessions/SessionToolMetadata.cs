using System.Text.Json.Nodes;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// DAM replay hints live on the native JSONL envelope, outside the messages sent to a model. This retains native
/// call encoding across repeated switches without injecting provider-specific fields into tool arguments.
/// </summary>
public static class SessionToolMetadata
{
    public static void Write(DeveEntry entry, JsonObject line)
    {
        if (entry.MigrationKind is not null) line["damHistoryKind"] = entry.MigrationKind;
        var hints = new JsonArray();
        foreach (var block in entry.Content ?? [])
        {
            if (block.Type is not ("toolCall" or "toolResult")) continue;
            hints.Add(new JsonObject
            {
                ["id"] = block.Id ?? block.ToolCallId, ["name"] = block.Name ?? block.ToolName,
                ["callType"] = block.CallType, ["namespace"] = block.Namespace, ["isError"] = block.IsError,
            });
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
            }
        }
    }
}
