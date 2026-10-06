using Microsoft.Extensions.Configuration;

namespace DeveAgentManager.Core.Configuration;

/// <summary>
/// Manager-wide settings. They come from environment variables (the compose file), using the same names as the
/// documentation, e.g. ADMIN_USERNAME or RUNNER_USERNAME.
/// </summary>
public sealed class ManagerOptions
{
    public const string DefaultAdminPassword = "change-me";

    public required string DataDirectory { get; init; }

    public string AdminUsername { get; init; } = "admin";

    public string AdminPassword { get; init; } = DefaultAdminPassword;

    public bool UsesDefaultAdminPassword => AdminPassword == DefaultAdminPassword;

    /// <summary>
    /// RESET_ADMIN_PASSWORD=true: at start-up, <see cref="AdminUsername"/> gets all roles and <see cref="AdminPassword"/>
    /// again (a way back in). ADMIN_USERNAME and ADMIN_PASSWORD otherwise only matter for the very first start.
    /// </summary>
    public bool ResetAdminPassword { get; init; }

    /// <summary>The Runner Account injected into new Runners (each gets a password of its own: see RunnerPasswords).</summary>
    public string RunnerUsername { get; init; } = "remoteman";

    public int DefaultCores { get; init; } = 4;

    public int DefaultMemoryMb { get; init; } = 8192;

    public int DefaultDiskGb { get; init; } = 64;

    /// <summary>The ADMIN_USERNAME user's git identity (see UserSettingsStore).</summary>
    public string? GitUserName { get; init; }

    public string? GitUserEmail { get; init; }

    /// <summary>
    /// The Manager's second port, which only serves VS Code. VS Code runs code from the Runner (extensions, and anything
    /// that took over code-server there), so it gets an origin of its own instead of running as the Portal.
    /// </summary>
    public int EditorPort { get; init; } = 8081;

    /// <summary>How browsers reach <see cref="EditorPort"/> when that isn't the Portal's host on that port (e.g. behind a proxy).</summary>
    public string? EditorUrl { get; init; }

    public string DatabasePath => Path.Combine(DataDirectory, "deveagentmanager.db");

    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    public string SshDirectory => Path.Combine(DataDirectory, "ssh");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string SessionsDirectory => Path.Combine(DataDirectory, "sessions");

    public static ManagerOptions FromConfiguration(IConfiguration configuration, string contentRootPath)
    {
        var dataDirectory = Value(configuration, "DATA_DIR");
        if (dataDirectory is null)
        {
            var inContainer = string.Equals(configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);
            dataDirectory = inContainer ? "/data" : Path.Combine(contentRootPath, "data");
        }

        return new ManagerOptions
        {
            DataDirectory = Path.GetFullPath(dataDirectory),
            AdminUsername = Value(configuration, "ADMIN_USERNAME") ?? "admin",
            AdminPassword = Value(configuration, "ADMIN_PASSWORD") ?? DefaultAdminPassword,
            ResetAdminPassword = string.Equals(Value(configuration, "RESET_ADMIN_PASSWORD"), "true", StringComparison.OrdinalIgnoreCase),
            RunnerUsername = Value(configuration, "RUNNER_USERNAME") ?? "remoteman",
            DefaultCores = Int(configuration, "RUNNER_DEFAULT_CORES", 4),
            DefaultMemoryMb = Int(configuration, "RUNNER_DEFAULT_MEMORY_MB", 8192),
            DefaultDiskGb = Int(configuration, "RUNNER_DEFAULT_DISK_GB", 64),
            GitUserName = Value(configuration, "GIT_USER_NAME"),
            GitUserEmail = Value(configuration, "GIT_USER_EMAIL"),
            EditorPort = Int(configuration, "EDITOR_PORT", 8081),
            EditorUrl = Value(configuration, "EDITOR_URL")?.TrimEnd('/'),
        };
    }

    private static string? Value(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int Int(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(Value(configuration, key), out var value) && value > 0 ? value : fallback;
}
