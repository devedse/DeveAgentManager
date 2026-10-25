using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Data;

public class Runner
{
    /// <summary>IANA timezone for new harness processes; null inherits the Manager default.</summary>
    public string? TimeZoneId { get; set; }
    public Guid Id { get; set; }

    /// <summary>Who created it (or got it from a runner manager). Has <see cref="RunnerAccess.Owner"/>.</summary>
    public Guid OwnerId { get; set; }

    public User? Owner { get; set; }

    /// <summary>Everyone else with access to it.</summary>
    public List<RunnerMember> Members { get; set; } = [];

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

    /// <summary>The Runner Account's password, encrypted: generated for each new VM, or entered for an existing machine.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>
    /// When the password was set. Null for a VM from before each Runner had its own password: it still has the one all
    /// Runners shared, and the next setup replaces it.
    /// </summary>
    public DateTime? PasswordChangedAt { get; set; }

    /// <summary>SSH refuses passwords; only keys log in (the Manager's, and those of the people with access).</summary>
    public bool KeysOnly { get; set; }

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

/// <summary>A repository or folder on one Runner. Existing agents migrate to separate projects.</summary>
public class Project
{
    public Guid Id { get; set; }
    public Guid RunnerId { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string RootDirectory { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>JSON list of folders kept when their workspace was removed; they can be deleted or reused.</summary>
    public string? KeptFoldersJson { get; set; }
    public List<Agent> Agents { get; set; } = [];
}

public class Agent
{
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    /// <summary>A workspace cloned next to Main, in &lt;main&gt;.workspaces/&lt;name&gt;. Main itself is not a clone.</summary>
    public bool IsClone { get; set; }
    /// <summary>Durable creation intent; Start verifies an interrupted creation without repeating it.</summary>
    public string? WorkspaceCreationJson { get; set; }
    /// <summary>Durable move intent and rollback checkpoints; credentials are never included.</summary>
    public string? MoveJson { get; set; }
    /// <summary>The latest completed move result, retained after its rollback checkpoints are cleared.</summary>
    public string? LastMoveJson { get; set; }
    /// <summary>Timestamp of the latest assistant text in the synced conversation.</summary>
    public DateTime? LastAssistantMessageAt { get; set; }
    /// <summary>Durable semantic observation and transition bookkeeping, independent of process lifecycle.</summary>
    public string? ActivityJson { get; set; }
    public long SeenCompletionSequence { get; set; }
    public string? DismissedAttentionToken { get; set; }
    public Guid Id { get; set; }

    public Guid RunnerId { get; set; }

    public Runner? Runner { get; set; }

    /// <summary>Who created it. Its harness runs with this user's git identity unless <see cref="GitIdentityUserId"/> says otherwise.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>
    /// Whose git identity, GitHub token and environment the harness runs with; null is the owner. Starting and
    /// restarting never change it: only an explicit switch (<see cref="Agents.AgentService.SetGitIdentityAsync"/>) does.
    /// </summary>
    public Guid? GitIdentityUserId { get; set; }

    /// <summary>The git identity the current harness was started with; null is the owner (or a start before identities).</summary>
    public Guid? RunningGitIdentityUserId { get; set; }

    /// <summary>The agent's task list (dam-tools tasks) as last read from its Runner, as JSON; null without one.</summary>
    public string? TasksJson { get; set; }

    /// <summary>The revision of <see cref="TasksJson"/>; the Runner's list is read again when its revision differs.</summary>
    public long TasksRevision { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public (string Username, string DisplayName)? GitIdentityName { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public (string Username, string DisplayName)? RunningGitIdentityName { get; set; }

    public User? Owner { get; set; }

    public List<AgentShare> Shares { get; set; } = [];

    public string Name { get; set; } = "";

    public string HarnessId { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";

    public string? RepositoryUrl { get; set; }

    public string? RepositoryBranch { get; set; }

    /// <summary>Name of the tmux session the harness runs in.</summary>
    public string TmuxSession { get; set; } = "";

    /// <summary>The harness's own session id, so a restart resumes the same conversation.</summary>
    public string HarnessSessionId { get; set; } = "";

    /// <summary>
    /// Selected harness login. Null uses the owner's personal default on the next start.
    /// Launch requires permission; a missing or revoked login never falls back to ambient Runner credentials.
    /// </summary>
    public Guid? AccountId { get; set; }
    /// <summary>The login actually used by the current harness, including default selections.</summary>
    public Guid? RunningAccountId { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? RunningAccountName { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? RunningAccountUsername { get; set; }
    /// <summary>Persisted before remote enforcement. Blocks launch until an explicit permitted selection.</summary>
    public bool LoginBlocked { get; set; }
    public bool LoginStopPending { get; set; }


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
    /// <summary>The project the conversation belongs to. It stays when its workspace is removed.</summary>
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    // The workspace it last ran in; AgentId identifies only where it is active now.
    public Guid? WorkspaceAgentId { get; set; }
    public Agent? WorkspaceAgent { get; set; }
    public bool TitlePinned { get; set; }
    public long SavedSize { get; set; }
    public long TotalSize { get; set; }

    public Guid Id { get; set; }

    /// <summary>Whose conversation it is: its workspace's owner, or who imported or continued it.</summary>
    public Guid OwnerId { get; set; }

    public User? Owner { get; set; }

    public List<SessionShare> Shares { get; set; } = [];

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

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public bool Enabled { get; set; } = true;
    public int AccessVersion { get; set; } = 1;
    public List<AccountGrant> Grants { get; set; } = [];

    public string HarnessId { get; set; } = "";

    public string Name { get; set; } = "";

    public AccountKind Kind { get; set; }

    /// <summary>The credential (a token, an API key or a harness's login file), encrypted.</summary>
    public string ProtectedSecret { get; set; } = "";

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsDefault { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public AccountAccess Access { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool CanManage { get; set; }

    /// <summary>Goes up when the credential is replaced (a new login), so Runners take the new one.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The Manager renews this subscription login and Runners only get its access token (see IHarness.RenewsAccounts).
    /// False for logins connected before that existed: their stored copy may already be used up on a Runner, so they
    /// keep renewing on the Runner until connected again.
    /// </summary>
    public bool ManagedRenewal { get; set; }

    /// <summary>Why the last renewal failed; null when it worked.</summary>
    public string? RenewalProblem { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public class AccountGrant
{
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public AccountAccess Access { get; set; }
    public Guid GrantedById { get; set; }
    public DateTime GrantedAt { get; set; }
}

public class UserHarnessDefault
{
    public Guid UserId { get; set; }
    public string HarnessId { get; set; } = "";
    public Guid AccountId { get; set; }
}

/// <summary>Legacy selection delegated only to this agent and its original owner.</summary>
public class AgentLoginGrant
{
    public Guid AgentId { get; set; }
    public Guid AccountId { get; set; }
    public Guid OwnerId { get; set; }
}

/// <summary>Durable cleanup, including offline Runners; not deleted with the login.</summary>
public class AccountCleanup
{
    public Guid AccountId { get; set; }
    public Guid RunnerId { get; set; }
}

/// <summary>Someone who signs in to the Portal.</summary>
public class User
{
    /// <summary>The user the first migration creates, so existing Runners, agents and conversations have an owner.</summary>
    public static readonly Guid BootstrapId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; }

    /// <summary>What they sign in with; unique, compared case-insensitively.</summary>
    public string Username { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>ASP.NET Core Identity's password hash. Empty until the user's first password is set.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Changes with the password, roles or disabling, which signs the user out everywhere.</summary>
    public string SecurityStamp { get; set; } = "";

    public UserRoles Roles { get; set; }

    public bool Disabled { get; set; }

    /// <summary>Set by a password a user manager chose: the user picks their own at the next sign-in.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Wrong passwords in a row; reset by a correct one.</summary>
    public int FailedSignIns { get; set; }

    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastSignInAt { get; set; }
}

/// <summary>A user's own setting (My settings), e.g. their git identity or GitHub token.</summary>
public class UserSetting
{
    public Guid UserId { get; set; }

    public string Key { get; set; } = "";

    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Access to a Runner for someone besides its owner.</summary>
public class RunnerMember
{
    public Guid RunnerId { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary><see cref="RunnerAccess.Use"/> or <see cref="RunnerAccess.Manage"/>.</summary>
    public RunnerAccess Access { get; set; }

    public Guid? GrantedById { get; set; }

    public DateTime GrantedAt { get; set; }
}

/// <summary>An agent its owner shared with another user.</summary>
public class AgentShare
{
    public Guid AgentId { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary><see cref="AgentAccess.Read"/> or <see cref="AgentAccess.Write"/>.</summary>
    public AgentAccess Access { get; set; }

    public Guid? GrantedById { get; set; }

    public DateTime GrantedAt { get; set; }
}

/// <summary>A conversation its owner shared, read-only, with another user.</summary>
public class SessionShare
{
    public Guid SessionId { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public Guid? GrantedById { get; set; }

    public DateTime GrantedAt { get; set; }
}

/// <summary>Something security-related that happened: sign-ins, users and roles, access, shares and Runner passwords.</summary>
public class AuditEvent
{
    public long Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>Who did it; null for the Manager itself (or a sign-in with an unknown username).</summary>
    public Guid? ActorId { get; set; }

    /// <summary>The actor's username at the time, so the entry stays readable after the user is gone.</summary>
    public string? ActorName { get; set; }

    /// <summary>What happened, e.g. "user-created" (see <see cref="AuditActions"/>).</summary>
    public string Action { get; set; } = "";

    /// <summary>What it happened to, as the Portal shows it, e.g. "Runner runner-01" or "User ketty".</summary>
    public string? Target { get; set; }

    public string? Detail { get; set; }
}
