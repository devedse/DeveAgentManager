using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Users;

/// <summary>
/// Agents and conversations their owners share with other users. An agent is shared read-only (watch its terminal, read
/// its conversations) or read-write (also type and prompt it, with the harness login and git identity it runs with; no
/// access to its Runner is needed). A conversation is
/// shared read-only. Owners change or end a share at any time; anyone can leave a share they got.
/// </summary>
public sealed class Sharing(IDbContextFactory<ManagerDbContext> dbFactory, AuditLog audit, ManagerEvents events)
{
    public async Task<List<AgentShare>> ListAgentSharesAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentShares.AsNoTracking().Include(s => s.User).Where(s => s.AgentId == agentId).OrderBy(s => s.User!.Username).ToListAsync(cancellationToken);
    }

    /// <summary>Shares the owner's agent with <paramref name="userId"/>, or changes how.</summary>
    public async Task ShareAgentAsync(CurrentUser actor, Guid agentId, Guid userId, AgentAccess access, CancellationToken cancellationToken = default)
    {
        if (access is not (AgentAccess.Read or AgentAccess.Write))
        {
            throw new ArgumentException("Choose read-only or read-write.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Shares).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken) ?? throw new KeyNotFoundException("Agent not found.");
        RequireOwner(actor, agent.OwnerId, "agent");
        var user = await RequireOtherUserAsync(db, actor, userId, cancellationToken);
        var share = agent.Shares.FirstOrDefault(s => s.UserId == userId);
        if (share?.Access == access)
        {
            return;
        }

        if (share is null)
        {
            share = new AgentShare { AgentId = agentId, UserId = userId };
            db.AgentShares.Add(share);
        }

        share.Access = access;
        share.GrantedById = actor.Id;
        share.GrantedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.AgentShared, $"Agent {agent.Name}", $"{user.Username}: {(access == AgentAccess.Write ? "read-write" : "read-only")}", cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, agent.RunnerId, agentId, UserId: userId));
        events.AgentChanged(agent.RunnerId, agentId);
    }

    /// <summary>Ends a share: the owner for anyone, or someone for their own.</summary>
    public async Task UnshareAgentAsync(CurrentUser actor, Guid agentId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken) ?? throw new KeyNotFoundException("Agent not found.");
        if (userId != actor.Id)
        {
            RequireOwner(actor, agent.OwnerId, "agent");
        }

        var share = await db.AgentShares.Include(s => s.User).FirstOrDefaultAsync(s => s.AgentId == agentId && s.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("This agent isn't shared with that user.");
        db.AgentShares.Remove(share);
        // Their git identity goes with their share: the next start uses the owner's, and a harness still running with
        // theirs is stopped (AgentService.EnforceGitIdentitiesAsync).
        if (agent.GitIdentityUserId == userId)
        {
            agent.GitIdentityUserId = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.AgentUnshared, $"Agent {agent.Name}", userId == actor.Id ? $"{share.User?.Username} left" : share.User?.Username, cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, agent.RunnerId, agentId, UserId: userId));
        events.AgentChanged(agent.RunnerId, agentId);
    }

    public async Task<List<SessionShare>> ListSessionSharesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SessionShares.AsNoTracking().Include(s => s.User).Where(s => s.SessionId == sessionId).OrderBy(s => s.User!.Username).ToListAsync(cancellationToken);
    }

    /// <summary>Shares the owner's conversation read-only with <paramref name="userId"/>.</summary>
    public async Task ShareSessionAsync(CurrentUser actor, Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.Include(s => s.Shares).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken) ?? throw new KeyNotFoundException("Session not found.");
        RequireOwner(actor, session.OwnerId, "conversation");
        var user = await RequireOtherUserAsync(db, actor, userId, cancellationToken);
        if (session.Shares.Any(s => s.UserId == userId))
        {
            return;
        }

        db.SessionShares.Add(new SessionShare { SessionId = sessionId, UserId = userId, GrantedById = actor.Id, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.SessionShared, $"Session {session.Title}", user.Username, cancellationToken);
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
