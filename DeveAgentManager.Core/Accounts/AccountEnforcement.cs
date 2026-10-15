using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace DeveAgentManager.Core.Accounts;

public sealed class AccountEnforcement(AccountService accounts, ILogger<AccountEnforcement> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try { await accounts.EnforceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Login access enforcement failed"); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
