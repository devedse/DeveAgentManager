using DeveAgentManager.Contracts;

namespace DeveAgentManager.Client.Shared;

/// <summary>
/// How agents are listed everywhere (AGENTS.md): the one that replied last first; agents that never replied after them,
/// newest first.
/// </summary>
public static class AgentOrder
{
    public static IEnumerable<AgentDto> ByLastReply(this IEnumerable<AgentDto> agents) => agents
        .OrderByDescending(a => a.LastAssistantMessageAt ?? DateTime.MinValue)
        .ThenByDescending(a => a.CreatedAt)
        .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
}
