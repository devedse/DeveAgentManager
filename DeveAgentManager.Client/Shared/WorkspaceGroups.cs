using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Client.Shared;

/// <summary>Shared navigation/filter rules. A stopped process, unread reply and unreachable Runner are distinct states.</summary>
public static class WorkspaceGroups
{
    public static Guid ProjectKey(AgentDto agent) => agent.ProjectId ?? agent.Id;
    public static string ProjectName(AgentDto agent) => agent.ProjectName ?? agent.Name;
    public static string WorkspaceName(AgentDto agent) => agent.IsClone ? agent.Name : "Main";
    /// <summary>A name that stands on its own outside a project group: "project · workspace" for a clone.</summary>
    public static string DisplayName(AgentDto agent) => agent.IsClone ? $"{ProjectName(agent)} · {agent.Name}" : agent.ProjectName ?? agent.Name;
    public static bool Running(AgentDto agent) => agent.State is AgentState.Running or AgentState.Starting;
    public static bool QuietStopped(AgentDto agent) => agent.RunnerState == RunnerState.Ready
        && agent.State is AgentState.Stopped or AgentState.Exited
        && agent.ExitCode is null or 0
        && agent.Activity?.UnseenCompletion != true && agent.StateMessage is null;
    public static bool Sidebar(AgentDto agent, Guid? selected) => Running(agent) || agent.Id == selected
        || agent.State is AgentState.Error or AgentState.Moving || agent.Activity?.UnseenCompletion == true
        || agent.State == AgentState.Exited && agent.ExitCode is not (null or 0);
    /// <summary>A project's conversations: its own, and those of its workspaces from before conversations had a project.</summary>
    public static IEnumerable<SessionDto> InProject(IEnumerable<SessionDto> sessions, Guid projectKey, IReadOnlyList<AgentDto> projectAgents) =>
        sessions.Where(s => s.ProjectId == projectKey || s.WorkspaceAgentId is { } workspace && projectAgents.Any(a => a.Id == workspace));
    /// <summary>Where a saved conversation last ran: a workspace's name, or its folder's when that workspace was removed.</summary>
    public static string LastRanIn(SessionDto session, IEnumerable<AgentDto> projectAgents) =>
        projectAgents.FirstOrDefault(a => a.Id == session.WorkspaceAgentId) is { } agent ? WorkspaceName(agent)
        : session.WorkingDirectory is { } folder ? $"{folder.TrimEnd('/').Split('/')[^1]} (removed)" : "removed workspace";
    public static IReadOnlyList<IReadOnlyList<AgentDto>> Group(IEnumerable<AgentDto> agents) => agents.ByLastReply()
        .GroupBy(ProjectKey).Select(g => (IReadOnlyList<AgentDto>)g.ToList()).ToList();
}
