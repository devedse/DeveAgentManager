using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Contracts;

// What the Manager's API returns to the browser app. Built from the database entities on the server, without their
// secrets: no account logins, no Runner passwords, no GitHub token.

/// <summary>The Manager itself: its version, login name and the defaults the Portal shows.</summary>
public sealed record ManagerInfoDto(
    string Version,
    string AdminUsername,
    bool UsesDefaultAdminPassword,
    string DefaultAdminPassword,
    string RunnerUsername,
    int DefaultCores,
    int DefaultMemoryMb,
    int DefaultDiskGb,
    string DataDirectory,
    string SshPublicKey,
    bool GitHubPatFromCompose,
    bool HasGitHubPat,
    int SetupVersion,
    IReadOnlyList<HarnessDto> Harnesses,
    IReadOnlyList<ProviderDto> Providers);

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
public sealed record RunnerDto(
    Guid Id,
    string Name,
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
    int? SetupVersion,
    string? InstalledVersions,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastSetupAt,
    string? Operation,
    MachineStatus? Status,
    IReadOnlyList<AgentDto> Agents);

/// <param name="AccountId">The account the agent picked; null uses the harness's default account.</param>
public sealed record AgentDto(
    Guid Id,
    Guid RunnerId,
    string RunnerName,
    RunnerState RunnerState,
    IReadOnlyList<string> RunnerHarnessIds,
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
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SessionDto(
    Guid Id,
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
    DateTime? LastSyncedAt);

public sealed record AccountDto(
    Guid Id,
    string HarnessId,
    string Name,
    AccountKind Kind,
    bool IsDefault,
    int Version,
    int AgentCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

// Requests.

public sealed record SwitchAgentRequest(string HarnessId, Guid? AccountId);

/// <param name="Tty">One screen to disconnect, or null for all but <paramref name="KeepTty"/>.</param>
public sealed record DisconnectViewersRequest(string? Tty, string? KeepTty);

public sealed record AddAccountRequest(string HarnessId, string? Name, AccountKind Kind, string Secret);

public sealed record ReplaceAccountSecretRequest(AccountKind Kind, string Secret);

public sealed record RenameRequest(string Name);

public sealed record GitHubPatRequest(string? Pat);

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
}
