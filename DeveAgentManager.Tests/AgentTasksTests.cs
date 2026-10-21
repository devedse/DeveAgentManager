using System.Text;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Agents' task lists (docs/agent-tasks.md): written by the agent with dam-tools on its Runner, read by the Manager when
/// their revision changed, bounded on the way in, put back on a Runner that lost them, and cleared from the Portal on the
/// Runner too.
/// </summary>
public sealed class AgentTasksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Line(Guid agent, object state) =>
        $"{AgentTasks.Marker} {agent:N} {Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state)))}";

    [Fact]
    public void CollectedListsAreBounded_AndAnythingThatIsntOneIsLeftOut()
    {
        var agent = Guid.NewGuid();
        var other = Guid.NewGuid();
        var tasks = Enumerable.Range(1, 60).Select(i => (object)new { id = i, name = $"Task {i}", percent = 10 }).ToList();
        tasks[0] = new { id = 1, name = new string('n', 300) + "\u001b[31m", percent = 250, description = "line one\nline two" + new string('d', 2000),
            startedAt = "2026-10-21T13:00:00Z", leftSeconds = -5, takenSeconds = 90 };
        tasks[1] = new { id = "two", name = "No number" };
        tasks[2] = new { id = 3, name = "  " };
        var output = string.Join('\n', "unrelated", Line(agent, new { revision = 7, updatedAt = "2026-10-21T13:05:00Z", tasks }),
            $"{AgentTasks.Marker} {other:N} not-base64!", $"{AgentTasks.Marker} not-a-guid {Convert.ToBase64String("{}"u8.ToArray())}");

        var lists = AgentTasks.Parse(output);
        var list = Assert.Single(lists).Value;
        Assert.Equal((7L, new DateTime(2026, 10, 21, 13, 5, 0, DateTimeKind.Utc)), (list.Revision, list.UpdatedAt));
        Assert.Equal(48, list.Tasks.Count); // 50 read, the two without a number or name left out.
        var first = list.Tasks[0];
        Assert.Equal(120, first.Name.Length);
        Assert.DoesNotContain('\u001b', first.Name);
        Assert.Equal(100, first.Percent);
        Assert.StartsWith("line one\nline two", first.Description);
        Assert.Equal(1000, first.Description.Length);
        Assert.Equal((90L, (long?)null), (first.TakenSeconds, first.LeftSeconds));
        Assert.Equal(new DateTime(2026, 10, 21, 13, 0, 0, DateTimeKind.Utc), first.StartedAt);
        Assert.Empty(AgentTasks.Parse(Line(agent, new { revision = "x", tasks = Array.Empty<object>() })));
        Assert.Equal("", AgentTasks.CollectScript([]));
    }

    [Fact(Timeout = 120_000)]
    public async Task TheAgentsListComesToTheManager_AndClearingItThereClearsItOnTheRunner()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd") || !File.Exists("/usr/bin/python3"), "Requires isolated OpenSSH and python3.");
        await using var ssh = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDeveAgentManagerCore(new ManagerOptions { DataDirectory = ssh.Root + "/manager", AdminPassword = "test-only-password" });
        collection.AddSingleton(ssh.Shell);
        await using var services = collection.BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        var tasks = services.GetRequiredService<AgentTasks>();
        var changes = new List<ManagerEvent>();
        services.GetRequiredService<ManagerEvents>().Changed += e => { lock (changes) changes.Add(e); };
        var agent = new Agent { Id = Guid.NewGuid(), RunnerId = ssh.Runner.Id, OwnerId = User.BootstrapId, Name = "tasks-test", HarnessId = "claude",
            TmuxSession = "dam-tasks-test", HarnessSessionId = Guid.NewGuid().ToString("N"), WorkingDirectory = ssh.Root + "/home", State = AgentState.Running };
        var elsewhere = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await db.Database.MigrateAsync(Ct);
            ssh.Runner.OwnerId = User.BootstrapId; ssh.Runner.State = RunnerState.Ready;
            db.Runners.Add(ssh.Runner);
            db.Agents.Add(agent);
            await db.SaveChangesAsync(Ct);
        }

        Directory.CreateDirectory(ssh.Root + "/home/.deveagentmanager");
        File.WriteAllText(ssh.Root + "/home/.deveagentmanager/dam-tools.py", RunnerSetupService.ReadResource("dam-tools.py"));
        async Task<string> AsAgent(string arguments) => (await ssh.Shell.RunAsync(ssh.Runner,
            $"DAM_AGENT_ID={agent.Id} python3 ~/.deveagentmanager/dam-tools.py tasks {arguments}", cancellationToken: Ct)).EnsureSuccess("dam-tools").Output;
        async Task CollectAsync()
        {
            await using var db = await factory.CreateDbContextAsync(Ct);
            var current = await db.Agents.AsNoTracking().Where(a => a.Id == agent.Id).ToListAsync(Ct);
            var output = (await ssh.Shell.RunAsync(ssh.Runner, AgentTasks.CollectScript(current), cancellationToken: Ct)).Output;
            var lists = AgentTasks.Parse(output);
            if (lists.TryGetValue(agent.Id, out var list)) lists[elsewhere] = list; // Another Runner's agent can't be written from here.
            await tasks.ApplyAsync(ssh.Runner.Id, lists, Ct);
        }

        // Nothing yet: nothing to read.
        await CollectAsync();
        Assert.Equal(AgentTaskListDto.Empty.Revision, (await tasks.GetAsync(agent.Id, Ct)).Revision);

        await AsAgent("set Build Test");
        await AsAgent("update 1 --percent 100 --description 'Built it'");
        await CollectAsync();
        var list = await tasks.GetAsync(agent.Id, Ct);
        Assert.Equal(2, list.Revision);
        Assert.Equal([("Build", 100, "Built it"), ("Test", 0, "")], list.Tasks.Select(t => (t.Name, t.Percent, t.Description)));
        Assert.Single(changes, e => e is { Kind: ManagerEventKind.TasksChanged, RunnerId: null } && e.AgentId == agent.Id);
        Assert.DoesNotContain(changes, e => e.AgentId == elsewhere);

        // Same revision: not read or announced again.
        await CollectAsync();
        Assert.Single(changes, e => e.Kind == ManagerEventKind.TasksChanged);

        // The Runner lost the list (the agent moved there): the Manager's copy goes back, unchanged and unannounced.
        (await ssh.Shell.RunAsync(ssh.Runner, $"rm ~/.deveagentmanager/tasks/{agent.Id:N}.json", cancellationToken: Ct)).EnsureSuccess("rm");
        await CollectAsync();
        Assert.Equal(2, (await tasks.GetAsync(agent.Id, Ct)).Revision);
        Assert.Contains("[100%] Build", await AsAgent(""));
        Assert.Single(changes, e => e.Kind == ManagerEventKind.TasksChanged);
        Assert.Equal("", (await ssh.Shell.RunAsync(ssh.Runner, "ls ~/.deveagentmanager/tasks | grep restore || true", cancellationToken: Ct)).Output.Trim());

        await tasks.ClearAsync(agent.Id, Ct);
        Assert.Equal((3L, 0), (await tasks.GetAsync(agent.Id, Ct)) is var cleared ? (cleared.Revision, cleared.Tasks.Count) : default);
        Assert.Contains("No tasks", await AsAgent(""));
        Assert.Equal(2, changes.Count(e => e.Kind == ManagerEventKind.TasksChanged));
    }
}
