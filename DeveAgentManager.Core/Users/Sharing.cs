using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Users;

/// <summary>
/// Agents and conversations their owners share with other users. An agent is shared read-only (watch its terminal, read
/// its conversations) or read-write (also type and prompt it, with the harness login and git identity it runs with; no
/// access to its Runner is needed). A conversation is
/// shared read-only. Owners change or end a share at any time; anyone can leave a share they got. A share with a time
/// limit ends by itself once the time is up (<see cref="ExpireAsync"/>).
/// </summary>
public sealed class Sharing(IDbContextFactory<ManagerDbContext> dbFactory, AuditLog audit, ManagerEvents events, ILogger<Sharing> logger)
{
    public async Task<List<AgentShare>> ListAgentSharesAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentShares.AsNoTracking().Include(s => s.User).Where(s => s.AgentId == agentId).OrderBy(s => s.User!.Username).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Shares the owner's agent (its whole project) with <paramref name="userId"/>, or changes how.
    /// <paramref name="expiry"/> is the share's time limit; left out, the share keeps the one it has.
    /// </summary>
    public async Task ShareAgentAsync(CurrentUser actor, Guid agentId, Guid userId, AgentAccess access, CancellationToken cancellationToken = default, AccessExpiry? expiry = null)
    {
        if (access is not (AgentAccess.Read or AgentAccess.Write))
        {
            throw new ArgumentException("Choose read-only or read-write.");
        }

        await Agents.AgentService.ProjectGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken) ?? throw new KeyNotFoundException("Agent not found.");
            RequireOwner(actor, agent.OwnerId, "project");
            var user = await RequireOtherUserAsync(db, actor, userId, cancellationToken);
            var workspaces = await db.Agents.Include(a => a.Shares)
                .Where(a => a.Id == agentId || agent.ProjectId != null && a.ProjectId == agent.ProjectId).ToListAsync(cancellationToken);
            // One time limit for the whole project, like its access.
            var current = workspaces.SelectMany(a => a.Shares).Where(s => s.UserId == userId).Select(s => s.ExpiresAt).Min();
            var expiresAt = AccessTimeLimits.Resolve(expiry, current, DateTime.UtcNow);
            foreach (var workspace in workspaces)
            {
                var share = workspace.Shares.FirstOrDefault(s => s.UserId == userId);
                if (share is null)
                {
                    share = new AgentShare { AgentId = workspace.Id, UserId = userId };
                    db.AgentShares.Add(share);
                }
                share.Access = access;
                share.ExpiresAt = expiresAt;
                share.GrantedById = actor.Id;
                share.GrantedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(actor, AuditActions.AgentShared, $"Project {agent.Name}", $"{user.Username}: {(access == AgentAccess.Write ? "read-write" : "read-only")}{AccessTimeLimits.Describe(expiresAt)}", cancellationToken);
            foreach (var workspace in workspaces)
            {
                events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, workspace.RunnerId, workspace.Id, UserId: userId));
                events.AgentChanged(workspace.RunnerId, workspace.Id);
            }
        }
        finally { Agents.AgentService.ProjectGate.Release(); }
    }

    /// <summary>Ends a project share: the owner for anyone, or someone for their own.</summary>
    public async Task UnshareAgentAsync(CurrentUser actor, Guid agentId, Guid userId, CancellationToken cancellationToken = default)
    {
        await Agents.AgentService.ProjectGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken) ?? throw new KeyNotFoundException("Agent not found.");
            if (userId != actor.Id) RequireOwner(actor, agent.OwnerId, "project");
            if (!await RemoveAgentSharesAsync(db, agent, userId, cancellationToken)) throw new KeyNotFoundException("This project isn't shared with that user.");
            await audit.RecordAsync(actor, AuditActions.AgentUnshared, $"Project {agent.Name}", userId == actor.Id ? "Left project" : "Project access removed", cancellationToken);
        }
        finally { Agents.AgentService.ProjectGate.Release(); }
    }

    /// <summary>Ends shares whose time limit is up, as removing them does (every 10 seconds, AccessExpiryService).</summary>
    public async Task ExpireAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        List<(Guid AgentId, Guid UserId)> agentShares;
        List<(Guid SessionId, Guid UserId)> sessionShares;
        await using (var check = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            agentShares = (await check.AgentShares.Where(s => s.ExpiresAt != null && s.ExpiresAt <= now).Select(s => new { s.AgentId, s.UserId })
                .ToListAsync(cancellationToken)).Select(s => (s.AgentId, s.UserId)).ToList();
            sessionShares = (await check.SessionShares.Where(s => s.ExpiresAt != null && s.ExpiresAt <= now).Select(s => new { s.SessionId, s.UserId })
                .ToListAsync(cancellationToken)).Select(s => (s.SessionId, s.UserId)).ToList();
        }

        foreach (var (agentId, userId) in agentShares)
        {
            try
            {
                await Agents.AgentService.ProjectGate.WaitAsync(cancellationToken);
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                    // Ended with an earlier workspace of the same project, or changed meanwhile.
                    if (!await db.AgentShares.AnyAsync(s => s.AgentId == agentId && s.UserId == userId && s.ExpiresAt != null && s.ExpiresAt <= now, cancellationToken)) continue;
                    var agent = await db.Agents.FirstAsync(a => a.Id == agentId, cancellationToken);
                    var username = await db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(cancellationToken);
                    await RemoveAgentSharesAsync(db, agent, userId, cancellationToken);
                    await audit.RecordAsync(null, AuditActions.AgentUnshared, $"Project {agent.Name}", $"{username}: time limit ended", cancellationToken);
                }
                finally { Agents.AgentService.ProjectGate.Release(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not end the timed share of agent {Agent} with {User}; trying again", agentId, userId);
            }
        }

        foreach (var (sessionId, userId) in sessionShares)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var share = await db.SessionShares.Include(s => s.User).FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId, cancellationToken);
                if (share is not { ExpiresAt: { } at } || at > now) continue;
                var session = await db.Sessions.FirstAsync(s => s.Id == sessionId, cancellationToken);
                db.SessionShares.Remove(share);
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(null, AuditActions.SessionUnshared, $"Session {session.Title}", $"{share.User?.Username}: time limit ended", cancellationToken);
                events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, SessionId: sessionId, UserId: userId));
                events.SessionChanged(sessionId, session.WorkspaceAgentId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not end the timed share of conversation {Session} with {User}; trying again", sessionId, userId);
            }
        }
    }

    /// <summary>Removes the user's share of every workspace of the agent's project; false when there was none.</summary>
    private async Task<bool> RemoveAgentSharesAsync(ManagerDbContext db, Agent agent, Guid userId, CancellationToken cancellationToken)
    {
        var workspaces = await db.Agents.Include(a => a.Shares)
            .Where(a => a.Id == agent.Id || agent.ProjectId != null && a.ProjectId == agent.ProjectId).ToListAsync(cancellationToken);
        if (!workspaces.Any(a => a.Shares.Any(s => s.UserId == userId))) return false;
        foreach (var workspace in workspaces)
        {
            db.AgentShares.RemoveRange(workspace.Shares.Where(s => s.UserId == userId));
            if (workspace.GitIdentityUserId == userId) workspace.GitIdentityUserId = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var workspace in workspaces)
        {
            events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, workspace.RunnerId, workspace.Id, UserId: userId));
            events.AgentChanged(workspace.RunnerId, workspace.Id);
        }
        return true;
    }

    public async Task<List<SessionShare>> ListSessionSharesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SessionShares.AsNoTracking().Include(s => s.User).Where(s => s.SessionId == sessionId).OrderBy(s => s.User!.Username).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Shares the owner's conversation read-only with <paramref name="userId"/>. <paramref name="expiry"/> is the share's
    /// time limit; left out, a share keeps the one it has.
    /// </summary>
    public async Task ShareSessionAsync(CurrentUser actor, Guid sessionId, Guid userId, CancellationToken cancellationToken = default, AccessExpiry? expiry = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.Include(s => s.Shares).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken) ?? throw new KeyNotFoundException("Session not found.");
        RequireOwner(actor, session.OwnerId, "conversation");
        var user = await RequireOtherUserAsync(db, actor, userId, cancellationToken);
        var share = session.Shares.FirstOrDefault(s => s.UserId == userId);
        var expiresAt = AccessTimeLimits.Resolve(expiry, share?.ExpiresAt, DateTime.UtcNow);
        if (share is not null && share.ExpiresAt == expiresAt)
        {
            return;
        }

        if (share is null)
        {
            db.SessionShares.Add(new SessionShare { SessionId = sessionId, UserId = userId, GrantedById = actor.Id, GrantedAt = DateTime.UtcNow, ExpiresAt = expiresAt });
        }
        else
        {
            share.ExpiresAt = expiresAt;
            share.GrantedById = actor.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.SessionShared, $"Session {session.Title}", user.Username + AccessTimeLimits.Describe(expiresAt), cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, SessionId: sessionId, UserId: userId));
        events.SessionChanged(sessionId, session.WorkspaceAgentId);
    }

    /// <summary>Ends a conversation's share: the owner for anyone, or someone for their own.</summary>
    public async Task UnshareSessionAsync(CurrentUser actor, Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken) ?? throw new KeyNotFoundException("Session not found.");
        if (userId != actor.Id)
        {
            RequireOwner(actor, session.OwnerId, "conversation");
        }

        var share = await db.SessionShares.Include(s => s.User).FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("This conversation isn't shared with that user.");
        db.SessionShares.Remove(share);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.SessionUnshared, $"Session {session.Title}", userId == actor.Id ? $"{share.User?.Username} left" : share.User?.Username, cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, SessionId: sessionId, UserId: userId));
        events.SessionChanged(sessionId, session.WorkspaceAgentId);
    }

    private static void RequireOwner(CurrentUser actor, Guid ownerId, string what)
    {
        if (ownerId != actor.Id && !actor.Has(UserRoles.ConversationManager))
        {
            throw new UnauthorizedAccessException($"Only the owner or a conversation manager can share this {what}.");
        }
    }

    private static async Task<User> RequireOtherUserAsync(ManagerDbContext db, CurrentUser actor, Guid userId, CancellationToken cancellationToken)
    {
        if (userId == actor.Id)
        {
            throw new ArgumentException("It's yours already.");
        }

        return await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.Disabled, cancellationToken)
            ?? throw new ArgumentException("Choose an enabled user.");
    }
}
