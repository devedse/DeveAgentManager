using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AgentArtifactTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("dam-artifact-tests-").FullName;
    private ManagerOptions Options => new() { DataDirectory = _root };
    private Factory Db => new(Options.DatabasePath);
    private SessionFileStore Files => new(Options);
    private SessionImageStore Images => new(Options);
    private readonly ManagerEvents _events = new();
    private AgentArtifacts Collection => new(Db, Options, Files, Images, new AccessControl(Db), _events);
    private SessionService Sessions => new(Db, null!, [], [], Images, Files, Options, _events, NullLogger<SessionService>.Instance, Collection);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

    private async Task<(Agent Agent, Guid Old, Guid Current)> SeedAsync(Guid? runner = null)
    {
        await using var db = Db.CreateDbContext();
        await db.Database.EnsureCreatedAsync(Ct);
        var machine = new Runner { Id = runner ?? Guid.NewGuid(), Name = "test runner", OwnerId = User.BootstrapId };
        var agent = new Agent { Id = Guid.NewGuid(), Name = "Source", OwnerId = User.BootstrapId, RunnerId = machine.Id };
        var old = Guid.NewGuid(); var current = Guid.NewGuid();
        db.Runners.Add(machine); db.Agents.Add(agent);
        db.Sessions.AddRange(new StoredSession { Id = old, WorkspaceAgentId = agent.Id, OwnerId = agent.OwnerId },
            new StoredSession { Id = current, WorkspaceAgentId = agent.Id, AgentId = agent.Id, OwnerId = agent.OwnerId });
        await db.SaveChangesAsync(Ct);
        return (agent, old, current);
    }

    private async Task<SessionFileDto> SaveAsync(Guid session, string text = "artifact content")
    {
        var data = Encoding.UTF8.GetBytes(text);
        using var incoming = await Files.ReceiveAsync(data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), (s, ct) => s.WriteAsync(data, ct).AsTask(), Ct);
        return await Files.CommitAsync(session, Guid.NewGuid(), incoming, "report.md", "Shared report", Ct);
    }

    [Fact]
    public async Task ProjectFilesAreSharedAcrossWorkspacesButConversationOnlySharesStayNarrow()
    {
        var (main, _, current) = await SeedAsync();
        var childId = Guid.NewGuid(); var childSession = Guid.NewGuid(); var reader = Guid.NewGuid();
        await using (var db = Db.CreateDbContext())
        {
            db.Projects.Add(new() { Id = main.Id, RunnerId = main.RunnerId, OwnerId = main.OwnerId, Name = "project" });
            (await db.Agents.SingleAsync(Ct)).ProjectId = main.Id;
            db.Agents.Add(new() { Id = childId, RunnerId = main.RunnerId, OwnerId = main.OwnerId, Name = "child", ProjectId = main.Id, IsWorktree = true });
            db.Sessions.Add(new() { Id = childSession, AgentId = childId, WorkspaceAgentId = childId, OwnerId = main.OwnerId });
            db.Users.Add(new() { Id = reader, Username = "conversation-reader", DisplayName = "Conversation reader" });
            db.SessionShares.Add(new() { SessionId = current, UserId = reader });
            await db.SaveChangesAsync(Ct);
        }
        var mainFile = await SaveAsync(current, "Main file"); var childFile = await SaveAsync(childSession, "Child file");
        var files = await Collection.ListFilesAsync(current, TestUsers.Admin, Ct);
        Assert.Equal(2, files.Count); Assert.Contains(files, f => f.Id == childFile.Id);
        Assert.Equal(2, (await Collection.ListFilesAsync(childSession, TestUsers.Admin, Ct)).Count);
        var shared = await Collection.ListFilesAsync(current, TestUsers.Plain(reader), Ct);
        Assert.Equal(mainFile.Id, Assert.Single(shared).Id);
        Assert.Equal(2, (await Collection.ForAgentAsync(TestUsers.Admin, childId, Ct)).Files.Count);
    }

    [Fact]
    public async Task LegacyArtifactsSurviveSessionDeletionRestartAndNewSessionsWithoutCopyingBytes()
    {
        var (agent, old, current) = await SeedAsync();
        var file = await SaveAsync(old);
        var image = await Images.SaveAsync(old, Guid.NewGuid(), Png, "chart.png", "Chart", Ct);
        var path = Files.DataPath(old, file.Id);
        var changes = new List<Guid?>(); _events.Changed += e => changes.Add(e.SessionId);
        await Collection.NotifyAsync(old, Ct);
        Assert.Contains(old, changes); Assert.Contains(current, changes);
        Assert.Equal(file with { SessionId = current }, Assert.Single(await Collection.ListFilesAsync(current, TestUsers.Admin, Ct)));
        Assert.Equal(image with { SessionId = current }, Assert.Single(await Collection.ListImagesAsync(current, TestUsers.Admin, Ct)));
        Assert.Equal(path, (await Collection.FileAsync(current, file.Id, TestUsers.Admin, Ct))!.Value.Path);
        Assert.Equal(Png, (await Collection.ImageAsync(current, image.Id, TestUsers.Admin, Ct))!.Value.Data);
        await Sessions.DeleteAsync(old, Ct);
        Assert.True(await Sessions.ArtifactPublicationBelongsAsync(agent.RunnerId, agent.Id, old, Ct));
        Assert.False(await Sessions.ArtifactPublicationBelongsAsync(Guid.NewGuid(), agent.Id, old, Ct));
        Assert.False(await Sessions.ArtifactPublicationBelongsAsync(agent.RunnerId, agent.Id, Guid.NewGuid(), Ct));
        var queued = new SessionFiles.Publication(Guid.NewGuid(), agent.Id, old, "queued.txt", "Queued before history deletion", 5,
            Convert.ToHexStringLower(SHA256.HashData("later"u8)));
        using (var collector = new SessionFiles(Db, null!, Sessions, Files, NullLogger<SessionFiles>.Instance))
            Assert.True(await collector.ImportAsync(agent.RunnerId, queued, async (stream, ct) => { await stream.WriteAsync("later"u8.ToArray(), ct); return true; }, Ct));
        var fresh = Guid.NewGuid();
        await using (var db = Db.CreateDbContext())
        {
            db.Sessions.Add(new StoredSession { Id = fresh, WorkspaceAgentId = agent.Id, OwnerId = agent.OwnerId });
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal(2, (await Collection.ListFilesAsync(fresh, TestUsers.Admin, Ct)).Count);
        Assert.Single(await Collection.ListImagesAsync(fresh, TestUsers.Admin, Ct));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(Options.SessionsDirectory, "files"), "*.file", SearchOption.AllDirectories).Count());
        Assert.True(await Sessions.DeleteFileAsync(fresh, file.Id, Ct, TestUsers.Admin));
        Assert.Equal(queued.Id, Assert.Single(await Collection.ListFilesAsync(current, TestUsers.Admin, Ct)).Id);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SessionOnlyShareDoesNotExposeTheRestOfTheAgentCollection()
    {
        var (agent, old, current) = await SeedAsync();
        var oldFile = await SaveAsync(old, "old"); var privateFile = await SaveAsync(current, "private");
        var reader = Guid.NewGuid();
        await using (var db = Db.CreateDbContext())
        {
            db.Users.Add(new User { Id = reader, Username = "artifact-reader", DisplayName = "Reader" });
            db.SessionShares.Add(new SessionShare { SessionId = old, UserId = reader });
            await db.SaveChangesAsync(Ct);
        }
        var actor = TestUsers.Plain(reader);
        Assert.Equal(oldFile, Assert.Single(await Collection.ListFilesAsync(old, actor, Ct)));
        Assert.Null(await Collection.FileAsync(old, privateFile.Id, actor, Ct));
        var sessionCopy = Guid.NewGuid();
        await Collection.CopyAttachmentsAsync(old, sessionCopy, actor, Ct);
        Assert.Single(await Files.ListAsync(sessionCopy, Ct));
        await using (var db = Db.CreateDbContext())
        {
            db.AgentShares.Add(new AgentShare { AgentId = agent.Id, UserId = reader, Access = AgentAccess.Read });
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal(2, (await Collection.ListFilesAsync(old, actor, Ct)).Count);
        Assert.NotNull(await Collection.FileAsync(old, privateFile.Id, actor, Ct));
        var collectionCopy = Guid.NewGuid();
        await Collection.CopyAttachmentsAsync(old, collectionCopy, actor, Ct);
        Assert.Equal(2, (await Files.ListAsync(collectionCopy, Ct)).Count);
    }

    [Fact]
    public async Task LastArchiveOfDeletedAgentRemovesRetainedArtifacts()
    {
        var (agent, old, current) = await SeedAsync();
        var file = await SaveAsync(old);
        await Collection.PreserveAsync(agent.Id, Ct);
        await using (var db = Db.CreateDbContext()) await db.Agents.Where(a => a.Id == agent.Id).ExecuteDeleteAsync(Ct);
        await Sessions.DeleteAsync(old, Ct);
        Assert.Single(await Collection.ListFilesAsync(current, TestUsers.Admin, Ct));
        await Sessions.DeleteAsync(current, Ct);
        Assert.False(File.Exists(Files.DataPath(old, file.Id)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Options.SessionsDirectory, "artifacts"), "*.json"));
    }

    [Fact]
    public async Task CrossAgentRequestsRequireCapabilityRunnerOwnershipAndCurrentReadPermission()
    {
        var (source, old, _) = await SeedAsync();
        var file = await SaveAsync(old);
        var callerOwner = Guid.NewGuid(); var callerRunner = Guid.NewGuid(); var session = Guid.NewGuid();
        var caller = new Agent { Id = Guid.NewGuid(), Name = "Reader", OwnerId = callerOwner, RunnerId = callerRunner };
        await using (var db = Db.CreateDbContext())
        {
            db.Users.Add(new User { Id = callerOwner, Username = "caller", DisplayName = "Caller" });
            db.Runners.Add(new Runner { Id = callerRunner, OwnerId = callerOwner }); db.Agents.Add(caller);
            db.Sessions.Add(new StoredSession { Id = session, OwnerId = callerOwner, WorkspaceAgentId = caller.Id, AgentId = caller.Id });
            await db.SaveChangesAsync(Ct);
        }
        var tokens = new ArtifactTokens(new EphemeralDataProtectionProvider());
        var transfers = new ArtifactTransfers(Db, Collection, Files, Images, new AccessControl(Db), null!, tokens);
        var request = new ArtifactTransfers.Request(Guid.NewGuid(), caller.Id, session, "fetch", source.Id.ToString(), file.Id, Token: tokens.Issue(caller));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => transfers.ResolveAsync(callerRunner, request, Ct));
        await using (var db = Db.CreateDbContext())
        {
            db.AgentShares.Add(new AgentShare { AgentId = source.Id, UserId = callerOwner, Access = AgentAccess.Read });
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal(Files.DataPath(old, file.Id), (await transfers.ResolveAsync(callerRunner, request, Ct)).Path);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => transfers.ResolveAsync(Guid.NewGuid(), request, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => transfers.ResolveAsync(callerRunner, request with { Token = null }, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => transfers.ResolveAsync(callerRunner, request with { AgentId = source.Id }, Ct));
        await using (var db = Db.CreateDbContext()) await db.AgentShares.Where(s => s.AgentId == source.Id).ExecuteDeleteAsync(Ct);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => transfers.ResolveAsync(callerRunner, request, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task RealSshToolListsAndFetchesFileAndImageFromAnotherRunnersAgent()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var (source, old, _) = await SeedAsync();
        var file = await SaveAsync(old, new string('x', 2 * 1024 * 1024));
        var image = await Images.SaveAsync(old, Guid.NewGuid(), Png, "chart.png", "Chart", Ct);
        var caller = new Agent { Id = Guid.NewGuid(), Name = "Caller", OwnerId = source.OwnerId, RunnerId = fixture.Runner.Id };
        var session = Guid.NewGuid();
        await using (var db = Db.CreateDbContext())
        {
            fixture.Runner.OwnerId = caller.OwnerId;
            db.Runners.Add(fixture.Runner); db.Agents.Add(caller);
            db.Sessions.Add(new StoredSession { Id = session, WorkspaceAgentId = caller.Id, AgentId = caller.Id, OwnerId = caller.OwnerId });
            await db.SaveChangesAsync(Ct);
        }
        var tokens = new ArtifactTokens(new EphemeralDataProtectionProvider());
        await tokens.InstallAsync(fixture.Shell, fixture.Runner, caller, Ct);
        await fixture.Shell.UploadAsync(fixture.Runner, "~/.deveagentmanager/dam-tools.py", await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Desktop", "dam-tools.py"), Ct), "700", Ct);
        var transfers = new ArtifactTransfers(Db, Collection, Files, Images, new AccessControl(Db), fixture.Shell, tokens);
        async Task<CommandResult> RunAsync(string command)
        {
            var task = fixture.Shell.RunAsync(fixture.Runner,
                $"export DAM_AGENT_ID={caller.Id:N} DAM_SESSION_ID={session:N}; python3 ~/.deveagentmanager/dam-tools.py {command}", TimeSpan.FromSeconds(20), Ct);
            ArtifactTransfers.Request? request = null;
            for (var attempt = 0; attempt < 100 && request is null; attempt++)
            {
                var pending = await fixture.Shell.RunAsync(fixture.Runner, "python3 ~/.deveagentmanager/dam-tools.py --pending-transfers", cancellationToken: Ct);
                using var document = JsonDocument.Parse(pending.Output);
                request = document.RootElement.GetProperty("requests").EnumerateArray().Select(e => e.Deserialize<ArtifactTransfers.Request>(ApiJson.Options)).FirstOrDefault();
                if (request is null) await Task.Delay(20, Ct);
            }
            Assert.NotNull(request);
            await transfers.ProcessAsync(fixture.Runner, request, Ct);
            return await task;
        }
        var listing = await RunAsync("list-artifacts --all");
        Assert.Equal(0, listing.ExitCode);
        using (var json = JsonDocument.Parse(listing.Output)) Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        var fetched = await RunAsync($"fetch-artifact {file.Id} --agent {source.Id}");
        Assert.Equal(0, fetched.ExitCode);
        Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fetched.Output.Trim(), Ct))));
        var chart = await RunAsync($"fetch-artifact {image.Id} --agent Source");
        Assert.Equal(0, chart.ExitCode);
        Assert.Equal(Png, await File.ReadAllBytesAsync(chart.Output.Trim(), Ct));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root + "/home/.deveagentmanager/artifact-requests"));
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path}").Options);
    }
    public void Dispose() { Directory.Delete(_root, true); GC.SuppressFinalize(this); }
}
