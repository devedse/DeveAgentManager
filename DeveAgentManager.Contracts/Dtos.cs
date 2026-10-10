using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;

namespace DeveAgentManager.Contracts;

/// <param name="Available">Whether the agent's harness shows usage-limit reset times the Manager recognizes (Codex, Claude); without
/// that, only one-time schedules work.</param>
public sealed record AutoContinueDto(bool Enabled = false, int? Remaining = null, int Sent = 0, string? Detail = null, bool Available = true);
public sealed record AutoContinueRequest(bool Enabled, int? Remaining = null);
public sealed record TimeZoneRequest(string? TimeZoneId);
public sealed record TimeZoneConfigDto(string DefaultTimeZoneId, string? OverrideTimeZoneId);
public sealed record AgentLimitDto(string Message, string? TimeZoneId, DateTime DetectedAt, DateTime? ResetAt,
    DateTime? ContinueAt, string Status, string? Detail, bool NativeAutoContinue, bool Manual = false, DateTime? DetectedResetAt = null);
public sealed record ScheduleContinueRequest(DateTime? ResetAtUtc = null, DateTime? ExpectedDetectedAt = null, double? ContinueAfterMinutes = null, bool Manual = false);

// What the Manager's API returns to the browser app. Built from the database entities on the server, without their
// secrets: no harness logins, no GitHub tokens, and a Runner's password only through its own endpoint.

/// <summary>
/// The Manager itself: its version and the defaults the Portal shows. Provider settings and the data folder are only
/// filled in for settings managers.
/// </summary>
public sealed record ManagerInfoDto(
    string Version,
    string RunnerUsername,
    int DefaultCores,
    int DefaultMemoryMb,
    int DefaultDiskGb,
    string? DataDirectory,
    string SshPublicKey,
    int SetupVersion,
    IReadOnlyList<HarnessDto> Harnesses,
    IReadOnlyList<ProviderDto> Providers,
    IReadOnlyList<RunnerToolDto> Tools);

/// <summary>Who is signed in, and what their roles let them do.</summary>
public sealed record CurrentUserDto(Guid Id, string Username, string DisplayName, UserRoles Roles, bool MustChangePassword)
{
    public bool Has(UserRoles role) => (Roles & role) == role;
}

/// <summary>A user, as user managers see them.</summary>
/// <param name="Locked">Signing in is blocked for a few minutes after too many wrong passwords.</param>
public sealed record UserDto(
    Guid Id,
    string Username,
    string DisplayName,
    UserRoles Roles,
    bool Disabled,
    bool MustChangePassword,
    bool Locked,
    DateTime CreatedAt,
    DateTime? LastSignInAt);

/// <summary>Someone to share with or give access to.</summary>
public sealed record UserSummaryDto(Guid Id, string Username, string DisplayName);

public sealed record AuditEventDto(long Id, DateTime At, string? ActorName, string Action, string? Target, string? Detail);

/// <summary>A user's own settings. The GitHub token is never sent back, only whether there is one.</summary>
public sealed record MySettingsDto(UserProfile Profile, bool HasGitHubToken);

public sealed record ScreenAwakeSettings(bool Enabled);

/// <summary>Someone with access to a Runner besides its owner.</summary>
public sealed record RunnerMemberDto(Guid UserId, string Username, string DisplayName, RunnerAccess Access, DateTime GrantedAt);

/// <summary>Someone an agent is shared with.</summary>
public sealed record AgentShareDto(Guid UserId, string Username, string DisplayName, AgentAccess Access, DateTime GrantedAt);

/// <summary>Someone a conversation is shared with (read-only).</summary>
public sealed record SessionShareDto(Guid UserId, string Username, string DisplayName, DateTime GrantedAt);

/// <summary>Software a Runner can have besides the harnesses, e.g. VS Code.</summary>
/// <param name="Icon">The logo: SVG content for a 24×24 view box.</param>
public sealed record RunnerToolDto(string Id, string DisplayName, string Description, string? Icon);

/// <param name="HasSessionFormat">Its conversations can be stored, exported and continued.</param>
/// <param name="Icon">The logo: SVG content for a 24×24 view box, as MudBlazor icons take it.</param>
public sealed record HarnessDto(
    string Id,
    string DisplayName,
    string Description,
    bool HasSessionFormat,
    bool HasModelCatalog,
    HarnessConfigSupport? ConfigSupport,
    HarnessAccountSupport? AccountSupport,
    string? Icon = null);

public sealed record ProviderDto(
    string Id,
    string DisplayName,
    bool IsConfigured,
    RunnerProviderCapabilities Capabilities,
    IReadOnlyList<RunnerTemplate> Templates,
    IReadOnlyList<ProviderSetting> Settings);

/// <param name="Operation">The operation running for the Runner (e.g. "Setting up"), or null.</param>
/// <param name="Status">Power state and resources, as last seen from the provider.</param>
/// <param name="Access">What the signed-in user may do with it.</param>
/// <param name="KeysOnly">SSH refuses passwords.</param>
/// <param name="Agents">The agents on it the signed-in user can see.</param>
public sealed record RunnerDto(
    Guid Id,
    string Name,
    Guid OwnerId,
    string OwnerName,
    RunnerAccess Access,
    bool KeysOnly,
    string ProviderId,
    string? TemplateId,
    RunnerKind Kind,
    string? MachineId,
    RunnerState State,
    string? StateMessage,
    string? Host,
    int SshPort,
    string Username,
    string? HostKeyFingerprint,
    int Cores,
    int MemoryMb,
    int DiskGb,
    IReadOnlyList<string> HarnessIds,
    IReadOnlyList<string> ToolIds,
    int? SetupVersion,
    string? InstalledVersions,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastSetupAt,
    string? Operation,
    MachineStatus? Status,
    IReadOnlyList<AgentDto> Agents);

/// <summary>An operation that ran on a Runner. <paramref name="Outcome"/> is null while it runs.</summary>
public sealed record RunnerActivityDto(long Id, string Name, DateTime StartedAt, DateTime? FinishedAt, RunnerActivityOutcome? Outcome, string? Error);

/// <summary>One provider reading for a Runner. A missing value leaves a gap in its usage graph.</summary>
public sealed record RunnerUsageSample(DateTime At, double? CpuPercent, double? MemoryPercent, double? DiskPercent = null);

/// <summary>Live desktop availability; never includes authorization cookies or paths.</summary>
public sealed record DesktopStatusDto(string State, string Message, string? SessionType = null, string? Display = null, bool AutomationAvailable = false);
public sealed record SessionImageDto(Guid Id, Guid SessionId, string FileName, string Caption, string ContentType, long Bytes, DateTime CreatedAt);
public sealed record SaveDesktopSnapshotRequest(Guid SessionId, string? Caption = null);

/// <param name="AccountId">The harness login it runs with; null uses its owner's default login.</param>
/// <param name="Access">What the signed-in user may do with it.</param>
public sealed record AgentDto(
    Guid Id,
    Guid RunnerId,
    Guid OwnerId,
    string OwnerName,
    AgentAccess Access,
    string RunnerName,
    RunnerState RunnerState,
    IReadOnlyList<string> RunnerHarnessIds,
    IReadOnlyList<string> RunnerToolIds,
    string Name,
    string HarnessId,
    string WorkingDirectory,
    string? RepositoryUrl,
    string? RepositoryBranch,
    AgentState State,
    string? StateMessage,
    int? ExitCode,
    Guid? AccountId,
    string? AccountName,
    Guid LoginOwnerId,
    string LoginOwnerName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long? IconVersion = null,
    bool Working = false,
    AgentActivityDto? Activity = null,
    DateTime? LastAssistantMessageAt = null,
    RunnerKind? RunnerKind = null);

/// <summary>An image in an agent's folder, for the icon picker. <paramref name="Path"/> is relative to the folder.</summary>
/// <param name="Automatic">What the Manager would pick by itself.</param>
public sealed record AgentIconImageDto(string Path, long Bytes, double? Width, double? Height, int Score, bool Automatic);

/// <param name="Current">The image in use, or null for the harness's logo.</param>
public sealed record AgentIconChoicesDto(IReadOnlyList<AgentIconImageDto> Images, string? Current, bool ChosenByUser);

/// <summary>Use this image (a path in the agent's folder) or none (null); or, with <paramref name="Automatic"/>, let the Manager pick again.</summary>
public sealed record SetAgentIconRequest(string? Path, bool Automatic = false);

/// <param name="Access">What the signed-in user may do with it.</param>
/// <param name="WorkspaceAccess">What they may do with the agent whose workspace it is in: with read-write access, they pick it
/// from the agent's sessions.</param>
public sealed record SessionDto(
    Guid Id,
    Guid OwnerId,
    string OwnerName,
    SessionAccess Access,
    string Title,
    string HarnessId,
    string? HarnessSessionId,
    string? HarnessTrail,
    Guid? AgentId,
    AgentDto? Agent,
    string? RunnerName,
    string? WorkingDirectory,
    string? RepositoryUrl,
    string? RepositoryBranch,
    int MessageCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastSyncedAt,
    Guid? WorkspaceAgentId = null,
    string? WorkspaceName = null,
    AgentAccess WorkspaceAccess = AgentAccess.None,
    long SourceSize = 0,
    long SavedSize = 0,
    long TotalSize = 0);

public sealed record AccountDto(
    Guid Id,
    string HarnessId,
    string Name,
    AccountKind Kind,
    bool IsDefault,
    int Version,
    int AgentCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? RenewalNote = null);

// Requests.

public sealed record SwitchAgentRequest(string HarnessId, Guid? AccountId);

/// <param name="Tty">One screen to disconnect, or null for all but <paramref name="KeepTty"/>.</param>
public sealed record DisconnectViewersRequest(string? Tty, string? KeepTty);

public sealed record AddAccountRequest(string HarnessId, string? Name, AccountKind Kind, string Secret);

public sealed record ReplaceAccountSecretRequest(AccountKind Kind, string Secret);

public sealed record RenameRequest(string Name);

public sealed record GitHubTokenRequest(string? Token);

public sealed record CreateUserRequest(string Username, string? DisplayName, string Password, UserRoles Roles);

public sealed record UpdateUserRequest(string? DisplayName, UserRoles Roles, bool Disabled);

public sealed record SetPasswordRequest(string Password);

public sealed record RunnerAccessRequest(RunnerAccess Access);

public sealed record ChangeOwnerRequest(Guid UserId);

public sealed record KeysOnlyRequest(bool KeysOnly);

public sealed record RunnerPasswordDto(string Password);

public sealed record ShareAgentRequest(AgentAccess Access);

public sealed record ImportHarnessFilesRequest(Guid RunnerId, string HarnessId);

/// <summary>An operation that was started in the background; its result arrives as an <see cref="OperationFinished"/>.</summary>
public sealed record OperationStartedDto(IReadOnlyList<Guid> RunnerIds);

// Live updates (SignalR, /hubs/live).

/// <summary>A background operation on a Runner ended. <paramref name="Error"/> is null when it worked.</summary>
public sealed record OperationFinished(Guid RunnerId, string RunnerName, string Operation, string? Error);

/// <summary>Names of the methods on the live hub (/hubs/live).</summary>
public static class LiveHub
{
    public const string Path = "/hubs/live";

    /// <summary>Server to client: a <see cref="Core.Events.ManagerEvent"/>.</summary>
    public const string Changed = "changed";

    /// <summary>Server to client: (Guid runnerId, RunnerLogLine line) for Runners the client follows.</summary>
    public const string Log = "log";

    /// <summary>Server to client: an <see cref="OperationFinished"/>.</summary>
    public const string OperationFinished = "operationFinished";

    /// <summary>Client to server: follow a Runner's log; returns its recent lines.</summary>
    public const string FollowLog = "FollowLog";

    public const string UnfollowLog = "UnfollowLog";
}

/// <summary>The terminal WebSocket (/ws/terminal): control messages are JSON text frames, terminal data is binary frames.</summary>
public static class TerminalSocket
{
    public const string Path = "/ws/terminal";

    /// <summary>Server to client: attached; <c>tty</c> is the terminal's tty on the Runner.</summary>
    public const string Ready = "ready";

    /// <summary>Server to client: the terminal ended; <c>reason</c> is one of the Closed* values.</summary>
    public const string Closed = "closed";

    /// <summary>Server to client (login terminals): the login was saved as an account, or failed.</summary>
    public const string Login = "login";

    /// <summary>Client to server: the terminal's size changed (<c>cols</c>, <c>rows</c>).</summary>
    public const string Resize = "resize";

    /// <summary>
    /// An image for the terminal's Runner. Client to server: <c>id</c>, <c>contentType</c>, <c>size</c>, followed by exactly
    /// <c>size</c> bytes in binary frames (they are the image, not input). Server to client: <c>id</c> and the stored
    /// file's <c>path</c>, or an <c>error</c>. Over the socket rather than an HTTP upload, so a reverse proxy's request
    /// size limit (nginx: 1 MB by default) doesn't apply.
    /// </summary>
    public const string Image = "image";

    /// <summary>The size of the binary frames an image is sent in.</summary>
    public const int ImageChunkBytes = 64 * 1024;

    public const string ClosedExited = "exited";
    public const string ClosedKicked = "kicked";
    public const string ClosedDetached = "detached";
    public const string ClosedError = "error";
}

/// <summary>The header every request that changes something must carry (see the Manager's CSRF check).</summary>
public static class ApiHeaders
{
    public const string Request = "X-DAM-Request";

    /// <summary>
    /// On a 403 for an admin action: the password was last entered too long ago. "password" or "password+code" (when the
    /// user has an authenticator). The Portal asks for it in a dialog, confirms it, and repeats the request.
    /// </summary>
    public const string ConfirmPassword = "X-DAM-Confirm-Password";
}

public sealed record ConfirmPasswordRequest(string Password, string? Code = null);

public sealed record SelectSessionRequest(Guid? SessionId, string? Title = null);
public sealed record RenameSessionRequest(string Title);
