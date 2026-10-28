namespace DeveAgentManager.Client.Shared;

public sealed record VoiceTranscriptUpdate(string Id, string? Role, string Text, bool Append, bool Complete);

public sealed class VoiceTranscriptLine(string id, string role, string text, bool complete)
{
    public string Id { get; } = id;
    public string Role { get; } = role;
    public string Text { get; internal set; } = text;
    public bool Complete { get; internal set; } = complete;
}

/// <summary>Ordered voice captions. Final snapshots replace their partial text without creating coding prompts.</summary>
public sealed class VoiceTranscriptLog
{
    private const int MaxLines = 500;
    private const int MaxLineLength = 65536;
    private readonly List<VoiceTranscriptLine> _lines = [];
    private readonly Dictionary<string, VoiceTranscriptLine> _byId = [];
    public IReadOnlyList<VoiceTranscriptLine> Lines => _lines;

    public bool Update(VoiceTranscriptUpdate? update)
    {
        if (update is null || string.IsNullOrEmpty(update.Id) || update.Id.Length > 256 || update.Text is null || update.Text.Length > MaxLineLength)
            return false;
        if (!_byId.TryGetValue(update.Id, out var line))
        {
            // The ordered voice channel introduces a turn with its role before sending deltas.
            if (update.Role is not ("user" or "assistant") || update.Append) return false;
            line = new(update.Id, update.Role, update.Text, update.Complete);
            _byId.Add(line.Id, line);
            _lines.Add(line);
            if (_lines.Count > MaxLines) { _byId.Remove(_lines[0].Id); _lines.RemoveAt(0); }
            return true;
        }
        if (update.Role is not null && update.Role != line.Role) return false;
        if (update.Append)
        {
            if (line.Complete || update.Text.Length == 0 || line.Text.Length + update.Text.Length > MaxLineLength) return false;
            line.Text += update.Text;
            return true;
        }
        // Ignore repeated starts; a final snapshot is authoritative, including revisions to earlier words.
        if (!update.Complete) return false;
        var text = update.Text.Length > 0 ? update.Text : line.Text;
        if (line.Complete && line.Text == text) return false;
        line.Text = text;
        line.Complete = true;
        return true;
    }

    public void Finish()
    {
        foreach (var line in _lines) line.Complete = true;
    }
}
