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

    /// <summary>The Runner Account injected into new Runners.</summary>
    public string RunnerUsername { get; init; } = "remoteman";

    public string RunnerPassword { get; init; } = "remoteman";

    public int DefaultCores { get; init; } = 4;

    public int DefaultMemoryMb { get; init; } = 8192;

    public int DefaultDiskGb { get; init; } = 64;

    /// <summary>Set in compose; when empty it can be set in the Portal.</summary>
    public string? GitHubPat { get; init; }

    public string? GitUserName { get; init; }

    public string? GitUserEmail { get; init; }

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
            RunnerUsername = Value(configuration, "RUNNER_USERNAME") ?? "remoteman",
            RunnerPassword = Value(configuration, "RUNNER_PASSWORD") ?? "remoteman",
            DefaultCores = Int(configuration, "RUNNER_DEFAULT_CORES", 4),
            DefaultMemoryMb = Int(configuration, "RUNNER_DEFAULT_MEMORY_MB", 8192),
            DefaultDiskGb = Int(configuration, "RUNNER_DEFAULT_DISK_GB", 64),
            GitHubPat = Value(configuration, "GITHUB_PAT"),
            GitUserName = Value(configuration, "GIT_USER_NAME"),
            GitUserEmail = Value(configuration, "GIT_USER_EMAIL"),
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
