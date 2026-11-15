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

    /// <summary>Power state, address and uptime. CPU, memory and disk are read inside the Runner, not from the provider.</summary>
    Task<MachineStatus> GetStatusAsync(RunnerMachine machine, CancellationToken cancellationToken);

    /// <summary>Waits until the machine has an address the Manager can SSH to and returns it.</summary>
    Task<string> WaitForAddressAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken);

    /// <summary>
    /// The account the Manager logs in with once, with its own key, to create the Runner Account on machines the provider
    /// couldn't give one (Proxmox containers have no cloud-init). Null: the Runner Account already exists.
    /// </summary>
    string? AccountBootstrapUser(RunnerMachine machine) => null;

    /// <summary>True when someone with root on the host can pass a GPU render node into the machine.</summary>
    bool SupportsGpuPassthrough(RunnerMachine machine) => false;

    /// <summary>
    /// The command to run as root on the host to pass <paramref name="renderDevice"/> into the machine, owned by the
    /// machine's group <paramref name="renderGroupId"/>. Only called when <see cref="SupportsGpuPassthrough"/> is true.
    /// </summary>
    string GpuAttachCommand(RunnerMachine machine, string renderDevice, int renderGroupId) => throw new NotSupportedException();
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
