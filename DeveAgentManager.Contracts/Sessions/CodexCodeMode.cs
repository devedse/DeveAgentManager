using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// Codex's code mode: a single <c>exec</c> call runs JavaScript that calls the real tools (<c>tools.exec_command</c>,
/// <c>tools.apply_patch</c>...) and prints their results as JSON. This reads the script and its output as plain tool use:
/// the commands that were run and what they printed.
/// </summary>
public static partial class CodexCodeMode
{
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"\btools\.(\w+)\(")]
    private static partial Regex ToolCall();

    [GeneratedRegex(@"\*\*\* (Add|Update|Delete) File: ([^\\""'`\r\n]+)")]
    private static partial Regex PatchFile();

    [GeneratedRegex(@"\AScript (completed|failed)\r?\nWall time [0-9.]+ seconds\r?\nOutput:\r?\n?")]
    private static partial Regex ResultHeader();

    public static bool IsScript(DeveBlock call, out string script)
    {
        script = call is { Name: "exec", Arguments: JsonObject arguments } && arguments["input"] is JsonValue value
            && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
        return script.Contains("tools.", StringComparison.Ordinal);
    }

    /// <summary>One line per tool the script calls, such as "ran `git status`".</summary>
    public static List<string> Describe(string script)
    {
        var lines = new List<string>();
        var calls = ToolCall().Matches(script);
        for (var i = 0; i < calls.Count; i++)
        {
            // The arguments of a call are in the text up to the next call.
            var end = i + 1 < calls.Count ? calls[i + 1].Index : script.Length;
            var segment = script[(calls[i].Index + calls[i].Length)..end];
            var name = calls[i].Groups[1].Value;
            switch (name)
            {
                case "exec_command":
                    lines.Add(Strings(segment, "cmd").FirstOrDefault() is { } command ? $"ran `{command}`" : "ran a shell command");
                    break;
                case "write_stdin":
                    lines.Add(Strings(segment, "chars").FirstOrDefault() is { Length: > 0 } chars
                        ? $"typed `{chars}` into a running command" : "waited for a running command");
                    break;
                case "apply_patch":
                    // The patch is often in a variable defined before the call, so read the whole script.
                    var files = PatchFiles(script);
                    lines.AddRange(files.Count > 0 ? files : ["applied a patch"]);
                    break;
                case "view_image":
                    lines.Add(Strings(segment, "path").FirstOrDefault() is { } path ? $"viewed {path}" : "viewed an image");
                    break;
                case "web__run":
                    var queries = Strings(segment, "q");
                    var pages = Strings(segment, "ref_id");
                    if (queries.Count > 0) lines.Add("searched the web for " + string.Join("; ", queries));
                    if (pages.Count > 0) lines.Add("opened " + string.Join(", ", pages));
                    if (queries.Count + pages.Count == 0) lines.Add("used the web");
                    break;
                default:
                    lines.Add($"used {name}");
                    break;
            }
        }

        return lines.Distinct().ToList();
    }

    /// <summary>"edited x", "created y" or "deleted z" for each file in an apply_patch patch.</summary>
    public static List<string> PatchFiles(string patch) =>
        PatchFile().Matches(patch).Select(m => (m.Groups[1].Value switch { "Add" => "created ", "Delete" => "deleted ", _ => "edited " }) + m.Groups[2].Value.Trim())
            .Distinct().ToList();

    /// <summary>
    /// The output of a script: the "Script completed" header is dropped and each printed result is unwrapped to the
    /// command's output. Returns false for anything else.
    /// </summary>
    public static bool TryReadResult(IReadOnlyList<DeveBlock> content, out string output, out bool failed)
    {
        output = "";
        failed = false;
        var texts = content.Where(b => b.Type == "text" && b.Text is not null).Select(b => b.Text!).ToList();
        if (texts.Count == 0 || ResultHeader().Match(texts[0]) is not { Success: true } header)
        {
            return false;
        }

        failed = header.Groups[1].Value == "failed";
        texts[0] = texts[0][header.Length..];
        output = string.Join("\n\n", texts.Select(Unwrap).Where(t => t.Trim().Length > 0)).TrimEnd().TrimStart('\r', '\n');
        return true;
    }

    private static string Unwrap(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('"'))
        {
            return text;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            return text;
        }

        // Promise.allSettled results, printed as {i, result: {status, value | reason}}.
        if (node is JsonObject { Count: 2 } indexed && indexed["i"] is JsonValue && indexed["result"] is JsonObject settled)
        {
            node = settled["status"]?.ToString() == "rejected" ? $"error: {settled["reason"]}" : settled["value"];
        }

        switch (node)
        {
            case null:
                return "";
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return value.GetValue<string>();
            case JsonObject { Count: 0 }:
                return "";
            case JsonObject command when command["output"] is JsonValue output && output.GetValueKind() == JsonValueKind.String
                                         && (command.ContainsKey("chunk_id") || command.ContainsKey("wall_time_seconds")):
                var printed = output.GetValue<string>().TrimEnd().TrimStart('\r', '\n');
                var exit = command["exit_code"] is JsonValue code && code.GetValueKind() == JsonValueKind.Number ? code.GetValue<int>() : (int?)null;
                var status = exit switch
                {
                    null when command.ContainsKey("session_id") => "(still running)",
                    null or 0 => null,
                    _ => $"(exit code {exit})",
                };
                return status is null ? printed : printed.Length == 0 ? status : $"{status}\n{printed}";
            default:
                return node.ToJsonString(Relaxed);
        }
    }

    /// <summary>The string literals given for <paramref name="key"/>, such as <c>cmd:"git status"</c>.</summary>
    private static List<string> Strings(string segment, string key)
    {
        var values = new List<string>();
        foreach (Match match in Regex.Matches(segment, $@"(?<![\w$])[""']?{key}[""']?\s*:\s*"))
        {
            if (Literal(segment, match.Index + match.Length) is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>A JavaScript string literal starting at <paramref name="start"/>, or null for anything else (a variable).</summary>
    private static string? Literal(string text, int start)
    {
        if (start >= text.Length || text[start] is not ('"' or '\'' or '`'))
        {
            return null;
        }

        var quote = text[start];
        var value = new StringBuilder();
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == quote)
            {
                return value.ToString();
            }

            if (c != '\\' || i + 1 >= text.Length)
            {
                value.Append(c);
                continue;
            }

            c = text[++i];
            switch (c)
            {
                case 'n': value.Append('\n'); break;
                case 't': value.Append('\t'); break;
                case 'r': value.Append('\r'); break;
                case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    value.Append((char)code);
                    i += 4;
                    break;
                case 'x' when i + 2 < text.Length && int.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    value.Append((char)code);
                    i += 2;
                    break;
                default: value.Append(c); break;
            }
        }

        return null;
    }
}
