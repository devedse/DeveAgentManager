using DeveAgentManager.Client.Shared;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
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
}
