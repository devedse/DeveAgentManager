using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Client.Shared;

/// <summary>Shared navigation/filter rules. A stopped process, unread reply and unreachable Runner are distinct states.</summary>
public static class WorkspaceGroups
{
    public static Guid ProjectKey(AgentDto agent) => agent.ProjectId ?? agent.Id;
    public static string ProjectName(AgentDto agent) => agent.ProjectName ?? agent.Name;
    public static string WorkspaceName(AgentDto agent) => agent.IsWorktree ? agent.Name : "Main";
    public static bool Running(AgentDto agent) => agent.State is AgentState.Running or AgentState.Starting;
    public static bool QuietStopped(AgentDto agent) => agent.RunnerState == RunnerState.Ready
        && agent.State is AgentState.Stopped or AgentState.Exited
        && agent.ExitCode is null or 0
        && agent.Activity?.UnseenCompletion != true && agent.StateMessage is null;
    public static bool Sidebar(AgentDto agent, Guid? selected) => Running(agent) || agent.Id == selected
        || agent.State is AgentState.Error or AgentState.Moving || agent.Activity?.UnseenCompletion == true
        || agent.State == AgentState.Exited && agent.ExitCode is not (null or 0);
    public static IReadOnlyList<IReadOnlyList<AgentDto>> Group(IEnumerable<AgentDto> agents) => agents.ByLastReply()
        .GroupBy(ProjectKey).Select(g => (IReadOnlyList<AgentDto>)g.ToList()).ToList();
}
