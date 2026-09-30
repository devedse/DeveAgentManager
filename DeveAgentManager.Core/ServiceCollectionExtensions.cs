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
        services.AddDbContextFactory<ManagerDbContext>(db => db.UseSqlite($"Data Source={options.DatabasePath}"));
        services.AddDataProtection()
            .SetApplicationName("DeveAgentManager")
            .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDirectory));

        services.AddSingleton<SecretProtector>();
        services.AddSingleton<ManagerEvents>();
        services.AddSingleton<RunnerLogStore>();
        services.AddSingleton(_ => ManagerSshKey.LoadOrCreate(options.SshDirectory));
        services.AddSingleton<SshConnector>();
        services.AddSingleton<RunnerShell>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<RunnerTimeZones>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILimitTerminal, LimitTerminal>();
        services.AddSingleton<AgentContinuations>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentContinuations>());
        services.AddSingleton<RunnerSetupService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<Accounts.AccountService>();
        services.AddSingleton<AgentService>();
        services.AddSingleton<RunnerService>();
        services.AddSingleton<RunnerStatusCache>();
        services.AddSingleton<TerminalService>();
        services.AddSingleton<IRunnerProvider, ExistingMachineProvider>();
        services.AddSingleton<IRunnerTool, VsCodeTool>();
        services.AddSingleton<EditorTunnels>();
        services.AddSingleton<IEditorUpstreams>(sp => sp.GetRequiredService<EditorTunnels>());
        services.AddHostedService(sp => sp.GetRequiredService<EditorTunnels>());
        services.AddHostedService<RunnerMonitor>();
        services.AddHostedService<RunnerUsageMonitor>();
        services.AddSingleton<AgentIconService>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentIconService>());
        return services;
    }
}
