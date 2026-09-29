using System.Collections.Concurrent;
using DeveAgentManager.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>Samples inexpensive per-machine resource counters once a second.</summary>
public sealed class RunnerUsageMonitor(RunnerService runners, RunnerStatusCache cache, ILogger<RunnerUsageMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var runnerList = await runners.ListAsync(stoppingToken);
        var refreshAt = DateTime.UtcNow.AddSeconds(15);
        var retryAfter = new ConcurrentDictionary<Guid, DateTime>();
        var warnAfter = new ConcurrentDictionary<Guid, DateTime>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= refreshAt)
                {
                    runnerList = await runners.ListAsync(stoppingToken);
                    refreshAt = DateTime.UtcNow.AddSeconds(15);
                    var active = runnerList.Select(r => r.Id).ToHashSet();
                    foreach (var id in retryAfter.Keys.Where(id => !active.Contains(id)).ToArray()) retryAfter.TryRemove(id, out _);
                    foreach (var id in warnAfter.Keys.Where(id => !active.Contains(id)).ToArray()) warnAfter.TryRemove(id, out _);
                }

                await Parallel.ForEachAsync(runnerList, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (runner, ct) =>
                {
                    if (runner.State != RunnerState.Ready || runner.MachineId is null ||
                        runners.CurrentOperation(runner.Id) is not null ||
                        retryAfter.TryGetValue(runner.Id, out var retry) && retry > DateTime.UtcNow) return;

                    var provider = runners.FindProvider(runner.ProviderId);
                    if (provider is not { SupportsUsageSampling: true }) return;
                    try
                    {
                        if (await provider.GetUsageAsync(runner.ToMachine(), ct) is { } status)
                        {
                            cache.RecordUsage(runner.Id, status);
                            if (warnAfter.TryRemove(runner.Id, out _)) logger.LogInformation("One-second usage sampling resumed for runner {Runner}", runner.Name);
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        retryAfter[runner.Id] = DateTime.UtcNow.AddSeconds(10);
                        if (!warnAfter.TryGetValue(runner.Id, out var warn) || warn <= DateTime.UtcNow)
                        {
                            warnAfter[runner.Id] = DateTime.UtcNow.AddMinutes(1);
                            logger.LogWarning(ex, "One-second usage sampling failed for runner {Runner}; only slow status readings are available. Retrying in 10 seconds", runner.Name);
                        }
                    }
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Runner usage sampling pass failed");
                refreshAt = DateTime.MinValue;
            }

            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
