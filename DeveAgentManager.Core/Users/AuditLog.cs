using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Users;

/// <summary>The actions the audit log records.</summary>
public static class AuditActions
{
    public const string SignedIn = "signed-in";
    public const string SignInFailed = "sign-in-failed";
    public const string SignInLocked = "sign-in-locked";
    public const string PasswordChanged = "password-changed";
    public const string UserCreated = "user-created";
    public const string UserUpdated = "user-updated";
    public const string UserPasswordReset = "user-password-reset";
    public const string UserDeleted = "user-deleted";
    public const string AdminPasswordReset = "admin-password-reset";
    public const string RunnerCreated = "runner-created";
    public const string RunnerDeleted = "runner-deleted";
    public const string RunnerOwnerChanged = "runner-owner-changed";
    public const string RunnerAccessChanged = "runner-access-changed";
    public const string RunnerAccessRemoved = "runner-access-removed";
    public const string RunnerPasswordViewed = "runner-password-viewed";
    public const string RunnerPasswordChanged = "runner-password-changed";
    public const string RunnerKeysOnlyChanged = "runner-keys-only-changed";
    public const string AgentShared = "agent-shared";
    public const string AgentUnshared = "agent-unshared";
    public const string SessionShared = "session-shared";
    public const string SessionUnshared = "session-unshared";
}

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
