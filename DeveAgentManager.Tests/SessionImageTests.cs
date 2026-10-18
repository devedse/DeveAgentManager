using DeveAgentManager.Core.Users;
#pragma warning disable xUnit1051
using System.Text;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class SessionImageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dam-images-").FullName;
    private ManagerOptions Options => new() { DataDirectory = _folder };
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII=");

    [Fact]
    public async Task NewestImagesComeFirstRegardlessOfImportOrder()
    {
        var store = new SessionImageStore(Options);
        var session = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var middle = await store.SaveAsync(session, Guid.NewGuid(), Png, "middle.png", "", default, now.AddMinutes(-1));
        var newest = await store.SaveAsync(session, Guid.NewGuid(), Png, "newest.png", "", default, now);
        var oldest = await store.SaveAsync(session, Guid.NewGuid(), Png, "oldest.png", "", default, now.AddMinutes(-2));
        Assert.Equal(new[] { newest, middle, oldest }, await new SessionImageStore(Options).ListAsync(session, default));
    }

    [Fact]
    public async Task ImagesPersistAcrossRestartAreIsolatedBySessionAndImportOnlyOnce()
    {
        var session = Guid.NewGuid(); var id = Guid.NewGuid();
        var store = new SessionImageStore(Options);
        var first = await store.SaveAsync(session, id, Png, "../../image.png", "A\ncaption", default);
        var restarted = new SessionImageStore(Options);
        Assert.Equal(first, await restarted.SaveAsync(session, id, Png, "different.png", "different", default));
        Assert.Single(await restarted.ListAsync(session, default));
        Assert.Empty(await restarted.ListAsync(Guid.NewGuid(), default));
        var read = await restarted.ReadAsync(session, id, default);
        Assert.NotNull(read);
        Assert.Equal(Png, read.Value.Data);
        Assert.Equal("image/png", read.Value.Image.ContentType);
        Assert.Equal("image.png", first.FileName);
        Assert.Equal("A caption", first.Caption);
    }

    [Fact]
    public async Task UnsupportedAndOversizedImagesNeverCreateAttachments()
    {
        var store = new SessionImageStore(Options); var session = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(session, Guid.NewGuid(), Encoding.UTF8.GetBytes("<svg onload='bad()'/>"), "x.svg", "", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(session, Guid.NewGuid(), new byte[SessionImageStore.MaxBytes + 1], "x.png", "", default));
        Assert.Empty(await store.ListAsync(session, default));
    }

    [Fact]
    public async Task SnapshotOwnershipSurvivesSessionSwitchAndHistoryDeletionButCannotCrossRunners()
    {
        var factory = new Factory(Path.Combine(_folder, "test.db"));
        var runnerId = Guid.NewGuid(); var agentId = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Runners.Add(new Runner { OwnerId = User.BootstrapId, Id = runnerId, Name = "desktop" });
            db.Agents.Add(new Agent { OwnerId = User.BootstrapId, Id = agentId, RunnerId = runnerId, Name = "agent" });
            // Inactive conversation in the same workspace: image ownership remains valid.
            db.Sessions.Add(new StoredSession { OwnerId = User.BootstrapId, Id = sessionId, WorkspaceAgentId = agentId, AgentId = null });
            await db.SaveChangesAsync();
        }
        var events = new ManagerEvents();
        var store = new SessionImageStore(Options);
        var sessions = new SessionService(factory, null!, [], [], store, new SessionFileStore(Options), Options, events, NullLogger<SessionService>.Instance);
        var images = new SessionImages(factory, null!, sessions, store, new AccessControl(factory), NullLogger<SessionImages>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => images.SaveSnapshotAsync(TestUsers.Admin, Guid.NewGuid(), sessionId, Png, "", default));
        // Only who can write to the conversation's agent saves into it.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => images.SaveSnapshotAsync(TestUsers.Plain(Guid.NewGuid()), runnerId, sessionId, Png, "", default));
        Assert.Empty(await store.ListAsync(sessionId, default));
        await images.SaveSnapshotAsync(TestUsers.Admin, runnerId, sessionId, Png, "UI", default);
        Assert.Single(await store.ListAsync(sessionId, default));
        await sessions.DeleteAsync(sessionId);
        Assert.Single(await store.ListAsync(sessionId, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => images.SaveSnapshotAsync(TestUsers.Admin, runnerId, sessionId, Png, "", default));
        images.Dispose();
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path}").Options);
    }
    public void Dispose() { Directory.Delete(_folder, true); GC.SuppressFinalize(this); }
}
