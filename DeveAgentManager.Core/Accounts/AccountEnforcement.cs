using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace DeveAgentManager.Core.Accounts;

/// <summary>Every 10 seconds: stops agents whose harness login or git identity is no longer theirs to use.</summary>
public sealed class AccountEnforcement(AccountService accounts, Agents.AgentService agents, ILogger<AccountEnforcement> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await accounts.EnforceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Login access enforcement failed"); }
                try { await agents.EnforceGitIdentitiesAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Git identity enforcement failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
