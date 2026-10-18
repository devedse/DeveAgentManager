using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>
/// Keeps dam-tools current on Ready Runners whose setup installed it (<see cref="RunnerSetupService.SyncAgentToolsAsync"/>):
/// each is checked once after the Manager starts, and again after a failure or a new setup.
/// </summary>
public sealed class AgentToolsSync(IDbContextFactory<ManagerDbContext> factory, RunnerSetupService setup, ILogger<AgentToolsSync> logger, ArtifactTokens tokens, RunnerShell shell) : BackgroundService
{
    private readonly HashSet<(Guid RunnerId, DateTime? SetupAt)> _current = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await using (var db = await factory.CreateDbContextAsync(stoppingToken))
                {
                    var runners = await db.Runners.AsNoTracking()
                        .Where(r => r.State == RunnerState.Ready && r.SetupVersion >= RunnerSetupService.AgentToolsSetupVersion).ToListAsync(stoppingToken);
                    foreach (var runner in runners.Where(r => !_current.Contains((r.Id, r.LastSetupAt))))
                    {
                        try
                        {
                            if (await setup.SyncAgentToolsAsync(runner, stoppingToken)) logger.LogInformation("Updated dam-tools on {Runner}", runner.Name);
                            foreach (var agent in await db.Agents.AsNoTracking().Where(a => a.RunnerId == runner.Id).ToListAsync(stoppingToken))
                                await tokens.InstallAsync(shell, runner, agent, stoppingToken);
                            _current.Add((runner.Id, runner.LastSetupAt));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                        {
                            logger.LogDebug(ex, "Could not check dam-tools on {Runner}", runner.Name);
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
