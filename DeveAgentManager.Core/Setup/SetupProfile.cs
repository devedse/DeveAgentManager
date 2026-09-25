namespace DeveAgentManager.Core.Setup;

/// <summary>
/// What gets installed and configured on every Runner on top of the base setup: environment variables
/// (API keys), files (logins, settings, plugins) and an extra script. Stored encrypted.
/// </summary>
public sealed class SetupProfile
{
    public string? GitUserName { get; set; }

    public string? GitUserEmail { get; set; }

    public List<SetupEnvironmentVariable> EnvironmentVariables { get; set; } = [];

    public List<SetupFile> Files { get; set; } = [];

    /// <summary>Bash run as the Runner Account at the end of setup, e.g. <c>pi install npm:some-package</c>.</summary>
    public string? Script { get; set; }
}

public sealed class SetupEnvironmentVariable
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed class SetupFile
{
    /// <summary>Path on the Runner; "~/" means the Runner Account's home.</summary>
    public string Path { get; set; } = "";

    public string Content { get; set; } = "";

    /// <summary>Octal file mode.</summary>
    public string Mode { get; set; } = "600";
}
