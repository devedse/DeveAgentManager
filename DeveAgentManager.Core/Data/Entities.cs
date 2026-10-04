using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Data;

public class Runner
{
    /// <summary>IANA timezone for new harness processes; null inherits the Manager default.</summary>
    public string? TimeZoneId { get; set; }
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string ProviderId { get; set; } = "";

    public string? TemplateId { get; set; }

    public RunnerKind Kind { get; set; }

    /// <summary>The provider's id for the machine (the VMID on Proxmox, the host for an existing machine).</summary>
    public string? MachineId { get; set; }

    public string? ProviderData { get; set; }

    public RunnerState State { get; set; }

    public string? StateMessage { get; set; }

    public string? Host { get; set; }

    public int SshPort { get; set; } = 22;

    public string Username { get; set; } = "";

    /// <summary>Only set for existing machines added with a password; encrypted.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>SHA256 fingerprint of the SSH host key, learned on first connect (trust on first use).</summary>
    public string? HostKeyFingerprint { get; set; }

    public string? HomeDirectory { get; set; }

    public int Cores { get; set; }

    public int MemoryMb { get; set; }

    public int DiskGb { get; set; }

    /// <summary>Comma separated harness ids to install, e.g. "pi".</summary>
    public string Harnesses { get; set; } = "";

    /// <summary>Comma separated ids of the other tools to install (<see cref="IRunnerTool"/>), e.g. "vscode".</summary>
    public string Tools { get; set; } = "";

    public int? SetupVersion { get; set; }

    /// <summary>Tool versions found after the last setup, e.g. "pi 0.87.1 · node v24.8.0 · tmux 3.5a".</summary>
    public string? InstalledVersions { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastSetupAt { get; set; }

    public List<Agent> Agents { get; set; } = [];

    public IReadOnlyList<string> HarnessIds => Harnesses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> ToolIds => Tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public RunnerMachine ToMachine() => new(Id, Name, MachineId, ProviderData, Host, SshPort);
}

/// <summary>One observed limit and an optional, explicitly armed, single continuation.</summary>
public class AgentAutoContinue
{
    public Guid AgentId { get; set; }
    public string SessionId { get; set; } = "";
    public bool Enabled { get; set; }
    public int? Remaining { get; set; }
    public int Sent { get; set; }
    public string? LastLimitKey { get; set; }
    public string? Detail { get; set; }
}

public class AgentLimit
{
    public bool Automatic { get; set; }
    public bool Manual { get; set; }
    public Guid AgentId { get; set; }
    public string SessionId { get; set; } = "";
    public string PaneIdentity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? TimeZoneId { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime? ResetAt { get; set; }
    public DateTime? ContinueAt { get; set; }
    public string Status { get; set; } = "Detected";
    public string? Detail { get; set; }
    public bool NativeAutoContinue { get; set; }
}

/// <summary>An operation that ran on a Runner (setup, start, an update), for the Runner's activity list.</summary>
public class RunnerActivity
{
    public long Id { get; set; }

    public Guid RunnerId { get; set; }

    /// <summary>What ran, as the Portal shows it while it runs, e.g. "Setting up" or "Updating pi".</summary>
    public string Name { get; set; } = "";

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>Null while it runs.</summary>
    public RunnerActivityOutcome? Outcome { get; set; }

    public string? Error { get; set; }
}

public class Agent
{
    /// <summary>Timestamp of the latest assistant text in the synced conversation.</summary>
    public DateTime? LastAssistantMessageAt { get; set; }
    /// <summary>Durable semantic observation and transition bookkeeping, independent of process lifecycle.</summary>
    public string? ActivityJson { get; set; }
    public long SeenCompletionSequence { get; set; }
    public string? DismissedAttentionToken { get; set; }
    public Guid Id { get; set; }

    public Guid RunnerId { get; set; }

    public Runner? Runner { get; set; }

    public string Name { get; set; } = "";

    public string HarnessId { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";

    public string? RepositoryUrl { get; set; }

    public string? RepositoryBranch { get; set; }

    /// <summary>Name of the tmux session the harness runs in.</summary>
    public string TmuxSession { get; set; } = "";

    /// <summary>The harness's own session id, so a restart resumes the same conversation.</summary>
    public string HarnessSessionId { get; set; } = "";

    /// <summary>The account the harness runs with; null uses the harness's default account (or the Runner's own login).</summary>
    public Guid? AccountId { get; set; }

    public Account? Account { get; set; }

    public AgentState State { get; set; }

    public string? StateMessage { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>
    /// Running and busy: the harness printed something in the last seconds (they animate a spinner while they work).
    /// False while it waits for input. See <see cref="Agents.AgentService.RefreshRunnerAsync"/>.
    /// </summary>
    public bool Working { get; set; }

    /// <summary>Changes when the agent's icon (<see cref="AgentIcon"/>) changes; null when it has none (the harness's logo is shown).</summary>
    public long? IconVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The icon of the project an agent works on: found in its folder (see <see cref="Agents.RepoIcons"/>) or picked in the
/// Portal, and kept by the Manager so lists show it without asking the Runner.
/// </summary>
public class AgentIcon
{
    public Guid AgentId { get; set; }

    /// <summary>The image, relative to the agent's folder; null for none (the harness's logo).</summary>
    public string? Path { get; set; }

    public string? ContentType { get; set; }

    public byte[]? Data { get; set; }

    /// <summary>Picked in the Portal (a file, or none): the Manager doesn't look for another one.</summary>
    public bool ChosenByUser { get; set; }

    /// <summary>When the folder was last searched.</summary>
    public DateTime CheckedAt { get; set; }
}

/// <summary>
/// A conversation stored by the Manager (the content is a DeveSession file in /data/sessions). It stays when its agent
/// or Runner is deleted, and can then be continued on another Runner.
/// </summary>
public class StoredSession
{
    // Workspace ownership survives deactivation; AgentId identifies only the selected conversation.
    public Guid? WorkspaceAgentId { get; set; }
    public Agent? WorkspaceAgent { get; set; }
    public bool TitlePinned { get; set; }
    public long SavedSize { get; set; }
    public long TotalSize { get; set; }

    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    /// <summary>The harness it last ran in, e.g. "pi".</summary>
    public string HarnessId { get; set; } = "";

    /// <summary>The harness's own session id on the Runner.</summary>
    public string? HarnessSessionId { get; set; }

    /// <summary>The harnesses the conversation went through, oldest first, e.g. "pi,codex,claude".</summary>
    public string? HarnessTrail { get; set; }

    /// <summary>The agent currently running this session; null once the agent (or its Runner) is deleted.</summary>
    public Guid? AgentId { get; set; }

    public Agent? Agent { get; set; }

    public string? RunnerName { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? RepositoryUrl { get; set; }

    public string? RepositoryBranch { get; set; }

    public int MessageCount { get; set; }

    /// <summary>Path and size of the harness's session file on the Runner at the last sync.</summary>
    public string? SourcePath { get; set; }

    public long SourceSize { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Last activity in the conversation.</summary>
    public DateTime UpdatedAt { get; set; }

    public DateTime? LastSyncedAt { get; set; }
}

public class SettingEntry
{
    public string Key { get; set; } = "";

    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A login for a harness (e.g. a Claude subscription), stored encrypted in the Manager and copied to Runners when an
/// agent uses it. A harness can have several (e.g. two Claude subscriptions, to switch when one hits its limit).
/// </summary>
public class Account
{
    public Guid Id { get; set; }

    public string HarnessId { get; set; } = "";

    public string Name { get; set; } = "";

    public AccountKind Kind { get; set; }

    /// <summary>The credential (a token, an API key or a harness's login file), encrypted.</summary>
    public string ProtectedSecret { get; set; } = "";

    /// <summary>Used by agents that don't pick an account.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Goes up when the credential is replaced (a new login), so Runners take the new one.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
