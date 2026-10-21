using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>
/// Who has access to a Runner: its owner, and members with <see cref="RunnerAccess.Use"/> or <see cref="RunnerAccess.Manage"/>.
/// People who manage a Runner give and take access; runner managers change its owner. Everyone with access shares the
/// Runner Account, so access to a Runner means trusting each other with what is on it (docs/users-plan.md).
/// </summary>
public sealed class RunnerMembers(
    IDbContextFactory<ManagerDbContext> dbFactory,
    RunnerUsers users,
    AgentService agents,
    AccessControl access,
    AuditLog audit,
    ManagerEvents events,
    ILogger<RunnerMembers> logger)
{
    public async Task<List<RunnerMember>> ListAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.RunnerMembers.AsNoTracking().Include(m => m.User).Where(m => m.RunnerId == runnerId)
            .OrderBy(m => m.User!.Username).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The people without access to the Runner whom agents on it are shared with, and how, those who can prompt one
    /// (read-write) first. Conversation managers reach every agent through their role and aren't listed.
    /// </summary>
    public async Task<List<RunnerAgentAccessDto>> ListAgentAccessAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var shares = await (from share in db.AgentShares
                            join agent in db.Agents on share.AgentId equals agent.Id
                            where agent.RunnerId == runnerId
                                && !db.Runners.Any(r => r.Id == runnerId && r.OwnerId == share.UserId)
                                && !db.RunnerMembers.Any(m => m.RunnerId == runnerId && m.UserId == share.UserId)
                            select new
                            {
                                share.UserId, share.User!.Username, share.User.DisplayName, share.User.Disabled, share.Access,
                                AgentId = agent.Id, AgentName = agent.Name, OwnerName = agent.Owner!.DisplayName,
                            }).ToListAsync(cancellationToken);
        return shares.GroupBy(s => s.UserId)
            .Select(g => new RunnerAgentAccessDto(g.Key, g.First().Username, g.First().DisplayName, g.First().Disabled,
                g.OrderByDescending(s => s.Access).ThenBy(s => s.AgentName, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new RunnerAgentShareDto(s.AgentId, s.AgentName, s.OwnerName, s.Access)).ToList()))
            .OrderByDescending(p => p.Agents.Max(a => a.Access)).ThenBy(p => p.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Gives <paramref name="userId"/> Use or Manage access, or changes theirs.</summary>
    public async Task SetAsync(CurrentUser actor, Guid runnerId, Guid userId, RunnerAccess access, CancellationToken cancellationToken = default)
    {
        if (access is not (RunnerAccess.Use or RunnerAccess.Manage))
        {
            throw new ArgumentException("Choose Use or Manage.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.Include(r => r.Members).FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            ?? throw new KeyNotFoundException("Runner not found.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.Disabled, cancellationToken)
            ?? throw new ArgumentException("Choose an enabled user.");
        if (runner.OwnerId == userId)
        {
            throw new InvalidOperationException($"{user.Username} owns this Runner.");
        }

        var member = runner.Members.FirstOrDefault(m => m.UserId == userId);
        if (member?.Access == access)
        {
            return;
        }

        if (member is null)
        {
            member = new RunnerMember { RunnerId = runnerId, UserId = userId };
            db.RunnerMembers.Add(member);
        }

        member.Access = access;
        member.GrantedById = actor.Id;
        member.GrantedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.RunnerAccessChanged, $"Runner {runner.Name}", $"{user.Username}: {access}", cancellationToken);
        Changed(runner, userId);
    }

    /// <summary>
    /// Takes away a member's access (people who manage the Runner, or the member themselves). Their agents on it are
    /// removed, their conversations stay theirs; their files and keys are removed from the Runner.
    /// </summary>
    public async Task RemoveAsync(CurrentUser actor, Guid runnerId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId != actor.Id && await access.RunnerAsync(actor, runnerId, cancellationToken) < RunnerAccess.Manage)
        {
            throw new UnauthorizedAccessException("Only people who manage this Runner can take someone's access away.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.Include(r => r.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            ?? throw new KeyNotFoundException("Runner not found.");
        if (runner.OwnerId == userId)
        {
            throw new InvalidOperationException("The owner keeps their access. A runner manager can give the Runner to someone else.");
        }

        var member = runner.Members.FirstOrDefault(m => m.UserId == userId) ?? throw new KeyNotFoundException("This user has no access to the Runner.");
        foreach (var agentId in await db.Agents.Where(a => a.RunnerId == runnerId && a.OwnerId == userId).Select(a => a.Id).ToListAsync(cancellationToken))
        {
            await agents.DeleteAsync(agentId, cancellationToken);
        }

        db.RunnerMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.RunnerAccessRemoved, $"Runner {runner.Name}",
            userId == actor.Id ? $"{member.User?.Username} left" : member.User?.Username, cancellationToken);
        Changed(runner, userId);
    }

    /// <summary>Gives the Runner to someone else (runner managers). The previous owner keeps Manage access.</summary>
    public async Task ChangeOwnerAsync(CurrentUser actor, Guid runnerId, Guid ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.Include(r => r.Members).Include(r => r.Owner).FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            ?? throw new KeyNotFoundException("Runner not found.");
        var owner = await db.Users.FirstOrDefaultAsync(u => u.Id == ownerId && !u.Disabled, cancellationToken)
            ?? throw new ArgumentException("Choose an enabled user.");
        if (runner.OwnerId == ownerId)
        {
            return;
        }

        var previous = runner.OwnerId;
        if (runner.Members.FirstOrDefault(m => m.UserId == ownerId) is { } membership)
        {
            db.RunnerMembers.Remove(membership);
        }

        db.RunnerMembers.Add(new RunnerMember { RunnerId = runnerId, UserId = previous, Access = RunnerAccess.Manage, GrantedById = actor.Id, GrantedAt = DateTime.UtcNow });
        runner.OwnerId = ownerId;
        runner.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(actor, AuditActions.RunnerOwnerChanged, $"Runner {runner.Name}", $"{runner.Owner?.Username} → {owner.Username}", cancellationToken);
        Changed(runner, ownerId);
        events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, runnerId, UserId: previous));
    }

    /// <summary>Tells the pages, and puts the Runner's user files in line with its access now (in the background).</summary>
    private void Changed(Runner runner, Guid userId)
    {
        events.Publish(new ManagerEvent(ManagerEventKind.AccessChanged, runner.Id, UserId: userId));
        events.RunnerChanged(runner.Id);
        if (runner.State != RunnerState.Ready)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await users.SyncAsync(runner);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not update the people with access on {Runner}", runner.Name);
            }
        });
    }
}
