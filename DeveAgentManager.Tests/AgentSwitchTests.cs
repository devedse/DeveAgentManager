using DeveAgentManager.Contracts;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Switching an agent's harness on a real Runner (isolated sshd and tmux). Converting its conversation runs without the
/// session lock, so other work goes on; the switching agent refuses other actions until it is done; and a request that
/// is dropped halfway (a closed tab, a proxy timeout) doesn't leave the agent stopped.
/// </summary>
public sealed class AgentSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120_000)]
    public async Task ConversionRunsWithoutTheSessionLock_RefusesOtherActions_AndSurvivesADroppedRequest()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd") || !File.Exists("/usr/bin/tmux"), "Requires isolated OpenSSH and tmux.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var home = ssh.Root + "/home";
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHarness>(new Harness("codex", "Codex test")).AddSingleton<IHarness>(new Harness("claude", "Claude test"));
        collection.AddSingleton<ISessionFormat>(new NativeHistoryTransportTests.RootedFormat(new CodexSessionFormat(), home));
        // Holds the conversion while it writes the converted history, to look at everything else meanwhile.
        collection.AddSingleton<ISessionFormat>(new NativeHistoryTransportTests.RootedFormat(new ClaudeSessionFormat(), home,
            () => { converting.TrySetResult(); release.Wait(TimeSpan.FromSeconds(60)); }));
        collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = ssh.Root + "/manager", AdminPassword = "test-only-password" });
        collection.AddSingleton(ssh.Shell);
        await using var services = collection.BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        var agents = services.GetRequiredService<AgentService>();
        var sessions = services.GetRequiredService<SessionService>();
        var agent = new Agent { Id = Guid.NewGuid(), RunnerId = ssh.Runner.Id, OwnerId = User.BootstrapId, Name = "switch-test",
            TmuxSession = "dam-switch-test-" + Guid.NewGuid().ToString("N"), HarnessId = "codex", HarnessSessionId = "empty-output-source",
            WorkingDirectory = home + "/work/project", State = AgentState.Stopped };
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await db.Database.MigrateAsync(Ct);
            ssh.Runner.OwnerId = User.BootstrapId; ssh.Runner.State = RunnerState.Ready; ssh.Runner.Harnesses = "codex,claude";
            db.Runners.Add(ssh.Runner);
            db.Agents.Add(agent);
            await db.SaveChangesAsync(Ct);
        }

        Directory.CreateDirectory(agent.WorkingDirectory);
        Directory.CreateDirectory(home + "/.deveagentmanager");
        await File.WriteAllTextAsync(home + "/.deveagentmanager/env.sh", "# test-only environment\n", Ct);
        var source = new CodexSessionFormat().GetPath(agent.HarnessSessionId, agent.WorkingDirectory, DateTime.UtcNow).Replace("~/", home + "/", StringComparison.Ordinal);
        await ssh.Shell.UploadAtomicAsync(ssh.Runner, source, SessionMigrationTests.CodexWithEmptyToolText, Ct);
        await sessions.AttachToAgentAsync(ssh.Runner, agent, null, Ct);
        await sessions.SyncRunnerAsync(ssh.Runner, Ct);
        async Task<Agent> SavedAsync()
        {
            await using var db = await factory.CreateDbContextAsync(Ct);
            return await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id, Ct);
        }

        try
        {
            await agents.StartAsync(agent.Id, cancellationToken: Ct);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var switching = agents.SwitchAsync(TestUsers.Admin, agent.Id, "claude", null, request.Token);
            await converting.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // While it converts, the session lock is free: other syncs, starts and state reads go on.
            using (await sessions.LockAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)) { }
            await sessions.SyncRunnerAsync(ssh.Runner, Ct);
            await agents.RefreshRunnerAsync(ssh.Runner, Ct);
            var during = await SavedAsync();
            Assert.Equal((AgentState.Starting, "Switching to Claude test…", "codex"), (during.State, during.StateMessage, during.HarnessId));
            // This agent waits for the switch instead.
            Assert.Contains("switching", (await Assert.ThrowsAsync<InvalidOperationException>(() => agents.StartAsync(agent.Id, cancellationToken: Ct))).Message);
            Assert.Contains("switching", (await Assert.ThrowsAsync<InvalidOperationException>(() => agents.StopAsync(agent.Id, Ct))).Message);
            Assert.Contains("already switching", (await Assert.ThrowsAsync<InvalidOperationException>(() => agents.SwitchAsync(TestUsers.Admin, agent.Id, "claude", null, Ct))).Message);

            // The request is dropped (a closed tab): the switch still finishes.
            request.Cancel();
            release.Set();
            await switching.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var after = await SavedAsync();
            Assert.Equal(("claude", AgentState.Running, (string?)null), (after.HarnessId, after.State, after.StateMessage));
            var stored = (await sessions.GetForAgentAsync(agent.Id, Ct))!;
            Assert.Equal(("claude", after.HarnessSessionId), (stored.HarnessId, stored.HarnessSessionId));
            var installed = new ClaudeSessionFormat().GetPath(after.HarnessSessionId, agent.WorkingDirectory, DateTime.UtcNow).Replace("~/", home + "/", StringComparison.Ordinal);
            var migrated = new ClaudeSessionFormat().Read(await File.ReadAllTextAsync(installed, Ct));
            Assert.Equal(3, migrated.ActivePath().SelectMany(e => e.Content ?? []).Count(b => b.Type == "toolCall"));
            // The original stays on the Runner, and the Manager keeps its exact records as a backup.
            Assert.Equal(SessionMigrationTests.CodexWithEmptyToolText, await File.ReadAllTextAsync(source, Ct));
            var backup = Directory.GetFiles(ssh.Root + "/manager/sessions", $"{stored.Id:N}.*.codex.jsonl").Single();
            Assert.Equal(SessionMigrationTests.CodexWithEmptyToolText, await File.ReadAllTextAsync(backup, Ct));
            // Afterwards the agent takes actions again.
            await agents.StartAsync(agent.Id, restart: true, cancellationToken: Ct);
        }
        finally
        {
            release.Set();
            await ssh.Shell.RunAsync(ssh.Runner, "tmux kill-session -t " + ShellQuote.Quote("=" + agent.TmuxSession) + " 2>/dev/null || true", cancellationToken: CancellationToken.None);
        }
    }

    private sealed class Harness(string id, string name) : IHarness
    {
        public string Id => id;
        public string DisplayName => name;
        public string Description => "Disposable process used only in tests.";
        public string InstallScript => "true";
        public string VersionCommand => "true";
        public IReadOnlyList<string> ConfigFiles => [];
        public string BuildLaunchCommand(HarnessLaunch launch) => "exec sleep 600";
    }
}
