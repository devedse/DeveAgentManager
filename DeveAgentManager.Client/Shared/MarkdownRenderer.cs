using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace DeveAgentManager.Client.Shared;

/// <summary>
/// Renders agent and user text as Markdown. The text comes from models (and whatever they read), so raw HTML is
/// disabled and links other than http(s), mailto and relative ones are neutralized.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString ToHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        var document = Markdown.Parse(text, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafe(link.Url))
            {
                link.Url = "#";
            }
        }

        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafe(link.Url))
            {
                link.Url = "#";
            }
        }

        return new MarkupString(document.ToHtml(Pipeline));
    }

    internal static bool IsSafe(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        var trimmed = url.Trim();
        var colon = trimmed.IndexOf(':');
        var slash = trimmed.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || (slash >= 0 && slash < colon))
        {
            // Relative link or fragment.
            return true;
        }

        return trimmed.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }
}
