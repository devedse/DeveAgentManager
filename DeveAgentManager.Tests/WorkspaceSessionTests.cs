using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Harnesses.Pi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class WorkspaceSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewAndSelectedSessionsKeepWorkspaceAndOnlyOneActiveConversation()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var original = await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct);
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, null, "Second chat", "second-native", Ct);
        var second = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        Assert.NotEqual(original!.Id, second.Id);
        Assert.Equal("Second chat", second.Title);
        Assert.Equal(rig.Agent.Id, (await rig.Service.GetAsync(original.Id, Ct))!.WorkspaceAgentId);
        Assert.Null((await rig.Service.GetAsync(original.Id, Ct))!.AgentId);
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, original.Id, null, "restored-native", Ct);
        Assert.Equal(original.Id, (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!.Id);
        await using var db = rig.Factory.CreateDbContext();
        Assert.Single(await db.Sessions.Where(s => s.AgentId == rig.Agent.Id).ToListAsync(Ct));
        Assert.Equal("restored-native", (await db.Agents.SingleAsync(Ct)).HarnessSessionId);
        Assert.Null((await db.Agents.SingleAsync(Ct)).LastAssistantMessageAt);
    }

    [Fact]
    public async Task CannotSelectAnotherWorkspaceOrDeleteActiveEvenWhenStopped()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var active = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.DeleteAsync(active.Id, Ct));
        await using (var db = rig.Factory.CreateDbContext())
        {
            db.Sessions.Add(new StoredSession { OwnerId = User.BootstrapId, Id = Guid.Empty, Title = "Other workspace", HarnessId = "pi" });
            await db.SaveChangesAsync(Ct);
        }
        using (await rig.Service.LockAsync(Ct))
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, Guid.Empty, null, "wrong", Ct));
        Assert.Equal(active.Id, (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!.Id);
    }

    [Fact]
    public async Task RenamePersistsThroughSyncAndExportAndCountsHistoryBackups()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var active = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        Directory.CreateDirectory(rig.Options.SessionsDirectory);
        var content = new DeveSession { Title = "Old harness name" }.ToJson();
        await File.WriteAllTextAsync(Path.Combine(rig.Options.SessionsDirectory, $"{active.Id:N}.json"), content, Ct);
        await File.WriteAllTextAsync(Path.Combine(rig.Options.SessionsDirectory, $"{active.Id:N}.backup.pi.json"), content, Ct);
        await rig.Service.RenameAsync(active.Id, "  Chosen name  ", Ct);
        var renamed = (await rig.Service.GetAsync(active.Id, Ct))!;
        SessionService.Apply(renamed, new DeveSession { Title = "New automatic title" }, rig.Agent.Name);
        Assert.Equal("Chosen name", renamed.Title);
        Assert.True(renamed.TitlePinned);
        Assert.True(renamed.SavedSize > 0);
        Assert.Equal(renamed.SavedSize * 2, renamed.TotalSize);
        var export = await rig.Service.ExportAsync(active.Id, "deve", Ct);
        Assert.Equal("Chosen name", DeveSession.FromJson(export.Content).Title);
    }

    [Fact]
    public async Task DeletesInactiveHistoryAndBackupsWithoutTouchingOtherSessions()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var first = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, null, null, "next", Ct);
        Directory.CreateDirectory(rig.Options.SessionsDirectory);
        var file = Path.Combine(rig.Options.SessionsDirectory, $"{first.Id:N}.backup.pi.json");
        await File.WriteAllTextAsync(file, "{}", Ct);
        await rig.Service.DeleteAsync(first.Id, Ct);
        Assert.False(File.Exists(file));
        Assert.Null(await rig.Service.GetAsync(first.Id, Ct));
        Assert.NotNull(await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct));
    }

    [Fact]
    public async Task MigrationPreservesOldOwnershipAndResolvesDuplicateActiveRows()
    {
        using var rig = new Rig();
        await using var db = rig.Factory.CreateDbContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var workspaceSessions = Array.FindIndex(migrations, m => m.EndsWith("_WorkspaceSessions", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(migrations[workspaceSessions - 1], Ct);
        // Rows as that schema had them (before workspaces and before users), not as the model has them now.
        await InsertAsync(db, "Runners", new() { ["Id"] = rig.Runner.Id, ["Name"] = rig.Runner.Name, ["State"] = "Ready" });
        await InsertAsync(db, "Agents", new() { ["Id"] = rig.Agent.Id, ["RunnerId"] = rig.Runner.Id, ["Name"] = rig.Agent.Name, ["HarnessId"] = "pi", ["State"] = "Stopped" });
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        foreach (var id in new[] { first, second })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Sessions (Id, AgentId, Title, HarnessId, MessageCount, SourceSize, CreatedAt, UpdatedAt) VALUES ({id}, {rig.Agent.Id}, 'Old chat', 'pi', 0, 0, '2026-10-03 00:00:00', '2026-10-03 00:00:00')", Ct);
        await db.Database.MigrateAsync(Ct);
        var rows = await db.Sessions.ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(rig.Agent.Id, row.WorkspaceAgentId));
        Assert.Single(rows, s => s.AgentId != null);
        // Everything from before users existed belongs to the first user.
        Assert.All(rows, row => Assert.Equal(User.BootstrapId, row.OwnerId));
        Assert.Equal(User.BootstrapId, await db.Agents.Select(a => a.OwnerId).SingleAsync(Ct));
        Assert.Equal(User.BootstrapId, await db.Runners.Select(r => r.OwnerId).SingleAsync(Ct));
        db.Sessions.Add(new StoredSession { OwnerId = User.BootstrapId, Id = Guid.NewGuid(), AgentId = rig.Agent.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task MissingSavedHistoryDoesNotDetachCurrentChat()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var original = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        var brokenId = Guid.NewGuid();
        await using (var db = rig.Factory.CreateDbContext())
        {
            db.Sessions.Add(new StoredSession { OwnerId = User.BootstrapId, Id = brokenId, WorkspaceAgentId = rig.Agent.Id, HarnessId = "pi", MessageCount = 3 });
            await db.SaveChangesAsync(Ct);
        }
        using (await rig.Service.LockAsync(Ct))
            await Assert.ThrowsAsync<IOException>(() => rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, brokenId, null, "new", Ct));
        Assert.Equal(original.Id, (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!.Id);
    }

    [Fact]
    public async Task HarnessMigrationPreservesAutomaticNameAndOriginalBackup()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var current = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var saved = await db.Sessions.SingleAsync(Ct);
            SessionService.Apply(saved, new DeveSession { Title = "Automatic useful title" }, rig.Agent.Name);
            await db.SaveChangesAsync(Ct);
        }
        Directory.CreateDirectory(rig.Options.SessionsDirectory);
        await File.WriteAllTextAsync(Path.Combine(rig.Options.SessionsDirectory, $"{current.Id:N}.json"), new DeveSession { Title = "Automatic useful title" }.ToJson(), Ct);
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.MoveToHarnessAsync(rig.Runner, rig.Agent, "codex", "new-native-id", Ct);
        var migrated = (await rig.Service.GetAsync(current.Id, Ct))!;
        SessionService.Apply(migrated, new DeveSession { Title = "Imported chat summary" }, rig.Agent.Name);
        Assert.Equal("Automatic useful title", migrated.Title);
        Assert.Equal("pi,codex", migrated.HarnessTrail);
        Assert.Single(Directory.GetFiles(rig.Options.SessionsDirectory, $"{current.Id:N}.*.json"));
    }

    /// <summary>
    /// Inserts a row into a table as an older migration left it: the columns that schema requires and these values don't
    /// give get a placeholder (0, or a date for *At columns).
    /// </summary>
    private static async Task InsertAsync(ManagerDbContext db, string table, Dictionary<string, object> values)
    {
        await db.Database.OpenConnectionAsync(Ct);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                // cid, name, type, notnull, dflt_value, pk
                var (name, type, required, hasDefault) = (reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, !reader.IsDBNull(4));
                if (required && !hasDefault)
                {
                    values.TryAdd(name, type == "INTEGER" ? 0 : name.EndsWith("At", StringComparison.Ordinal) ? "2026-10-03 00:00:00" : "");
                }
            }
        }

        var columns = values.Keys.ToList();
        await db.Database.ExecuteSqlRawAsync($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => $"{{{i}}}"))})",
            columns.Select(c => values[c]), Ct);
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
    private sealed class Rig : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("dam-workspace-").FullName;
        public readonly Runner Runner = new() { OwnerId = User.BootstrapId, Id = Guid.NewGuid(), Name = "runner", State = RunnerState.Ready };
        public Agent Agent { get; }
        public ManagerOptions Options { get; }
        public Factory Factory { get; }
        public SessionService Service { get; }
        public Rig()
        {
            Options = new() { DataDirectory = _folder };
            Factory = new(Options.DatabasePath);
            Agent = new() { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "workspace", Runner = Runner, RunnerId = Runner.Id, HarnessId = "pi", HarnessSessionId = "first", State = AgentState.Stopped, LastAssistantMessageAt = DateTime.UtcNow };
            Service = new(Factory, null!, [new PiSessionFormat(), new DeveAgentManager.Harnesses.Codex.CodexSessionFormat()], [], new SessionImageStore(Options), Options, new ManagerEvents(), NullLogger<SessionService>.Instance);
        }
        public async Task CreateAsync()
        {
            await using var db = Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync(Ct);
            db.Agents.Add(Agent);
            await db.SaveChangesAsync(Ct);
            await Service.AttachToAgentAsync(Runner, Agent, null, Ct);
        }
        public void Dispose() => Directory.Delete(_folder, true);
    }
}
