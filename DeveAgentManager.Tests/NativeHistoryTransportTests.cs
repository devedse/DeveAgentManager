using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public class NativeHistoryTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        Assert.Equal(new ClaudeSessionFormat().Read(content + extra).Entries.Count, DeveSession.FromJson(exported.Content).Entries.Count);
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
        using (await service.LockAsync(Ct))
            await service.MoveToHarnessAsync(runner, agent, "codex", "moved-session", Ct);
        Assert.Single(Directory.GetFiles(options.SessionsDirectory, $"{saved.Id:N}.*.json"));
        using (await service.LockAsync(Ct))
            await service.SelectLockedAsync(runner, agent, null, "new chat", "new-native", Ct);
        await service.DeleteAsync(saved.Id, Ct);
        Assert.False(Directory.Exists(Path.Combine(options.SessionsDirectory, "native", saved.Id.ToString("N"))));
        Assert.Empty(Directory.GetFiles(options.SessionsDirectory, $"{saved.Id:N}.*.json"));
        Assert.Equal(png, (await imageStore.ReadAsync(copy.Id, imageId, Ct))!.Value.Data);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(fileStore.DataPath(copy.Id, fileId), Ct));
    }

    // The fixture overrides shell HOME; SFTP still starts in the operating system user's home.
    private sealed class RootedFormat(ISessionFormat inner, string home) : ISessionFormat
    {
        public string HarnessId => inner.HarnessId;
        public string LocateCommand(string id) => inner.LocateCommand(id);
        public string GetPath(string id, string cwd, DateTime at) => inner.GetPath(id, cwd, at).Replace("~/", home + "/", StringComparison.Ordinal);
        public DeveSession Read(string content) => inner.Read(content);
        public DeveSession Read(TextReader reader) => inner.Read(reader);
        public string Write(DeveSession session, string id, string cwd) => inner.Write(session, id, cwd);
        public bool SupportsIncrementalRead => inner.SupportsIncrementalRead;
        public void AppendLines(SessionReadCursor cursor, IEnumerable<System.Text.Json.Nodes.JsonObject> lines) => inner.AppendLines(cursor, lines);
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
