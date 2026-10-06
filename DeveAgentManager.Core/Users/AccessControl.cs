using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Users;

/// <summary>
/// What a user may do with Runners, agents, conversations and harness logins (see docs/users-plan.md):
/// <list type="bullet">
/// <item>A Runner: its owner and runner managers have <see cref="RunnerAccess.Owner"/>; others have what their membership
/// gives (<see cref="RunnerAccess.Use"/> or <see cref="RunnerAccess.Manage"/>).</item>
/// <item>An agent: its owner has <see cref="AgentAccess.Owner"/>, users it is shared with have the share's level, and who
/// manages its Runner sees it is there (<see cref="AgentAccess.Listed"/>). Writing needs Use access to the Runner as
/// well: without it, an agent can only be read.</item>
/// <item>A conversation: its owner, users it is shared with, and users its workspace's agent is shared with (read-only).</item>
/// <item>A harness login: only its owner.</item>
/// </list>
/// Roles never give access to other users' agents or conversations.
/// </summary>
public sealed class AccessControl(IDbContextFactory<ManagerDbContext> dbFactory)
{
    public static RunnerAccess ForRunner(CurrentUser user, Guid ownerId, IEnumerable<RunnerMember> members)
    {
        if (ownerId == user.Id || user.Has(UserRoles.RunnerManager))
        {
            return RunnerAccess.Owner;
        }

        return members.FirstOrDefault(m => m.UserId == user.Id)?.Access ?? RunnerAccess.None;
    }

    public static AgentAccess ForAgent(CurrentUser user, Guid ownerId, IEnumerable<AgentShare> shares, RunnerAccess runner)
    {
        var access = ownerId == user.Id ? AgentAccess.Owner : shares.FirstOrDefault(s => s.UserId == user.Id)?.Access ?? AgentAccess.None;
        if (access >= AgentAccess.Write && runner < RunnerAccess.Use)
        {
            access = AgentAccess.Read;
        }

        return runner >= RunnerAccess.Manage && access < AgentAccess.Listed ? AgentAccess.Listed : access;
    }

    public static SessionAccess ForSession(CurrentUser user, Guid ownerId, IEnumerable<SessionShare> shares, AgentAccess workspace)
    {
        if (ownerId == user.Id)
        {
            return SessionAccess.Owner;
        }

        return shares.Any(s => s.UserId == user.Id) || workspace >= AgentAccess.Read ? SessionAccess.Read : SessionAccess.None;
    }

    /// <summary>The Runners the user can see, for lists.</summary>
    public static IQueryable<Runner> VisibleRunners(ManagerDbContext db, CurrentUser user) =>
        user.Has(UserRoles.RunnerManager) ? db.Runners : db.Runners.Where(r => r.OwnerId == user.Id || r.Members.Any(m => m.UserId == user.Id));

    /// <summary>The agents the user can see (at least <see cref="AgentAccess.Listed"/>), for lists.</summary>
    public static IQueryable<Agent> VisibleAgents(ManagerDbContext db, CurrentUser user)
    {
        if (user.Has(UserRoles.RunnerManager))
        {
            return db.Agents;
        }

        return db.Agents.Where(a => a.OwnerId == user.Id || a.Shares.Any(s => s.UserId == user.Id)
            || db.Runners.Any(r => r.Id == a.RunnerId && (r.OwnerId == user.Id || r.Members.Any(m => m.UserId == user.Id && m.Access == RunnerAccess.Manage))));
    }

    /// <summary>The conversations the user can read, for lists.</summary>
    public static IQueryable<StoredSession> VisibleSessions(ManagerDbContext db, CurrentUser user) =>
        db.Sessions.Where(s => s.OwnerId == user.Id || s.Shares.Any(x => x.UserId == user.Id)
            || (s.WorkspaceAgentId != null && db.AgentShares.Any(x => x.AgentId == s.WorkspaceAgentId && x.UserId == user.Id)));

    public async Task<RunnerAccess> RunnerAsync(CurrentUser user, Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await RunnerAsync(db, user, runnerId, cancellationToken);
    }

    public async Task<AgentAccess> AgentAsync(CurrentUser user, Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await AgentAsync(db, user, agentId, cancellationToken);
    }

    public async Task<SessionAccess> SessionAsync(CurrentUser user, Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId)
            .Select(s => new { s.OwnerId, s.WorkspaceAgentId, Shares = s.Shares.Where(x => x.UserId == user.Id).ToList() })
            .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return SessionAccess.None;
        }

        var workspace = session.WorkspaceAgentId is { } agentId && session.OwnerId != user.Id && session.Shares.Count == 0
            ? await AgentAsync(db, user, agentId, cancellationToken)
            : AgentAccess.None;
        return ForSession(user, session.OwnerId, session.Shares, workspace);
    }

    public async Task<bool> OwnsAccountAsync(CurrentUser user, Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts.AnyAsync(a => a.Id == accountId && a.OwnerId == user.Id, cancellationToken);
    }

    /// <summary>
    /// The users a change concerns, for live updates; null for everyone. Removals go to everyone: what was removed can't
    /// be looked up any more, and the event carries nothing but its id.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> AudienceAsync(ManagerEvent change, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var audience = new HashSet<Guid>();
        switch (change.Kind)
        {
            case ManagerEventKind.RunnerRemoved or ManagerEventKind.AgentRemoved or ManagerEventKind.SessionRemoved or ManagerEventKind.UserRemoved:
                return null;
            case ManagerEventKind.SettingsChanged when change.UserId is null:
                return null;
            case ManagerEventKind.UserChanged:
                audience.UnionWith(await UsersWithRoleAsync(db, UserRoles.UserManager, cancellationToken));
                break;
        }

        if (change.UserId is { } userId)
        {
            audience.Add(userId);
        }

        if (change.SessionId is { } sessionId)
        {
            audience.UnionWith(await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.OwnerId).ToListAsync(cancellationToken));
            audience.UnionWith(await db.SessionShares.Where(s => s.SessionId == sessionId).Select(s => s.UserId).ToListAsync(cancellationToken));
            audience.UnionWith(await db.Sessions.Where(s => s.Id == sessionId && s.WorkspaceAgentId != null)
                .SelectMany(s => db.AgentShares.Where(x => x.AgentId == s.WorkspaceAgentId).Select(x => x.UserId)).ToListAsync(cancellationToken));
        }

        if (change.AgentId is { } agentId)
        {
            audience.UnionWith(await db.Agents.Where(a => a.Id == agentId).Select(a => a.OwnerId).ToListAsync(cancellationToken));
            audience.UnionWith(await db.AgentShares.Where(s => s.AgentId == agentId).Select(s => s.UserId).ToListAsync(cancellationToken));
        }

        if (change.RunnerId is { } runnerId)
        {
            // An agent's change concerns the people who see its Runner's agents; a Runner's change everyone with access.
            var agentChange = change.Kind == ManagerEventKind.AgentChanged;
            audience.UnionWith(await db.Runners.Where(r => r.Id == runnerId).Select(r => r.OwnerId).ToListAsync(cancellationToken));
            audience.UnionWith(await db.RunnerMembers.Where(m => m.RunnerId == runnerId && (!agentChange || m.Access == RunnerAccess.Manage))
                .Select(m => m.UserId).ToListAsync(cancellationToken));
            audience.UnionWith(await UsersWithRoleAsync(db, UserRoles.RunnerManager, cancellationToken));
        }

        return audience;
    }

    private static async Task<RunnerAccess> RunnerAsync(ManagerDbContext db, CurrentUser user, Guid runnerId, CancellationToken cancellationToken)
    {
        var runner = await db.Runners.AsNoTracking().Where(r => r.Id == runnerId)
            .Select(r => new { r.OwnerId, Members = r.Members.Where(m => m.UserId == user.Id).ToList() })
            .FirstOrDefaultAsync(cancellationToken);
        return runner is null ? RunnerAccess.None : ForRunner(user, runner.OwnerId, runner.Members);
    }

    private static async Task<AgentAccess> AgentAsync(ManagerDbContext db, CurrentUser user, Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Where(a => a.Id == agentId)
            .Select(a => new { a.OwnerId, a.RunnerId, Shares = a.Shares.Where(s => s.UserId == user.Id).ToList() })
            .FirstOrDefaultAsync(cancellationToken);
        if (agent is null)
        {
            return AgentAccess.None;
        }

        return ForAgent(user, agent.OwnerId, agent.Shares, await RunnerAsync(db, user, agent.RunnerId, cancellationToken));
    }

    private static async Task<List<Guid>> UsersWithRoleAsync(ManagerDbContext db, UserRoles role, CancellationToken cancellationToken) =>
        (await db.Users.Where(u => !u.Disabled).Select(u => new { u.Id, u.Roles }).ToListAsync(cancellationToken))
            .Where(u => (u.Roles & role) == role).Select(u => u.Id).ToList();
}
