using System.Text;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Small parsing state: tree topology and bounded message snippets, never native payloads or images.</summary>
public sealed class SessionReadCursor
{
    public DeveSession Session { get; set; } = new();
    public int LineCount { get; set; }
    public string? CustomTitle { get; set; }
    public string? AutoTitle { get; set; }

    public void Compact(int from)
    {
        foreach (var source in Session.Sources) source.Header = null;
        for (var i = from; i < Session.Entries.Count; i++)
        {
            var e = Session.Entries[i];
            // Preserve whether there is assistant text, along with enough text for existing previews and titles.
            var parts = e.Text is not null ? new[] { e.Text } : (e.Content ?? [])
                .Where(b => e.Role == DeveRole.User || b.Type == "text").Select(b => b.Text);
            e.Text = Snippet(parts, e.Role == DeveRole.User ? 80 : 281);
            e.Content = null;
            e.Native = null;
            e.Data = null;
            e.Model = null;
            e.Usage = null;
            e.Error = null;
        }
    }

    internal static string Snippet(IEnumerable<string?> parts, int limit)
    {
        var result = new StringBuilder(limit + 1);
        var space = false;
        foreach (var part in parts)
        {
            foreach (var c in part ?? "")
            {
                if (char.IsWhiteSpace(c)) { space = result.Length > 0; continue; }
                if (space) { result.Append(' '); space = false; }
                result.Append(c);
                if (result.Length > limit) return result.ToString(0, limit - 1) + "…";
            }
            space = result.Length > 0;
        }
        return result.ToString();
    }
}
