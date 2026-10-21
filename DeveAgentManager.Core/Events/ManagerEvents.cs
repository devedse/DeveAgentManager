using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Events;

/// <summary>In-process notifications so open Portal pages update live.</summary>
public sealed class ManagerEvents
{
    public event Action<ManagerEvent>? Changed;

    public event Action<Guid, RunnerLogLine>? RunnerLogWritten;

    public void Publish(ManagerEvent managerEvent) => Raise(() => Changed?.Invoke(managerEvent));

    public void RunnerChanged(Guid runnerId) => Publish(new ManagerEvent(ManagerEventKind.RunnerChanged, runnerId));

    public void RunnerUsageChanged(Guid runnerId) => Publish(new ManagerEvent(ManagerEventKind.RunnerUsageChanged, runnerId));

    // Without the Runner: only the people who can read the agent hear about its tasks.
    public void TasksChanged(Guid agentId) => Publish(new ManagerEvent(ManagerEventKind.TasksChanged, AgentId: agentId));

    public void AgentChanged(Guid runnerId, Guid agentId) => Publish(new ManagerEvent(ManagerEventKind.AgentChanged, runnerId, agentId));

    public void SessionChanged(Guid sessionId, Guid? agentId = null) => Publish(new ManagerEvent(ManagerEventKind.SessionChanged, AgentId: agentId, SessionId: sessionId));

    public void PublishLog(Guid runnerId, RunnerLogLine line) => Raise(() => RunnerLogWritten?.Invoke(runnerId, line));

    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A broken subscriber (for example a disconnected Blazor circuit) must never break the operation that raised the event.
        }
    }
}
