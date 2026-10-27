using System.Text.Json;
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
/// The session lock is held only briefly, on a real Runner (isolated sshd and tmux): status checks never need it, history
/// syncs transfer without it, and resuming or exporting a conversation writes it without it. Each step that does take it
/// rechecks what changed meanwhile.
/// </summary>
public sealed class SessionLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120_000)]
    public async Task StatusChecksRunWhileTheSessionLockIsHeld_AndDropAReadingAnOperationOverlapped()
    {
        Assert.SkipWhen(!Rig.Available, "Requires isolated OpenSSH and tmux.");
        await using var rig = await Rig.StartAsync();
        try
        {
            await rig.Agents.StartAsync(rig.Agent.Id, cancellationToken: Ct);
            using (await rig.Sessions.LockAsync(Ct))
                await rig.Agents.RefreshRunnerAsync(rig.Runner, Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            var observed = await rig.SavedAsync();
            Assert.True(observed.State == AgentState.Running, $"{observed.State}: {observed.StateMessage} / {await rig.PaneAsync()}");

            // The terminal is read while the agent is stopped: the reading is dropped, rather than reporting an unexpected exit.
            var reading = rig.Agents.RefreshRunnerAsync(rig.Runner, Ct, leadingScript: "sleep 2");
            await Task.Delay(300, Ct);
            await rig.Agents.StopAsync(rig.Agent.Id, Ct);
            await reading.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            var stopped = await rig.SavedAsync();
            Assert.Equal((AgentState.Stopped, (string?)null), (stopped.State, stopped.StateMessage));
        }
        finally { await rig.StopTmuxAsync(); }
    }

    [Fact(Timeout = 120_000)]
    public async Task HistorySyncTransfersWithoutTheSessionLock_AndRecordsOnlyAnUnchangedConversation()
    {
        Assert.SkipWhen(!Rig.Available, "Requires isolated OpenSSH and tmux.");
        await using var rig = await Rig.StartAsync();
        var stored = (await rig.Sessions.GetForAgentAsync(rig.Agent.Id, Ct))!;
        var synced = stored.LastSyncedAt;

        // New records are transferred while the lock is held, and recorded once it is free.
        await rig.AppendAsync("A later request");
        Task sync;
        using (await rig.Sessions.LockAsync(Ct))
        {
            sync = rig.Sessions.SyncRunnerAsync(rig.Runner, Ct);
            await rig.UntilAsync(async () => (await rig.Sessions.LoadAsync(stored.Id, Ct)).Entries.Any(e => e.PlainText().Contains("A later request")));
            Assert.Equal(synced, (await rig.Sessions.GetAsync(stored.Id, Ct))!.LastSyncedAt);
        }
        await sync.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var recorded = (await rig.Sessions.GetAsync(stored.Id, Ct))!;
        Assert.NotEqual(synced, recorded.LastSyncedAt);

        // A conversation that left the agent while it was transferred is not recorded.
        await rig.AppendAsync("Another request");
        using (await rig.Sessions.LockAsync(Ct))
        {
            sync = rig.Sessions.SyncRunnerAsync(rig.Runner, Ct);
            await rig.UntilAsync(async () => (await rig.Sessions.LoadAsync(stored.Id, Ct)).Entries.Any(e => e.PlainText().Contains("Another request")));
            await using var db = await rig.Factory.CreateDbContextAsync(Ct);
            await db.Sessions.Where(s => s.Id == stored.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentId, (Guid?)null), Ct);
        }
        await sync.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(recorded.LastSyncedAt, (await rig.Sessions.GetAsync(stored.Id, Ct))!.LastSyncedAt);
    }

    [Fact(Timeout = 120_000)]
    public async Task SyncsLeaveANewAgentsConversationToItsCreation()
    {
        // Creating an agent or workspace writes the conversation it continues without the lock; a sync meanwhile must not
        // give the new agent an empty one, which would take that conversation's place.
        Assert.SkipWhen(!Rig.Available, "Requires isolated OpenSSH and tmux.");
        await using var rig = await Rig.StartAsync();
        var created = new Agent { Id = Guid.NewGuid(), RunnerId = rig.Runner.Id, OwnerId = User.BootstrapId, Name = "being-created",
            TmuxSession = "dam-being-created", HarnessId = "codex", HarnessSessionId = "not-written-yet", WorkingDirectory = rig.Agent.WorkingDirectory + "-created", State = AgentState.Starting };
        await using (var db = await rig.Factory.CreateDbContextAsync(Ct))
        {
            db.Agents.Add(created);
            await db.SaveChangesAsync(Ct);
        }
        using (rig.Sessions.PrepareConversation(created.Id))
        {
            await rig.Sessions.SyncRunnerAsync(rig.Runner, Ct);
            Assert.Null(await rig.Sessions.GetForAgentAsync(created.Id, Ct));
        }
        // An agent left without one (its registration failed) still gets one.
        await rig.Sessions.SyncRunnerAsync(rig.Runner, Ct);
        Assert.NotNull(await rig.Sessions.GetForAgentAsync(created.Id, Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ResumingAConversationWritesItWithoutTheSessionLock_AndRefusesOtherActionsMeanwhile()
    {
        Assert.SkipWhen(!Rig.Available, "Requires isolated OpenSSH and tmux.");
        await using var rig = await Rig.StartAsync();
        try
        {
            var first = (await rig.Sessions.GetForAgentAsync(rig.Agent.Id, Ct))!;
            await rig.Agents.StartAsync(rig.Agent.Id, cancellationToken: Ct);
            await rig.Agents.SelectSessionAsync(rig.Agent.Id, null, "Second conversation", Ct);
            Assert.NotEqual(first.Id, (await rig.Sessions.GetForAgentAsync(rig.Agent.Id, Ct))!.Id);

            rig.Block = true;
            var resuming = rig.Agents.SelectSessionAsync(rig.Agent.Id, first.Id, null, Ct);
            await rig.Writing.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            // Writing it holds no session lock: other work goes on, and this agent waits for the change.
            using (await rig.Sessions.LockAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)) { }
            await rig.Agents.RefreshRunnerAsync(rig.Runner, Ct);
            var during = await rig.SavedAsync();
            Assert.Equal((AgentState.Starting, "Changing its conversation…"), (during.State, during.StateMessage));
            Assert.Contains("changing its conversation", (await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Agents.StartAsync(rig.Agent.Id, cancellationToken: Ct))).Message);
            rig.Release.Set();
            await resuming.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            var after = await rig.SavedAsync();
            Assert.Equal(AgentState.Running, after.State);
            var resumed = (await rig.Sessions.GetForAgentAsync(rig.Agent.Id, Ct))!;
            Assert.Equal((first.Id, after.HarnessSessionId), (resumed.Id, resumed.HarnessSessionId));
            // Written again record by record, exactly as the whole conversation would be, then the note that it continues in
            // another folder (its history ran in /workspace).
            var installed = await File.ReadAllTextAsync(rig.CodexPath(after.HarnessSessionId), Ct);
            var codex = new CodexSessionFormat();
            var expected = codex.Write(codex.Read(SessionMigrationTests.CodexWithEmptyToolText), after.HarnessSessionId, rig.Agent.WorkingDirectory);
            Assert.StartsWith(expected, installed);
            var note = Assert.Single(codex.Read(installed).ActivePath(), e => e.MigrationKind == ManagerNotes.FolderKind);
            Assert.Contains("Until now it ran in /workspace", note.PlainText());
        }
        finally { rig.Release.Set(); await rig.StopTmuxAsync(); }
    }

    [Fact(Timeout = 120_000)]
    public async Task ExportsRunWhileTheSessionLockIsHeld_AndStreamExactlyWhatTheWholeConversationGives()
    {
        Assert.SkipWhen(!Rig.Available, "Requires isolated OpenSSH and tmux.");
        await using var rig = await Rig.StartAsync();
        var stored = (await rig.Sessions.GetForAgentAsync(rig.Agent.Id, Ct))!;
        var whole = await rig.Sessions.LoadAsync(stored.Id, Ct);
        whole.Title = stored.Title;
        using (await rig.Sessions.LockAsync(Ct))
        {
            var deve = await rig.Sessions.ExportAsync(stored.Id, "deve", Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.Equal(JsonSerializer.Serialize(whole, new JsonSerializerOptions(DeveSession.JsonOptions) { WriteIndented = true }), deve.ReadAllText());
            var codex = await rig.Sessions.ExportAsync(stored.Id, "codex", Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.Equal(new CodexSessionFormat().Write(whole, stored.HarnessSessionId!, whole.Cwd!), codex.ReadAllText());
            var claude = await rig.Sessions.ExportAsync(stored.Id, "claude", Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            SessionMigration.ValidateWritten(SessionMigration.Prepare(whole, "Codex", "Claude Code", "claude"), new ClaudeSessionFormat().Read(claude.ReadAllText()));
        }
    }

    /// <summary>A Runner with a Codex agent whose conversation is synced; Codex writes can be held to look at the rest.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public static bool Available => OperatingSystem.IsLinux() && File.Exists("/usr/sbin/sshd") && File.Exists("/usr/bin/tmux");
        private RunnerShellIntegrationTests.Fixture _ssh = default!;
        private ServiceProvider _services = default!;
        public Runner Runner => _ssh.Runner;
        public Agent Agent { get; private set; } = default!;
        public AgentService Agents => _services.GetRequiredService<AgentService>();
        public SessionService Sessions => _services.GetRequiredService<SessionService>();
        public IDbContextFactory<ManagerDbContext> Factory => _services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        public bool Block { get; set; }
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        private string Home => _ssh.Root + "/home";
        // Where the agent's history was uploaded: a Codex path holds the second it was written.
        private string Source { get; set; } = default!;
        public string CodexPath(string id) => Assert.Single(NativeHistoryTransportTests.CodexHistories(Home, id));

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig { _ssh = await RunnerShellIntegrationTests.Fixture.StartAsync() };
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IHarness>(new Harness("codex", "Codex test")).AddSingleton<IHarness>(new Harness("claude", "Claude test"));
            collection.AddSingleton<ISessionFormat>(new NativeHistoryTransportTests.RootedFormat(new CodexSessionFormat(), rig.Home, () =>
            {
                if (!rig.Block) return;
                rig.Writing.TrySetResult();
                rig.Release.Wait(TimeSpan.FromSeconds(60));
            }));
            collection.AddSingleton<ISessionFormat>(new NativeHistoryTransportTests.RootedFormat(new ClaudeSessionFormat(), rig.Home));
            collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = rig._ssh.Root + "/manager", AdminPassword = "test-only-password" });
            collection.AddSingleton(rig._ssh.Shell);
            rig._services = collection.BuildServiceProvider();
            var project = Guid.NewGuid();
            rig.Agent = new Agent { Id = project, RunnerId = rig.Runner.Id, OwnerId = User.BootstrapId, Name = "lock-test",
                TmuxSession = "dam-lock-test-" + Guid.NewGuid().ToString("N"), HarnessId = "codex", HarnessSessionId = "empty-output-source",
                WorkingDirectory = rig.Home + "/work/project", State = AgentState.Stopped,
                Project = new Project { Id = project, RunnerId = rig.Runner.Id, OwnerId = User.BootstrapId, Name = "lock-test", RootDirectory = rig.Home + "/work/project" } };
            await using (var db = await rig.Factory.CreateDbContextAsync(Ct))
            {
                await db.Database.MigrateAsync(Ct);
                rig.Runner.OwnerId = User.BootstrapId; rig.Runner.State = RunnerState.Ready; rig.Runner.Harnesses = "codex,claude";
                db.Runners.Add(rig.Runner);
                db.Agents.Add(rig.Agent);
                await db.SaveChangesAsync(Ct);
            }
            rig.Agent.Project = null;
            Directory.CreateDirectory(rig.Agent.WorkingDirectory);
            Directory.CreateDirectory(rig.Home + "/.deveagentmanager");
            await File.WriteAllTextAsync(rig.Home + "/.deveagentmanager/env.sh", "# test-only environment\n", Ct);
            rig.Source = new CodexSessionFormat().GetPath(rig.Agent.HarnessSessionId, rig.Agent.WorkingDirectory, DateTime.UtcNow).Replace("~/", rig.Home + "/", StringComparison.Ordinal);
            await rig._ssh.Shell.UploadAtomicAsync(rig.Runner, rig.Source, SessionMigrationTests.CodexWithEmptyToolText, Ct);
            await rig.Sessions.AttachToAgentAsync(rig.Runner, rig.Agent, null, Ct);
            await rig.Sessions.SyncRunnerAsync(rig.Runner, Ct);
            return rig;
        }

        public async Task<Agent> SavedAsync()
        {
            await using var db = await Factory.CreateDbContextAsync(Ct);
            return await db.Agents.AsNoTracking().SingleAsync(a => a.Id == Agent.Id, Ct);
        }

        // The sample history has no final newline: a record appended to it starts its own line.
        public async Task AppendAsync(string request) => await File.AppendAllTextAsync(Source, (File.ReadAllText(Source).EndsWith('\n') ? "" : "\n")
            + """{"timestamp":"2026-10-27T12:00:00Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":""" + JsonSerializer.Serialize(request) + "}]}}\n", Ct);

        public async Task UntilAsync(Func<Task<bool>> condition)
        {
            for (var i = 0; i < 200; i++)
            {
                if (await condition()) return;
                await Task.Delay(50, Ct);
            }
            throw new TimeoutException("The condition was not met.");
        }

        public async Task<string> PaneAsync() => (await _ssh.Shell.RunAsync(Runner, "tmux list-panes -a -F '#{session_name} dead=#{pane_dead} status=#{pane_dead_status} cmd=#{pane_current_command}' 2>&1; tmux capture-pane -p -t " + ShellQuote.Quote("=" + Agent.TmuxSession) + " 2>&1 | tail -5", cancellationToken: CancellationToken.None)).Output;

        public Task StopTmuxAsync() => _ssh.Shell.RunAsync(Runner, "tmux kill-session -t " + ShellQuote.Quote("=" + Agent.TmuxSession) + " 2>/dev/null || true", cancellationToken: CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            Release.Set();
            await _services.DisposeAsync();
            await _ssh.DisposeAsync();
            Release.Dispose();
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
        public string BuildLaunchCommand(HarnessLaunch launch) => "sleep 600";
    }
}
