using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Runners;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Users;

/// <summary>
/// Ends access whose time limit is up (AccessTimeLimits), as Remove access would: to logins, Runners, projects and
/// conversations. Checks when the Manager starts and every 10 seconds after, so access ends within seconds of its time.
/// </summary>
public sealed class AccessExpiryService(AccountService accounts, RunnerMembers members, Sharing sharing, ILogger<AccessExpiryService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await ExpireAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One check of every kind of access; a failing kind doesn't hold up the others.</summary>
    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        try { await accounts.ExpireGrantsAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Ending timed login access failed"); }
        try { await sharing.ExpireAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Ending timed shares failed"); }
        try { await members.ExpireAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Ending timed Runner access failed"); }
    }
}
