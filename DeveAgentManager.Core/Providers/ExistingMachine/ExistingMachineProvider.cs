using System.Net.Sockets;
using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Providers.ExistingMachine;

/// <summary>
/// Uses a Linux machine that already exists (any VM, bare metal, WSL...). The Manager connects with the given login,
/// authorizes its own SSH key and runs the normal setup. It never creates, powers or deletes the machine.
/// </summary>
public sealed class ExistingMachineProvider : IRunnerProvider
{
    public const string ProviderId = "existing";

    public string Id => ProviderId;

    public string DisplayName => "Existing machine";

    public bool IsConfigured => true;

    public RunnerProviderCapabilities Capabilities { get; } = new(
        CanStartStop: false,
        CanDestroy: false,
        SupportsTemplates: false,
        SupportsSizing: false,
        RequiresConnectionDetails: true);

    public IReadOnlyList<RunnerTemplate> GetTemplates() => [];

    public IReadOnlyList<ProviderSetting> GetSettings() => [];

    public Task<IReadOnlyList<ProviderCheck>> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProviderCheck>>([new ProviderCheck("Available", CheckStatus.Ok, "Add any Debian/Ubuntu machine the Manager can reach over SSH.")]);

    public async Task<MachineHandle> CreateMachineAsync(MachineCreateRequest request, Func<MachineHandle, Task> onAllocated, IRunnerLog log, CancellationToken cancellationToken)
    {
        var connection = request.Connection ?? throw new ArgumentException("Connection details are required.");
        log.Info($"Using existing machine {connection.Username}@{connection.Host}:{connection.Port}");
        var handle = new MachineHandle(connection.Host, null);
        await onAllocated(handle);
        return handle;
    }

    public Task StartMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Existing machines are not powered by the Manager.");

    public Task DestroyMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Existing machines are never deleted by the Manager.");

    public async Task<MachineStatus> GetStatusAsync(RunnerMachine machine, CancellationToken cancellationToken)
    {
        if (machine.Host is null)
        {
            return new MachineStatus(MachinePowerState.Unknown);
        }

        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(machine.Host, machine.SshPort, timeout.Token);
            return new MachineStatus(MachinePowerState.Running, machine.Host);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new MachineStatus(MachinePowerState.Unknown, machine.Host, Detail: "SSH port not reachable");
        }
    }

    public Task<string> WaitForAddressAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken) =>
        Task.FromResult(machine.Host ?? throw new InvalidOperationException("The machine has no host."));
}
