using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// A conversation in the harness-neutral DeveSession format (see docs/session-formats.md). Entries form a tree via
/// <see cref="DeveEntry.ParentId"/>; <see cref="LeafId"/> is where the conversation currently is.
/// </summary>
public sealed class DeveSession
{
    public const string FormatName = "deve-session";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    public string? Title { get; set; }

    public string? Cwd { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? LeafId { get; set; }

    public List<DeveSource> Sources { get; set; } = [];

    public List<DeveEntry> Entries { get; set; } = [];

    /// <summary>The latest cross-harness conversion, without conversation or credential contents.</summary>
    public SessionMigrationReport? Migration { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson(bool indented = false) =>
        JsonSerializer.Serialize(this, indented ? new JsonSerializerOptions(JsonOptions) { WriteIndented = true } : JsonOptions);

    public Task WriteAsync(Stream stream, CancellationToken ct = default) => JsonSerializer.SerializeAsync(stream, this, JsonOptions, ct);

    public static async Task<DeveSession> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var session = await JsonSerializer.DeserializeAsync<DeveSession>(stream, JsonOptions, ct)
            ?? throw new FormatException("Empty DeveSession.");
        if (session.Format != FormatName) throw new FormatException("Not a DeveSession.");
        return session;
    }

    public static DeveSession FromJson(string json)
    {
        var session = JsonSerializer.Deserialize<DeveSession>(json, JsonOptions) ?? throw new FormatException("Empty DeveSession.");
        if (session.Format != FormatName)
        {
            throw new FormatException($"Not a DeveSession (format '{session.Format}').");
        }

        return session;
    }

    /// <summary>The active branch: the entries from the root to <see cref="LeafId"/> (or the last entry), in order.</summary>
    public List<DeveEntry> ActivePath()
    {
        var byId = new Dictionary<string, DeveEntry>();
        foreach (var entry in Entries)
        {
            byId.TryAdd(entry.Id, entry);
        }

        var leaf = (LeafId is not null ? byId.GetValueOrDefault(LeafId) : null) ?? Entries.LastOrDefault();
        var path = new List<DeveEntry>();
        var seen = new HashSet<string>();
        for (var current = leaf; current is not null && seen.Add(current.Id); current = current.ParentId is null ? null : byId.GetValueOrDefault(current.ParentId))
        {
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    /// <summary>The first thing the user asked, on one line, as a readable title.</summary>
    public string? FirstPrompt(int maxLength = 80)
    {
        var text = Entries.FirstOrDefault(e => e is { Kind: DeveEntryKind.Message, Role: DeveRole.User } && e.PlainText().Trim().Length > 0)?.PlainText();
        if (text is null)
        {
            return null;
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    /// <summary>Number of leaves besides the active one, i.e. abandoned branches.</summary>
    public int OtherBranchCount()
    {
        var parents = Entries.Where(e => e.ParentId is not null).Select(e => e.ParentId!).ToHashSet();
        var leaves = Entries.Count(e => !parents.Contains(e.Id));
        return Math.Max(0, leaves - 1);
    }
}

/// <summary>A harness session this DeveSession was read from.</summary>
public sealed class DeveSource
{
    public string Harness { get; set; } = "";

    public string? SessionId { get; set; }

    /// <summary>The native header line (pi's "session" line), kept so the session can be written back unchanged.</summary>
    public JsonObject? Header { get; set; }
}

public enum DeveEntryKind
{
    Message,
    Summary,
    Event,
    Native,
}

public enum DeveRole
{
    System,
    User,
    Assistant,
    Tool,
}

public sealed class DeveEntry
{
    public string Id { get; set; } = "";

    public string? ParentId { get; set; }

    public DateTime Timestamp { get; set; }

    public DeveEntryKind Kind { get; set; }

    /// <summary>For <see cref="DeveEntryKind.Message"/>.</summary>
    public DeveRole? Role { get; set; }

    public List<DeveBlock>? Content { get; set; }

    public DeveModel? Model { get; set; }

    public DeveUsage? Usage { get; set; }

    public string? StopReason { get; set; }

    public string? Error { get; set; }

    /// <summary>DAM-generated migration context: notice, archive or summary; never a new user instruction.</summary>
    public string? MigrationKind { get; set; }

    /// <summary>For <see cref="DeveEntryKind.Summary"/>: "compaction" or "branch".</summary>
    public string? SummaryKind { get; set; }

    public string? Text { get; set; }

    public string? FirstKeptId { get; set; }

    /// <summary>For <see cref="DeveEntryKind.Event"/>: e.g. "model_change", "thinking_level", "title", "label", "shell".</summary>
    public string? Event { get; set; }

    public JsonNode? Data { get; set; }

    /// <summary>The harness the entry came from, and its original line (written back unchanged to that harness).</summary>
    public string? Harness { get; set; }

    public JsonObject? Native { get; set; }

    /// <summary>All text in the entry, for titles and search.</summary>
    public string PlainText() =>
        Text ?? string.Join("\n", (Content ?? []).Select(b => b.Text).Where(t => !string.IsNullOrEmpty(t)));
}

public sealed class DeveBlock
{
    /// <summary>text, image, thinking, toolCall or toolResult.</summary>
    public string Type { get; set; } = "";

    public string? Text { get; set; }

    public string? MimeType { get; set; }

    public string? Data { get; set; }

    public bool? Redacted { get; set; }

    public DeveSignature? Signature { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    /// <summary>shell, read, write, edit, search, web or other: how the call maps to other harnesses' tools.</summary>
    public string? ToolKind { get; set; }

    /// <summary>Native call encoding (function or custom), retained through other harnesses as replay metadata.</summary>
    public string? CallType { get; set; }

    public string? Namespace { get; set; }

    public JsonNode? Arguments { get; set; }

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public bool? IsError { get; set; }

    public List<DeveBlock>? Content { get; set; }

    public static DeveBlock OfText(string text) => new() { Type = "text", Text = text };
}

/// <summary>Provider-specific replay data (thinking signatures). Only valid for the same provider.</summary>
public sealed class DeveSignature
{
    public string? Harness { get; set; }

    public string? Provider { get; set; }

    public string Value { get; set; } = "";
}

public sealed class DeveModel
{
    public string? Provider { get; set; }

    public string? Id { get; set; }
}

public sealed class DeveUsage
{
    public long Input { get; set; }

    public long Output { get; set; }

    public long CacheRead { get; set; }

    public long CacheWrite { get; set; }

    public long Reasoning { get; set; }

    public decimal CostUsd { get; set; }
}

public static class ToolKinds
{
    public const string Shell = "shell";
    public const string Read = "read";
    public const string Write = "write";
    public const string Edit = "edit";
    public const string Search = "search";
    public const string Web = "web";
    public const string Other = "other";

    /// <summary>Tool names of pi, Claude Code and Codex mapped to a kind (see docs/session-formats.md).</summary>
    public static string For(string? name) => name?.ToLowerInvariant() switch
    {
        "bash" or "powershell" or "exec" or "exec_command" or "shell" or "local_shell" or "run_terminal_command" => Shell,
        "read" or "read_file" => Read,
        "write" => Write,
        "edit" or "multiedit" or "apply_patch" or "search_replace" => Edit,
        "grep" or "glob" or "find" or "ls" or "list_dir" => Search,
        "web_search" or "websearch" or "webfetch" or "fetch_content" => Web,
        _ => Other,
    };
}
