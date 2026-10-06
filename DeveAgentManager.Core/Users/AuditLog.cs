using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Users;

/// <summary>
/// Records what happened to users, access, shares and Runner passwords, for user managers to read. Keeps the most
/// recent <see cref="Kept"/> entries. Recording never fails the action it records.
/// </summary>
public sealed class AuditLog(IDbContextFactory<ManagerDbContext> dbFactory, ILogger<AuditLog> logger)
{
    public const int Kept = 10_000;

    public async Task RecordAsync(CurrentUser? actor, string action, string? target, string? detail = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            db.AuditEvents.Add(new AuditEvent
            {
                At = DateTime.UtcNow,
                ActorId = actor?.Id,
                ActorName = actor?.Username,
                Action = action,
                Target = Clip(target, 200),
                Detail = Clip(detail, 1000),
            });
            await db.SaveChangesAsync(cancellationToken);
            // Trimming every hundredth entry keeps the table near its limit without a query per action.
            var last = await db.AuditEvents.MaxAsync(a => a.Id, cancellationToken);
            if (last % 100 == 0)
            {
                await db.AuditEvents.Where(a => a.Id <= last - Kept).ExecuteDeleteAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record {Action} on {Target} in the audit log", action, target);
        }
    }

    /// <summary>The newest entries first.</summary>
    public async Task<List<AuditEvent>> ListAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AuditEvents.AsNoTracking().OrderByDescending(a => a.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
    }

    private static string? Clip(string? text, int length) => text is null || text.Length <= length ? text : text[..length];
}
