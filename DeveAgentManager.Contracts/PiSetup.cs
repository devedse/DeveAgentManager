using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Contracts;

/// <summary>The explicit, single-login pi preview; ordinary pi logins remain an independent choice.</summary>
public static class PiManagedLogin
{
    public static bool Supports(string harnessId, AccountKind kind) =>
        kind == AccountKind.Subscription && harnessId is "claude" or "codex";

    public static string Provider(string harnessId) => harnessId switch
    {
        "claude" => "claude-code",
        "codex" => "dam-codex",
        _ => throw new ArgumentException("Choose a Claude or Codex subscription login for pi.")
    };

    public static string Label(string harnessId) => harnessId == "claude" ? "Claude Code" : "Codex (legacy)";
}

/// <summary>Ordered model patterns, with null for inheritance and an empty list for all available models.</summary>
public static class PiModelScope
{
    public const int MaxPatterns = 64;

    public static string? Problem(IReadOnlyList<string>? patterns)
    {
        if (patterns is null) return null;
        if (patterns.Count > MaxPatterns) return $"Use at most {MaxPatterns} model patterns.";
        if (patterns.Any(p => p is null || p.Trim().Length > 200 || p.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ',')))
            return "Use one model pattern per line, without spaces or commas (at most 200 characters).";
        return null;
    }

    public static List<string>? Clean(IEnumerable<string>? patterns) => patterns?.Select(p => p.Trim())
        .Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}
