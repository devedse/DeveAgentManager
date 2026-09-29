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

    /// <summary>Whether this provider offers a cheap per-machine CPU and memory read.</summary>
    bool SupportsUsageSampling => false;

    /// <summary>Reads usage without doing the full status/address lookup.</summary>
    Task<MachineStatus?> GetUsageAsync(RunnerMachine machine, CancellationToken cancellationToken) => Task.FromResult<MachineStatus?>(null);

    /// <summary>Waits until the machine has an address the Manager can SSH to and returns it.</summary>
    Task<string> WaitForAddressAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken);
}

/// <summary>The account that the provider should create on the machine (Proxmox injects it through cloud-init).</summary>
public sealed record RunnerAccount(string Username, string Password, string SshPublicKey);

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
