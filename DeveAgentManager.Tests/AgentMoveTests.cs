using System.Text.Json;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AgentMoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120_000)]
    public async Task QueueRequiresOwnershipDestinationUseAndExclusiveWorkspace_AndBlocksConcurrentActions()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var f = await Fixture.CreateAsync();
        var actor = TestUsers.Plain(f.Other.Id, f.Other.Username);
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Agents.RequestMoveAsync(actor, f.Agent.Id, f.Target.Id, Ct));
        f.Target.OwnerId = actor.Id;
        db.Runners.Update(f.Target); await db.SaveChangesAsync(Ct);
        await db.Users.Where(u => u.Id == User.BootstrapId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Roles, UserRoles.None), Ct);
        var owner = TestUsers.Plain(User.BootstrapId, "admin");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Agents.RequestMoveAsync(owner, f.Agent.Id, f.Target.Id, Ct));
        db.RunnerMembers.Add(new() { RunnerId = f.Target.Id, UserId = User.BootstrapId, Access = RunnerAccess.Use });
        var sibling = new Agent { Id = Guid.NewGuid(), RunnerId = f.Source.Id, OwnerId = User.BootstrapId,
            Name = "overlapping", WorkingDirectory = f.Agent.WorkingDirectory + "/nested", HarnessId = "move-test" };
        db.Agents.Add(sibling); await db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Agents.RequestMoveAsync(TestUsers.Admin, f.Agent.Id, f.Target.Id, Ct));
        db.Agents.Remove(sibling); await db.SaveChangesAsync(Ct);
        await AgentMoveCoordination.Gate.WaitAsync(Ct);
        try { AgentMoveCoordination.OperatingRunners.Add(f.Target.Id); }
        finally { AgentMoveCoordination.Gate.Release(); }
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => f.Agents.RequestMoveAsync(TestUsers.Admin, f.Agent.Id, f.Target.Id, Ct)); }
        finally
        {
            await AgentMoveCoordination.Gate.WaitAsync(Ct);
            try { AgentMoveCoordination.OperatingRunners.Remove(f.Target.Id); }
            finally { AgentMoveCoordination.Gate.Release(); }
        }
        await f.Agents.RequestMoveAsync(TestUsers.Admin, f.Agent.Id, f.Target.Id, Ct);
        db.ChangeTracker.Clear();
        var queued = await db.Agents.SingleAsync(a => a.Id == f.Agent.Id, Ct);
        Assert.Equal(AgentState.Moving, queued.State);
        Assert.Equal(f.Source.Id, queued.RunnerId); // No early cutover or duplicate identity.
        Assert.Equal("Queued", JsonSerializer.Deserialize<AgentMoveState>(queued.MoveJson!)!.Phase);
        Assert.False(await f.Services.GetRequiredService<AccessControl>().CanPromptAgentAsync(f.Agent.Id, Ct));
        await Assert.ThrowsAsync<AgentMoveBusyException>(() => f.Agents.EnsureRunnerNotMovingAsync(f.Source.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Agents.StartAsync(f.Agent.Id, cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Agents.StopAsync(f.Agent.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Agents.DeleteAsync(f.Agent.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Agents.CreateAsync(TestUsers.Admin,
            new(f.Target.Id, "different-name", "move-test", AgentWorkspaceMode.ExistingFolder, ExistingPath: f.Agent.WorkingDirectory), Ct));
        Assert.Single(await db.Agents.ToListAsync(Ct));
        Assert.Equal("original workspace", await File.ReadAllTextAsync(f.Agent.WorkingDirectory + "/data.txt", Ct));
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedMoveFallsBackOnSourceAndPreservesIdentityHistorySharingAndPolicy(bool alreadyLaunched)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var f = await Fixture.CreateAsync();
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        var move = f.Move with { Phase = "Rollback", Resume = true, FallbackLaunchAttempted = alreadyLaunched };
        if (alreadyLaunched)
        {
            await f.Agents.StartAsync(f.Agent.Id, cancellationToken: Ct);
            await Task.Delay(300, Ct);
        }
        await db.Agents.Where(a => a.Id == f.Agent.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.RunnerId, f.Target.Id).SetProperty(a => a.State, AgentState.Moving)
            .SetProperty(a => a.MoveJson, JsonSerializer.Serialize(move)), Ct);
        await f.Agents.ExecuteMoveAsync(move, f.Ssh.Root + "/transport", Ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(f.Agent.Id, agent.Id);
        Assert.Equal(f.Source.Id, agent.RunnerId);
        Assert.Equal(f.Agent.HarnessSessionId, agent.HarnessSessionId);
        Assert.Equal(AgentState.Running, agent.State);
        Assert.Null(agent.MoveJson);
        Assert.Contains("source retained", agent.StateMessage);
        Assert.Equal("original workspace", await File.ReadAllTextAsync(f.Agent.WorkingDirectory + "/data.txt", Ct));
        Assert.Equal("launch\n", await File.ReadAllTextAsync(f.Ssh.Root + "/launches", Ct));
        Assert.Equal(f.Source.Name, (await db.Sessions.SingleAsync(Ct)).RunnerName);
        Assert.Single(await db.AgentShares.ToListAsync(Ct));
        Assert.True((await db.AgentAutoContinues.SingleAsync(Ct)).Enabled);
    }

    [Fact(Timeout = 120_000)]
    public async Task UncertainFallbackIsNotLaunchedAgainWithoutExplicitOwnerRecovery()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var f = await Fixture.CreateAsync();
        var move = f.Move with { Phase = "Rollback", Resume = true, FallbackLaunchAttempted = true };
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        await db.Agents.Where(a => a.Id == f.Agent.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.State, AgentState.Moving).SetProperty(a => a.MoveJson, JsonSerializer.Serialize(move)), Ct);
        await f.Agents.ExecuteMoveAsync(move, f.Ssh.Root + "/transport", Ct);
        var failed = await db.Agents.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("Failed", JsonSerializer.Deserialize<AgentMoveState>(failed.MoveJson!)!.Phase);
        Assert.False(File.Exists(f.Ssh.Root + "/launches"));
        Assert.False(await f.Services.GetRequiredService<AccessControl>().CanPromptAgentAsync(f.Agent.Id, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Agents.RecoverMoveAsync(TestUsers.Plain(f.Other.Id), f.Agent.Id, Ct));
        await f.Agents.RecoverMoveAsync(TestUsers.Admin, f.Agent.Id, Ct);
        failed = await db.Agents.AsNoTracking().SingleAsync(Ct);
        var recovery = JsonSerializer.Deserialize<AgentMoveState>(failed.MoveJson!)!;
        Assert.Equal("Recover", recovery.Phase); Assert.False(recovery.FallbackLaunchAttempted);
        await f.Agents.ExecuteMoveAsync(recovery, f.Ssh.Root + "/transport", Ct);
        var resumed = await db.Agents.AsNoTracking().SingleAsync(Ct);
        Assert.True(resumed.MoveJson is null, resumed.StateMessage);
        Assert.Equal("launch\n", await File.ReadAllTextAsync(f.Ssh.Root + "/launches", Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task UnknownDestinationLivenessPreventsSourceLaunch()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var f = await Fixture.CreateAsync();
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        await db.Runners.Where(r => r.Id == f.Target.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SshPort, 1), Ct);
        var move = f.Move with { Phase = "StartingTarget", Resume = true, TargetLaunchAttempted = true };
        await db.Agents.Where(a => a.Id == f.Agent.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.State, AgentState.Moving).SetProperty(a => a.MoveJson, JsonSerializer.Serialize(move)), Ct);
        await f.Agents.ExecuteMoveAsync(move, f.Ssh.Root + "/transport", Ct);
        var failed = await db.Agents.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("Failed", JsonSerializer.Deserialize<AgentMoveState>(failed.MoveJson!)!.Phase);
        Assert.False(File.Exists(f.Ssh.Root + "/launches"));
        Assert.Equal("original workspace", await File.ReadAllTextAsync(f.Agent.WorkingDirectory + "/data.txt", Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ChangedDestinationFilesDoNotBlockConfirmedSourceFallback_AndReserveCleanup()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var f = await Fixture.CreateAsync();
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        var move = f.Move with { Phase = "StartingTarget", Resume = true, TargetLaunchAttempted = true };
        var operation = f.Ssh.Root + "/home/.deveagentmanager/moves/" + move.Id;
        Directory.CreateDirectory(operation);
        // An installed destination whose contents no longer match the verified transfer.
        await File.WriteAllTextAsync(operation + "/manifest.json", JsonSerializer.Serialize(new[] { new { path = f.Agent.WorkingDirectory, remove = true, items = Array.Empty<object>() } }), Ct);
        await File.WriteAllTextAsync(operation + "/installed.json", JsonSerializer.Serialize(new[] { new { path = f.Agent.WorkingDirectory, stage = f.Agent.WorkingDirectory + ".missing-stage" } }), Ct);
        await db.Agents.Where(a => a.Id == f.Agent.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.State, AgentState.Moving).SetProperty(a => a.MoveJson, JsonSerializer.Serialize(move)), Ct);
        await f.Agents.ExecuteMoveAsync(move, f.Ssh.Root + "/transport", Ct);
        var restored = await db.Agents.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(AgentState.Running, restored.State);
        Assert.Equal(f.Source.Id, restored.RunnerId);
        Assert.Equal("TargetCleanup", JsonSerializer.Deserialize<AgentMoveState>(restored.MoveJson!)!.Phase);
        Assert.Equal("launch\n", await File.ReadAllTextAsync(f.Ssh.Root + "/launches", Ct));
        Assert.Equal("original workspace", await File.ReadAllTextAsync(f.Agent.WorkingDirectory + "/data.txt", Ct));
        await Assert.ThrowsAsync<AgentMoveBusyException>(() => f.Agents.EnsureRunnerNotMovingAsync(f.Target.Id, Ct, deleting: true));
    }

    private sealed class Harness(string launches) : IHarness
    {
        public string Id => "move-test";
        public string DisplayName => "Move test";
        public string Description => "Disposable process used only in tests.";
        public string InstallScript => "true";
        public string VersionCommand => "true";
        public IReadOnlyList<string> ConfigFiles => [];
        public string BuildLaunchCommand(HarnessLaunch launch) => "bash -c " + ShellQuote.Quote("printf 'launch\\n' >> " + ShellQuote.Quote(launches) + "; exec sleep 600");
    }

    private sealed class Fixture(RunnerShellIntegrationTests.Fixture ssh, ServiceProvider services, Agent agent, Runner target, User other) : IAsyncDisposable
    {
        internal RunnerShellIntegrationTests.Fixture Ssh => ssh;
        internal ServiceProvider Services => services;
        internal AgentService Agents => services.GetRequiredService<AgentService>();
        internal IDbContextFactory<ManagerDbContext> Factory => services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        internal Agent Agent => agent;
        internal Runner Source => ssh.Runner;
        internal Runner Target => target;
        internal User Other => other;
        internal AgentMoveState Move => new(Guid.NewGuid().ToString("N"), agent.Id, Source.Id, target.Id, agent.WorkingDirectory,
            agent.OwnerId.ToString("N"), agent.Id.ToString("N"), AgentState.Stopped, false, Home: ssh.Root + "/home");
        internal static async Task<Fixture> CreateAsync()
        {
            Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH.");
            var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IHarness>(new Harness(ssh.Root + "/launches"));
            collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = ssh.Root + "/manager", AdminPassword = "test-only-password" });
            collection.AddSingleton(ssh.Shell);
            var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
            await using var db = await factory.CreateDbContextAsync(Ct);
            await db.Database.MigrateAsync(Ct);
            var other = new User { Id = Guid.NewGuid(), Username = "other", DisplayName = "Other" };
            db.Users.Add(other);
            ssh.Runner.OwnerId = User.BootstrapId; ssh.Runner.State = RunnerState.Ready; ssh.Runner.Harnesses = "move-test";
            var target = new Runner { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "destination", State = RunnerState.Ready,
                Host = ssh.Runner.Host, SshPort = ssh.Runner.SshPort, Username = ssh.Runner.Username, HostKeyFingerprint = ssh.Runner.HostKeyFingerprint, Harnesses = "move-test" };
            var agent = new Agent { Id = Guid.NewGuid(), RunnerId = ssh.Runner.Id, OwnerId = User.BootstrapId, Name = "move-test",
                TmuxSession = "dam-move-test-" + Guid.NewGuid().ToString("N"), HarnessId = "move-test", HarnessSessionId = Guid.NewGuid().ToString("N"),
                WorkingDirectory = ssh.Root + "/home/work/project", State = AgentState.Stopped };
            db.Runners.AddRange(ssh.Runner, target); db.Agents.Add(agent);
            db.Sessions.Add(new() { Id = Guid.NewGuid(), AgentId = agent.Id, WorkspaceAgentId = agent.Id, OwnerId = agent.OwnerId,
                HarnessId = agent.HarnessId, HarnessSessionId = agent.HarnessSessionId, RunnerName = ssh.Runner.Name });
            db.AgentShares.Add(new() { AgentId = agent.Id, UserId = other.Id, Access = AgentAccess.Write });
            db.AgentAutoContinues.Add(new() { AgentId = agent.Id, Enabled = true, SessionId = agent.HarnessSessionId });
            await db.SaveChangesAsync(Ct);
            Directory.CreateDirectory(agent.WorkingDirectory);
            Directory.CreateDirectory(ssh.Root + "/home/.deveagentmanager");
            await File.WriteAllTextAsync(ssh.Root + "/home/.deveagentmanager/env.sh", "# test-only environment\n", Ct);
            await File.WriteAllTextAsync(agent.WorkingDirectory + "/data.txt", "original workspace", Ct);
            return new(ssh, services, agent, target, other);
        }
        public async ValueTask DisposeAsync()
        {
            try { await ssh.Shell.RunAsync(ssh.Runner, "tmux kill-session -t " + ShellQuote.Quote("=" + agent.TmuxSession) + " 2>/dev/null || true", cancellationToken: CancellationToken.None); }
            finally { await services.DisposeAsync(); await ssh.DisposeAsync(); }
        }
    }
}
