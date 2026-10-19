using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Editor;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Providers.ExistingMachine;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeveAgentManager.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeveAgentManagerCore(this IServiceCollection services, ManagerOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);

        services.AddSingleton(options);
        services.AddMemoryCache();
        services.AddDbContextFactory<ManagerDbContext>(db => db.UseSqlite($"Data Source={options.DatabasePath}").AddInterceptors(new Diagnostics.CaptureCommandInterceptor()));
        services.AddDataProtection()
            .SetApplicationName("DeveAgentManager")
            .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDirectory));

        services.AddSingleton<SecretProtector>();
        services.AddSingleton<ManagerEvents>();
        services.AddSingleton<AuditLog>();
        services.AddSingleton<UserService>();
        services.AddSingleton<SignInSessions>();
        services.AddSingleton<SecondFactors>();
        services.AddSingleton<AccessControl>();
        services.AddSingleton<Sharing>();
        services.AddSingleton<RunnerLogStore>();
        services.AddSingleton(_ => ManagerSshKey.LoadOrCreate(options.SshDirectory));
        services.AddSingleton<SshConnector>();
        services.AddSingleton<RunnerShell>();
        services.AddSingleton<UsageLimitReader>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<UserSettingsStore>();
        services.AddSingleton<RunnerUsers>();
        services.AddSingleton<RunnerPasswords>();
        services.AddSingleton<RunnerMembers>();
        services.AddSingleton<RunnerTimeZones>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILimitTerminal, LimitTerminal>();
        services.AddSingleton<AgentContinuations>();
        services.AddSingleton<AgentDiagnostics>();
        services.AddSingleton<AgentQuickCommands>();
        services.AddSingleton<RunnerSetupService>();
        services.AddSingleton<RunnerDesktop>();
        services.AddSingleton<AgentArtifacts>();
        services.AddSingleton<ArtifactTransfers>();
        services.AddSingleton<ArtifactTokens>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<SessionImageStore>();
        services.AddHostedService<AgentJobCompletions>();
        services.AddSingleton<SessionImages>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionImages>());
        services.AddSingleton<SessionFileStore>();
        services.AddSingleton<SessionFiles>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionFiles>());
        services.AddSingleton<Accounts.AccountService>();
        services.AddHostedService<Accounts.AccountEnforcement>();
        services.AddSingleton<AgentService>();
        services.AddSingleton<AgentAttentionService>();
        services.AddSingleton<RunnerService>();
        services.AddSingleton<RunnerStatusCache>();
        services.AddSingleton<TerminalService>();
        services.AddSingleton<IRunnerProvider, ExistingMachineProvider>();
        services.AddSingleton<IRunnerTool, VsCodeTool>();
        services.AddSingleton<EditorTunnels>();
        services.AddSingleton<IEditorUpstreams>(sp => sp.GetRequiredService<EditorTunnels>());
        services.AddHostedService(sp => sp.GetRequiredService<EditorTunnels>());
        services.AddHostedService<RunnerMonitor>();
        services.AddHostedService<AgentToolsSync>();
        services.AddHostedService<Accounts.AccountRenewalService>();
        services.AddHostedService<RunnerUsageMonitor>();
        services.AddSingleton<AgentIconService>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentIconService>());
        return services;
    }
}
