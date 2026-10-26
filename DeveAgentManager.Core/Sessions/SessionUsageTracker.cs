using System.Text.Json.Nodes;
using DeveAgentManager.Contracts.Pets;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Incremental token metadata, retained before content is compacted. Native records are untouched.</summary>
public static class SessionUsageTracker
{
    public static void Observe(SessionReadCursor cursor, DeveEntry entry)
    {
        if (entry is { Kind: DeveEntryKind.Message, Role: DeveRole.User, MigrationKind: null }
            && entry.Content?.Any(b => b.Type is "text" or "image") == true)
        {
            cursor.UsageTurns.Add(new() { PromptId = entry.Id, StartedAt = entry.Timestamp });
            cursor.UsageMessageTotals.Clear();
        }
        long tokens = 0;
        var native = entry.Native;
        // Codex reports cumulative totals. Cached input and reasoning are subsets of input/output.
        if (entry.Harness == "codex" && (string?)native?["type"] == "event_msg"
            && (string?)native?["payload"]?["type"] == "token_count"
            && native?["payload"]?["info"]?["total_token_usage"] is JsonObject usage)
        {
            var total = Number(usage["total_tokens"]);
            if (total == 0) total = Number(usage["input_tokens"]) + Number(usage["output_tokens"]);
            tokens = Math.Max(0, total - cursor.CodexUsageTotal);
            cursor.CodexUsageTotal = Math.Max(cursor.CodexUsageTotal, total);
        }
        else if (entry.Usage is { } u && entry.MigrationKind is null)
        {
            var id = (string?)native?["message"]?["id"] ?? entry.Id;
            var total = Math.Max(0, u.Input) + Math.Max(0, u.Output) + Math.Max(0, u.CacheRead) + Math.Max(0, u.CacheWrite);
            tokens = Math.Max(0, total - cursor.UsageMessageTotals.GetValueOrDefault(id));
            cursor.UsageMessageTotals[id] = Math.Max(total, cursor.UsageMessageTotals.GetValueOrDefault(id));
        }
        if (cursor.UsageTurns.LastOrDefault() is { } turn) turn.Tokens += tokens;
    }
    private static long Number(JsonNode? value) => value is JsonValue v && v.TryGetValue<long>(out var n) ? Math.Max(0, n) : 0;
}
