#pragma warning disable xUnit1051
using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Files agents share with dam-tools share-file: received, checked, stored with their conversation.</summary>
public sealed class SessionFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dam-files-").FullName;
    private ManagerOptions Options => new() { DataDirectory = _folder };
    private string Incoming => Path.Combine(Options.SessionsDirectory, "files", ".incoming");

    private static (byte[] Data, string Sha256) Content(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        return (data, Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    private static Func<Stream, CancellationToken, Task> Writes(byte[] data) => (target, ct) => target.WriteAsync(data, ct).AsTask();

    private static async Task<SessionFileDto> SaveAsync(SessionFileStore store, Guid sessionId, string text, string name = "app.apk", string caption = "", DateTime? at = null, Guid? id = null)
    {
        var (data, sha) = Content(text);
        using var incoming = await store.ReceiveAsync(data.Length, sha, Writes(data), default);
        return await store.CommitAsync(sessionId, id ?? Guid.NewGuid(), incoming, name, caption, default, at);
    }

    [Fact]
    public async Task ReceivedFilesAreCommittedOnce_PersistAcrossRestart_AndListNewestFirst()
    {
        var store = new SessionFileStore(Options);
        var session = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var middle = await SaveAsync(store, session, "middle", at: now.AddMinutes(-1));
        var newest = await SaveAsync(store, session, "newest build", "../../app-release.apk", "Debug\nbuild", now);
        var oldest = await SaveAsync(store, session, "oldest", at: now.AddMinutes(-2));

        var restarted = new SessionFileStore(Options);
        Assert.Equal(new[] { newest, middle, oldest }, await restarted.ListAsync(session, default));
        Assert.Empty(await restarted.ListAsync(Guid.NewGuid(), default));
        Assert.Equal(("app-release.apk", "Debug build", 12L, Content("newest build").Sha256), (newest.FileName, newest.Caption, newest.Bytes, newest.Sha256));
        Assert.Equal("newest build", await File.ReadAllTextAsync(restarted.DataPath(session, newest.Id)));

        // Importing the same file again (the Runner never got the acknowledgement) keeps the first and discards the copy.
        var (data, sha) = Content("something else");
        var again = await restarted.ReceiveAsync(data.Length, sha, Writes(data), default);
        Assert.Equal(newest, await restarted.CommitAsync(session, newest.Id, again, "other.apk", "other", default));
        again.Dispose();
        Assert.Empty(Directory.EnumerateFiles(Incoming));
        Assert.Equal("newest build", await File.ReadAllTextAsync(restarted.DataPath(session, newest.Id)));
    }

    [Fact]
    public async Task ContentThatIsNotWhatWasShared_IsRefusedAndLeavesNothingBehind()
    {
        var store = new SessionFileStore(Options);
        var (data, sha) = Content("an installer");
        var other = Content("another file").Sha256;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReceiveAsync(data.Length, other, Writes(data), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReceiveAsync(data.Length + 1, sha, Writes(data), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReceiveAsync(data.Length - 1, sha, Writes(data), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReceiveAsync(data.Length, sha.ToUpperInvariant(), Writes(data), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReceiveAsync(0, sha, Writes([]), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReceiveAsync(SessionFileStore.MaxBytes + 1, sha, Writes(data), default));
        // A failing transfer leaves no partial file either.
        await Assert.ThrowsAsync<IOException>(() => store.ReceiveAsync(data.Length, sha, (_, _) => throw new IOException("connection lost"), default));
        Assert.Empty(Directory.EnumerateFiles(Incoming));
    }

    [Fact]
    public async Task DeletingRemovesFiles_AndCopiesKeepIdentityAndContent()
    {
        var store = new SessionFileStore(Options);
        var session = Guid.NewGuid();
        var kept = await SaveAsync(store, session, "kept", "kept.zip", "Archive", DateTime.UtcNow.AddHours(-1));
        var removed = await SaveAsync(store, session, "removed");
        Assert.True(await store.DeleteAsync(session, removed.Id, default));
        Assert.False(await store.DeleteAsync(session, removed.Id, default));
        Assert.Equal([kept], await store.ListAsync(session, default));

        var copy = Guid.NewGuid();
        await store.CopyAsync(session, copy, default);
        await store.CopyAsync(session, copy, default);
        Assert.Equal([kept with { SessionId = copy }], await store.ListAsync(copy, default));
        Assert.Equal("kept", await File.ReadAllTextAsync(store.DataPath(copy, kept.Id)));

        store.DeleteSession(session);
        Assert.Empty(await store.ListAsync(session, default));
        Assert.Single(await store.ListAsync(copy, default));
    }

    [Theory]
    [InlineData("app-release.apk", "application/vnd.android.package-archive")]
    [InlineData("Setup.EXE", "application/octet-stream")]
    [InlineData("report.html", "application/octet-stream")]
    [InlineData("image.svg", "application/octet-stream")]
    [InlineData("build.zip", "application/zip")]
    [InlineData("no-extension", "application/octet-stream")]
    public void DownloadsOnlyUseTypesThatDoNotRunInThePage(string name, string type) => Assert.Equal(type, SessionFileStore.DownloadType(name));

    [Fact]
    public async Task Import_OnlyBringsFilesIntoTheSharingAgentsConversation_AndDropsWhatCanNeverArrive()
    {
        var factory = new Factory(Path.Combine(_folder, "test.db"));
        var runnerId = Guid.NewGuid(); var agentId = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Runners.Add(new Runner { OwnerId = User.BootstrapId, Id = runnerId, Name = "builder" });
            db.Agents.Add(new Agent { OwnerId = User.BootstrapId, Id = agentId, RunnerId = runnerId, Name = "agent" });
            db.Sessions.Add(new StoredSession { OwnerId = User.BootstrapId, Id = sessionId, WorkspaceAgentId = agentId, AgentId = agentId });
            await db.SaveChangesAsync();
        }

        var events = new ManagerEvents();
        var changed = new List<Guid?>();
        events.Changed += e => changed.Add(e.SessionId);
        var store = new SessionFileStore(Options);
        var sessions = new SessionService(factory, null!, [], [], new SessionImageStore(Options), store, Options, events, NullLogger<SessionService>.Instance);
        var files = new SessionFiles(factory, null!, sessions, store, events, NullLogger<SessionFiles>.Instance);
        var (data, sha) = Content("PK android package");
        var downloads = 0;
        Func<Stream, CancellationToken, Task<bool>> download = async (target, ct) => { downloads++; await target.WriteAsync(data, ct); return true; };
        SessionFiles.Publication Shared(Guid? agent = null, string? hash = null) =>
            new(Guid.NewGuid(), agent ?? agentId, sessionId, "app-release.apk", "Debug build", data.Length, hash ?? sha);

        // Another Runner's agent can't put files in this conversation; the outbox entry is dropped without a transfer.
        Assert.True(await files.ImportAsync(Guid.NewGuid(), Shared(), download, default));
        Assert.True(await files.ImportAsync(runnerId, Shared(agent: Guid.NewGuid()), download, default));
        Assert.Equal(0, downloads);

        var item = Shared();
        Assert.True(await files.ImportAsync(runnerId, item, download, default));
        var imported = Assert.Single(await store.ListAsync(sessionId, default));
        Assert.Equal((item.Id, "app-release.apk", "Debug build", sha), (imported.Id, imported.FileName, imported.Caption, imported.Sha256));
        Assert.Contains(sessionId, changed);
        // Already there (the acknowledgement was lost): acknowledged again without another transfer.
        Assert.True(await files.ImportAsync(runnerId, item, download, default));
        Assert.Equal(1, downloads);

        // Changed after sharing, or gone: dropped. A lost connection: kept on the Runner for the next try.
        Assert.True(await files.ImportAsync(runnerId, Shared(hash: Content("other").Sha256), download, default));
        Assert.True(await files.ImportAsync(runnerId, Shared(), (_, _) => Task.FromResult(false), default));
        await Assert.ThrowsAsync<IOException>(() => files.ImportAsync(runnerId, Shared(), (_, _) => throw new IOException("connection lost"), default));
        Assert.Single(await store.ListAsync(sessionId, default));
        Assert.Empty(Directory.EnumerateFiles(Incoming));

        // Deleting the conversation deletes its files.
        await using (var db = factory.CreateDbContext())
        {
            await db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentId, (Guid?)null));
        }
        await sessions.DeleteAsync(sessionId);
        Assert.Empty(await store.ListAsync(sessionId, default));
        files.Dispose();
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    public void Dispose() { Directory.Delete(_folder, true); GC.SuppressFinalize(this); }
}
