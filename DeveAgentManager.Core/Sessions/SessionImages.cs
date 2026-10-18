using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Imports explicit image publications independently of whether any browser is open.</summary>
public sealed class SessionImages(IDbContextFactory<ManagerDbContext> factory, RunnerShell shell,
    SessionService sessions, SessionImageStore store, AccessControl access, ILogger<SessionImages> logger) : BackgroundService
{
    private sealed record Publication(Guid Id, Guid AgentId, Guid SessionId, string FileName, string Caption, string ContentType, long Bytes);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var runners = await db.Runners.AsNoTracking().Where(r => r.State == RunnerState.Ready).ToListAsync(stoppingToken);
                await Task.WhenAll(runners.Select(r => CollectSafelyAsync(r, stoppingToken)));
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Image collection failed"); await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        }
    }

    private async Task CollectSafelyAsync(Runner runner, CancellationToken ct)
    {
        try { await CollectAsync(runner, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogDebug(ex, "Could not collect images from {Runner}", runner.Id); }
    }

    internal async Task CollectAsync(Runner runner, CancellationToken ct)
    {
        // Only start Python when something was shared: this runs every few seconds on every runner.
        var result = await shell.RunAsync(runner,
            "if [ -x ~/.deveagentmanager/dam-tools.py ] && [ -n \"$(ls -A ~/.deveagentmanager/shared-images 2>/dev/null)\" ]; then python3 ~/.deveagentmanager/dam-tools.py --pending-images; else printf '[]'; fi", TimeSpan.FromSeconds(15), ct);
        if (!result.Succeeded || result.Output.Length > 100_000) return;
        var publications = JsonSerializer.Deserialize<List<Publication>>(result.Output, ApiJson.Options) ?? [];
        foreach (var item in publications.Take(20))
        {
            if (item.Id == Guid.Empty || item.Bytes is <= 0 or > SessionImageStore.MaxBytes || item.FileName is null || item.Caption is null) continue;
            if (!await sessions.ArtifactPublicationBelongsAsync(runner.Id, item.AgentId, item.SessionId, ct))
            {
                // Removed agents and unknown sources cannot accumulate a forever-retrying outbox.
                await AcknowledgeAsync(runner, item.Id, ct);
                continue;
            }
            var data = await shell.DownloadBytesAsync(runner, $"~/.deveagentmanager/shared-images/{item.Id:N}/image", SessionImageStore.MaxBytes, ct);
            if (data is null) continue;
            try
            {
                using var lease = await sessions.LockAsync(ct);
                if (await sessions.ArtifactPublicationBelongsAsync(runner.Id, item.AgentId, item.SessionId, ct))
                {
                    await store.SaveAsync(item.SessionId, item.Id, data, item.FileName, item.Caption, ct);
                    await sessions.ArtifactsChangedAsync(item.SessionId, ct);
                }
            }
            catch (ArgumentException) { logger.LogDebug("Discarding invalid image {Image}", item.Id); }
            await AcknowledgeAsync(runner, item.Id, ct);
        }
    }

    internal static Task<bool> BelongsAsync(ManagerDbContext db, Guid runnerId, Guid agentId, Guid sessionId, CancellationToken ct) =>
        db.Sessions.AnyAsync(s => s.Id == sessionId && (s.WorkspaceAgentId == agentId || s.AgentId == agentId)
            && db.Agents.Any(a => a.Id == agentId && a.RunnerId == runnerId), ct);

    private Task<CommandResult> AcknowledgeAsync(Runner runner, Guid id, CancellationToken ct) =>
        shell.RunAsync(runner, $"python3 ~/.deveagentmanager/dam-tools.py --ack-image {id:N}", TimeSpan.FromSeconds(10), ct);

    /// <summary>Saves a desktop snapshot in a conversation on that Runner, whose agent <paramref name="actor"/> can write to.</summary>
    public async Task<SessionImageDto> SaveSnapshotAsync(CurrentUser actor, Guid runnerId, Guid sessionId, byte[] data, string? caption, CancellationToken ct)
    {
        using var lease = await sessions.LockAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct) ?? throw new KeyNotFoundException("Session not found.");
        var owner = session.WorkspaceAgentId ?? session.AgentId;
        if (owner is null || await access.AgentAsync(actor, owner.Value, ct) < AgentAccess.Write)
            throw new KeyNotFoundException("Session not found.");
        if (!await BelongsAsync(db, runnerId, owner.Value, sessionId, ct)) throw new ArgumentException("Choose a conversation on this runner.");
        var image = await store.SaveAsync(sessionId, Guid.NewGuid(), data, "desktop.jpg", caption ?? "Desktop snapshot", ct);
        await sessions.ArtifactsChangedAsync(sessionId, ct);
        return image;
    }
}
