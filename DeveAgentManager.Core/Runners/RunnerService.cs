using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

public sealed record CreateRunnerRequest(
    string Name,
    string ProviderId,
    string? TemplateId,
    int Cores,
    int MemoryMb,
    int DiskGb,
    IReadOnlyList<string> Harnesses,
    ConnectionDetails? Connection);

/// <summary>
/// Creates and manages Runners. Long operations (create, start, setup, delete) run in the background, one at a time
/// per Runner, and report progress in the Runner's setup log.
/// </summary>
public sealed partial class RunnerService(
    IDbContextFactory<ManagerDbContext> dbFactory,
    IEnumerable<IRunnerProvider> providers,
    RunnerSetupService setup,
    AgentService agents,
    RunnerShell shell,
    RunnerLogStore logs,
    ManagerEvents events,
    ManagerOptions options,
    ManagerSshKey sshKey,
    SecretProtector protector,
    IHostApplicationLifetime lifetime,
    ILogger<RunnerService> logger)
{
    private static readonly TimeSpan SshTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, Operation> _operations = new();
    private readonly Dictionary<string, IRunnerProvider> _providers = providers.ToDictionary(p => p.Id);

    public IReadOnlyCollection<IRunnerProvider> Providers => _providers.Values;

    public IRunnerProvider? FindProvider(string providerId) => _providers.GetValueOrDefault(providerId);

    /// <summary>The operation currently running for a Runner, e.g. "Creating", or null.</summary>
    public string? CurrentOperation(Guid runnerId) => _operations.GetValueOrDefault(runnerId)?.Name;

    /// <summary>Cancels the running operation (for example a setup that hangs). The Runner ends up in the Error state.</summary>
    public bool Cancel(Guid runnerId)
    {
        if (!_operations.TryGetValue(runnerId, out var operation) || operation.Name == "Deleting")
        {
            return false;
        }

        logs.For(runnerId).Warning($"Cancelling: {operation.Name.ToLowerInvariant()}...");
        operation.Cancellation.Cancel();
        return true;
    }

    public async Task<List<Runner>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Runners.AsNoTracking().Include(r => r.Agents).OrderBy(r => r.Name).ToListAsync(cancellationToken);
    }

    public async Task<Runner?> GetAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Runners.AsNoTracking().Include(r => r.Agents).FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken);
    }

    public async Task<Runner> CreateAsync(CreateRunnerRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim().ToLowerInvariant();
        if (!RunnerName().IsMatch(name))
        {
            throw new ArgumentException("Use lowercase letters, digits and dashes (it becomes the hostname), for example 'runner-01'.");
        }

        var provider = FindProvider(request.ProviderId) ?? throw new ArgumentException($"Unknown provider '{request.ProviderId}'.");
        if (!provider.IsConfigured)
        {
            throw new InvalidOperationException($"{provider.DisplayName} is not configured. Check Settings.");
        }

        if (provider.Capabilities.RequiresConnectionDetails && string.IsNullOrWhiteSpace(request.Connection?.Host))
        {
            throw new ArgumentException("Enter the host of the machine.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Runners.AnyAsync(r => r.Name == name, cancellationToken))
        {
            throw new ArgumentException($"A Runner named '{name}' already exists.");
        }

        var template = provider.GetTemplates().FirstOrDefault(t => t.Id == request.TemplateId);
        var now = DateTime.UtcNow;
        var runner = new Runner
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = provider.Id,
            TemplateId = template?.Id,
            Kind = template?.Kind ?? RunnerKind.Headless,
            State = RunnerState.Creating,
            StateMessage = "Queued",
            // Existing machines have whatever size they have.
            Cores = provider.Capabilities.SupportsSizing ? request.Cores : 0,
            MemoryMb = provider.Capabilities.SupportsSizing ? request.MemoryMb : 0,
            DiskGb = provider.Capabilities.SupportsSizing ? request.DiskGb : 0,
            Harnesses = string.Join(',', request.Harnesses),
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (request.Connection is { } connection)
        {
            runner.Host = connection.Host.Trim();
            runner.SshPort = connection.Port;
            runner.Username = connection.Username.Trim();
            runner.ProtectedPassword = protector.Protect(connection.Password);
        }
        else
        {
            runner.Username = options.RunnerUsername;
            runner.ProtectedPassword = protector.Protect(options.RunnerPassword);
        }

        db.Runners.Add(runner);
        await db.SaveChangesAsync(cancellationToken);
        events.RunnerChanged(runner.Id);

        StartOperation(runner.Id, "Creating", async (op, cancellation) =>
        {
            op.Log.Step($"Creating Runner '{runner.Name}' with {provider.DisplayName}");
            var request2 = new MachineCreateRequest(
                runner.Name,
                runner.TemplateId,
                runner.Cores,
                runner.MemoryMb,
                runner.DiskGb,
                new RunnerAccount(options.RunnerUsername, options.RunnerPassword, sshKey.PublicKey),
                request.Connection);

            await op.SetStateAsync(RunnerState.Creating, "Creating the machine");
            var handle = await provider.CreateMachineAsync(request2, async allocated =>
            {
                op.Runner.MachineId = allocated.MachineId;
                op.Runner.ProviderData = allocated.ProviderData;
                await op.SaveAsync();
            }, op.Log, cancellation);

            op.Runner.MachineId = handle.MachineId;
            op.Runner.ProviderData = handle.ProviderData;
            await op.SaveAsync();
            await BringOnlineAsync(op, provider, runSetup: true, cancellation);
        });

        return runner;
    }

    public void Start(Guid runnerId) => StartOperation(runnerId, "Starting", async (op, cancellation) =>
    {
        var provider = GetProvider(op.Runner);
        await BringOnlineAsync(op, provider, runSetup: op.Runner.SetupVersion is null, cancellation);
    });

    public void RerunSetup(Guid runnerId) => StartOperation(runnerId, "Setting up", async (op, cancellation) =>
    {
        var provider = GetProvider(op.Runner);
        await BringOnlineAsync(op, provider, runSetup: true, cancellation);
    });

    public void Stop(Guid runnerId) => StartOperation(runnerId, "Stopping", async (op, cancellation) =>
    {
        var provider = GetProvider(op.Runner);
        if (!provider.Capabilities.CanStartStop)
        {
            throw new InvalidOperationException($"{provider.DisplayName} Runners can't be stopped from the Manager.");
        }

        await op.SetStateAsync(RunnerState.Stopping, "Shutting down");
        shell.Invalidate(op.Runner.Id);
        await provider.StopMachineAsync(op.Runner.ToMachine(), op.Log, cancellation);
        await agents.MarkAllStoppedAsync(op.Runner.Id, cancellation);
        await op.SetStateAsync(RunnerState.Stopped, null);
        op.Log.Success("Runner stopped");
    });

    /// <summary>Removes the Runner. With <paramref name="destroyMachine"/> the machine itself is deleted too (Proxmox VM).</summary>
    public void Delete(Guid runnerId, bool destroyMachine) => StartOperation(runnerId, "Deleting", async (op, cancellation) =>
    {
        var provider = FindProvider(op.Runner.ProviderId);
        await op.SetStateAsync(RunnerState.Deleting, "Deleting");

        if (destroyMachine && provider is { Capabilities.CanDestroy: true } && op.Runner.MachineId is not null)
        {
            await provider.DestroyMachineAsync(op.Runner.ToMachine(), op.Log, cancellation);
        }
        else if (op.Runner.Host is not null && op.Runner.Agents.Count > 0)
        {
            // An existing machine keeps running: stop the agents we started on it.
            try
            {
                await agents.StopAllOnRunnerAsync(op.Runner, cancellation);
            }
            catch (Exception ex)
            {
                op.Log.Warning($"Could not stop the agents on the machine: {ex.Message}");
            }
        }

        shell.Invalidate(op.Runner.Id);
        await op.RemoveAsync();
        logs.Delete(op.Runner.Id);
        events.Publish(new ManagerEvent(ManagerEventKind.RunnerRemoved, op.Runner.Id));
    }, deleting: true, replaceRunning: true);

    public async Task ForgetHostKeyAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Runners.Where(r => r.Id == runnerId).ExecuteUpdateAsync(s => s.SetProperty(r => r.HostKeyFingerprint, (string?)null), cancellationToken);
        shell.Invalidate(runnerId);
        logs.For(runnerId).Warning("SSH host key forgotten; the next connection trusts the key it sees.");
        events.RunnerChanged(runnerId);
    }

    /// <summary>After a Manager restart, operations that were running are gone: mark those Runners so the user can retry.</summary>
    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var transient = new[] { RunnerState.Creating, RunnerState.Starting, RunnerState.WaitingForNetwork, RunnerState.SettingUp, RunnerState.Stopping, RunnerState.Deleting };
        var interrupted = await db.Runners.Where(r => transient.Contains(r.State)).ToListAsync(cancellationToken);
        foreach (var runner in interrupted)
        {
            runner.StateMessage = $"Interrupted ({Regex.Replace(runner.State.ToString(), "(?<!^)([A-Z])", " $1").ToLowerInvariant()}) because the Manager restarted. Use Re-run setup or Delete.";
            runner.State = RunnerState.Error;
            runner.UpdatedAt = DateTime.UtcNow;
            logs.For(runner.Id).Warning(runner.StateMessage);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Called by the monitor when a Runner's power state changed outside the Manager (e.g. in the Proxmox UI).</summary>
    public async Task ApplyObservedStatusAsync(Guid runnerId, MachineStatus status, CancellationToken cancellationToken)
    {
        if (CurrentOperation(runnerId) is not null)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken);
        if (runner is null)
        {
            return;
        }

        switch (status.PowerState)
        {
            case MachinePowerState.Stopped when runner.State == RunnerState.Ready:
                runner.State = RunnerState.Stopped;
                runner.StateMessage = "The machine was stopped outside the Manager.";
                shell.Invalidate(runner.Id);
                await agents.MarkAllStoppedAsync(runner.Id, cancellationToken);
                break;
            case MachinePowerState.Missing when runner.State is RunnerState.Ready or RunnerState.Stopped:
                runner.State = RunnerState.Error;
                runner.StateMessage = "The machine no longer exists on the provider.";
                break;
            case MachinePowerState.Running when runner.State == RunnerState.Stopped:
                Start(runner.Id);
                return;
            case MachinePowerState.Running when runner.State == RunnerState.Ready && status.Address is not null && status.Address != runner.Host:
                logs.For(runner.Id).Info($"Address changed from {runner.Host} to {status.Address}");
                runner.Host = status.Address;
                shell.Invalidate(runner.Id);
                break;
            default:
                return;
        }

        runner.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.RunnerChanged(runner.Id);
    }

    private async Task BringOnlineAsync(RunnerOperation op, IRunnerProvider provider, bool runSetup, CancellationToken cancellationToken)
    {
        var machine = op.Runner.ToMachine();
        if (provider.Capabilities.CanStartStop)
        {
            await op.SetStateAsync(RunnerState.Starting, "Starting the machine");
            await provider.StartMachineAsync(machine, op.Log, cancellationToken);
        }

        await op.SetStateAsync(RunnerState.WaitingForNetwork, "Waiting for an IP address");
        var address = await provider.WaitForAddressAsync(machine, op.Log, cancellationToken);
        if (address != op.Runner.Host)
        {
            shell.Invalidate(op.Runner.Id);
        }

        op.Runner.Host = address;
        await op.SaveAsync();

        await op.SetStateAsync(RunnerState.WaitingForNetwork, "Waiting for SSH");
        await setup.WaitForSshAsync(op.Runner, op.Log, SshTimeout, cancellationToken);

        if (runSetup)
        {
            await op.SetStateAsync(RunnerState.SettingUp, "Installing tools and harnesses");
            await setup.RunAsync(op.Runner, op.Log, cancellationToken);
            await op.SaveAsync();
        }

        await op.SetStateAsync(RunnerState.Ready, null);
        op.Log.Success($"Runner is ready at {op.Runner.Host}");
        await agents.RefreshRunnerAsync(op.Runner, cancellationToken);
    }

    private IRunnerProvider GetProvider(Runner runner) =>
        FindProvider(runner.ProviderId) ?? throw new InvalidOperationException($"The provider '{runner.ProviderId}' of this Runner is not available.");

    /// <param name="replaceRunning">Cancel the operation that is running (if any) and run this one after it stopped. Used by Delete.</param>
    private void StartOperation(Guid runnerId, string name, Func<RunnerOperation, CancellationToken, Task> work, bool deleting = false, bool replaceRunning = false)
    {
        var operation = new Operation(name, CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping));
        Operation? previous = null;
        if (replaceRunning)
        {
            _operations.AddOrUpdate(runnerId, operation, (_, running) =>
            {
                previous = running;
                return operation;
            });
            previous?.Cancellation.Cancel();
        }
        else if (!_operations.TryAdd(runnerId, operation))
        {
            operation.Cancellation.Dispose();
            throw new InvalidOperationException($"The Runner is busy ({CurrentOperation(runnerId)?.ToLowerInvariant()}). Wait for it to finish.");
        }

        events.RunnerChanged(runnerId);
        var cancellation = operation.Cancellation.Token;
        operation.Completion = Task.Run(async () =>
        {
            var log = logs.For(runnerId);
            try
            {
                if (previous?.Completion is { } running)
                {
                    await running;
                }

                await using var db = await dbFactory.CreateDbContextAsync(cancellation);
                var runner = await db.Runners.Include(r => r.Agents).FirstOrDefaultAsync(r => r.Id == runnerId, cancellation)
                    ?? throw new InvalidOperationException("The Runner no longer exists.");
                await work(new RunnerOperation(db, runner, log, events), cancellation);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                log.Warning("Interrupted because the Manager is shutting down.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                log.Warning($"{name} was cancelled.");
                await MarkFailedAsync(runnerId, $"{name} was cancelled.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Operation} failed for runner {RunnerId}", name, runnerId);
                log.Error(ex.Message);
                await MarkFailedAsync(runnerId, deleting ? $"Delete failed: {ex.Message}" : ex.Message);
            }
            finally
            {
                // Only remove our own entry: a Delete may already have replaced it.
                _operations.TryRemove(new KeyValuePair<Guid, Operation>(runnerId, operation));
                operation.Cancellation.Dispose();
                events.RunnerChanged(runnerId);
            }
        }, CancellationToken.None);
    }

    private async Task MarkFailedAsync(Guid runnerId, string message)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Runners.Where(r => r.Id == runnerId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, RunnerState.Error)
                .SetProperty(r => r.StateMessage, message)
                .SetProperty(r => r.UpdatedAt, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark runner {RunnerId} as failed", runnerId);
        }
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex RunnerName();

    private sealed class Operation(string name, CancellationTokenSource cancellation)
    {
        public string Name { get; } = name;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? Completion { get; set; }
    }

    private sealed class RunnerOperation(ManagerDbContext db, Runner runner, IRunnerLog log, ManagerEvents events)
    {
        public Runner Runner { get; } = runner;

        public IRunnerLog Log { get; } = log;

        public async Task SetStateAsync(RunnerState state, string? message)
        {
            Runner.State = state;
            Runner.StateMessage = message;
            await SaveAsync();
        }

        public async Task SaveAsync()
        {
            Runner.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            events.RunnerChanged(Runner.Id);
        }

        public async Task RemoveAsync()
        {
            db.Runners.Remove(Runner);
            await db.SaveChangesAsync();
        }
    }
}
