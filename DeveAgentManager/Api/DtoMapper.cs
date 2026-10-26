using System.Reflection;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>
/// Builds what the API returns from the database entities and the live state (operations, resources), for one user:
/// their access to each thing, and only the agents they can see. Leaves out every secret: harness logins, GitHub tokens
/// and Runner passwords never reach the browser here.
/// </summary>
public sealed class DtoMapper(
    RunnerService runners,
    RunnerStatusCache statuses,
    RunnerUpdateCache updates,
    AgentService agents,
    SessionService sessions,
    RunnerSetupService setup,
    ManagerOptions options,
    ManagerSshKey sshKey)
{
    public static readonly string BuildId =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    public static readonly string Version = BuildId.Split('+')[0];

    public ManagerInfoDto Info(CurrentUser user)
    {
        var settingsManager = user.Has(UserRoles.SettingsManager);
        return new(
            Version,
            options.RunnerUsername,
            options.DefaultCores,
            options.DefaultMemoryMb,
            options.DefaultDiskGb,
            settingsManager ? options.DataDirectory : null,
            sshKey.PublicKey,
            RunnerSetupService.SetupVersion,
            agents.Harnesses.OrderBy(h => h.Id == "pi" ? 0 : 1).ThenBy(h => h.DisplayName).Select(Harness).ToList(),
            runners.Providers.OrderBy(p => p.Capabilities.RequiresConnectionDetails).Select(p => Provider(p, settingsManager)).ToList(),
            setup.Tools.OrderBy(t => t.DisplayName).Select(t => new RunnerToolDto(t.Id, t.DisplayName, t.Description, t.Icon)).ToList());
    }

    public HarnessDto Harness(IHarness harness) => new(
        harness.Id,
        harness.DisplayName,
        harness.Description,
        sessions.SupportsHarness(harness.Id),
        harness.ModelsCommand is not null,
        harness.ConfigSupport,
        harness.AccountSupport,
        harness.Icon);

    /// <summary>Its settings (the Proxmox host, node...) only for settings managers; masked secrets either way.</summary>
    public static ProviderDto Provider(IRunnerProvider provider, bool withSettings) => new(
        provider.Id,
        provider.DisplayName,
        provider.IsConfigured,
        provider.Capabilities,
        provider.GetTemplates(),
        // Providers mask their secrets themselves; masked again here, so no provider can leak one by accident.
        withSettings ? provider.GetSettings().Select(s => s.IsSecret && s.Value is not null ? s with { Value = "••••••••" } : s).ToList() : []);

    /// <summary>A Runner with its agents (loaded with their owners and shares) that <paramref name="user"/> can see.</summary>
    public RunnerDto Runner(Runner runner, CurrentUser user)
    {
        var access = AccessControl.ForRunner(user, runner.OwnerId, runner.Members);
        return new(
            runner.Id,
            runner.Name,
            runner.OwnerId,
            runner.Owner?.DisplayName ?? "",
            access,
            runner.KeysOnly,
            runner.ProviderId,
            runner.TemplateId,
            runner.Kind,
            runner.MachineId,
            runner.State,
            runner.StateMessage,
            runner.Host,
            runner.SshPort,
            runner.Username,
            runner.HostKeyFingerprint,
            runner.Cores,
            runner.MemoryMb,
            runner.DiskGb,
            runner.HarnessIds,
            runner.ToolIds,
            runner.SetupVersion,
            runner.InstalledVersions,
            runner.CreatedAt,
            runner.UpdatedAt,
            runner.LastSetupAt,
            runners.CurrentOperation(runner.Id),
            statuses.Get(runner.Id),
            runner.Agents.Select(a => (Agent: a, Access: AccessControl.ForAgent(user, a.OwnerId, a.Shares, access)))
                .Where(a => a.Access >= AgentAccess.Listed).OrderByDescending(a => a.Agent.CreatedAt)
                .Select(a => Agent(a.Agent, runner, a.Access, access, a.Agent.Shares.Any(s => s.UserId == user.Id))).ToList(),
            access >= RunnerAccess.Manage ? updates.Get(runner.Id) : null);
    }

    /// <summary>An agent loaded with its Runner (and the Runner's members), owner, shares and harness login.</summary>
    public static AgentDto Agent(Agent agent, CurrentUser user)
    {
        var runner = agent.Runner ?? throw new InvalidOperationException("Load the agent with its Runner.");
        var runnerAccess = AccessControl.ForRunner(user, runner.OwnerId, runner.Members);
        return Agent(agent, runner, AccessControl.ForAgent(user, agent.OwnerId, agent.Shares, runnerAccess), runnerAccess, agent.Shares.Any(s => s.UserId == user.Id));
    }

    // Runner access only decides what the Portal offers beyond the agent (VS Code, the desktop, the Runner page).
    private static AgentDto Agent(Agent agent, Runner runner, AgentAccess access, RunnerAccess runnerAccess, bool sharedWithMe) => new(
        agent.Id,
        runner.Id,
        agent.OwnerId,
        agent.Owner?.DisplayName ?? "",
        access,
        runner.Name,
        runner.State,
        runner.HarnessIds,
        runner.ToolIds,
        agent.Name,
        agent.HarnessId,
        agent.WorkingDirectory,
        agent.RepositoryUrl,
        agent.RepositoryBranch,
        agent.State,
        agent.StateMessage,
        agent.ExitCode,
        agent.AccountId,
        agent.Account?.Name,
        Guid.Empty,
        "",
        agent.CreatedAt,
        agent.UpdatedAt,
        agent.IconVersion,
        agent.State == AgentState.Running && agent.Working,
        AgentActivityTracker.GetDto(agent, runnerState: runner.State),
        agent.LastAssistantMessageAt,
        runner.Kind, agent.RunningAccountId, sharedWithMe, agent.RunningAccountName, agent.MoveJson is not null,
        access >= AgentAccess.Read ? AgentService.MoveDetails(agent) : null,
        agent.AccountId is null ? agent.Owner?.Username : agent.Account?.CreatedBy?.Username,
        agent.RunningAccountUsername,
        runnerAccess >= RunnerAccess.Use,
        access >= AgentAccess.Read ? GitIdentity(agent, AgentService.GitIdentity(agent), agent.GitIdentityName) : null,
        access >= AgentAccess.Read ? GitIdentity(agent, AgentService.RunningGitIdentity(agent), agent.RunningGitIdentityName) : null,
        agent.ProjectId ?? agent.Id, agent.Project?.Name ?? agent.Name, agent.IsClone, agent.WorkspaceCreationJson is not null, agent.ProjectWorkspaces);

    private static AgentGitIdentityDto GitIdentity(Agent agent, Guid userId, (string Username, string DisplayName)? name)
    {
        name ??= userId == agent.OwnerId && agent.Owner is { } owner ? (owner.Username, owner.DisplayName) : null;
        return new(userId, name?.Username, name?.DisplayName);
    }

    /// <summary>A conversation loaded with its owner, shares, agent and workspace agent (see SessionService).</summary>
    public static SessionDto Session(StoredSession session, CurrentUser user)
    {
        // A removed workspace's conversation takes its project's access: sharing is the same for every workspace.
        var workspace = (session.WorkspaceAgent ?? session.Project?.Agents.OrderBy(a => a.IsClone).FirstOrDefault()) is { } owner
            ? AccessControl.ForAgent(user, owner.OwnerId, owner.Shares, owner.Runner is { } runner ? AccessControl.ForRunner(user, runner.OwnerId, runner.Members) : RunnerAccess.None)
            : AgentAccess.None;
        return new(
            session.Id,
            session.OwnerId,
            session.Owner?.DisplayName ?? "",
            AccessControl.ForSession(user, session.OwnerId, session.Shares, workspace),
            session.Title,
            session.HarnessId,
            session.HarnessSessionId,
            session.HarnessTrail,
            session.AgentId,
            session.Agent is { Runner: not null } agent ? Agent(agent, user) : null,
            session.RunnerName,
            session.WorkingDirectory,
            session.RepositoryUrl,
            session.RepositoryBranch,
            session.MessageCount,
            session.CreatedAt,
            session.UpdatedAt,
            session.LastSyncedAt,
            session.WorkspaceAgentId,
            session.WorkspaceAgent?.Name,
            workspace,
            session.SourceSize,
            session.SavedSize,
            session.TotalSize,
            session.ProjectId ?? session.WorkspaceAgent?.ProjectId);
    }

    /// <param name="renewable">Its harness lets the Manager renew subscription logins.</param>
    public static AccountDto Account(Account account, int agentCount, bool renewable) => new(
        account.Id,
        account.HarnessId,
        account.Name,
        account.Kind,
        account.IsDefault,
        account.Version,
        agentCount,
        account.CreatedAt,
        account.UpdatedAt,
        account.RenewalProblem ?? (renewable && account.Kind == AccountKind.Subscription && !account.ManagedRenewal
            ? "Connect it again so the Manager keeps it signed in" : null), account.Access, account.CanManage, account.Enabled);

    public static UserDto User(User user) => new(
        user.Id,
        user.Username,
        user.DisplayName,
        user.Roles,
        user.Disabled,
        user.MustChangePassword,
        user.LockedUntil > DateTime.UtcNow,
        user.CreatedAt,
        user.LastSignInAt);

    public static UserSummaryDto Summary(User user) => new(user.Id, user.Username, user.DisplayName);
}
