using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// Prepares a conversation for another harness ("conversation as text", see docs/session-formats.md): the active branch
/// becomes alternating user and assistant messages with text and images only. Tool calls and their results are written
/// as short lines of text, because a model can't be handed calls to tools it doesn't have, and providers reject tool
/// calls and results that don't pair up exactly. Thinking is dropped: it can only be replayed to the provider that made it.
/// Long conversations keep their most recent part (whatever fits <see cref="MaxCharacters"/>).
/// </summary>
public static class SessionFlattener
{
    /// <summary>About 100k tokens of text: leaves room in every current model's context for the work that follows.</summary>
    public const int MaxCharacters = 400_000;

    private const int MaxToolOutput = 1200;
    private const int MaxArguments = 300;

    public static DeveSession Flatten(DeveSession source, string fromHarness, string toHarness, int maxCharacters = MaxCharacters)
    {
        var turns = new List<Turn>();
        foreach (var entry in source.ActivePath())
        {
            switch (entry)
            {
                case { Kind: DeveEntryKind.Message, Role: DeveRole.User }:
                    Add(turns, DeveRole.User, entry, Text(entry.Content), Images(entry.Content));
                    break;
                case { Kind: DeveEntryKind.Message, Role: DeveRole.Assistant }:
                    Add(turns, DeveRole.Assistant, entry, AssistantText(entry.Content), []);
                    break;
                case { Kind: DeveEntryKind.Message, Role: DeveRole.Tool }:
                    Add(turns, DeveRole.Assistant, entry, ToolResults(entry.Content), []);
                    break;
                case { Kind: DeveEntryKind.Summary } when !string.IsNullOrWhiteSpace(entry.Text):
                    Add(turns, DeveRole.User, entry, $"Summary of the earlier conversation:\n\n{entry.Text}", []);
                    break;
                case { Kind: DeveEntryKind.Event, Event: "shell" }:
                    var output = Truncate(entry.Data?["output"]?.ToString() ?? "", MaxToolOutput);
                    Add(turns, DeveRole.User, entry, $"(I ran `{entry.Data?["command"] ?? entry.Text}` in the shell.)\n{Fence(output)}", []);
                    break;
                // System prompts, UI state, model changes and other harnesses' extras stay behind.
            }
        }

        var note = $"(This conversation was moved from {fromHarness} to {toHarness} by DeveAgentManager. Tool calls from before the move are shown as text; what they changed is in the files.)";
        var kept = FitToBudget(turns, maxCharacters, out var omitted);
        if (omitted > 0)
        {
            note += $"\n({omitted} earlier messages were left out to fit; the Manager keeps the full conversation.)";
        }

        if (kept.Count == 0 || kept[0].Role != DeveRole.User)
        {
            kept.Insert(0, new Turn(DeveRole.User, kept.FirstOrDefault()?.Timestamp ?? source.CreatedAt));
        }

        kept[0].Text.Insert(0, kept[0].Text.Length > 0 ? note + "\n\n" : note);

        var flat = new DeveSession
        {
            Title = source.Title,
            Cwd = source.Cwd,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
        };
        string? parent = null;
        foreach (var turn in kept)
        {
            var id = Guid.NewGuid().ToString("N");
            var content = new List<DeveBlock>();
            if (turn.Text.Length > 0)
            {
                content.Add(DeveBlock.OfText(turn.Text.ToString().Trim()));
            }

            content.AddRange(turn.Images);
            flat.Entries.Add(new DeveEntry
            {
                Id = id,
                ParentId = parent,
                Timestamp = turn.Timestamp,
                Kind = DeveEntryKind.Message,
                Role = turn.Role,
                Content = content,
                Model = turn.Model,
            });
            parent = id;
        }

        flat.LeafId = parent;
        return flat;
    }

    private static void Add(List<Turn> turns, DeveRole role, DeveEntry entry, string text, List<DeveBlock> images)
    {
        if (text.Trim().Length == 0 && images.Count == 0)
        {
            return;
        }

        // Consecutive messages of one side become one message: providers expect user and assistant to alternate.
        var turn = turns.Count > 0 && turns[^1].Role == role ? turns[^1] : null;
        if (turn is null)
        {
            turn = new Turn(role, entry.Timestamp);
            turns.Add(turn);
        }

        if (text.Trim().Length > 0)
        {
            turn.Text.Append(turn.Text.Length > 0 ? "\n\n" : "").Append(text.Trim());
        }

        turn.Images.AddRange(images);
        turn.Model ??= entry.Model;
    }

    /// <summary>The most recent turns that fit, starting with a user turn.</summary>
    private static List<Turn> FitToBudget(List<Turn> turns, int maxCharacters, out int omitted)
    {
        var total = 0;
        var start = turns.Count;
        while (start > 0 && total + turns[start - 1].Size <= maxCharacters)
        {
            start--;
            total += turns[start].Size;
        }

        // Start the kept part at a user message, so the history begins with what was asked.
        while (start < turns.Count && start > 0 && turns[start].Role != DeveRole.User)
        {
            start++;
        }

        omitted = start;
        return turns.Skip(start).ToList();
    }

    private static string Text(List<DeveBlock>? content) =>
        string.Join("\n\n", (content ?? []).Where(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text)).Select(b => b.Text!.Trim()));

    private static List<DeveBlock> Images(List<DeveBlock>? content) =>
        (content ?? []).Where(b => b.Type == "image" && !string.IsNullOrEmpty(b.Data))
            .Select(b => new DeveBlock { Type = "image", MimeType = b.MimeType, Data = b.Data }).ToList();

    private static string AssistantText(List<DeveBlock>? content)
    {
        var text = new StringBuilder();
        foreach (var block in content ?? [])
        {
            var line = block.Type switch
            {
                "text" => block.Text?.Trim(),
                "toolCall" => "▸ " + DescribeCall(block),
                _ => null,
            };
            if (!string.IsNullOrEmpty(line))
            {
                text.Append(text.Length > 0 ? "\n\n" : "").Append(line);
            }
        }

        return text.ToString();
    }

    private static string DescribeCall(DeveBlock call)
    {
        var arguments = call.Arguments as JsonObject;
        string? Arg(params string[] names) => names.Select(n => arguments?[n]).FirstOrDefault(v => v is JsonValue)?.ToString();
        return (call.ToolKind ?? ToolKinds.For(call.Name)) switch
        {
            ToolKinds.Shell when Arg("command", "cmd") is { } command => $"ran `{OneLine(command)}`",
            ToolKinds.Read when Arg("path", "file_path") is { } path => $"read {path}",
            ToolKinds.Write when Arg("path", "file_path") is { } path => $"wrote {path}",
            ToolKinds.Edit when Arg("path", "file_path") is { } path => $"edited {path}",
            _ => $"{call.Name} {Truncate(OneLine(call.Arguments?.ToJsonString() ?? ""), MaxArguments)}".Trim(),
        };
    }

    private static string ToolResults(List<DeveBlock>? content)
    {
        var text = new StringBuilder();
        foreach (var result in (content ?? []).Where(b => b.Type == "toolResult"))
        {
            var output = Truncate(Text(result.Content), MaxToolOutput);
            var label = result.IsError == true ? "error" : "output";
            text.Append(text.Length > 0 ? "\n" : "").Append(output.Length == 0 ? $"({label}: nothing)" : $"{label}:\n{Fence(output)}");
        }

        return text.ToString();
    }

    private static string Fence(string text) => text.Length == 0 ? "" : $"```\n{text.Replace("```", "ʼʼʼ")}\n```";

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max / 2)] + $"\n… ({text.Length - max} characters left out) …\n" + text[^(max / 2)..];
    }

    private sealed class Turn(DeveRole role, DateTime timestamp)
    {
        public DeveRole Role { get; } = role;

        public DateTime Timestamp { get; } = timestamp;

        public StringBuilder Text { get; } = new();

        public List<DeveBlock> Images { get; } = [];

        public DeveModel? Model { get; set; }

        public int Size => Text.Length + Images.Sum(i => i.Data?.Length ?? 0) / 4;
    }
}
