using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Helpers for the harnesses' JSONL session files.</summary>
public static class JsonLines
{
    private static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The JSON objects in a JSONL file. Lines cut off while the harness was writing them are skipped.</summary>
    public static IEnumerable<JsonObject> Parse(string content)
    {
        using var reader = new StringReader(content);
        foreach (var item in Parse(reader)) yield return item;
    }

    public static IEnumerable<JsonObject> Parse(TextReader reader)
    {
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject? item;
            try
            {
                item = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (item is not null)
            {
                yield return item;
            }
        }
    }

    public static void Append(StringBuilder output, JsonObject line) => output.Append(line.ToJsonString(LineOptions)).Append('\n');

    public static string? Str(JsonObject? item, string name) =>
        item?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool True(JsonObject? item, string name) => item?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    public static long Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    public static DateTime? Time(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
        && DateTime.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    public static string Iso(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
