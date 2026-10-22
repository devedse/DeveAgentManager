using DeveAgentManager.Contracts;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Restarting a Runner (the Runner actions' Restart): the machine is shut down as with Stop, its agents marked stopped,
/// then started again as with Start. Providers that can't power machines (existing machines) refuse it.
/// </summary>
public sealed class RunnerRestartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restart_ShutsTheMachineDownThenStartsIt_AndIsRefusedWhereMachinesCantBePowered()
    {
        var data = Directory.CreateTempSubdirectory("dam-restart-").FullName;
        try
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = data, AdminPassword = "test-only-password" });
            var power = new PowerProvider();
            collection.AddSingleton<IRunnerProvider>(power);
            collection.AddSingleton<IHostApplicationLifetime>(new Lifetime());
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
            Runner Runner(string name, string provider) => new()
            {
                Id = Guid.NewGuid(), Name = name, ProviderId = provider, OwnerId = User.BootstrapId, MachineId = "30001", Host = "runner.test",
                Username = "runneruser", State = RunnerState.Error, SetupVersion = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var proxmoxLike = Runner("powered", power.Id);
            var existing = Runner("existing", "existing");
            var agent = new Agent { Id = Guid.NewGuid(), RunnerId = proxmoxLike.Id, OwnerId = User.BootstrapId, Name = "restart-test", HarnessId = "claude",
                TmuxSession = "dam-restart-test", HarnessSessionId = Guid.NewGuid().ToString("N"), WorkingDirectory = "/work/example", State = AgentState.Running };
            await using (var db = await factory.CreateDbContextAsync(Ct))
            {
                await db.Database.MigrateAsync(Ct);
                db.Runners.AddRange(proxmoxLike, existing);
                db.Agents.Add(agent);
                await db.SaveChangesAsync(Ct);
            }

            var runners = services.GetRequiredService<RunnerService>();
            var error = await runners.Restart(proxmoxLike.Id).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(PowerProvider.Stopped, error); // The test provider ends it once the machine is started.
            Assert.Equal(["stop", "start"], power.Calls);
            await using (var db = await factory.CreateDbContextAsync(Ct))
                Assert.Equal(AgentState.Stopped, (await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct)).State);

            Assert.Equal("Existing machine Runners can't be restarted from the Manager.", await runners.Restart(existing.Id).WaitAsync(TimeSpan.FromSeconds(30), Ct));
            Assert.Equal(["stop", "start"], power.Calls);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(data, recursive: true);
        }
    }

    private sealed class PowerProvider : IRunnerProvider
    {
        public const string Stopped = "The test stops after the machine was started.";
        public List<string> Calls { get; } = [];
        public string Id => "power-test";
        public string DisplayName => "Power test";
        public bool IsConfigured => true;
        public RunnerProviderCapabilities Capabilities { get; } = new(CanStartStop: true, CanDestroy: false, SupportsTemplates: false, SupportsSizing: false, RequiresConnectionDetails: false);
        public IReadOnlyList<RunnerTemplate> GetTemplates() => [];
        public IReadOnlyList<ProviderSetting> GetSettings() => [];
        public Task<IReadOnlyList<ProviderCheck>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCheck>>([]);
        public Task<MachineHandle> CreateMachineAsync(MachineCreateRequest request, Func<MachineHandle, Task> onAllocated, IRunnerLog log, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task StopMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }
        public Task StartMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            throw new InvalidOperationException(Stopped);
        }
        public Task DestroyMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MachineStatus> GetStatusAsync(RunnerMachine machine, CancellationToken cancellationToken) => Task.FromResult(new MachineStatus(MachinePowerState.Running));
        public Task<string> WaitForAddressAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
