using DeveAgentManager.Core.Sessions;
namespace DeveAgentManager.Client.Shared;
public static class ConversationCopy
{
    public static string Markdown(DeveEntry entry) => string.Join("\n\n", (entry.Content ?? [])
        .Where(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text)).Select(b => b.Text));
    public static string LastReply(DeveSession session) => session.ActivePath()
        .LastOrDefault(e => e.Kind == DeveEntryKind.Message && e.Role == DeveRole.Assistant && Markdown(e).Length > 0)
        is { } entry ? Markdown(entry) : "";
}
