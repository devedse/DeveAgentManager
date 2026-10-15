using System.Text.RegularExpressions;

namespace DeveAgentManager.Core.Agents;

public sealed record UsageLimitFailure(string Stage, string Kind, string Subject, string? Pattern = null,
    RegexOptions Options = RegexOptions.None, TimeSpan? Timeout = null);

/// <summary>An unavailable interpretation must never be treated as a successful no-limit observation.</summary>
public sealed record UsageLimitRead(ParsedUsageLimit? Limit, UsageLimitFailure? Failure = null)
{
    public bool Available => Failure is null;
    public string? Problem => Available ? null : "Usage-limit detection is temporarily unavailable. Saved settings are retained; retry detection.";

    internal static UsageLimitRead Capture(Func<ParsedUsageLimit?> parse)
    {
        try { return new(parse()); }
        catch (UsageLimitRegexException ex) { return new(null, ex.Failure); }
        catch (UsageLimitInputException ex) { return new(null, new(ex.Stage, "input-budget", ex.Subject)); }
        catch (RegexMatchTimeoutException ex) { return new(null, new("regex", "timeout", ex.Input, ex.Pattern, Timeout: ex.MatchTimeout)); }
    }
}

internal sealed class UsageLimitRegexException(string stage, string text, string pattern, RegexOptions options, TimeSpan timeout)
    : Exception("Usage-limit matching exceeded its budget.")
{
    public UsageLimitFailure Failure { get; } = new(stage, "timeout", text, pattern, options | RegexOptions.NonBacktracking, timeout);
}

internal sealed class UsageLimitInputException(string stage, string subject) : Exception("Usage-limit input exceeded its budget.")
{
    public string Stage { get; } = stage;
    public string Subject { get; } = subject;
}
