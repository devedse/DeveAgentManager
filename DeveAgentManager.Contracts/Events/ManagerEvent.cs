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

    /// <summary>A user was added or changed (name, roles, password, disabled); <see cref="ManagerEvent.UserId"/> says who.</summary>
    UserChanged,
    UserRemoved,

    /// <summary>Who may see something changed (an owner, Runner access or a share): pages reload what they show.</summary>
    AccessChanged,

    /// <summary>
    /// A Runner's CPU, memory or disk reading was refreshed (every 15 seconds). Only what shows them reloads; anything
    /// else about the Runner changing is <see cref="RunnerChanged"/>.
    /// </summary>
    RunnerUsageChanged,
}

/// <param name="UserId">For changes that concern one user only: their settings, logins or account.</param>
public sealed record ManagerEvent(ManagerEventKind Kind, Guid? RunnerId = null, Guid? AgentId = null, Guid? SessionId = null, Guid? UserId = null);
