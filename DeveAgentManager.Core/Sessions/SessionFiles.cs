using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// Imports files agents share with <c>dam-tools share-file</c>, also when no browser is open. Every Runner is collected on
/// its own, so a large transfer from one never holds up the others.
/// </summary>
public sealed class SessionFiles(IDbContextFactory<ManagerDbContext> factory, RunnerShell shell, SessionService sessions,
    SessionFileStore store, ManagerEvents events, ILogger<SessionFiles> logger) : BackgroundService
{
    internal sealed record Publication(Guid Id, Guid AgentId, Guid SessionId, string FileName, string Caption, long Bytes, string Sha256);

    /// <summary>Only touched by the collection loop.</summary>
    private readonly Dictionary<Guid, Task> _collecting = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var runners = await db.Runners.AsNoTracking().Where(r => r.State == RunnerState.Ready).ToListAsync(stoppingToken);
                    foreach (var runner in runners)
                    {
                        if (_collecting.TryGetValue(runner.Id, out var running) && !running.IsCompleted) continue;
                        _collecting[runner.Id] = Task.Run(() => CollectSafelyAsync(runner, stoppingToken), CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "File collection failed"); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(_collecting.Values);
        }
    }

    private async Task CollectSafelyAsync(Runner runner, CancellationToken ct)
    {
        try { await CollectAsync(runner, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogDebug(ex, "Could not collect files from {Runner}", runner.Id); }
    }

    private async Task CollectAsync(Runner runner, CancellationToken ct)
    {
        // Only start Python when something was shared: this runs every few seconds on every runner.
        var result = await shell.RunAsync(runner,
            "if [ -x ~/.deveagentmanager/dam-tools.py ] && [ -n \"$(ls -A ~/.deveagentmanager/shared-files 2>/dev/null)\" ]; then python3 ~/.deveagentmanager/dam-tools.py --pending-files; else printf '[]'; fi", TimeSpan.FromSeconds(15), ct);
        if (!result.Succeeded || result.Output.Length > 100_000) return;
        var publications = JsonSerializer.Deserialize<List<Publication>>(result.Output, ApiJson.Options) ?? [];
        foreach (var item in publications.Take(20))
        {
            var remote = $"~/.deveagentmanager/shared-files/{item.Id:N}/file";
            if (await ImportAsync(runner.Id, item, (target, token) => shell.DownloadToAsync(runner, remote, target, SessionFileStore.MaxBytes, token), ct))
            {
                await shell.RunAsync(runner, $"python3 ~/.deveagentmanager/dam-tools.py --ack-file {item.Id:N}", TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /// <summary>
    /// Brings one shared file into its conversation. True when the Runner can drop its copy: it was imported (now or
    /// before), or it can never be (the conversation is gone or the file isn't what was shared). Transfer and disk errors
    /// throw, so the file is tried again later.
    /// </summary>
    internal async Task<bool> ImportAsync(Guid runnerId, Publication item, Func<Stream, CancellationToken, Task<bool>> download, CancellationToken ct)
    {
        if (item.Id == Guid.Empty || item.FileName is null || item.Caption is null) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        // Deleted conversations cannot accumulate a forever-retrying outbox.
        if (!await SessionImages.BelongsAsync(db, runnerId, item.AgentId, item.SessionId, ct)) return true;
        if (await store.GetAsync(item.SessionId, item.Id, ct) is not null) return true;
        try
        {
            // Received outside the conversation lock: a large transfer must not hold up everything else.
            using var incoming = await store.ReceiveAsync(item.Bytes, item.Sha256, async (target, token) =>
            {
                if (!await download(target, token)) throw new InvalidDataException("The shared file is gone or too large.");
            }, ct);
            using var lease = await sessions.LockAsync(ct);
            if (await SessionImages.BelongsAsync(db, runnerId, item.AgentId, item.SessionId, ct))
            {
                await store.CommitAsync(item.SessionId, item.Id, incoming, item.FileName, item.Caption, ct);
                events.SessionChanged(item.SessionId, item.AgentId);
            }
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("Discarding shared file {File} ({Name}): {Reason}", item.Id, item.FileName, ex.Message);
        }

        return true;
    }
}
