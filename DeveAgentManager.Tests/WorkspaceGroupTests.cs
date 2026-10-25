using DeveAgentManager.Client.Dialogs;
using DeveAgentManager.Client.Shared;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeveAgentManager.Tests;

public class WorkspaceGroupTests
{
    private static AgentDto Agent(Guid id, Guid project, AgentState state, DateTime? reply = null) => new(id, Guid.NewGuid(), Guid.Empty, "owner", AgentAccess.Owner,
        "runner", RunnerState.Ready, [], [], id.ToString(), "pi", "/work/project", null, "feature", state, null, null, null, null, Guid.Empty, "owner",
        DateTime.UtcNow, DateTime.UtcNow, ProjectId: project, ProjectName: "project", LastAssistantMessageAt: reply);

    [Fact]
    public void SidebarKeepsTheSoleRunningAgentAndTheCurrentlyViewedStoppedWorkspace()
    {
        var project = Guid.NewGuid();
        var running = Agent(Guid.NewGuid(), project, AgentState.Running);
        var stopped = Agent(Guid.NewGuid(), project, AgentState.Stopped);
        Assert.True(WorkspaceGroups.Sidebar(running, null));
        Assert.False(WorkspaceGroups.Sidebar(stopped, null));
        Assert.True(WorkspaceGroups.Sidebar(stopped, stopped.Id));
        Assert.Single(WorkspaceGroups.Group(new[] { running, stopped }.Where(a => WorkspaceGroups.Sidebar(a, null)))[0]);
        Assert.Equal(2, WorkspaceGroups.Group(new[] { running, stopped }.Where(a => WorkspaceGroups.Sidebar(a, stopped.Id)))[0].Count);
    }

    [Fact]
    public void ErrorsUnreachableRunnersAndUnreadResultsAreNeverQuietStoppedWorkspaces()
    {
        var agent = Agent(Guid.NewGuid(), Guid.NewGuid(), AgentState.Stopped);
        Assert.True(WorkspaceGroups.QuietStopped(agent));
        Assert.False(WorkspaceGroups.QuietStopped(agent with { State = AgentState.Error }));
        Assert.False(WorkspaceGroups.QuietStopped(agent with { State = AgentState.Exited, ExitCode = 1 }));
        Assert.False(WorkspaceGroups.QuietStopped(agent with { RunnerState = RunnerState.Error }));
        var unread = agent with { Activity = new(AgentActivityStatus.Ready, "Done", "test", null, null, DateTime.UtcNow, 1, false, DateTime.UtcNow, 2, 1) };
        Assert.False(WorkspaceGroups.QuietStopped(unread));
        Assert.True(WorkspaceGroups.Sidebar(unread, null));
    }

    [Fact]
    public void ProjectsAreOrderedByTheirLatestReplyAndMatchingRepositoriesRemainSeparate()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var agents = new[] { Agent(Guid.NewGuid(), first, AgentState.Running, DateTime.UtcNow.AddHours(-1)),
            Agent(Guid.NewGuid(), second, AgentState.Running, DateTime.UtcNow.AddMinutes(-5)),
            Agent(Guid.NewGuid(), first, AgentState.Running, DateTime.UtcNow) };
        var groups = WorkspaceGroups.Group(agents);
        Assert.Equal(2, groups.Count); Assert.Equal(first, WorkspaceGroups.ProjectKey(groups[0][0]));
        Assert.Equal(2, groups[0].Count); Assert.Equal(agents[2].Id, groups[0][0].Id);
    }
    [Fact]
    public async Task EveryAgentKnowsHowManyWorkspacesItsProjectHas()
    {
        var root = Directory.CreateTempSubdirectory("dam-workspace-count-").FullName;
        try { await CountAsync(Path.Combine(root, "manager.db")); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task CountAsync(string path)
    {
        await using var db = new ManagerDbContext(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path}").Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var runner = new Runner { Id = Guid.NewGuid(), Name = "runner", OwnerId = User.BootstrapId };
        var project = new Project { Id = Guid.NewGuid(), RunnerId = runner.Id, OwnerId = User.BootstrapId, Name = "project" };
        Agent Add(string name, Guid? projectId, bool clone = false) =>
            db.Agents.Add(new Agent { Id = Guid.NewGuid(), RunnerId = runner.Id, OwnerId = User.BootstrapId, Name = name, ProjectId = projectId, IsClone = clone }).Entity;
        db.Runners.Add(runner); db.Projects.Add(project);
        var main = Add("project", project.Id); var fix = Add("fix", project.Id, true); Add("docs", project.Id, true);
        var alone = Add("alone", null);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // Listing only some of them (a filtered list) still counts the whole project.
        await AgentAccountNames.LoadAsync(db, [main, fix, alone], TestContext.Current.CancellationToken);
        Assert.Equal((2, 2, 0), (main.ProjectWorkspaces, fix.ProjectWorkspaces, alone.ProjectWorkspaces));
    }

    [Fact]
    public void MovingFromAnAgentsOwnMenuSaysWhenItMovesAWholeProject()
    {
        var agent = Agent(Guid.NewGuid(), Guid.NewGuid(), AgentState.Running);
        Assert.Equal("Move to another runner…", MoveAgentDialog.MenuLabel(agent));
        Assert.Equal("Move project to another runner…", MoveAgentDialog.MenuLabel(agent with { Workspaces = 1 }));
        Assert.Equal("Move project to another runner…", MoveAgentDialog.MenuLabel(agent with { IsClone = true, Workspaces = 1 }));
    }
}
