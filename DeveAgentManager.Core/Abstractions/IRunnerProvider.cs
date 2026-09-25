namespace DeveAgentManager.Core.Abstractions;

/// <summary>
/// A place Runners come from (Proxmox today; Kubernetes, Azure, AWS later).
/// A provider only manages the machine itself. Everything that happens on the machine
/// (setup, agents, terminals) goes over SSH and is provider independent.
/// </summary>
public interface IRunnerProvider
{
    /// <summary>Stable id stored on every Runner, e.g. "proxmox".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>False when the provider is not configured (for example no PROXMOX_URL); it is then hidden when creating Runners.</summary>
    bool IsConfigured { get; }

    RunnerProviderCapabilities Capabilities { get; }

    IReadOnlyList<RunnerTemplate> GetTemplates();

    /// <summary>Settings shown read-only in the Portal (secrets already masked).</summary>
    IReadOnlyList<ProviderSetting> GetSettings();

    /// <summary>Checks the configuration and permissions and reports every check separately.</summary>
    Task<IReadOnlyList<ProviderCheck>> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates (but does not start) a machine. Call <paramref name="onAllocated"/> as soon as the machine id is known,
    /// so it is stored even if a later step fails and the machine can still be cleaned up.
    /// </summary>
    Task<MachineHandle> CreateMachineAsync(MachineCreateRequest request, Func<MachineHandle, Task> onAllocated, IRunnerLog log, CancellationToken cancellationToken);

    Task StartMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken);

    Task StopMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken);

    Task DestroyMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken);

    Task<MachineStatus> GetStatusAsync(RunnerMachine machine, CancellationToken cancellationToken);

    /// <summary>Waits until the machine has an address the Manager can SSH to and returns it.</summary>
    Task<string> WaitForAddressAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken);
}

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

/// <summary>The account that the provider should create on the machine (Proxmox injects it through cloud-init).</summary>
public sealed record RunnerAccount(string Username, string Password, string SshPublicKey);

/// <summary>Connection details for providers that register an existing machine instead of creating one.</summary>
public sealed record ConnectionDetails(string Host, int Port, string Username, string? Password);

public sealed record MachineCreateRequest(
    string Name,
    string? TemplateId,
    int Cores,
    int MemoryMb,
    int DiskGb,
    RunnerAccount Account,
    ConnectionDetails? Connection);

/// <summary>What the provider needs to find the machine again. <see cref="ProviderData"/> is provider specific JSON.</summary>
public sealed record MachineHandle(string MachineId, string? ProviderData);

public sealed record RunnerMachine(Guid RunnerId, string Name, string? MachineId, string? ProviderData, string? Host, int SshPort);

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
    string? Detail = null);
