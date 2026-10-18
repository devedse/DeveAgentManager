namespace DeveAgentManager.Contracts;

public sealed record SessionFilePreview(string? Text, bool Markdown, string? UnavailableReason,
    string? SourceUrl = null, string? HtmlUrl = null);
