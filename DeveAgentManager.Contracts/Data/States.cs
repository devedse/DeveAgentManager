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

/// <summary>How an operation on a Runner (setup, start, an update) ended.</summary>
public enum RunnerActivityOutcome
{
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>The Manager stopped (or restarted) while it ran.</summary>
    Interrupted,
}

/// <summary>
/// What a user may do besides using what is theirs or shared with them. A user can have any combination; Admin is all of
/// them.
/// </summary>
[Flags]
public enum UserRoles
{
    None = 0,

    /// <summary>Manager settings: providers, templates, backups, the default timezone and the shared Setup Profile.</summary>
    SettingsManager = 1,

    /// <summary>Create and disable users, reset passwords, grant roles they have themselves, read the audit log.</summary>
    UserManager = 2,

    /// <summary>Create Runners (a Proxmox VM or an existing machine); the creator becomes the owner.</summary>
    RunnerCreator = 4,

    /// <summary>Every Runner: see it, give people access, change its owner, view and set its password, delete it.</summary>
    RunnerManager = 8,

    /// <summary>Manage every agent and conversation, including prompting, sharing and deletion.</summary>
    ConversationManager = 16,

    /// <summary>Read and download the Manager’s application logs.</summary>
    LogViewer = 32,

    /// <summary>Manage every harness login and its access list; usage requires an explicit grant.</summary>
    HarnessLoginManager = 64,

    Admin = HarnessLoginManager | LogViewer | SettingsManager | UserManager | RunnerCreator | RunnerManager | ConversationManager,
}

/// <summary>What a user may do with a Runner. Each level includes the ones before it.</summary>
public enum RunnerAccess
{
    None,

    /// <summary>Their own agents on it, its shell, VS Code and desktop, and their SSH key on it.</summary>
    Use,

    /// <summary>Also start, stop, set up, size, timezone, its password and "keys only", and who has access.</summary>
    Manage,

    /// <summary>Also delete it. The owner, and runner managers on every Runner.</summary>
    Owner,
}

/// <summary>What a user may do with an agent. Each level includes the ones before it.</summary>
public enum AgentAccess
{
    None,

    /// <summary>See that it exists and its status (people who manage its Runner); not its terminal or conversations.</summary>
    Listed,

    /// <summary>Read its conversations and watch its terminal.</summary>
    Read,

    /// <summary>Also type in its terminal, start, stop and switch it (only to their own harness login).</summary>
    Write,

    /// <summary>Also share and delete it.</summary>
    Owner,
}

/// <summary>What a user may do with a stored conversation.</summary>
public enum SessionAccess
{
    None,

    /// <summary>Read it, its images and export it; continue a copy of it in their own agent.</summary>
    Read,

    /// <summary>Also rename, share and delete it.</summary>
    Owner,
}

/// <summary>A login grant. Manage includes Use; global administration does not implicitly grant Use.</summary>
public enum AccountAccess { None, Use, Manage }
