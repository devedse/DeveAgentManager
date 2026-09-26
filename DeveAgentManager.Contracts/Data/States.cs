namespace DeveAgentManager.Core.Data;

public enum RunnerState
{
    Creating,
    Starting,
    WaitingForNetwork,
    SettingUp,
    Ready,
    Stopping,
    Stopped,
    Deleting,
    Error,
}

public enum AgentState
{
    Starting,
    Running,
    Exited,
    Stopped,
    Unknown,
    Error,
}

/// <summary>How an account logs in.</summary>
public enum AccountKind
{
    /// <summary>A subscription (Claude Pro/Max, ChatGPT Plus/Pro), logged in once in a terminal.</summary>
    Subscription,

    /// <summary>An API key, billed per use.</summary>
    ApiKey,
}
