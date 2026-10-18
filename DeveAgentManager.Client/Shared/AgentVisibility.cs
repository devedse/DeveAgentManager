using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Client.Shared;

public static class AgentVisibility
{
    public static IEnumerable<AgentDto> ForAgentList(this IEnumerable<AgentDto> agents, CurrentUserDto? user) =>
        user is null ? [] : agents.Where(a => a.OwnerId == user.Id || a.SharedWithMe
            || user.Has(UserRoles.ConversationManager) || user.Has(UserRoles.RunnerManager));
}
