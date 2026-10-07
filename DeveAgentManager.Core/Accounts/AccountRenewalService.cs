using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Accounts;

/// <summary>
/// The only place logins the Manager renews are renewed in the background (agent starts renew too, through the same
/// lock): every 10 minutes, then Runners get the current copies. Assumes one Manager per database.
/// </summary>
public sealed class AccountRenewalService(AccountService accounts, ILogger<AccountRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            do
            {
                try
                {
                    await accounts.SyncRunnersAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Renewing logins failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
