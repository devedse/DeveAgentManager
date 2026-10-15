using System.Text.RegularExpressions;

namespace DeveAgentManager.Core.Security;

/// <summary>Sanitize before any log sink sees text; bound work before processing and never fall back to raw input.</summary>
public static class DiagnosticText
{
    private static readonly RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex Bearer = new(@"\b(Bearer\s+)[A-Za-z0-9._~+/-]+=*", Options, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Authorization = new("""\b((?:Proxy-)?Authorization\s*:\s*)(?:Bearer|Basic)\s+[^\s"',;]+""", Options, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Cookies = new(@"\b(Cookie\s*:\s*)[^\r\n]*", Options, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Credentials = new("([\\\"']?(?:password|token|secret|authorization|cookie|api[_-]?key)[\\\"']?\\s*[:=]\\s*)(?:\\\"[^\\\"]*(?:\\\"|$)|'[^']*(?:'|$)|[^\\s,;]+)", Options, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Keys = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|\z)", Options, TimeSpan.FromMilliseconds(100));

    public static string Redact(string text)
    {
        if (text.Length > 262_144) return "[Payload omitted: diagnostic input exceeded its size limit]";
        try
        {
            text = Authorization.Replace(text, "$1[redacted]");
            text = Cookies.Replace(text, "$1[redacted]");
            return Keys.Replace(Credentials.Replace(Bearer.Replace(text, "$1[redacted]"), "$1[redacted]"), "[private key redacted]");
        }
        catch (RegexMatchTimeoutException) { return "[Payload omitted: diagnostic sanitization exceeded its time limit]"; }
    }

    public static string Excerpt(string sanitized, int maximum)
    {
        if (sanitized.Length <= maximum) return sanitized;
        var half = maximum / 2;
        return sanitized[..half] + $" [truncated: {sanitized.Length - maximum} characters omitted] " + sanitized[^half..];
    }
}
