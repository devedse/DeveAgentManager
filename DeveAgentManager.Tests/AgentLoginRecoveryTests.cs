using DeveAgentManager.Contracts;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AgentLoginRecoveryTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("revoke", false)]
    [InlineData("disable", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("pending", true)]
    public async Task AccessLossStopsTheHarness_PreservesTheReason_AndAnExplicitSwitchRecovers(string change, bool useDefault)
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd") || !File.Exists("/usr/bin/tmux"), "Requires isolated OpenSSH and tmux.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        await using var sentinel = await RunnerShellIntegrationTests.Fixture.StartAsync();
        (await sentinel.Shell.RunAsync(sentinel.Runner, "tmux new-session -d -s sentinel 'sleep 120'", cancellationToken: ct)).EnsureSuccess("Starting isolated sentinel");
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHarness>(new Harness());
        collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = ssh.Root + "/manager", AdminPassword = "test-only-password" });
        collection.AddSingleton(ssh.Shell);
        await using var services = collection.BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        var agents = services.GetRequiredService<AgentService>();
        var accounts = services.GetRequiredService<AccountService>();
        var friend = new User { Id = Guid.NewGuid(), Username = "friend", DisplayName = "Friend" };
        var agent = new Agent { Id = Guid.NewGuid(), RunnerId = ssh.Runner.Id, OwnerId = User.BootstrapId, Name = "recovery-test",
            TmuxSession = "dam-recovery-" + Guid.NewGuid().ToString("N"), HarnessId = "login-test", HarnessSessionId = Guid.NewGuid().ToString(),
            WorkingDirectory = ssh.Root + "/home/work/project", State = AgentState.Stopped };
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
            db.Users.Add(friend);
            ssh.Runner.OwnerId = User.BootstrapId; ssh.Runner.State = RunnerState.Ready; ssh.Runner.Harnesses = "login-test";
            db.Runners.Add(ssh.Runner); db.Agents.Add(agent); await db.SaveChangesAsync(ct);
        }
        Directory.CreateDirectory(agent.WorkingDirectory);
        Directory.CreateDirectory(ssh.Root + "/home/.deveagentmanager");
        await File.WriteAllTextAsync(ssh.Root + "/home/.deveagentmanager/env.sh", "# test-only environment\n", ct);
        var login = await accounts.AddAsync(friend.Id, "login-test", "Shared test login", AccountKind.ApiKey, "test-only-key", ct);
        await accounts.SetGrantAsync(CurrentUser.From(friend), login.Id, User.BootstrapId, AccountAccess.Use, ct);
        if (useDefault) await accounts.SetDefaultAsync(TestUsers.Admin, login.Id, ct);
        else
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            (await db.Agents.SingleAsync(a => a.Id == agent.Id, ct)).AccountId = login.Id;
            await db.SaveChangesAsync(ct);
        }
        await agents.StartAsync(agent.Id, cancellationToken: ct);
        Assert.True((await agents.GetAsync(agent.Id, ct))!.LoginStatus!.CanStart);
        if (change == "pending")
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            (await db.Runners.SingleAsync(r => r.Id == ssh.Runner.Id, ct)).State = RunnerState.Stopped;
            await db.SaveChangesAsync(ct);
        }
        if (change is "revoke" or "pending") await accounts.SetGrantAsync(CurrentUser.From(friend), login.Id, User.BootstrapId, null, ct);
        else if (change == "disable") await accounts.SetEnabledAsync(CurrentUser.From(friend), login.Id, false, ct);
        else await accounts.DeleteAsync(CurrentUser.From(friend), login.Id, ct);
        await agents.RefreshRunnerAsync(ssh.Runner, ct);
        var stopped = (await agents.GetAsync(agent.Id, ct))!;
        Assert.True(stopped.LoginBlocked);
        var pending = change == "pending";
        var problem = pending ? AccountService.LoginStopPendingMessage : AccountService.LoginSelectionRequired;
        Assert.Equal(pending, stopped.LoginStopPending);
        Assert.Equal(problem, stopped.StateMessage);
        Assert.False(stopped.LoginStatus!.CanStart);
        Assert.Equal(problem, stopped.LoginStatus.Reason);
        if (!pending) Assert.NotEqual(AgentState.Running, stopped.State);
        Assert.Equal(pending ? "0" : "1", (await ssh.Shell.RunAsync(ssh.Runner, $"tmux display-message -p -t {ShellQuote.Quote("=" + agent.TmuxSession + ":")} '#{{pane_dead}}'", cancellationToken: ct)).Output.Trim());
        if (change == "delete") Assert.Null(stopped.AccountId);
        var replacement = await accounts.AddAsync(User.BootstrapId, "login-test", "Replacement test login", AccountKind.ApiKey, "replacement-test-only-key", ct);
        // Even a newly configured personal default does not silently replace a revoked binding.
        await accounts.SetDefaultAsync(TestUsers.Admin, replacement.Id, ct);
        if (pending)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            (await db.Runners.SingleAsync(r => r.Id == ssh.Runner.Id, ct)).State = RunnerState.Ready;
            await db.SaveChangesAsync(ct);
        }
        Assert.Equal(problem,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => agents.StartAsync(agent.Id, cancellationToken: ct))).Message);
        await agents.SwitchAsync(TestUsers.Admin, agent.Id, "login-test", replacement.Id, ct);
        var recovered = (await agents.GetAsync(agent.Id, ct))!;
        Assert.Equal(AgentState.Running, recovered.State);
        Assert.Equal(replacement.Id, recovered.RunningAccountId);
        Assert.Equal(agent.HarnessSessionId, recovered.HarnessSessionId);
        Assert.False(recovered.LoginBlocked); Assert.False(recovered.LoginStopPending);
        Assert.True(recovered.LoginStatus!.CanStart);
        // Choosing the already configured login on a stopped agent must start it, rather than return as a no-op.
        await agents.StopAsync(agent.Id, ct);
        await agents.SwitchAsync(TestUsers.Admin, agent.Id, "login-test", replacement.Id, ct);
        Assert.Equal(AgentState.Running, (await agents.GetAsync(agent.Id, ct))!.State);
        Assert.Equal("sentinel", (await sentinel.Shell.RunAsync(sentinel.Runner, "tmux list-sessions -F '#{session_name}'", cancellationToken: ct)).Output.Trim());
    }

    private sealed class Harness : IHarness
    {
        public string Id => "login-test";
        public string DisplayName => "Test harness";
        public string Description => "Disposable test process";
        public string InstallScript => "true";
        public string VersionCommand => "true";
        public IReadOnlyList<string> ConfigFiles => [];
        public HarnessAccountSupport AccountSupport { get; } = new("Test login", "", "", null, null, "TEST_KEY", null);
        public HarnessAccountSetup BuildAccountSetup(string accountFolder, AccountKind kind, string secret) => new([], "# test-only environment", null);
        public string BuildLaunchScript(HarnessLaunch launch) => "exec sleep 600";
    }
}
