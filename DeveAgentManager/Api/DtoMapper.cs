using System.Reflection;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Api;

/// <summary>
/// Builds what the API returns from the database entities and the live state (operations, resources). Leaves out every
/// secret: account logins, Runner passwords and the GitHub token never reach the browser.
/// </summary>
public sealed class DtoMapper(
    RunnerService runners,
    RunnerStatusCache statuses,
    AgentService agents,
    SessionService sessions,
    AccountService accounts,
    SettingsStore settings,
    RunnerSetupService setup,
    ManagerOptions options,
    ManagerSshKey sshKey)
{
    public static readonly string Version =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev").Split('+')[0];

    public async Task<ManagerInfoDto> InfoAsync(CancellationToken cancellationToken) => new(
        Version,
        options.AdminUsername,
        options.UsesDefaultAdminPassword,
        ManagerOptions.DefaultAdminPassword,
        options.RunnerUsername,
        options.DefaultCores,
        options.DefaultMemoryMb,
        options.DefaultDiskGb,
        options.DataDirectory,
        sshKey.PublicKey,
        settings.GitHubPatFromCompose,
        await settings.GetGitHubPatAsync(cancellationToken) is not null,
        RunnerSetupService.SetupVersion,
        agents.Harnesses.OrderBy(h => h.Id == "pi" ? 0 : 1).ThenBy(h => h.DisplayName).Select(Harness).ToList(),
        runners.Providers.OrderBy(p => p.Capabilities.RequiresConnectionDetails).Select(Provider).ToList(),
        setup.Tools.OrderBy(t => t.DisplayName).Select(t => new RunnerToolDto(t.Id, t.DisplayName, t.Description, t.Icon)).ToList());

    public HarnessDto Harness(IHarness harness) => new(
        harness.Id,
        harness.DisplayName,
        harness.Description,
        sessions.SupportsHarness(harness.Id),
        harness.ModelsCommand is not null,
        harness.ConfigSupport,
        harness.AccountSupport,
        harness.Icon);

    public static ProviderDto Provider(IRunnerProvider provider) => new(
        provider.Id,
        provider.DisplayName,
        provider.IsConfigured,
        provider.Capabilities,
        provider.GetTemplates(),
        // Providers mask their secrets themselves; masked again here, so no provider can leak one by accident.
        provider.GetSettings().Select(s => s.IsSecret && s.Value is not null ? s with { Value = "••••••••" } : s).ToList());

    /// <summary>Account names by id, for agents.</summary>
    public async Task<Dictionary<Guid, string>> AccountNamesAsync(CancellationToken cancellationToken) =>
        (await accounts.ListAsync(cancellationToken: cancellationToken)).ToDictionary(a => a.Id, a => a.Name);

    public RunnerDto Runner(Runner runner, IReadOnlyDictionary<Guid, string> accountNames) => new(
        runner.Id,
        runner.Name,
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
        runner.Agents.OrderByDescending(a => a.CreatedAt).Select(a => Agent(a, runner, accountNames)).ToList());

    public static AgentDto Agent(Agent agent, Runner runner, IReadOnlyDictionary<Guid, string> accountNames) => new(
        agent.Id,
        runner.Id,
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
        agent.AccountId is { } id ? accountNames.GetValueOrDefault(id) : null,
        agent.CreatedAt,
        agent.UpdatedAt);

    public static SessionDto Session(StoredSession session, IReadOnlyDictionary<Guid, string> accountNames) => new(
        session.Id,
        session.Title,
        session.HarnessId,
        session.HarnessSessionId,
        session.HarnessTrail,
        session.AgentId,
        session.Agent is { Runner: { } runner } agent ? Agent(agent, runner, accountNames) : null,
        session.RunnerName,
        session.WorkingDirectory,
        session.RepositoryUrl,
        session.RepositoryBranch,
        session.MessageCount,
        session.CreatedAt,
        session.UpdatedAt,
        session.LastSyncedAt);

    public static AccountDto Account(Account account, int agentCount) => new(
        account.Id,
        account.HarnessId,
        account.Name,
        account.Kind,
        account.IsDefault,
        account.Version,
        agentCount,
        account.CreatedAt,
        account.UpdatedAt);
}
