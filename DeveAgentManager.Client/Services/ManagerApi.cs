using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Setup;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace DeveAgentManager.Client.Services;

/// <summary>A problem the Manager reported; the message is meant for the user.</summary>
public sealed class ApiException(string message, HttpStatusCode status, string? code = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>
/// The Manager's API (/api). Every request carries <see cref="ApiHeaders.Request"/> (see the Manager's CSRF check).
/// When the login ran out, the browser goes to the login page and comes back afterwards.
/// </summary>
public sealed partial class ManagerApi(HttpClient http, NavigationManager navigation)
{
    private Task<ManagerInfoDto>? _info;

    /// <summary>
    /// Asks for the password (true: and an authenticator code) before an admin action; true when it was confirmed. Set by
    /// the layout, which can show dialogs. When an admin action needs it, the request is repeated once afterwards.
    /// </summary>
    public Func<bool, Task<bool>>? ConfirmPassword { get; set; }

    public Task ConfirmPasswordAsync(string password, string? code) =>
        SendAsync(HttpMethod.Post, "api/me/confirm-password", new ConfirmPasswordRequest(password, code));

    /// <summary>The Manager's info (version, harnesses, providers); asked once (shared by every page), then kept.</summary>
    public Task<ManagerInfoDto> InfoAsync(bool refresh = false)
    {
        if (refresh || _info is null || _info.IsFaulted)
        {
            _info = GetAsync<ManagerInfoDto>("api/info");
        }

        return _info;
    }

    // You

    public Task<AppBuildDto> GetBuildAsync() => GetAsync<AppBuildDto>("api/build");

    public Task<CurrentUserDto> MeAsync() => GetAsync<CurrentUserDto>("api/me");

    public Task<MySettingsDto> GetMySettingsAsync() => GetAsync<MySettingsDto>("api/me/settings");

    public Task<ScreenAwakeSettings> GetScreenAwakeAsync() => GetAsync<ScreenAwakeSettings>("api/me/settings/screen-awake");

    public Task<ScreenAwakeSettings> SaveScreenAwakeAsync(bool enabled) =>
        SendAsync<ScreenAwakeSettings>(HttpMethod.Put, "api/me/settings/screen-awake", new ScreenAwakeSettings(enabled));

    public Task<UserProfile> SaveMyProfileAsync(UserProfile profile) => SendAsync<UserProfile>(HttpMethod.Put, "api/me/settings/profile", profile);

    public Task<List<ModelDefaultsDto>> GetMyModelDefaultsAsync() => GetAsync<List<ModelDefaultsDto>>("api/me/settings/models");

    public Task<List<ModelDefaultsDto>> SaveMyModelDefaultsAsync(Dictionary<string, ModelDefault> defaults) =>
        SendAsync<List<ModelDefaultsDto>>(HttpMethod.Put, "api/me/settings/models", defaults);

    public Task SaveGitHubTokenAsync(string? token) => SendAsync(HttpMethod.Put, "api/me/settings/github-token", new GitHubTokenRequest(token));

    // Users

    /// <summary>Everyone to share with or give access to.</summary>
    public Task<List<UserSummaryDto>> ListUserDirectoryAsync() => GetAsync<List<UserSummaryDto>>("api/users/directory");

    public Task<List<UserDto>> ListUsersAsync() => GetAsync<List<UserDto>>("api/users");

    public Task<UserDto> CreateUserAsync(CreateUserRequest request) => SendAsync<UserDto>(HttpMethod.Post, "api/users", request);

    public Task<UserDto> UpdateUserAsync(Guid userId, UpdateUserRequest request) => SendAsync<UserDto>(HttpMethod.Put, $"api/users/{userId}", request);

    public Task ResetUserPasswordAsync(Guid userId, string password) => SendAsync(HttpMethod.Put, $"api/users/{userId}/password", new SetPasswordRequest(password));

    public Task DeleteUserAsync(Guid userId, Guid transferTo) => SendAsync(HttpMethod.Delete, $"api/users/{userId}?transferTo={transferTo}");

    public Task<List<ManagerLogEntry>> ListManagerLogsAsync(string minimum, string search, int take = 300) =>
        GetAsync<List<ManagerLogEntry>>($"api/logs?minimum={Uri.EscapeDataString(minimum)}&search={Uri.EscapeDataString(search)}&take={take}");

    public Task<List<AuditEventDto>> ListAuditAsync(int skip, int take) => GetAsync<List<AuditEventDto>>($"api/audit?skip={skip}&take={take}");

    public Task<ManagerCpuTraceStatus> GetCpuTracesAsync() => GetAsync<ManagerCpuTraceStatus>("api/diagnostics/cpu");
    public Task<HistorySyncSettings> GetHistorySyncSettingsAsync() => GetAsync<HistorySyncSettings>("api/settings/history-sync");
    public Task<HistorySyncSettings> SaveHistorySyncSettingsAsync(HistorySyncSettings settings) =>
        SendAsync<HistorySyncSettings>(HttpMethod.Put, "api/settings/history-sync", settings);
    public Task<ManagerCpuTrace> StartCpuTraceAsync() => SendAsync<ManagerCpuTrace>(HttpMethod.Post, "api/diagnostics/cpu");
    public Task CancelCpuTraceAsync(Guid id) => SendAsync(HttpMethod.Post, $"api/diagnostics/cpu/{id}/cancel");
    public Task DeleteCpuTraceAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/diagnostics/cpu/{id}");

    // Runners
    public Task<List<SessionImageDto>> GetSessionImagesAsync(Guid sessionId) => GetAsync<List<SessionImageDto>>($"api/sessions/{sessionId}/images");
    public Task<List<SessionFileDto>> GetSessionFilesAsync(Guid sessionId) => GetAsync<List<SessionFileDto>>($"api/sessions/{sessionId}/files");
    public Task<SessionFilePreview> PreviewSessionFileAsync(SessionFileDto file) => GetAsync<SessionFilePreview>($"api/sessions/{file.SessionId}/files/{file.Id}/preview");
    public Task<string> PreviewSourceAsync(string url, CancellationToken ct = default) => http.GetStringAsync(url, ct);
    public Task DeleteSessionFileAsync(SessionFileDto file) => SendAsync(HttpMethod.Delete, $"api/sessions/{file.SessionId}/files/{file.Id}");
    public static string DownloadUrl(SessionFileDto file) => $"api/sessions/{file.SessionId}/files/{file.Id}/download";
    public Task<SessionImageDto> SaveDesktopSnapshotAsync(Guid runnerId, Guid sessionId) =>
        SendAsync<SessionImageDto>(HttpMethod.Post, $"api/runners/{runnerId}/desktop/snapshot", new SaveDesktopSnapshotRequest(sessionId));

    public Task<List<string>> TimeZoneIdsAsync() => GetAsync<List<string>>("api/settings/timezones");
    public Task<TimeZoneConfigDto> GetTimeZoneAsync(Guid? runnerId) => GetAsync<TimeZoneConfigDto>(runnerId is { } id ? $"api/runners/{id}/timezone" : "api/settings/timezone");
    public Task<TimeZoneConfigDto> SaveTimeZoneAsync(Guid? runnerId, string? zone) => SendAsync<TimeZoneConfigDto>(HttpMethod.Put,
        runnerId is { } id ? $"api/runners/{id}/timezone" : "api/settings/timezone", new TimeZoneRequest(zone));
    public Task<List<AgentDiagnosticDto>> GetDiagnosticsAsync(Guid id) => GetAsync<List<AgentDiagnosticDto>>($"api/agents/{id}/diagnostics");
    public Task<List<AgentQuickCommandDto>> GetQuickCommandsAsync(Guid id) => GetAsync<List<AgentQuickCommandDto>>($"api/agents/{id}/commands");
    public Task SendQuickCommandAsync(Guid id, string harness, string command) => SendAsync(HttpMethod.Post, $"api/agents/{id}/commands", new AgentQuickCommandRequest(command, harness));
    public Task<List<AgentReplyPreviewDto>> GetReplyPreviewsAsync() => GetAsync<List<AgentReplyPreviewDto>>("api/agents/reply-previews");
    public Task<AutoContinueDto> GetAutoContinueAsync(Guid id) => GetAsync<AutoContinueDto>($"api/agents/{id}/limit/automatic");
    public Task SetAutoContinueAsync(Guid id, bool enabled, int? remaining) => SendAsync(HttpMethod.Put, $"api/agents/{id}/limit/automatic", new AutoContinueRequest(enabled, remaining));
    public Task<AgentLimitDto?> GetAgentLimitAsync(Guid id) => FindAsync<AgentLimitDto>($"api/agents/{id}/limit");
    public Task ScheduleContinueAsync(Guid id, DateTime? reset, DateTime? detectedAt, double? continueAfterMinutes = null, bool manual = false) => SendAsync(HttpMethod.Post, $"api/agents/{id}/limit/continue", new ScheduleContinueRequest(reset, detectedAt, continueAfterMinutes, manual));
    public Task CancelContinueAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/agents/{id}/limit/continue");

    public Task<List<RunnerDto>> ListRunnersAsync() => GetAsync<List<RunnerDto>>("api/runners");

    public Task<RunnerDto?> GetRunnerAsync(Guid runnerId) => FindAsync<RunnerDto>($"api/runners/{runnerId}");
    public Task<DesktopStatusDto> GetDesktopStatusAsync(Guid runnerId) => GetAsync<DesktopStatusDto>($"api/runners/{runnerId}/desktop");

    public Task<List<RunnerUsageSample>> GetRunnerUsageAsync(Guid runnerId, int minutes = 60) => GetAsync<List<RunnerUsageSample>>($"api/runners/{runnerId}/usage?minutes={minutes}");

    public Task<RunnerDto> CreateRunnerAsync(CreateRunnerRequest request) => SendAsync<RunnerDto>(HttpMethod.Post, "api/runners", request);

    public Task StartRunnerAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/start");

    public Task StopRunnerAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/stop");

    public Task RestartRunnerAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/restart");

    public Task CancelOperationAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/cancel");

    public Task RerunSetupAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/setup");
    public Task CheckRunnerUpdatesAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/updates/check");
    public Task UpdateSystemPackagesAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/system/update");

    public Task DeleteRunnerAsync(Guid runnerId, bool destroyMachine) => SendAsync(HttpMethod.Delete, $"api/runners/{runnerId}?destroyMachine={destroyMachine.ToString().ToLowerInvariant()}");

    public Task ForgetHostKeyAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/forget-host-key");

    /// <summary>Starts updating a harness; the result arrives as <see cref="LiveUpdates.OperationFinished"/>.</summary>
    public Task UpdateHarnessAsync(Guid runnerId, string harnessId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/harnesses/{Uri.EscapeDataString(harnessId)}/update");

    /// <summary>Installs a tool (e.g. VS Code), or updates it; the result arrives as an operation.</summary>
    public Task InstallToolAsync(Guid runnerId, string toolId, bool update) =>
        SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/tools/{Uri.EscapeDataString(toolId)}/{(update ? "update" : "install")}");

    public Task InstallHarnessAsync(Guid runnerId, string harnessId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/harnesses/{Uri.EscapeDataString(harnessId)}/install");

    public Task<List<RunnerMemberDto>> ListRunnerMembersAsync(Guid runnerId) => GetAsync<List<RunnerMemberDto>>($"api/runners/{runnerId}/members");

    public Task<List<RunnerAgentAccessDto>> ListRunnerAgentAccessAsync(Guid runnerId) => GetAsync<List<RunnerAgentAccessDto>>($"api/runners/{runnerId}/agent-access");

    public Task SetRunnerAccessAsync(Guid runnerId, Guid userId, RunnerAccess access) =>
        SendAsync(HttpMethod.Put, $"api/runners/{runnerId}/members/{userId}", new RunnerAccessRequest(access));

    /// <summary>Takes someone's access away, or (for yourself) leaves the Runner.</summary>
    public Task RemoveRunnerAccessAsync(Guid runnerId, Guid userId) => SendAsync(HttpMethod.Delete, $"api/runners/{runnerId}/members/{userId}");

    public Task ChangeRunnerOwnerAsync(Guid runnerId, Guid userId) => SendAsync(HttpMethod.Put, $"api/runners/{runnerId}/owner", new ChangeOwnerRequest(userId));

    public Task<RunnerPasswordDto> GetRunnerPasswordAsync(Guid runnerId) => GetAsync<RunnerPasswordDto>($"api/runners/{runnerId}/password");

    public Task SetRunnerPasswordAsync(Guid runnerId, string password) => SendAsync(HttpMethod.Put, $"api/runners/{runnerId}/password", new SetPasswordRequest(password));

    public Task SetKeysOnlyAsync(Guid runnerId, bool keysOnly) => SendAsync(HttpMethod.Put, $"api/runners/{runnerId}/keys-only", new KeysOnlyRequest(keysOnly));

    public Task<List<RunnerActivityDto>> ListRunnerActivityAsync(Guid runnerId) => GetAsync<List<RunnerActivityDto>>($"api/runners/{runnerId}/activity");

    public Task RefreshRunnerDetailsAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/refresh-details");

    public Task RefreshRunnerAsync(Guid runnerId) => SendAsync(HttpMethod.Post, $"api/runners/{runnerId}/refresh");

    public Task DismissAttentionAsync(Guid id, string token) => SendAsync(HttpMethod.Post, $"api/agents/{id}/attention/dismiss", new DismissAgentAttentionRequest(token));
    public Task<List<AgentAttentionDto>> ListAttentionAsync() => GetAsync<List<AgentAttentionDto>>("api/agents/attention");
    public Task MarkAgentSeenAsync(Guid id, long sequence) => SendAsync(HttpMethod.Post, $"api/agents/{id}/seen", new MarkAgentSeenRequest(sequence));

    // Agents

    public Task<List<AgentDto>> ListAgentsAsync() => GetAsync<List<AgentDto>>("api/agents");

    public Task<AgentDto?> GetAgentAsync(Guid agentId) => FindAsync<AgentDto>($"api/agents/{agentId}");

    public Task<AgentDto> CreateAgentAsync(CreateAgentRequest request) => SendAsync<AgentDto>(HttpMethod.Post, "api/agents", request);
    public Task<AgentDto> CreateWorkspaceAsync(Guid agentId, CreateWorkspaceRequest request) => SendAsync<AgentDto>(HttpMethod.Post, $"api/agents/{agentId}/workspaces", request);

    /// <summary>Canceling (closing the dialog) ends the request, and with it the measurement on the Runner.</summary>
    public async Task<WorkspaceEstimateDto> EstimateWorkspaceAsync(Guid agentId, WorkspaceStart start, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/agents/{agentId}/workspaces/estimate?start={start}", ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<WorkspaceEstimateDto>(ApiJson.Options, ct))!;
    }

    public Task<Dictionary<Guid, string?>> RefreshWorkspaceBranchesAsync(Guid agentId) =>
        SendAsync<Dictionary<Guid, string?>>(HttpMethod.Post, $"api/agents/{agentId}/workspaces/branches");

    public Task<WorkspaceChangesDto> GetWorkspaceChangesAsync(Guid agentId) => GetAsync<WorkspaceChangesDto>($"api/agents/{agentId}/workspace/changes");

    public Task RemoveWorkspaceAsync(Guid agentId, bool keepFiles) =>
        SendAsync(HttpMethod.Post, $"api/agents/{agentId}/workspace/remove", new RemoveWorkspaceRequest(keepFiles));

    public Task<List<KeptFolderDto>> ListKeptFoldersAsync(Guid agentId) => GetAsync<List<KeptFolderDto>>($"api/agents/{agentId}/kept-folders");

    public Task DeleteKeptFolderAsync(Guid agentId, string path) =>
        SendAsync(HttpMethod.Delete, $"api/agents/{agentId}/kept-folders?path={Uri.EscapeDataString(path)}");

    public Task StartAgentAsync(Guid agentId, bool restart = false) => SendAsync(HttpMethod.Post, $"api/agents/{agentId}/start?restart={restart.ToString().ToLowerInvariant()}");

    public Task StopAgentAsync(Guid agentId) => SendAsync(HttpMethod.Post, $"api/agents/{agentId}/stop");

    public Task DeleteAgentAsync(Guid agentId) => SendAsync(HttpMethod.Delete, $"api/agents/{agentId}");

    public Task SwitchAgentAsync(Guid agentId, string harnessId, Guid? accountId) =>
        SendAsync(HttpMethod.Post, $"api/agents/{agentId}/switch", new SwitchAgentRequest(harnessId, accountId));

    /// <summary>The agent's task list (dam-tools tasks). One that can't be read is a load error, not a crash.</summary>
    public async Task<AgentTaskListDto> GetAgentTasksAsync(Guid agentId)
    {
        try { return await GetAsync<AgentTaskListDto>($"api/agents/{agentId}/tasks"); }
        catch (System.Text.Json.JsonException) { throw new ApiException("The task list couldn't be read.", HttpStatusCode.BadGateway); }
    }

    public Task ClearAgentTasksAsync(Guid agentId) => SendAsync(HttpMethod.Delete, $"api/agents/{agentId}/tasks");

    public Task SetGitIdentityAsync(Guid agentId, Guid userId) =>
        SendAsync(HttpMethod.Post, $"api/agents/{agentId}/git-identity", new SetGitIdentityRequest(userId));

    public Task<List<AgentViewer>> ListViewersAsync(Guid agentId) => GetAsync<List<AgentViewer>>($"api/agents/{agentId}/viewers");

    public Task<AgentTerminalText> GetTerminalTextAsync(Guid agentId, int lines) => GetAsync<AgentTerminalText>($"api/agents/{agentId}/terminal-text?lines={lines}");

    public Task DisconnectViewersAsync(Guid agentId, string? tty, string? keepTty) =>
        SendAsync(HttpMethod.Post, $"api/agents/{agentId}/viewers/disconnect", new DisconnectViewersRequest(tty, keepTty));

    /// <summary>Where the browser loads an agent's icon (the version in the URL makes it cacheable); null when it has none.</summary>
    public static string? IconUrl(AgentDto agent) => agent.IconVersion is { } version ? $"api/agents/{agent.Id}/icon?v={version}" : null;

    /// <summary>An image in an agent's folder, for the icon picker.</summary>
    public static string IconImageUrl(Guid agentId, string path) => $"api/agents/{agentId}/icon/image?path={Uri.EscapeDataString(path)}";

    public Task<AgentIconChoicesDto> GetAgentIconChoicesAsync(Guid agentId) => GetAsync<AgentIconChoicesDto>($"api/agents/{agentId}/icon/choices");

    public Task SetAgentIconAsync(Guid agentId, SetAgentIconRequest request) => SendAsync(HttpMethod.Put, $"api/agents/{agentId}/icon", request);

    public Task<SessionDto?> GetAgentSessionAsync(Guid agentId) => FindAsync<SessionDto>($"api/agents/{agentId}/session");

    public Task<List<AgentShareDto>> ListAgentSharesAsync(Guid agentId) => GetAsync<List<AgentShareDto>>($"api/agents/{agentId}/shares");

    public Task ShareAgentAsync(Guid agentId, Guid userId, AgentAccess access) =>
        SendAsync(HttpMethod.Put, $"api/agents/{agentId}/shares/{userId}", new ShareAgentRequest(access));

    /// <summary>Ends a share, or (for yourself) leaves it.</summary>
    public Task UnshareAgentAsync(Guid agentId, Guid userId) => SendAsync(HttpMethod.Delete, $"api/agents/{agentId}/shares/{userId}");

    // Sessions

    public Task<List<SessionDto>> ListSessionsAsync() => GetAsync<List<SessionDto>>("api/sessions");

    public Task<SessionDto?> GetSessionAsync(Guid sessionId) => FindAsync<SessionDto>($"api/sessions/{sessionId}");

    /// <summary>The whole conversation; a conversation too large to show throws <see cref="ApiException"/> with status 413.</summary>
    public async Task<DeveSession> GetSessionContentAsync(Guid sessionId)
    {
        using var response = await http.GetAsync($"api/sessions/{sessionId}/content", HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await DeveSession.ReadAsync(stream);
    }

    /// <summary>Where the browser downloads an export (the login cookie authorizes it).</summary>
    public static string ExportUrl(Guid sessionId, string format) => $"api/sessions/{sessionId}/export?format={Uri.EscapeDataString(format)}";

    public async Task<SessionDto> ImportSessionAsync(IBrowserFile file)
    {
        using var content = new MultipartFormDataContent();
        var stream = file.OpenReadStream(maxAllowedSize: 200 * 1024 * 1024);
        content.Add(new StreamContent(stream), "file", file.Name);
        using var response = await http.PostAsync("api/sessions/import", content);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<SessionDto>(ApiJson.Options))!;
    }

    public Task SelectSessionAsync(Guid agentId, Guid? sessionId, string? title = null, bool update = false) =>
        SendAsync(HttpMethod.Post, $"api/agents/{agentId}/session", new SelectSessionRequest(sessionId, title, update));
    public Task RenameSessionAsync(Guid sessionId, string title) =>
        SendAsync(HttpMethod.Put, $"api/sessions/{sessionId}/title", new RenameSessionRequest(title));

    public Task DeleteSessionAsync(Guid sessionId) => SendAsync(HttpMethod.Delete, $"api/sessions/{sessionId}");

    public Task<List<SessionShareDto>> ListSessionSharesAsync(Guid sessionId) => GetAsync<List<SessionShareDto>>($"api/sessions/{sessionId}/shares");

    public Task ShareSessionAsync(Guid sessionId, Guid userId) => SendAsync(HttpMethod.Put, $"api/sessions/{sessionId}/shares/{userId}");

    /// <summary>Ends a share, or (for yourself) leaves it.</summary>
    public Task UnshareSessionAsync(Guid sessionId, Guid userId) => SendAsync(HttpMethod.Delete, $"api/sessions/{sessionId}/shares/{userId}");

    // Accounts

    public Task<List<AgentMoveProcessDto>> ListMoveProcessesAsync(Guid agentId) => GetAsync<List<AgentMoveProcessDto>>($"api/agents/{agentId}/move/processes");
    public Task MoveAgentAsync(Guid agentId, Guid runnerId, IReadOnlyList<AgentProcessIdentity>? stopProcesses = null) =>
        SendAsync(HttpMethod.Post, $"api/agents/{agentId}/move", new MoveAgentRequest(runnerId, stopProcesses));
    public Task RecoverAgentMoveAsync(Guid agentId) => SendAsync(HttpMethod.Post, $"api/agents/{agentId}/move/recover");

    public Task<List<AccountDto>> ListAccountsAsync(bool all = false) => GetAsync<List<AccountDto>>(all ? "api/accounts/all" : "api/accounts");
    public Task<List<LoginCleanupDto>> PendingLoginCleanupAsync() => GetAsync<List<LoginCleanupDto>>("api/accounts/pending");
    public Task<List<AccountGrantDto>> ListAccountGrantsAsync(Guid id) => GetAsync<List<AccountGrantDto>>($"api/accounts/{id}/grants");
    public Task SetAccountGrantAsync(Guid id, Guid userId, AccountAccess access) => SendAsync(HttpMethod.Put, $"api/accounts/{id}/grants/{userId}", new AccountGrantRequest(access));
    public Task RemoveAccountGrantAsync(Guid id, Guid userId) => SendAsync(HttpMethod.Delete, $"api/accounts/{id}/grants/{userId}");
    public Task RemoveLegacyLoginGrantAsync(Guid id, Guid agentId) => SendAsync(HttpMethod.Delete, $"api/accounts/{id}/agent-grants/{agentId}");
    public Task SetAccountEnabledAsync(Guid id, bool enabled) => SendAsync(HttpMethod.Put, $"api/accounts/{id}/enabled", new AccountEnabledRequest(enabled));
    public Task<AccountImpactDto> AccountImpactAsync(Guid id, Guid? userId = null) => GetAsync<AccountImpactDto>($"api/accounts/{id}/impact" + (userId is null ? "" : $"?userId={userId}"));
    public Task<List<LoginDependencyDto>> LoginDependenciesAsync(Guid userId) => GetAsync<List<LoginDependencyDto>>($"api/users/{userId}/login-dependencies");


    public Task<AccountDto> AddAccountAsync(AddAccountRequest request) => SendAsync<AccountDto>(HttpMethod.Post, "api/accounts", request);

    public Task ReplaceAccountSecretAsync(Guid accountId, ReplaceAccountSecretRequest request) => SendAsync(HttpMethod.Put, $"api/accounts/{accountId}/secret", request);

    public Task RenameAccountAsync(Guid accountId, string name) => SendAsync(HttpMethod.Put, $"api/accounts/{accountId}/name", new RenameRequest(name));

    public Task SetDefaultAccountAsync(Guid accountId) => SendAsync(HttpMethod.Post, $"api/accounts/{accountId}/default");

    public Task DeleteAccountAsync(Guid accountId) => SendAsync(HttpMethod.Delete, $"api/accounts/{accountId}");

    // Settings

    public Task<SetupProfile> GetProfileAsync() => GetAsync<SetupProfile>("api/settings/profile");

    public Task<SetupProfile> SaveProfileAsync(SetupProfile profile) => SendAsync<SetupProfile>(HttpMethod.Put, "api/settings/profile", profile);

    public Task<List<string>> ImportHarnessFilesAsync(Guid runnerId, string harnessId) =>
        SendAsync<List<string>>(HttpMethod.Post, "api/settings/profile/import", new ImportHarnessFilesRequest(runnerId, harnessId));

    /// <summary>Starts applying a harness's configuration on its ready Runners; returns them. Results arrive as operations.</summary>
    public Task<OperationStartedDto> ApplyHarnessConfigAsync(string harnessId) =>
        SendAsync<OperationStartedDto>(HttpMethod.Post, $"api/settings/harnesses/{Uri.EscapeDataString(harnessId)}/apply");

    public Task<List<ProviderCheck>> CheckProviderAsync(string providerId) => GetAsync<List<ProviderCheck>>($"api/providers/{Uri.EscapeDataString(providerId)}/check");

    public Task<List<ModelChoice>> ListModelsAsync(string harnessId) => GetAsync<List<ModelChoice>>($"api/harnesses/{Uri.EscapeDataString(harnessId)}/models");

    // Plumbing

    private async Task<T> GetAsync<T>(string url)
    {
        using var response = await ConfirmedAsync(() => http.GetAsync(url));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(ApiJson.Options))!;
    }

    /// <summary>Null when the thing doesn't exist (any more).</summary>
    private async Task<T?> FindAsync<T>(string url) where T : class
    {
        using var response = await ConfirmedAsync(() => http.GetAsync(url));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(ApiJson.Options);
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var response = await ConfirmedAsync(() => SendRawAsync(method, url, body));
        await EnsureSuccessAsync(response);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var response = await ConfirmedAsync(() => SendRawAsync(method, url, body));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(ApiJson.Options))!;
    }

    /// <summary>Sends; when the Manager asks for the password first (an admin action), prompts and sends once more.</summary>
    private async Task<HttpResponseMessage> ConfirmedAsync(Func<Task<HttpResponseMessage>> send)
    {
        var response = await send();
        if (response.StatusCode != HttpStatusCode.Forbidden || ConfirmPassword is null
            || !response.Headers.TryGetValues(ApiHeaders.ConfirmPassword, out var asked)
            || !await ConfirmPassword(asked.FirstOrDefault() == "password+code"))
        {
            return response;
        }

        response.Dispose();
        return await send();
    }

    private Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: ApiJson.Options);
        }

        return http.SendAsync(request);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The sign-in ran out (or was ended: a new password, disabled): sign in again and come back here.
            var returnUrl = "/" + navigation.ToBaseRelativePath(navigation.Uri);
            navigation.NavigateTo($"login?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
            throw new ApiException("Signed out. Sign in again.", response.StatusCode);
        }

        var message = $"The Manager answered {(int)response.StatusCode} {response.ReasonPhrase}.";
        string? code = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            if (text.Length > 0 && JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } problem)
            {
                if (problem.TryGetProperty("code", out var problemCode) && problemCode.ValueKind == JsonValueKind.String) code = problemCode.GetString();
                message = problem.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } d ? d
                    : problem.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t ? t
                    : message;
            }
        }
        catch (JsonException)
        {
            // Not a problem document; keep the status.
        }

        throw new ApiException(message, response.StatusCode, code);
    }
}
