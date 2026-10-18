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

    public bool UsesDefaultAdminPassword => string.IsNullOrEmpty(AdminPassword) || AdminPassword == DefaultAdminPassword;

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

    /// <summary>
    /// Optional local editor-only port. Public deployments route separate Runner editor hostnames on the Portal port.
    /// </summary>
    public int EditorPort { get; init; } = 8081;

    /// <summary>Public editor origin template; {runner} is within the first hostname label, {host} comes from the public allowlist.</summary>
    public string? EditorUrl { get; init; }

    public string[] PublicOrigins { get; init; } = [];
    public string[] TrustedProxies { get; init; } = [];
    public bool AllowLocalAccess { get; init; } = true;
    public string[] ProxyIdentityHeaders { get; init; } = [];


    public string DatabasePath => Path.Combine(DataDirectory, "deveagentmanager.db");

    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    public string SshDirectory => Path.Combine(DataDirectory, "ssh");

    public bool MemoryDiagnosticsEnabled { get; init; }

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
            MemoryDiagnosticsEnabled = Value(configuration, "MEMORY_DIAGNOSTICS_ENABLED") is { } memorySetting
                ? string.Equals(memorySetting, "true", StringComparison.OrdinalIgnoreCase)
                : string.Equals(configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase),
            AdminUsername = Value(configuration, "ADMIN_USERNAME") ?? "admin",
            AdminPassword = Value(configuration, "ADMIN_PASSWORD") ?? DefaultAdminPassword,
            ResetAdminPassword = string.Equals(Value(configuration, "RESET_ADMIN_PASSWORD"), "true", StringComparison.OrdinalIgnoreCase),
            RunnerUsername = Value(configuration, "RUNNER_USERNAME") ?? "remoteman",
            DefaultCores = Int(configuration, "RUNNER_DEFAULT_CORES", 4),
            DefaultMemoryMb = Int(configuration, "RUNNER_DEFAULT_MEMORY_MB", 8192),
            DefaultDiskGb = Int(configuration, "RUNNER_DEFAULT_DISK_GB", 64),
            EditorPort = Int(configuration, "EDITOR_PORT", 8081),
            EditorUrl = Value(configuration, "EDITOR_URL")?.TrimEnd('/'),
            PublicOrigins = List(configuration, "PUBLIC_ORIGINS"),
            TrustedProxies = List(configuration, "TRUSTED_PROXIES"),
            ProxyIdentityHeaders = List(configuration, "PROXY_IDENTITY_HEADERS"),
            AllowLocalAccess = !string.Equals(Value(configuration, "ALLOW_LOCAL_ACCESS"), "false", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string[] List(IConfiguration configuration, string key) =>
        (Value(configuration, key) ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? Value(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int Int(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(Value(configuration, key), out var value) && value > 0 ? value : fallback;
}
