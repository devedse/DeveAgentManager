using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Accounts;
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
    public async Task ProjectMigrationPreservesLiveIdentityAndCanBeRevertedWithoutRemovingAgentsOrConversations()
    {
        using var rig = new Rig();
        await using var db = rig.Factory.CreateDbContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var project = Array.FindIndex(migrations, m => m.EndsWith("_ProjectWorkspaces", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(migrations[project - 1], Ct);
        await InsertAsync(db, "Runners", new() { ["Id"] = rig.Runner.Id, ["OwnerId"] = User.BootstrapId, ["Name"] = "test-runner", ["State"] = "Ready" });
        await InsertAsync(db, "Agents", new() { ["Id"] = rig.Agent.Id, ["OwnerId"] = User.BootstrapId, ["RunnerId"] = rig.Runner.Id,
            ["Name"] = "project", ["WorkingDirectory"] = "/work/example", ["HarnessId"] = "pi", ["TmuxSession"] = "same-live-terminal", ["HarnessSessionId"] = "same-native-id", ["State"] = "Running" });
        var session = Guid.NewGuid();
        await InsertAsync(db, "Sessions", new() { ["Id"] = session, ["OwnerId"] = User.BootstrapId, ["AgentId"] = rig.Agent.Id,
            ["WorkspaceAgentId"] = rig.Agent.Id, ["Title"] = "Existing conversation", ["HarnessId"] = "pi" });
        await db.Database.MigrateAsync(Ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((rig.Agent.Id, "same-live-terminal", "same-native-id", AgentState.Running), (agent.ProjectId, agent.TmuxSession, agent.HarnessSessionId, agent.State));
        Assert.Equal("/work/example", (await db.Projects.SingleAsync(Ct)).RootDirectory);
        Assert.Equal(session, (await db.Sessions.AsNoTracking().SingleAsync(Ct)).Id);
        // Runner deletion must cascade through both Projects and Agents without a foreign-key ordering failure.
        var deleteRunner = new Runner { Id = Guid.NewGuid(), Name = "delete-test", OwnerId = User.BootstrapId };
        var deleteProject = new Project { Id = Guid.NewGuid(), RunnerId = deleteRunner.Id, OwnerId = User.BootstrapId, Name = "delete-test" };
        db.Runners.Add(deleteRunner); db.Projects.Add(deleteProject);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), RunnerId = deleteRunner.Id, OwnerId = User.BootstrapId, Project = deleteProject, Name = "delete-test" });
        await db.SaveChangesAsync(Ct);
        db.Runners.Remove(deleteRunner); await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync(migrations[project - 1], Ct);
        Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM Agents").SingleAsync(Ct));
        Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM Sessions").SingleAsync(Ct));
        await db.Database.MigrateAsync(Ct);
        Assert.Equal(rig.Agent.Id, (await db.Projects.AsNoTracking().SingleAsync(Ct)).Id);
    }

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
    public async Task SavedConversationsResumeInAnyWorkspaceOfTheirProjectAndOutliveRemovedWorkspaces()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var project = new Project { Id = rig.Agent.Id, RunnerId = rig.Runner.Id, OwnerId = User.BootstrapId, Name = "workspace", RootDirectory = "/work/example" };
        Agent Workspace(string name, Guid? projectId) => new() { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = name, Runner = rig.Runner, RunnerId = rig.Runner.Id,
            ProjectId = projectId, IsClone = projectId is not null, HarnessId = "pi", HarnessSessionId = name, WorkingDirectory = "/work/example.workspaces/" + name, State = AgentState.Stopped };
        var clone = Workspace("workspace-2", project.Id);
        var stranger = Workspace("elsewhere", null);
        await using (var db = rig.Factory.CreateDbContext())
        {
            db.Runners.Attach(rig.Runner);
            db.Projects.Add(project);
            (await db.Agents.SingleAsync(a => a.Id == rig.Agent.Id, Ct)).ProjectId = project.Id;
            db.Agents.AddRange(clone, stranger);
            await db.SaveChangesAsync(Ct);
        }
        rig.Agent.ProjectId = project.Id;
        var original = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, null, "Next", "next", Ct);
        // Saved in Main, it resumes in another workspace of the project and moves there.
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, clone, original.Id, null, "resumed", Ct);
        var resumed = (await rig.Service.GetAsync(original.Id, Ct))!;
        Assert.Equal((clone.Id, clone.Id, project.Id, clone.WorkingDirectory), (resumed.AgentId, resumed.WorkspaceAgentId, resumed.ProjectId, resumed.WorkingDirectory));
        // Active in one workspace, it cannot be taken over by another; another project never gets it.
        using (await rig.Service.LockAsync(Ct))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, original.Id, null, "taken", Ct));
            await rig.Service.SelectLockedAsync(rig.Runner, clone, null, "Fresh", "fresh", Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.SelectLockedAsync(rig.Runner, stranger, original.Id, null, "stranger", Ct));
        }
        // Removing its workspace keeps the conversation with the project, and Main can still resume it.
        await using (var db = rig.Factory.CreateDbContext())
        {
            db.Agents.Remove(await db.Agents.SingleAsync(a => a.Id == clone.Id, Ct));
            await db.SaveChangesAsync(Ct);
        }
        var orphan = (await rig.Service.GetAsync(original.Id, Ct))!;
        Assert.Equal((null, null, project.Id), (orphan.AgentId, orphan.WorkspaceAgentId, orphan.ProjectId));
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, original.Id, null, "back-in-main", Ct);
        Assert.Equal(rig.Agent.Id, (await rig.Service.GetAsync(original.Id, Ct))!.AgentId);
    }

    [Fact]
    public void TheHarnessNameNamesTheWorkspaceNotItsConversation()
    {
        // Agents start their harness with --name <workspace>; until the first prompt, that is not the conversation's title.
        var stored = new StoredSession { Title = "testworkspace" };
        SessionService.Apply(stored, new DeveSession { Title = "testworkspace" }, "testworkspace");
        Assert.Equal(SessionService.NewTitle, stored.Title);
        var prompted = new DeveSession { Title = "testworkspace" };
        prompted.Entries.Add(new DeveEntry { Id = "u1", Kind = DeveEntryKind.Message, Role = DeveRole.User, Content = [DeveBlock.OfText("Fix the toolbar on phones")] });
        SessionService.Apply(stored, prompted, "testworkspace");
        Assert.Equal("Fix the toolbar on phones", stored.Title);
        var pinned = new StoredSession { Title = "testworkspace", TitlePinned = true };
        SessionService.Apply(pinned, new DeveSession { Title = "testworkspace" }, "testworkspace");
        Assert.Equal("testworkspace", pinned.Title);
    }

    [Fact]
    public async Task SelectingAnotherHarnessRestoresIt_AndNewSessionsUseTheRestoredHarness()
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var original = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        var codexId = Guid.NewGuid();
        await using (var db = rig.Factory.CreateDbContext())
        {
            db.Sessions.Add(new StoredSession { Id = codexId, OwnerId = rig.Agent.OwnerId, WorkspaceAgentId = rig.Agent.Id,
                HarnessId = "codex", HarnessSessionId = "saved-codex", HarnessTrail = "claude,codex", Title = "Saved Codex chat" });
            await db.SaveChangesAsync(Ct);
        }
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, codexId, null, "restored-codex", Ct);
        Assert.Equal("codex", rig.Agent.HarnessId);
        Assert.Equal("claude,codex", (await rig.Service.GetAsync(codexId, Ct))!.HarnessTrail);
        Assert.Equal("pi", (await rig.Service.GetAsync(original.Id, Ct))!.HarnessId);
        await using (var db = rig.Factory.CreateDbContext())
        {
            var agent = await db.Agents.SingleAsync(Ct);
            Assert.Equal("codex", agent.HarnessId);
            Assert.Equal("restored-codex", agent.HarnessSessionId);
        }
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, null, "New Codex chat", "new-codex", Ct);
        Assert.Equal("codex", (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!.HarnessId);
        using (await rig.Service.LockAsync(Ct))
            await rig.Service.SelectLockedAsync(rig.Runner, rig.Agent, original.Id, null, "back-to-pi", Ct);
        Assert.Equal("pi", rig.Agent.HarnessId);
        Assert.Null(rig.Agent.AccountId);
        Assert.Equal("codex", (await rig.Service.GetAsync(codexId, Ct))!.HarnessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSavedHarnessOrDefaultLoginFailsBeforeStoppingOrSelecting(bool installed)
    {
        using var rig = new Rig();
        await rig.CreateAsync();
        var original = (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!;
        var other = Guid.NewGuid();
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.Runners.SingleAsync(Ct)).Harnesses = installed ? "pi,codex" : "pi";
            db.Sessions.Add(new StoredSession { Id = other, OwnerId = rig.Agent.OwnerId, WorkspaceAgentId = rig.Agent.Id, HarnessId = "codex" });
            await db.SaveChangesAsync(Ct);
        }
        IHarness[] harnesses = [new PiHarness(), new DeveAgentManager.Harnesses.Codex.CodexHarness()];
        var events = new ManagerEvents();
        var accounts = new AccountService(rig.Factory, null!, null!, harnesses, events, NullLogger<AccountService>.Instance);
        // No SSH dependency: either preflight must reject before any attempt to stop the current process.
        var service = new AgentService(rig.Factory, null!, harnesses, rig.Service, accounts, null!, null!, null!, events, NullLogger<AgentService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectSessionAsync(rig.Agent.Id, other, null, Ct));
        Assert.Contains(installed ? "login" : "not installed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.Id, (await rig.Service.GetForAgentAsync(rig.Agent.Id, Ct))!.Id);
        await using var check = rig.Factory.CreateDbContext();
        var agent = await check.Agents.SingleAsync(Ct);
        Assert.Equal("pi", agent.HarnessId);
        Assert.Equal(AgentState.Stopped, agent.State);
        Assert.Equal("first", agent.HarnessSessionId);
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
        Assert.Equal("Chosen name", DeveSession.FromJson(export.ReadAllText()).Title);
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
            Service = new(Factory, null!, [new PiSessionFormat(), new DeveAgentManager.Harnesses.Codex.CodexSessionFormat()], [], new SessionImageStore(Options), new SessionFileStore(Options), Options, new ManagerEvents(), NullLogger<SessionService>.Instance);
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
