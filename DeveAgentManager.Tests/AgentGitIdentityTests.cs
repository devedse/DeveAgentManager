using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Whose git identity an agent's harness runs with, on a real Runner (isolated sshd and tmux): the owner's until someone
/// switches it on purpose, kept by restarts, and dropped (the harness stopped, the files removed) when that person's
/// access to the agent ends.
/// </summary>
public sealed class AgentGitIdentityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120_000)]
    public async Task SwitchingIsExplicit_RestartsKeepIt_AndEndingTheShareStopsTheHarnessAndRemovesTheFiles()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd") || !File.Exists("/usr/bin/tmux"), "Requires isolated OpenSSH and tmux.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var launches = ssh.Root + "/launches";
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHarness>(new Harness(launches));
        collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = ssh.Root + "/manager", AdminPassword = "test-only-password" });
        var delayedSync = new DelayedIdentitySync();
        collection.AddDbContextFactory<ManagerDbContext>(options => options.AddInterceptors(delayedSync));
        collection.AddSingleton(ssh.Shell);
        await using var services = collection.BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        var agents = services.GetRequiredService<AgentService>();
        var other = new User { Id = Guid.NewGuid(), Username = "ketty", DisplayName = "Ketty" };
        var agent = new Agent { Id = Guid.NewGuid(), RunnerId = ssh.Runner.Id, OwnerId = User.BootstrapId, Name = "git-test",
            TmuxSession = "dam-git-test-" + Guid.NewGuid().ToString("N"), HarnessId = "git-test", HarnessSessionId = Guid.NewGuid().ToString("N"),
            WorkingDirectory = ssh.Root + "/home/work/project", State = AgentState.Stopped };
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await db.Database.MigrateAsync(Ct);
            db.Users.Add(other);
            // Ketty shares the agent read-write but has no access to its Runner.
            ssh.Runner.OwnerId = User.BootstrapId; ssh.Runner.State = RunnerState.Ready; ssh.Runner.Harnesses = "git-test";
            db.Runners.Add(ssh.Runner);
            db.Agents.Add(agent);
            db.AgentShares.Add(new() { AgentId = agent.Id, UserId = other.Id, Access = AgentAccess.Write });
            await db.SaveChangesAsync(Ct);
        }

        Directory.CreateDirectory(agent.WorkingDirectory);
        Directory.CreateDirectory(ssh.Root + "/home/.deveagentmanager");
        await File.WriteAllTextAsync(ssh.Root + "/home/.deveagentmanager/env.sh", "# test-only environment\n", Ct);
        var ketty = CurrentUser.From(other);
        var ownerConfig = ssh.Root + $"/home/.deveagentmanager/users/{User.BootstrapId:N}/gitconfig";
        var kettyFolder = ssh.Root + $"/home/.deveagentmanager/users/{other.Id:N}";
        string LastLaunch() => File.ReadAllLines(launches)[^1];
        async Task<Agent> SavedAsync()
        {
            await using var db = await factory.CreateDbContextAsync(Ct);
            return await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id, Ct);
        }

        try
        {
            await agents.StartAsync(agent.Id, cancellationToken: Ct);
            Assert.Equal(ownerConfig, await WaitForLaunchAsync(launches, 1));

            // A read-write user switches it to herself on purpose: the harness restarts with her identity.
            await agents.SetGitIdentityAsync(ketty, agent.Id, other.Id, Ct);
            Assert.Equal(kettyFolder + "/gitconfig", await WaitForLaunchAsync(launches, 2));
            var switched = await SavedAsync();
            Assert.Equal((other.Id, other.Id, AgentState.Running), (switched.GitIdentityUserId, switched.RunningGitIdentityUserId, switched.State));

            // Restarting, by anyone, keeps it.
            await agents.StartAsync(agent.Id, restart: true, cancellationToken: Ct);
            Assert.Equal(kettyFolder + "/gitconfig", await WaitForLaunchAsync(launches, 3));
            // Her files stay on the Runner while it uses them, though she has no access to the Runner.
            await services.GetRequiredService<RunnerUsers>().SyncAsync(ssh.Runner, Ct);
            Assert.True(File.Exists(kettyFolder + "/gitconfig"));
            // Nothing to enforce while she is its identity.
            await agents.EnforceGitIdentitiesAsync(Ct);
            Assert.Equal(AgentState.Running, (await SavedAsync()).State);

            // Pause an older sync after it chose the still-permitted identity, before writing its files.
            // Revocation and the next sync must not let those stale writes recreate her directory afterwards.
            delayedSync.HoldNext();
            var staleSync = services.GetRequiredService<RunnerUsers>().SyncAsync(ssh.Runner, Ct);
            await delayedSync.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            // Her share ends: the next start uses the owner's, and the harness still running with hers is stopped.
            await services.GetRequiredService<Sharing>().UnshareAgentAsync(ketty, agent.Id, other.Id, Ct);
            Assert.Null((await SavedAsync()).GitIdentityUserId);
            await agents.EnforceGitIdentitiesAsync(Ct);
            var stopped = await SavedAsync();
            Assert.Equal(AgentState.Stopped, stopped.State);
            Assert.Null(stopped.RunningGitIdentityUserId);
            Assert.Contains("no longer available", stopped.StateMessage);
            var pane = await ssh.Shell.RunAsync(ssh.Runner, $"tmux display-message -p -t {ShellQuote.Quote("=" + agent.TmuxSession + ":")} '#{{pane_dead}}' 2>/dev/null || echo gone", cancellationToken: Ct);
            Assert.Contains(pane.Output.Trim(), new[] { "1", "gone" });
            var cleanup = services.GetRequiredService<RunnerUsers>().SyncAsync(ssh.Runner, Ct);
            try
            {
                // A cleanup that overtakes the blocked sync could report success and then lose to its stale uploads.
                await Task.WhenAny(cleanup, Task.Delay(1000, Ct));
                Assert.False(cleanup.IsCompleted);
            }
            finally { delayedSync.Resume.TrySetResult(); }
            await Task.WhenAll(staleSync, cleanup);
            Assert.False(Directory.Exists(kettyFolder));

            await agents.StartAsync(agent.Id, cancellationToken: Ct);
            Assert.Equal(ownerConfig, await WaitForLaunchAsync(launches, 4));
            Assert.Equal(User.BootstrapId, (await SavedAsync()).RunningGitIdentityUserId);
            Assert.Equal(ownerConfig, LastLaunch());
        }
        finally
        {
            delayedSync.Resume.TrySetResult();
            await ssh.Shell.RunAsync(ssh.Runner, "tmux kill-session -t " + ShellQuote.Quote("=" + agent.TmuxSession) + " 2>/dev/null || true", cancellationToken: CancellationToken.None);
        }
    }

    // EnsureCore reads a username only after Sync has captured the set of permitted identities.
    private sealed class DelayedIdentitySync : DbCommandInterceptor
    {
        private int _hold;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNext() => Volatile.Write(ref _hold, 1);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT \"u\".\"Username\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _hold, 0) == 1)
            {
                Paused.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    // The launch log gets a line when the harness process starts, shortly after the launch script returns.
    private static async Task<string> WaitForLaunchAsync(string launches, int count)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(launches) && File.ReadAllLines(launches) is { } lines && lines.Length >= count) return lines[count - 1];
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Launch {count} was not recorded.");
    }

    // Records which git config the harness was started with.
    private sealed class Harness(string launches) : IHarness
    {
        public string Id => "git-test";
        public string DisplayName => "Git identity test";
        public string Description => "Disposable process used only in tests.";
        public string InstallScript => "true";
        public string VersionCommand => "true";
        public IReadOnlyList<string> ConfigFiles => [];
        public string BuildLaunchCommand(HarnessLaunch launch) =>
            "bash -c " + ShellQuote.Quote("printf '%s\\n' \"$GIT_CONFIG_GLOBAL\" >> " + ShellQuote.Quote(launches) + "; exec sleep 600");
    }
}
