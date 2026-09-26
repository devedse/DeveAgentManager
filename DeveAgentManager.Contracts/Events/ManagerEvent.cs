namespace DeveAgentManager.Core.Events;

public enum ManagerEventKind
{
    RunnerChanged,
    RunnerRemoved,
    AgentChanged,
    AgentRemoved,
    SettingsChanged,
    SessionChanged,
    SessionRemoved,
}

public sealed record ManagerEvent(ManagerEventKind Kind, Guid? RunnerId = null, Guid? AgentId = null, Guid? SessionId = null);
