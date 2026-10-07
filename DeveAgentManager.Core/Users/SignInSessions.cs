using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Users;

/// <summary>Durable, revocable browser sessions; no self-contained credential survives server logout.</summary>
public sealed class SignInSessions(IDbContextFactory<ManagerDbContext> factory, TimeProvider clock)
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan RecentLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _creation = new(1);
    private readonly SemaphoreSlim _ticketIssuance = new(1);
    public event Action<Guid>? Revoked;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<SignInSession> CreateAsync(User user, bool local, string ip, string browser, CancellationToken ct = default)
    {
        await _creation.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var now = Now;
            await db.EditorTickets.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(ct);
            await db.SignInSessions.Where(s => s.AbsoluteExpiresAt <= now || s.RevokedAt != null).ExecuteDeleteAsync(ct);
            // Keep credential storage bounded even if a user repeatedly signs in without signing out.
            var older = await db.SignInSessions.Where(s => s.UserId == user.Id).OrderByDescending(s => s.CreatedAt).Skip(19).Select(s => s.Id).ToListAsync(ct);
            foreach (var id in older) await RevokeAsync(user.Id, id, ct);
            var session = new SignInSession
            {
                Id = Guid.NewGuid(), UserId = user.Id, SecurityStamp = user.SecurityStamp, Local = local,
                CreatedAt = now, LastSeenAt = now, AuthenticatedAt = now,
                ExpiresAt = now + IdleLifetime, AbsoluteExpiresAt = now + AbsoluteLifetime,
                ClientIp = ip[..Math.Min(ip.Length, 64)], Browser = browser[..Math.Min(browser.Length, 256)],
            };
            db.SignInSessions.Add(session);
            await db.SaveChangesAsync(ct);
            return session;
        }
        finally { _creation.Release(); }
    }

    public async Task<SignInSession?> ValidateAsync(Guid id, Guid user, string? stamp, bool? local = null, bool touch = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        var session = await db.SignInSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.UserId == user
            && s.SecurityStamp == stamp && s.RevokedAt == null && s.ExpiresAt > now && s.AbsoluteExpiresAt > now
            && (local == null || s.Local == local), ct);
        if (session is not null && touch && session.LastSeenAt < now.AddMinutes(-1))
        {
            var expires = new[] { now + IdleLifetime, session.AbsoluteExpiresAt }.Min();
            await db.SignInSessions.Where(s => s.Id == id && s.RevokedAt == null).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastSeenAt, now).SetProperty(s => s.ExpiresAt, expires), ct);
        }
        return session;
    }

    public async Task<List<SignInSession>> ListAsync(Guid user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        return await db.SignInSessions.AsNoTracking().Where(s => s.UserId == user && s.RevokedAt == null && s.ExpiresAt > now && s.AbsoluteExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt).ToListAsync(ct);
    }

    public async Task RevokeAsync(Guid user, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        await db.SignInSessions.Where(s => s.Id == id && s.UserId == user).ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now), ct);
        Revoked?.Invoke(id);
    }

    public async Task ReauthenticateAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        await db.SignInSessions.Where(s => s.Id == id && s.RevokedAt == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.AuthenticatedAt, now), ct);
    }

    public async Task<string> IssueEditorTicketAsync(Guid session, Guid runner, string origin, CancellationToken ct = default)
    {
        await _ticketIssuance.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var now = Now;
            await db.EditorTickets.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(ct);
            var older = await db.EditorTickets.Where(t => t.SessionId == session).OrderByDescending(t => t.ExpiresAt).Skip(63).Select(t => t.Id).ToListAsync(ct);
            if (older.Count > 0) await db.EditorTickets.Where(t => older.Contains(t.Id)).ExecuteDeleteAsync(ct);
            var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            db.EditorTickets.Add(new EditorTicket { Id = id, SessionId = session, RunnerId = runner, Origin = origin, ExpiresAt = Now.AddSeconds(30) });
            await db.SaveChangesAsync(ct);
            return id;
        }
        finally { _ticketIssuance.Release(); }
    }

    public async Task<SignInSession?> ConsumeEditorTicketAsync(string id, Guid runner, string origin, CancellationToken ct = default)
    {
        if (id.Length != 64) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        var ticket = await db.EditorTickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.RunnerId == runner && t.Origin == origin && t.ExpiresAt > now, ct);
        // The delete wins once, even across processes; read+delete in memory is not sufficient.
        if (ticket is null || await db.EditorTickets.Where(t => t.Id == id && t.ExpiresAt > now).ExecuteDeleteAsync(ct) != 1) return null;
        var session = await db.SignInSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == ticket.SessionId, ct);
        return session is null ? null : await ValidateAsync(session.Id, session.UserId, session.SecurityStamp, ct: ct);
    }
}
