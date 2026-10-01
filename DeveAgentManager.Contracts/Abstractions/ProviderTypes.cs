namespace DeveAgentManager.Core.Abstractions;

public sealed record RunnerProviderCapabilities(
    bool CanStartStop,
    bool CanDestroy,
    bool SupportsTemplates,
    bool SupportsSizing,
    bool RequiresConnectionDetails);

public enum RunnerKind
{
    Headless,
    Desktop,
}

public sealed record RunnerTemplate(string Id, string DisplayName, RunnerKind Kind, string Description);

public sealed record ProviderSetting(string Name, string? Value, bool IsSecret = false);

public enum CheckStatus
{
    Ok,
    Warning,
    Failed,
}

public sealed record ProviderCheck(string Name, CheckStatus Status, string Message);

/// <summary>Connection details for providers that register an existing machine instead of creating one.</summary>
public sealed record ConnectionDetails(string Host, int Port, string Username, string? Password);

public enum MachinePowerState
{
    Unknown,
    Running,
    Stopped,
    Missing,
}

/// <param name="CpuUsage">Percent, 0-100.</param>
public sealed record MachineStatus(
    MachinePowerState PowerState,
    string? Address = null,
    double? CpuUsage = null,
    long? MemoryUsedBytes = null,
    long? MemoryTotalBytes = null,
    TimeSpan? Uptime = null,
    string? Detail = null,
    long? DiskUsedBytes = null,
    long? DiskTotalBytes = null);
