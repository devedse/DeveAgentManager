using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using DeveAgentManager.Harnesses.Grok;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public class NativeHistoryTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task GrokCompanionOnlyChangesSyncAuditAndExportWithTheAuthoritativeLog()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var options = new ManagerOptions { DataDirectory = fixture.Root + "/data" };
        Directory.CreateDirectory(options.DataDirectory);
        var factory = new Factory(options.DatabasePath);
        var runner = fixture.Runner; runner.OwnerId = User.BootstrapId; runner.State = RunnerState.Ready;
        var agent = new Agent { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "Grok test", Runner = runner,
            RunnerId = runner.Id, HarnessId = "grok", HarnessSessionId = GrokTests.Id, State = AgentState.Running, Working = true };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(Ct); db.Agents.Add(agent); await db.SaveChangesAsync(Ct);
        }
        var nativeFormat = new GrokSessionFormat();
        ISessionFormat format = new RootedFormat(nativeFormat, fixture.Root + "/home");
        var service = new SessionService(factory, fixture.Shell, [format], [new GrokHarness()], new SessionImageStore(options), new SessionFileStore(options),
            options, new ManagerEvents(), NullLogger<SessionService>.Instance);
        await service.AttachToAgentAsync(runner, agent, null, Ct);
        var saved = (await service.GetForAgentAsync(agent.Id, Ct))!;
        var remote = format.GetPath(GrokTests.Id, "/workspace", DateTime.UtcNow);
        var folder = Path.GetDirectoryName(remote)!; Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(remote, GrokTests.History + "\n", Ct);
        var cache = "{\"type\":\"user\",\"testOnly\":\"" + new string('x', 200_000) + "\"}\n";
        await File.WriteAllTextAsync(folder + "/chat_history.jsonl", cache, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        const string suffix = "{\"type\":\"assistant\",\"content\":\"test-only later cache write\"}\n";
        await File.AppendAllTextAsync(folder + "/chat_history.jsonl", suffix, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        var log = new NativeHistoryStore(options.SessionsDirectory);
        var companions = new SessionCompanionStore(options.SessionsDirectory);
        var state = (await log.SummaryAsync(saved.Id, Ct))!;
        Assert.True(await companions.HasPendingVerificationAsync(saved.Id, state.CompanionManifest, Ct));
        var export = await service.ExportAsync(saved.Id, "grok", Ct);
        Assert.EndsWith(".grok.json", export.FileName);
        var restored = nativeFormat.Read(export.ReadAllText());
        Assert.Equal(cache + suffix, nativeFormat.WriteCompanionFiles(restored, GrokTests.Id, "/workspace")["chat_history.jsonl"]);
        state = (await log.SummaryAsync(saved.Id, Ct))!;
        Assert.False(await companions.HasPendingVerificationAsync(saved.Id, state.CompanionManifest, Ct));
        var other = new User { Id = Guid.NewGuid(), Username = "grok-copy-owner", DisplayName = "Copy owner" };
        var copyAgent = new Agent { Id = Guid.NewGuid(), OwnerId = other.Id, Name = "Copy", RunnerId = runner.Id, HarnessId = "grok",
            HarnessSessionId = Guid.NewGuid().ToString(), WorkingDirectory = "/new-workspace" };
        await using (var db = factory.CreateDbContext()) { db.Users.Add(other); db.Agents.Add(copyAgent); await db.SaveChangesAsync(Ct); }
        await service.AttachToAgentAsync(runner, copyAgent, saved.Id, Ct);
        var copied = format.GetPath(copyAgent.HarnessSessionId, copyAgent.WorkingDirectory, DateTime.UtcNow);
        Assert.Equal(cache + suffix, await File.ReadAllTextAsync(Path.GetDirectoryName(copied)! + "/chat_history.jsonl", Ct));
        Assert.Equal(copyAgent.HarnessSessionId, nativeFormat.Read(await File.ReadAllTextAsync(copied, Ct)).Sources[0].SessionId);
    }

    [Fact(Timeout = 60_000)]
    public async Task SftpTransfersOnlySuffixVerifiesWholePrefixAndAllowsGrowthPastRequestedSnapshot()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var path = fixture.Root + "/history with spaces.jsonl";
        var prefix = Encoding.UTF8.GetBytes(new string('a', 1024 * 1024));
        await File.WriteAllBytesAsync(path, prefix, Ct);
        await File.AppendAllTextAsync(path, "new bytes 🌱", Ct);
        using var output = new MemoryStream();
        Assert.NotNull(await fixture.Shell.DownloadHistoryRangeAsync(fixture.Runner, path, prefix.Length, new FileInfo(path).Length,
            Convert.ToHexString(SHA256.HashData(prefix)), output, Ct));
        Assert.Equal("new bytes 🌱", Encoding.UTF8.GetString(output.ToArray()));
        using (var edit = File.OpenWrite(path)) edit.WriteByte((byte)'b'); // Edit outside the transferred suffix.
        output.SetLength(0);
        Assert.Null(await fixture.Shell.DownloadHistoryRangeAsync(fixture.Runner, path, prefix.Length, new FileInfo(path).Length,
            Convert.ToHexString(SHA256.HashData(prefix)), output, Ct));
        Assert.Equal(0, output.Length);
        await File.AppendAllTextAsync(path, "changed", Ct);
        output.SetLength(0);
        var requested = new FileInfo(path).Length;
        await File.AppendAllTextAsync(path, "appended after locate", Ct);
        var snapshot = await fixture.Shell.DownloadHistoryRangeAsync(fixture.Runner, path, 0, requested, null, output, Ct);
        Assert.Equal(requested, output.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(output.ToArray())), snapshot, ignoreCase: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task SampledSftpReadsKnownRangesAndRejectsChangedFingerprintsAndSampleMismatches()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var path = fixture.Root + "/history with spaces.jsonl";
        var prefix = Encoding.UTF8.GetBytes(new string('a', 256 * 1024));
        await File.WriteAllBytesAsync(path, prefix, Ct);
        await File.AppendAllTextAsync(path, "new bytes 🌱", Ct);
        async Task<string> Stamp() => (await fixture.Shell.RunAsync(fixture.Runner,
            $"stat -L -c '%d:%i:%y:%z' -- {ShellQuote.Quote(path)}", cancellationToken: Ct)).Output.TrimEnd('\r', '\n');
        var stamp = await Stamp();
        NativeHistorySample[] samples = [new(0, prefix[..4096]), new(prefix.Length - 65536, prefix[^65536..])];
        using var output = new MemoryStream();
        var commands = fixture.CommandSessions;
        Assert.True(await fixture.Shell.DownloadHistoryAppendAsync(fixture.Runner, path, prefix.Length, new FileInfo(path).Length, stamp, samples, output, Ct));
        Assert.Equal("new bytes 🌱", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(commands + 2, fixture.CommandSessions); // Two stat commands; no prefix/snapshot hash commands.
        using (var moving = new MutationStream(() => File.AppendAllTextAsync(path, "changed during transfer", Ct)))
            Assert.False(await fixture.Shell.DownloadHistoryAppendAsync(fixture.Runner, path, prefix.Length, new FileInfo(path).Length, stamp, samples, moving, Ct));
        using (var edit = File.OpenWrite(path)) edit.WriteByte((byte)'b');
        output.SetLength(0);
        Assert.False(await fixture.Shell.DownloadHistoryAppendAsync(fixture.Runner, path, prefix.Length, new FileInfo(path).Length, stamp, samples, output, Ct));
        Assert.False(await fixture.Shell.DownloadHistoryAppendAsync(fixture.Runner, path, prefix.Length, new FileInfo(path).Length, await Stamp(), samples, output, Ct));
        Assert.Equal(0, output.Length);
        await File.WriteAllBytesAsync(path + ".new", prefix, Ct);
        File.Move(path + ".new", path, true);
        Assert.False(await fixture.Shell.DownloadHistoryAppendAsync(fixture.Runner, path, prefix.Length, prefix.Length + 1, stamp, samples, output, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task SampledSessionExportsAndCopiesAuditMiddleEditsAndOfflineExportsCannotUsePendingHistory()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var options = new ManagerOptions { DataDirectory = fixture.Root + "/data" };
        Directory.CreateDirectory(options.DataDirectory);
        var factory = new Factory(options.DatabasePath);
        var runner = fixture.Runner; runner.State = RunnerState.Ready; runner.OwnerId = User.BootstrapId;
        var agent = new Agent { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "agent", Runner = runner,
            RunnerId = runner.Id, HarnessId = "claude", HarnessSessionId = "sampled-session", State = AgentState.Running, Working = true };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(Ct); db.Agents.Add(agent); await db.SaveChangesAsync(Ct);
        }
        var formats = new[] { new RootedFormat(new ClaudeSessionFormat(), fixture.Root + "/home") };
        var service = new SessionService(factory, fixture.Shell, formats, [], new SessionImageStore(options), new SessionFileStore(options),
            options, new ManagerEvents(), NullLogger<SessionService>.Instance);
        await service.AttachToAgentAsync(runner, agent, null, Ct);
        var saved = (await service.GetForAgentAsync(agent.Id, Ct))!;
        var folder = fixture.Root + "/home/.claude/projects/test";
        Directory.CreateDirectory(folder);
        var remote = folder + "/sampled-session.jsonl";
        var original = NativeHistoryStoreTests.Sample("claude") + "\n{\"type\":\"padding\",\"data\":\"" + new string('A', 192 * 1024) + "\"}\n";
        await File.WriteAllTextAsync(remote, original, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        var native = new NativeHistoryStore(options.SessionsDirectory);
        var appended = "{\"type\":\"queue-operation\",\"content\":\"next\"}\n";
        var changed = original[..90000] + 'B' + original[90001..] + appended;
        await File.WriteAllTextAsync(remote, changed, Ct); // Same inode, changed middle, matching sample ranges.
        await service.SyncRunnerAsync(runner, Ct);
        Assert.Empty((await native.SummaryAsync(saved.Id, Ct))!.Sha256);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Runners.SingleAsync(Ct)).State = RunnerState.Stopped; await db.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(saved.Id, "deve", Ct));
        await using (var db = factory.CreateDbContext())
        {
            (await db.Runners.SingleAsync(Ct)).State = RunnerState.Ready; await db.SaveChangesAsync(Ct);
        }
        var export = await service.ExportAsync(saved.Id, "deve", Ct);
        Assert.Equal(new ClaudeSessionFormat().Read(changed).ToJson(), DeveSession.FromJson(export.ReadAllText()).ToJson());
        Assert.NotEmpty((await native.SummaryAsync(saved.Id, Ct))!.Sha256);
        // A second unverified append must be audited before a shared conversation is copied.
        changed = changed[..95000] + 'C' + changed[95001..] + appended;
        await File.WriteAllTextAsync(remote, changed, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        Assert.Empty((await native.SummaryAsync(saved.Id, Ct))!.Sha256);
        var owner = new User { Id = Guid.NewGuid(), Username = "sampled-copy-owner", DisplayName = "Copy owner" };
        var copyAgent = new Agent { Id = Guid.NewGuid(), OwnerId = owner.Id, Name = "copy", RunnerId = runner.Id,
            HarnessId = "claude", HarnessSessionId = "sampled-copy", WorkingDirectory = "/workspace" };
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(owner); db.Agents.Add(copyAgent); await db.SaveChangesAsync(Ct);
        }
        await service.AttachToAgentAsync(runner, copyAgent, saved.Id, Ct);
        var copy = (await service.GetForAgentAsync(copyAgent.Id, Ct))!;
        Assert.Equal(new ClaudeSessionFormat().Read(changed).ToJson(), (await service.LoadAsync(copy.Id, Ct)).ToJson());
        Assert.NotEmpty((await native.SummaryAsync(saved.Id, Ct))!.Sha256);
        // A saved mode affects the existing SessionService on its next pass; neither service nor Manager restarts.
        var settings = new HistorySyncSettingsStore(factory, new ManagerEvents());
        await settings.SaveAsync(new(HistorySyncMode.Full), Ct);
        changed += appended;
        await File.WriteAllTextAsync(remote, changed, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        Assert.NotEmpty((await native.SummaryAsync(saved.Id, Ct))!.Sha256);
        await settings.SaveAsync(new(HistorySyncMode.Sampled), Ct);
        changed += appended;
        await File.WriteAllTextAsync(remote, changed, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        Assert.Empty((await native.SummaryAsync(saved.Id, Ct))!.Sha256);
    }

    [Fact(Timeout = 60_000)]
    public async Task RealSessionSyncMigratesLegacyStorageDetectsSameSizeEditsAndSupportsExportCopySwitchAndDelete()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var options = new ManagerOptions { DataDirectory = fixture.Root + "/data" };
        Directory.CreateDirectory(options.DataDirectory);
        var factory = new Factory(options.DatabasePath);
        var runner = fixture.Runner; runner.OwnerId = User.BootstrapId;
        var agent = new Agent { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "agent", Runner = runner,
            RunnerId = runner.Id, HarnessId = "claude", HarnessSessionId = "test-session", State = AgentState.Stopped };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(Ct); db.Agents.Add(agent); await db.SaveChangesAsync(Ct);
        }
        ISessionFormat[] formats = [new RootedFormat(new ClaudeSessionFormat(), fixture.Root + "/home"), new RootedFormat(new CodexSessionFormat(), fixture.Root + "/home"), new RootedFormat(new PiSessionFormat(), fixture.Root + "/home")];
        var service = new SessionService(factory, fixture.Shell, formats, [], new SessionImageStore(options), new SessionFileStore(options),
            options, new ManagerEvents(), NullLogger<SessionService>.Instance);
        await service.AttachToAgentAsync(runner, agent, null, Ct);
        var saved = (await service.GetForAgentAsync(agent.Id, Ct))!;
        Directory.CreateDirectory(options.SessionsDirectory);
        await File.WriteAllTextAsync(Path.Combine(options.SessionsDirectory, $"{saved.Id:N}.json"), new DeveSession().ToJson(), Ct);
        var folder = fixture.Root + "/home/.claude/projects/test";
        Directory.CreateDirectory(folder);
        var remote = folder + "/test-session.jsonl";
        var content = NativeHistoryStoreTests.Sample("claude");
        await File.WriteAllTextAsync(remote + ".source", content, Ct);
        File.CreateSymbolicLink(remote, remote + ".source");
        await service.SyncRunnerAsync(runner, Ct);
        Assert.False(File.Exists(Path.Combine(options.SessionsDirectory, $"{saved.Id:N}.json")));
        Assert.Equal(new ClaudeSessionFormat().Read(content).ToJson(), (await service.LoadAsync(saved.Id, Ct)).ToJson());
        // The unchanged-size early exit must not hide an in-place rewrite.
        content = content.Replace("hello there", "HELLO THERE", StringComparison.Ordinal);
        await File.WriteAllTextAsync(remote, content, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        Assert.Equal(new ClaudeSessionFormat().Read(content).ToJson(), (await service.LoadAsync(saved.Id, Ct)).ToJson());
        var extra = """{"type":"assistant","uuid":"extra","parentUuid":"a2","timestamp":"2026-10-18T12:00:00Z","message":{"content":[{"type":"text","text":"Latest reply"}]}}""" + "\n";
        await File.AppendAllTextAsync(remote, extra, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        var exported = await service.ExportAsync(saved.Id, "deve", Ct);
        Assert.Equal(new ClaudeSessionFormat().Read(content + extra).Entries.Count, DeveSession.FromJson(exported.ReadAllText()).Entries.Count);
        var previews = await service.ReplyPreviewsAsync(new(User.BootstrapId, "admin", "Admin", UserRoles.ConversationManager), Ct);
        Assert.Equal("Latest reply", Assert.Single(previews).Text);
        var imageStore = new SessionImageStore(options);
        var fileStore = new SessionFileStore(options);
        var imageId = Guid.NewGuid(); var fileId = Guid.NewGuid();
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
        var fileBytes = "shared file contents"u8.ToArray();
        await imageStore.SaveAsync(saved.Id, imageId, png, "image.png", "test image", Ct);
        using (var incoming = await fileStore.ReceiveAsync(fileBytes.Length, Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant(),
            async (output, ct) => await output.WriteAsync(fileBytes, ct), Ct))
            await fileStore.CommitAsync(saved.Id, fileId, incoming, "notes.txt", "test file", Ct);
        var other = new User { Id = Guid.NewGuid(), Username = "copy-owner", DisplayName = "Copy owner" };
        var copyAgent = new Agent { Id = Guid.NewGuid(), OwnerId = other.Id, Name = "copy", RunnerId = runner.Id,
            HarnessId = "claude", HarnessSessionId = "copied-session", WorkingDirectory = "/workspace" };
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(other); db.Agents.Add(copyAgent); await db.SaveChangesAsync(Ct);
        }
        await service.AttachToAgentAsync(runner, copyAgent, saved.Id, Ct);
        var copy = (await service.GetForAgentAsync(copyAgent.Id, Ct))!;
        Assert.NotEqual(saved.Id, copy.Id);
        Assert.Equal((await service.LoadAsync(saved.Id, Ct)).ToJson(), (await service.LoadAsync(copy.Id, Ct)).ToJson());
        Assert.Equal(png, (await imageStore.ReadAsync(copy.Id, imageId, Ct))!.Value.Data);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(fileStore.DataPath(copy.Id, fileId), Ct));

        var foreignExport = await service.ExportAsync(saved.Id, "codex", Ct);
        var exportedCalls = new CodexSessionFormat().Read(foreignExport.ReadAllText()).ActivePath().SelectMany(e => e.Content ?? []).Where(b => b.Type == "toolCall").ToList();
        Assert.Single(exportedCalls);
        Assert.Equal("Bash", exportedCalls[0].Name);
        var brokenFormats = formats.Select(f => f.HarnessId == "codex" ? new LossyFormat(f) : f).ToArray();
        var broken = new SessionService(factory, fixture.Shell, brokenFormats, [], imageStore, fileStore, options,
            new ManagerEvents(), NullLogger<SessionService>.Instance);
        var beforeFailure = (await service.LoadAsync(saved.Id, Ct)).ToJson();
        using (await service.LockAsync(Ct))
            await Assert.ThrowsAsync<SessionMigrationException>(() => broken.MoveToHarnessAsync(runner, agent, "codex", "rejected-session", Ct));
        Assert.Equal(beforeFailure, (await service.LoadAsync(saved.Id, Ct)).ToJson());
        Assert.Equal("claude", (await service.GetAsync(saved.Id, Ct))!.HarnessId);
        Assert.False(File.Exists(formats.Single(f => f.HarnessId == "codex").GetPath("rejected-session", agent.WorkingDirectory, DateTime.UtcNow)));
        using (await service.LockAsync(Ct))
            await service.MoveToHarnessAsync(runner, agent, "codex", "moved-session", Ct);
        // Backups are the exact native records, copied rather than loaded: one per attempt, also the rejected one.
        var backups = Directory.GetFiles(options.SessionsDirectory, $"{saved.Id:N}.*.claude.jsonl");
        Assert.Equal(2, backups.Length);
        foreach (var backup in backups) Assert.Equal(beforeFailure, new ClaudeSessionFormat().Read(await File.ReadAllTextAsync(backup, Ct)).ToJson());
        var moved = (await service.GetAsync(saved.Id, Ct))!;
        var movedPath = Directory.EnumerateFiles(fixture.Root + "/home/.codex/sessions", "rollout-*-moved-session.jsonl", SearchOption.AllDirectories).Single();
        var migrated = new CodexSessionFormat().Read(await File.ReadAllTextAsync(movedPath, Ct));
        Assert.Single(migrated.ActivePath().SelectMany(e => e.Content ?? []), b => b.Type == "toolCall");
        Assert.Equal("codex", moved.HarnessId);
        Assert.Equal("moved-session", moved.HarnessSessionId);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(movedPath)!, "*.dam-tmp-*"));
        // A native sync after switching keeps the content-free report separately from conversion backups.
        agent.HarnessId = "codex"; agent.HarnessSessionId = "moved-session";
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
            row.HarnessId = "codex"; row.HarnessSessionId = "moved-session"; await db.SaveChangesAsync(Ct);
        }
        await service.SyncRunnerAsync(runner, Ct);
        var afterSync = await service.LoadAsync(saved.Id, Ct);
        Assert.Equal(1, afterSync.Migration!.PreservedCalls);
        Assert.Equal("moved-session", afterSync.Migration.TargetSessionId);
        Assert.Equal("codex", afterSync.Sources.Last().Harness);
        // Selecting an older native Claude session restores Claude, regardless of the currently running Codex chat.
        var older = await service.ImportAsync(User.BootstrapId, "older.claude.jsonl", content, Ct);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Sessions.SingleAsync(s => s.Id == older.Id, Ct)).WorkspaceAgentId = agent.Id;
            var login = new Account { Id = Guid.NewGuid(), CreatedById = User.BootstrapId, HarnessId = "codex", Name = "Previous test login",
                Kind = AccountKind.ApiKey, ProtectedSecret = "test-only-unusable-secret", Enabled = true };
            db.Accounts.Add(login);
            var row = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
            row.AccountId = login.Id; row.RunningAccountId = login.Id;
            agent.AccountId = login.Id; agent.RunningAccountId = login.Id;
            await db.SaveChangesAsync(Ct);
        }
        using (await service.LockAsync(Ct))
            await service.SelectLockedAsync(runner, agent, older.Id, null, "restored-claude", Ct);
        Assert.Equal("claude", agent.HarnessId);
        var restored = (await service.GetAsync(older.Id, Ct))!;
        Assert.Equal("claude", restored.HarnessId);
        Assert.Equal("claude", restored.HarnessTrail);
        Assert.Equal("codex", (await service.GetAsync(saved.Id, Ct))!.HarnessId);
        var restoredPath = formats.Single(f => f.HarnessId == "claude").GetPath("restored-claude", agent.WorkingDirectory, DateTime.UtcNow);
        var restoredNative = new ClaudeSessionFormat().Read(await File.ReadAllTextAsync(restoredPath, Ct));
        Assert.Contains(restoredNative.ActivePath().SelectMany(e => e.Content ?? []), b => b.Type == "toolCall" && b.Id == "toolu_1");
        Assert.DoesNotContain("DAM session migration", await File.ReadAllTextAsync(restoredPath, Ct));
        Assert.Empty(Directory.GetFiles(options.SessionsDirectory, $"{older.Id:N}.*.json"));
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
            Assert.Equal("claude", row.HarnessId);
            Assert.Equal("restored-claude", row.HarnessSessionId);
            Assert.Null(row.AccountId);
            Assert.Null(row.RunningAccountId);
            Assert.Single(await db.Sessions.Where(s => s.AgentId == agent.Id).ToListAsync(Ct));
        }
        using (await service.LockAsync(Ct))
            await service.SelectLockedAsync(runner, agent, null, "new chat", "new-native", Ct);
        await service.DeleteAsync(saved.Id, Ct);
        Assert.False(Directory.Exists(Path.Combine(options.SessionsDirectory, "native", saved.Id.ToString("N"))));
        Assert.Empty(Directory.GetFiles(options.SessionsDirectory, $"{saved.Id:N}.*.json"));
        Assert.False(File.Exists(Path.Combine(options.SessionsDirectory, "migrations", $"{saved.Id:N}.json")));
        Assert.Equal(png, (await imageStore.ReadAsync(copy.Id, imageId, Ct))!.Value.Data);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(fileStore.DataPath(copy.Id, fileId), Ct));

        // Continuing an archive of a removed agent gets an independent artifact collection. The old source folder
        // may belong to an already-deleted session; its retained files still travel with a permitted continued copy.
        var artifacts = new AgentArtifacts(factory, options, fileStore, imageStore,
            new DeveAgentManager.Core.Users.AccessControl(factory), new ManagerEvents());
        await artifacts.PreserveAsync(agent.Id, Ct);
        var continuedAgent = new Agent { Id = Guid.NewGuid(), OwnerId = agent.OwnerId, Name = "continued archive", RunnerId = runner.Id,
            HarnessId = "claude", HarnessSessionId = "continued-archive", WorkingDirectory = "/continued-workspace" };
        await using (var db = factory.CreateDbContext())
        {
            await db.Agents.Where(a => a.Id == agent.Id).ExecuteDeleteAsync(Ct);
            db.Agents.Add(continuedAgent); await db.SaveChangesAsync(Ct);
        }
        await service.AttachToAgentAsync(runner, continuedAgent, older.Id, Ct);
        var continued = (await service.GetForAgentAsync(continuedAgent.Id, Ct))!;
        Assert.NotEqual(older.Id, continued.Id);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(fileStore.DataPath(continued.Id, fileId), Ct));
        var privateImage = Guid.NewGuid();
        await imageStore.SaveAsync(continued.Id, privateImage, png, "new.png", "New agent only", Ct);
        Assert.DoesNotContain(await artifacts.ListImagesAsync(older.Id, TestUsers.Admin, Ct), i => i.Id == privateImage);
    }

    [Fact(Timeout = 60_000)]
    public async Task CodexEmptyToolOutputSurvivesExportSwitchNativeSyncAndSwitchBackOverSsh()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var options = new ManagerOptions { DataDirectory = fixture.Root + "/data" };
        Directory.CreateDirectory(options.DataDirectory);
        var factory = new Factory(options.DatabasePath);
        var runner = fixture.Runner; runner.OwnerId = User.BootstrapId;
        var agent = new Agent { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "empty-output-agent", Runner = runner,
            RunnerId = runner.Id, HarnessId = "codex", HarnessSessionId = "empty-output-source", State = AgentState.Stopped, WorkingDirectory = "/workspace" };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(Ct); db.Agents.Add(agent); await db.SaveChangesAsync(Ct);
        }
        ISessionFormat[] formats = [new RootedFormat(new CodexSessionFormat(), fixture.Root + "/home"), new RootedFormat(new ClaudeSessionFormat(), fixture.Root + "/home")];
        var service = new SessionService(factory, fixture.Shell, formats, [], new SessionImageStore(options), new SessionFileStore(options),
            options, new ManagerEvents(), NullLogger<SessionService>.Instance);
        await service.AttachToAgentAsync(runner, agent, null, Ct);
        var stored = (await service.GetForAgentAsync(agent.Id, Ct))!;
        var sourcePath = formats[0].GetPath(agent.HarnessSessionId, agent.WorkingDirectory, DateTime.UtcNow);
        await fixture.Shell.UploadAtomicAsync(runner, sourcePath, SessionMigrationTests.CodexWithEmptyToolText, Ct);
        await service.SyncRunnerAsync(runner, Ct);
        var source = await service.LoadAsync(stored.Id, Ct);
        var expected = SessionMigration.Prepare(source, "Codex", "Claude Code", "claude");
        var export = await service.ExportAsync(stored.Id, "claude", Ct);
        SessionMigration.ValidateWritten(expected, new ClaudeSessionFormat().Read(export.ReadAllText()));
        using (await service.LockAsync(Ct))
            await service.MoveToHarnessAsync(runner, agent, "claude", "empty-output-claude", Ct);
        Assert.Equal(SessionMigrationTests.CodexWithEmptyToolText, await File.ReadAllTextAsync(sourcePath, Ct));
        var backup = Directory.GetFiles(options.SessionsDirectory, $"{stored.Id:N}.*.codex.jsonl").Single();
        Assert.Equal(SessionMigrationTests.CodexWithEmptyToolText, await File.ReadAllTextAsync(backup, Ct));
        var targetPath = formats[1].GetPath("empty-output-claude", agent.WorkingDirectory, DateTime.UtcNow);
        SessionMigration.ValidateWritten(expected, new ClaudeSessionFormat().Read(await File.ReadAllTextAsync(targetPath, Ct)));
        agent.HarnessId = "claude"; agent.HarnessSessionId = "empty-output-claude";
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
            row.HarnessId = agent.HarnessId; row.HarnessSessionId = agent.HarnessSessionId; await db.SaveChangesAsync(Ct);
        }
        await service.SyncRunnerAsync(runner, Ct);
        var synced = await service.LoadAsync(stored.Id, Ct);
        SessionMigration.ValidateWritten(expected, synced);
        var expectedBack = SessionMigration.Prepare(synced, "Claude Code", "Codex", "codex");
        using (await service.LockAsync(Ct))
            await service.MoveToHarnessAsync(runner, agent, "codex", "empty-output-back", Ct);
        var returnedPath = formats[0].GetPath("empty-output-back", agent.WorkingDirectory, DateTime.UtcNow);
        SessionMigration.ValidateWritten(expectedBack, new CodexSessionFormat().Read(await File.ReadAllTextAsync(returnedPath, Ct)));
        Assert.Equal("codex", (await service.GetAsync(stored.Id, Ct))!.HarnessId);
    }

    [Fact(Timeout = 60_000)]
    public async Task AtomicInstallationRejectsCorruptionAndKeepsThePreviousFile()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var path = fixture.Root + "/home/previous history.jsonl";
        await fixture.Shell.UploadAtomicAsync(fixture.Runner, path, "last good history", Ct);
        Assert.Equal("last good history", await File.ReadAllTextAsync(path, Ct));
        var bin = fixture.Root + "/bad-checksum";
        Directory.CreateDirectory(bin);
        var checksum = bin + "/sha256sum";
        await File.WriteAllTextAsync(checksum, "#!/bin/sh\nprintf 'incorrect  checksum\\n'\n", Ct);
        File.SetUnixFileMode(checksum, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = fixture.Root + "/home/.deveagentmanager/env.sh";
        Directory.CreateDirectory(Path.GetDirectoryName(environment)!);
        await File.WriteAllTextAsync(environment, $"export PATH={ShellQuote.Quote(bin)}:\"$PATH\"\n", Ct);
        await Assert.ThrowsAsync<RunnerCommandException>(() => fixture.Shell.UploadAtomicAsync(fixture.Runner, path, "corrupted new history", Ct));
        Assert.Equal("last good history", await File.ReadAllTextAsync(path, Ct));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.dam-tmp-*"));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Shell.UploadAtomicAsync(fixture.Runner, path, "interrupted history", canceled.Token));
        Assert.Equal("last good history", await File.ReadAllTextAsync(path, Ct));
    }

    private sealed class LossyFormat(ISessionFormat inner) : ISessionFormat
    {
        public string HarnessId => inner.HarnessId;
        public string LocateCommand(string id) => inner.LocateCommand(id);
        public string GetPath(string id, string cwd, DateTime at) => inner.GetPath(id, cwd, at);
        public DeveSession Read(string content) => inner.Read(content);
        public string Write(DeveSession session, string id, string cwd)
        {
            var damaged = DeveSession.FromJson(session.ToJson());
            foreach (var entry in damaged.Entries) entry.Content?.RemoveAll(b => b.Type is "toolCall" or "toolResult");
            return inner.Write(damaged, id, cwd);
        }
    }

    private sealed class MutationStream(Func<Task> mutate) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            await mutate();
        }
    }

    // The fixture overrides shell HOME; SFTP still starts in the operating system user's home.
    /// <summary>A format whose home folder is the fixture's. <paramref name="beforeWrite"/> runs as a conversion writes.</summary>
    internal sealed class RootedFormat(ISessionFormat inner, string home, Action? beforeWrite = null) : ISessionFormat
    {
        public string HarnessId => inner.HarnessId;
        public string LocateCommand(string id) => inner.LocateCommand(id);
        public string GetPath(string id, string cwd, DateTime at) => inner.GetPath(id, cwd, at).Replace("~/", home + "/", StringComparison.Ordinal);
        public DeveSession Read(string content) => inner.Read(content);
        public DeveSession Read(TextReader reader) => inner.Read(reader);
        public string Write(DeveSession session, string id, string cwd) { beforeWrite?.Invoke(); return inner.Write(session, id, cwd); }
        public void Write(DeveSession session, IEnumerable<DeveEntry> entries, string id, string cwd, TextWriter output) { beforeWrite?.Invoke(); inner.Write(session, entries, id, cwd, output); }
        public bool SupportsIncrementalRead => inner.SupportsIncrementalRead;
        public bool AppendsCompleteEntries => inner.AppendsCompleteEntries;
        public bool HasCompanionFiles => inner.HasCompanionFiles;
        public IReadOnlyList<SessionCompanionFile> CompanionFiles(DeveSession session) => inner.CompanionFiles(session);
        public IReadOnlyDictionary<string, string> WriteCompanionFiles(DeveSession session, string id, string cwd) => inner.WriteCompanionFiles(session, id, cwd);
        public void ApplyCompanionFiles(DeveSession session, IReadOnlyDictionary<string, string> files) => inner.ApplyCompanionFiles(session, files);
        public string Export(DeveSession session, string id, string cwd) => inner.Export(session, id, cwd);
        public string ExportExtension => inner.ExportExtension;
        public string ExportContentType => inner.ExportContentType;
        public void AppendLines(SessionReadCursor cursor, IEnumerable<System.Text.Json.Nodes.JsonObject> lines) => inner.AppendLines(cursor, lines);
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
