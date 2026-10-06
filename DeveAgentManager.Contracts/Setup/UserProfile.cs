namespace DeveAgentManager.Core.Setup;

/// <summary>
/// A user's own settings (My settings), stored encrypted and put on every Runner they can use: their git identity and
/// environment variables (API keys) for their agents and shells, and their SSH keys for logging in to those Runners.
/// Their GitHub token is kept apart, so it is never sent back to the browser.
/// </summary>
public sealed class UserProfile
{
    public const int MaxSshKeys = 20;

    public string? GitUserName { get; set; }

    public string? GitUserEmail { get; set; }

    public List<SetupEnvironmentVariable> EnvironmentVariables { get; set; } = [];

    /// <summary>Public keys as in authorized_keys, e.g. <c>ssh-ed25519 AAAA… ketty@laptop</c>.</summary>
    public List<string> SshKeys { get; set; } = [];

    /// <summary>A name bash can export: letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsValidVariableName(string name) =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Variables the Manager sets itself for every agent; a user's own value would break them.</summary>
    public static readonly IReadOnlySet<string> ReservedVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        "GIT_CONFIG_GLOBAL", "GH_TOKEN", "DAM_USER", "DAM_AGENT_ID", "DAM_SESSION_ID", "TZ", "HOME", "PATH",
    };

    /// <summary>Why this variable can't be used, or null. An empty row is fine: it is left out when saving.</summary>
    public string? Problem(SetupEnvironmentVariable variable)
    {
        var name = variable.Name.Trim();
        if (name.Length == 0)
        {
            return variable.Value.Length == 0 ? null : "Enter a name for this value.";
        }

        if (!IsValidVariableName(name))
        {
            return "Letters, digits and _ only, not starting with a digit.";
        }

        if (ReservedVariables.Contains(name))
        {
            return $"{name} is set by the Manager.";
        }

        return EnvironmentVariables.Count(v => v.Name.Trim() == name) > 1 ? $"{name} is listed twice." : null;
    }

    /// <summary>Why this public key can't go in authorized_keys, or null. An empty row is fine: it is left out when saving.</summary>
    public static string? SshKeyProblem(string key)
    {
        var line = key.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        if (line.Contains('\n') || line.Contains('\r'))
        {
            return "One key per row.";
        }

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && (parts[0].StartsWith("ssh-", StringComparison.Ordinal) || parts[0].StartsWith("ecdsa-", StringComparison.Ordinal)
                || parts[0].StartsWith("sk-", StringComparison.Ordinal)) && IsBase64(parts[1])
            ? null
            : "A public key like ssh-ed25519 AAAA… (the contents of a .pub file).";
    }

    /// <summary>Throws with a readable message for the first thing that can't be applied.</summary>
    public void Validate()
    {
        foreach (var variable in EnvironmentVariables)
        {
            if (Problem(variable) is { } problem)
            {
                throw new ArgumentException($"Environment variable '{variable.Name.Trim()}': {problem}");
            }
        }

        foreach (var key in SshKeys)
        {
            if (SshKeyProblem(key) is { } problem)
            {
                throw new ArgumentException($"SSH key: {problem}");
            }
        }

        if (SshKeys.Count(k => k.Trim().Length > 0) > MaxSshKeys)
        {
            throw new ArgumentException($"Add at most {MaxSshKeys} SSH keys.");
        }

        if ((GitUserName ?? "").Length > 200 || (GitUserEmail ?? "").Length > 200)
        {
            throw new ArgumentException("Use at most 200 characters for the git name and email.");
        }
    }

    /// <summary>Without empty rows, whitespace around names and keys, and line breaks in the git identity.</summary>
    public UserProfile Cleaned() => new()
    {
        GitUserName = OneLine(GitUserName),
        GitUserEmail = OneLine(GitUserEmail),
        EnvironmentVariables = EnvironmentVariables.Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => new SetupEnvironmentVariable { Name = v.Name.Trim(), Value = v.Value }).ToList(),
        SshKeys = SshKeys.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
    };

    private static string? OneLine(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsBase64(string text)
    {
        Span<byte> buffer = new byte[text.Length];
        return text.Length > 16 && Convert.TryFromBase64String(text, buffer, out _);
    }
}
