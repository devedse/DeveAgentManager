using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Harnesses.Pi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Deleting an agent with its folder: what the dialog shows (unsaved Git work, size), and which folders are never deleted,
/// checked on the Runner both when asked and right before deleting.
/// </summary>
public sealed class AgentDeletionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FolderCheckReportsUnsavedWorkAndRefusesFoldersThatArentOnlyTheAgents()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("dam-agent-folder-").FullName;
        try
        {
            var home = root + "/home";
            var repo = home + "/work/admin/app";
            Directory.CreateDirectory(repo);
            await File.WriteAllTextAsync(repo + "/app.txt", "committed", Ct);
            await RunAsync(home, $"git -C {ShellQuote.Quote(repo)} init -q -b main && git -C {ShellQuote.Quote(repo)} add app.txt && git -C {ShellQuote.Quote(repo)} -c user.name=Test -c user.email=test@example.com commit -qm initial");
            await File.WriteAllTextAsync(repo + "/notes.txt", "not committed", Ct);
            var found = await PhaseAsync(home, "folder", new { Path = repo });
            Assert.Equal((true, null, true, 1, 1), (found.GetProperty("Exists").GetBoolean(), found.GetProperty("Refusal").GetString(), found.GetProperty("Git").GetBoolean(),
                found.GetProperty("Uncommitted").GetInt32(), found.GetProperty("Unpushed").GetInt32()));
            Assert.Equal(["notes.txt"], found.GetProperty("Sample").EnumerateArray().Select(s => s.GetString()));

            var plain = home + "/work/admin/plain";
            Directory.CreateDirectory(plain);
            var folder = await PhaseAsync(home, "folder", new { Path = plain });
            Assert.Equal((true, false, JsonValueKind.Null), (folder.GetProperty("Exists").GetBoolean(), folder.GetProperty("Git").GetBoolean(), folder.GetProperty("Refusal").ValueKind));
            Assert.False((await PhaseAsync(home, "folder", new { Path = home + "/work/admin/gone" })).GetProperty("Exists").GetBoolean());

            Directory.CreateSymbolicLink(home + "/work/link", plain);
            Directory.CreateDirectory(home + "/.deveagentmanager/uploads");
            string? Refusal(JsonElement e) => e.GetProperty("Refusal").GetString();
            Assert.Contains("home folder", Refusal(await PhaseAsync(home, "folder", new { Path = home })));
            Assert.Contains("home folder", Refusal(await PhaseAsync(home, "folder", new { Path = root })));
            Assert.Contains("top-level", Refusal(await PhaseAsync(home, "folder", new { Path = "/tmp" })));
            Assert.Contains("symbolic link", Refusal(await PhaseAsync(home, "folder", new { Path = home + "/work/link" })));
            Assert.Contains("the Manager", Refusal(await PhaseAsync(home, "folder", new { Path = home + "/.deveagentmanager/uploads" })));
            Assert.Contains("the system", Refusal(await PhaseAsync(home, "folder", new { Path = "/usr/share" })));
            Assert.Contains("not absolute", Refusal(await PhaseAsync(home, "folder", new { Path = "work/admin/plain" })));
            if (Environment.UserName != "root" && Directory.Exists("/var/lib"))
                Assert.Contains("another user", Refusal(await PhaseAsync(home, "folder", new { Path = "/var/lib" })));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SizeCountsEachFileOnceAndLeavesOutWhatIsAlsoLinkedFromOutside()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("dam-agent-size-").FullName;
        try
        {
            var folder = root + "/app";
            Directory.CreateDirectory(folder + "/sub");
            var data = new byte[64 * 1024];
            Random.Shared.NextBytes(data);
            await File.WriteAllBytesAsync(folder + "/a.bin", data, Ct);
            await File.WriteAllBytesAsync(folder + "/sub/d.bin", data, Ct);
            await File.WriteAllBytesAsync(root + "/outside.bin", data, Ct);
            await File.WriteAllBytesAsync(root + "/large.bin", new byte[1024 * 1024].Select((_, i) => (byte)i).ToArray(), Ct);
            // A second name for a.bin inside the folder, a file shared with a folder outside it (like Git objects of a
            // clone), and a symbolic link to a large file elsewhere.
            await RunAsync(root, $"cd {ShellQuote.Quote(folder)} && ln a.bin b.bin && ln ../outside.bin c.bin && ln -s ../large.bin link");
            var size = await PhaseAsync(root, "measure", new { Path = folder });
            Assert.Equal(5, size.GetProperty("Files").GetInt64());
            var (bytes, freed) = (size.GetProperty("Bytes").GetInt64(), size.GetProperty("FreedBytes").GetInt64());
            Assert.InRange(bytes, 3 * data.Length, 1024 * 1024 - 1);
            Assert.InRange(bytes - freed, data.Length, 2 * data.Length - 1);
            Assert.Equal(0, (await PhaseAsync(root, "measure", new { Path = root + "/gone" })).GetProperty("Files").GetInt64());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeleteChecksAgainAndOnlyDeletesTheAgentsOwnFolder()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("dam-agent-delete-").FullName;
        try
        {
            var home = root + "/home";
            var folder = home + "/work/admin/app";
            Directory.CreateDirectory(folder + "/node_modules/package");
            await File.WriteAllTextAsync(folder + "/node_modules/package/index.js", "", Ct);
            Directory.CreateSymbolicLink(home + "/work/link", home + "/work/admin");
            Assert.True((await PhaseAsync(home, "delete", new { Path = folder })).GetProperty("Removed").GetBoolean());
            Assert.False(Directory.Exists(folder));
            Assert.False((await PhaseAsync(home, "delete", new { Path = folder })).GetProperty("Removed").GetBoolean());

            var refused = await PhaseAsync(home, "delete", new { Path = home }, refused: true);
            Assert.Contains("left untouched", refused.GetString());
            await PhaseAsync(home, "delete", new { Path = home + "/work/link" }, refused: true);
            Assert.True(Directory.Exists(home + "/work/admin"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact(Timeout = 120_000)]
    public async Task DeletingWithTheFolderStopsTheAgentDeletesItsFolderAndKeepsItsConversation()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"), "Requires isolated OpenSSH.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var data = Directory.CreateTempSubdirectory("dam-agent-deletion-").FullName;
        try
        {
            var options = new ManagerOptions { DataDirectory = data };
            var factory = new Factory(options.DatabasePath);
            var events = new ManagerEvents();
            var sessions = new SessionService(factory, ssh.Shell, [new PiSessionFormat()], [], new SessionImageStore(options), new SessionFileStore(options), options, events,
                NullLogger<SessionService>.Instance);
            var service = new AgentService(factory, ssh.Shell, [new PiHarness()], sessions, null!, null!, null!, null!, events, NullLogger<AgentService>.Instance);
            var folder = ssh.Root + "/home/work/admin/app";
            Directory.CreateDirectory(folder + "/src");
            await File.WriteAllTextAsync(folder + "/src/main.py", "print('hello')", Ct);
            await File.WriteAllTextAsync(folder + "/README.md", "# App", Ct);

            ssh.Runner.State = RunnerState.Ready;
            ssh.Runner.OwnerId = User.BootstrapId;
            var agent = NewAgent(ssh.Runner.Id, "app", folder);
            var session = new StoredSession { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, AgentId = agent.Id, WorkspaceAgentId = agent.Id, Title = "Keep me", HarnessId = "pi" };
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync(Ct);
                db.Runners.Add(ssh.Runner);
                db.Agents.Add(agent);
                db.Sessions.Add(session);
                await db.SaveChangesAsync(Ct);
            }

            var deletion = await service.GetDeletionAsync(agent.Id, Ct);
            Assert.Equal((folder, 1, true, true, null, false), (deletion.Path, deletion.Conversations, deletion.Checked, deletion.Exists, deletion.Blocked, deletion.Git));
            Assert.Equal(2, (await service.MeasureFolderAsync(agent.Id, Ct)).Files);

            // Another agent working inside it: only the agent can be deleted, and nothing stops.
            var nested = NewAgent(ssh.Runner.Id, "nested", folder + "/src");
            await using (var db = factory.CreateDbContext()) { db.Agents.Add(nested); await db.SaveChangesAsync(Ct); }
            Assert.Contains("'nested'", (await service.GetDeletionAsync(agent.Id, Ct)).Blocked);
            await Assert.ThrowsAsync<WorkspaceRefusedException>(() => service.DeleteWithFolderAsync(agent.Id, Ct));
            Assert.True(File.Exists(folder + "/src/main.py"));
            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(AgentState.Running, (await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct)).State);
                db.Agents.Remove(await db.Agents.SingleAsync(a => a.Id == nested.Id, Ct));
                await db.SaveChangesAsync(Ct);
            }

            await service.DeleteWithFolderAsync(agent.Id, Ct);
            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(ssh.Root + "/home/work/admin"));
            await using (var db = factory.CreateDbContext())
            {
                Assert.False(await db.Agents.AnyAsync(a => a.Id == agent.Id, Ct));
                Assert.Equal("Keep me", (await db.Sessions.SingleAsync(s => s.Id == session.Id, Ct)).Title);
            }

            // The home folder is refused before the agent stops; deleting the agent alone still works and keeps it.
            var risky = NewAgent(ssh.Runner.Id, "risky", ssh.Root + "/home");
            await using (var db = factory.CreateDbContext()) { db.Agents.Add(risky); await db.SaveChangesAsync(Ct); }
            Assert.Contains("home folder", (await service.GetDeletionAsync(risky.Id, Ct)).Blocked);
            await Assert.ThrowsAsync<WorkspaceRefusedException>(() => service.DeleteWithFolderAsync(risky.Id, Ct));
            await using (var db = factory.CreateDbContext())
                Assert.Equal((AgentState.Running, null), ((await db.Agents.SingleAsync(a => a.Id == risky.Id, Ct)).State, (await db.Agents.SingleAsync(a => a.Id == risky.Id, Ct)).StateMessage));
            await service.DeleteAsync(risky.Id, Ct);
            Assert.True(Directory.Exists(ssh.Root + "/home/work"));
        }
        finally { Directory.Delete(data, true); }
    }

    private static Agent NewAgent(Guid runnerId, string name, string folder)
    {
        var id = Guid.NewGuid();
        return new Agent { Id = id, RunnerId = runnerId, OwnerId = User.BootstrapId, Name = name, HarnessId = "pi", WorkingDirectory = folder,
            TmuxSession = $"dam-test-{id:N}", HarnessSessionId = "native", State = AgentState.Running,
            Project = new Project { Id = id, RunnerId = runnerId, OwnerId = User.BootstrapId, Name = name, RootDirectory = folder } };
    }

    private static async Task<JsonElement> PhaseAsync(string home, string phase, object data, bool refused = false)
    {
        var (code, output, error) = await RunAsync(home, AgentService.WorkspaceCommand(phase, data), check: false);
        if (refused)
        {
            Assert.Equal(3, code);
            return JsonDocument.Parse(JsonSerializer.Serialize(error.Trim())).RootElement.Clone();
        }
        Assert.True(code == 0, error);
        return JsonDocument.Parse(output.Trim().Split('\n')[^1]).RootElement.Clone();
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string home, string script, bool check = true)
    {
        var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["HOME"] = home;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        if (check) Assert.True(process.ExitCode == 0, await error);
        return (process.ExitCode, await output, await error);
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
