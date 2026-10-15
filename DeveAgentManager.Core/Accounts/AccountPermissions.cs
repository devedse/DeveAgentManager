using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Accounts;

public sealed partial class AccountService
{
    public async Task<CurrentUser> GetActorAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && !u.Disabled, ct)
            ?? throw new UnauthorizedAccessException("User is disabled or removed.");
        return CurrentUser.From(user);
    }

    public async Task RequireLoginHarnessAsync(CurrentUser actor, Guid id, string harnessId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (a, _) = await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        if (a.HarnessId != harnessId) throw new ArgumentException("The login belongs to a different harness.");
    }

    public async Task RemoveLoginFolderAsync(Runner runner, string folder)
    {
        try { await shell.RunAsync(runner, $"rm -rf \"{folder}\"", TimeSpan.FromSeconds(20), CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Login terminal cleanup failed on Runner {Runner}", runner.Id); }
    }

    internal static async Task<bool> CanUseForAgentAsync(ManagerDbContext db, Agent agent, Guid id, CancellationToken ct) =>
        await db.Users.AnyAsync(u => u.Id == agent.OwnerId && !u.Disabled, ct)
        && (await db.AccountGrants.AnyAsync(g => g.AccountId == id && g.UserId == agent.OwnerId && g.Access >= AccountAccess.Use, ct)
            || await db.AgentLoginGrants.AnyAsync(g => g.AgentId == agent.Id && g.OwnerId == agent.OwnerId && g.AccountId == id, ct));

    internal static async Task RequireManagersRemainAsync(ManagerDbContext db, Guid userId, CancellationToken ct)
    {
        var names = await db.Accounts.Where(a => a.Grants.Any(g => g.UserId == userId && g.Access == AccountAccess.Manage)
            && !a.Grants.Any(g => g.UserId != userId && g.Access == AccountAccess.Manage && !g.User.Disabled)).Select(a => a.Name).ToListAsync(ct);
        if (names.Count > 0) throw new InvalidOperationException("Assign another manager or remove these harness logins before disabling or deleting this user: " + string.Join(", ", names));
    }

    private async Task<(Account Account, CurrentUser User)> RequireAsync(ManagerDbContext db, CurrentUser actor, Guid id, AccountAccess needed, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actor.Id && !u.Disabled, ct)
            ?? throw new UnauthorizedAccessException("User is disabled or removed.");
        var account = await db.Accounts.Include(a => a.Grants).ThenInclude(g => g.User).SingleOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException("Harness login not found.");
        var own = account.Grants.FirstOrDefault(g => g.UserId == user.Id)?.Access ?? AccountAccess.None;
        var manages = (user.Roles & UserRoles.HarnessLoginManager) != 0;
        if (own == AccountAccess.None && !manages) throw new KeyNotFoundException("Harness login not found.");
        if (needed == AccountAccess.Use ? own < needed || !account.Enabled : own < needed && !manages)
            throw new UnauthorizedAccessException("This needs " + needed + " access to the harness login.");
        return (account, CurrentUser.From(user));
    }

    public async Task<List<AccountDto>> ListVisibleAsync(CurrentUser actor, bool all, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == actor.Id && !u.Disabled, ct);
        var admin = (user.Roles & UserRoles.HarnessLoginManager) != 0;
        if (all && !admin) throw new UnauthorizedAccessException("Harness login manager role required.");
        var defaults = await db.UserHarnessDefaults.Where(p => p.UserId == actor.Id).Select(p => p.AccountId).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().Include(a => a.Grants).ThenInclude(g => g.User)
            .Where(a => all || a.Grants.Any(g => g.UserId == actor.Id)).OrderBy(a => a.HarnessId).ThenBy(a => a.Name).ToListAsync(ct);
        var result = new List<AccountDto>();
        foreach (var a in accounts)
        {
            var grant = a.Grants.FirstOrDefault(g => g.UserId == actor.Id)?.Access ?? AccountAccess.None;
            var manages = admin || grant == AccountAccess.Manage;
            var usage = RelevantAgents(db, a.Id);
            if (!manages) usage = usage.Where(x => x.OwnerId == actor.Id);
            result.Add(new(a.Id, a.HarnessId, a.Name, a.Kind, defaults.Contains(a.Id), a.Version, await usage.CountAsync(ct),
                a.CreatedAt, a.UpdatedAt, a.RenewalProblem ?? (GetHarness(a.HarnessId).RenewsAccounts && a.Kind == AccountKind.Subscription && !a.ManagedRenewal
                    ? "Reconnect so the Manager keeps it signed in" : null), grant, manages, a.Enabled,
                manages ? await db.AccountCleanups.CountAsync(c => c.AccountId == a.Id, ct) + await usage.CountAsync(x => x.LoginStopPending, ct) : 0,
                manages ? a.Grants.Where(g => g.Access == AccountAccess.Manage).Select(g => g.User.DisplayName + (g.User.Disabled ? " (disabled)" : "")).ToList() : null,
                manages ? a.Grants.Count : 0));
        }
        return result;
    }

    public async Task<List<LoginCleanupDto>> PendingAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var current = await db.Users.SingleOrDefaultAsync(u => u.Id == actor.Id && !u.Disabled, ct);
        if (current is null || (current.Roles & UserRoles.HarnessLoginManager) == 0) throw new UnauthorizedAccessException("Harness login manager role required.");
        var cleanup = await (from c in db.AccountCleanups
            join r in db.Runners on c.RunnerId equals r.Id
            join a in db.Accounts on c.AccountId equals a.Id into logins
            from login in logins.DefaultIfEmpty()
            select new LoginCleanupDto(login == null ? "Removed login" : login.Name, r.Name, "Remove credential copy")).ToListAsync(ct);
        cleanup.AddRange(await db.Agents.Where(a => a.LoginStopPending)
            .Select(a => new LoginCleanupDto("Revoked agent login", a.Runner!.Name, "Stop affected harness")).ToListAsync(ct));
        return cleanup;
    }

    internal static IQueryable<Agent> RelevantAgents(ManagerDbContext db, Guid id) => db.Agents.Where(a =>
        a.AccountId == id || a.RunningAccountId == id && (a.State == AgentState.Running || a.State == AgentState.Starting || a.LoginStopPending) || a.AccountId == null
        && db.UserHarnessDefaults.Any(p => p.UserId == a.OwnerId && p.HarnessId == a.HarnessId && p.AccountId == id));

    public async Task<List<AccountGrantDto>> GrantsAsync(CurrentUser actor, Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (a, _) = await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        var rows = a.Grants.OrderBy(g => g.User.Username).Select(g => new AccountGrantDto(g.UserId, g.User.Username, g.User.DisplayName, g.Access, g.User.Disabled)).ToList();
        rows.AddRange(await (from g in db.AgentLoginGrants
            join u in db.Users on g.OwnerId equals u.Id
            where g.AccountId == id
            select new AccountGrantDto(u.Id, u.Username, u.DisplayName, AccountAccess.Use, u.Disabled, g.AgentId)).ToListAsync(ct));
        return rows;
    }

    public async Task<AccountImpactDto> ImpactAsync(CurrentUser actor, Guid id, Guid? userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Use recipients can inspect only their own removal impact.
        var current = await db.Users.SingleAsync(u => u.Id == actor.Id && !u.Disabled, ct);
        var manager = (current.Roles & UserRoles.HarnessLoginManager) != 0
            || await db.AccountGrants.AnyAsync(g => g.AccountId == id && g.UserId == actor.Id && g.Access == AccountAccess.Manage, ct);
        if (!manager)
        {
            if (!await db.AccountGrants.AnyAsync(g => g.AccountId == id && g.UserId == actor.Id, ct)) throw new KeyNotFoundException("Harness login not found.");
            if (userId != actor.Id) throw new UnauthorizedAccessException("Login management required.");
        }
        var visible = await AccessControl.VisibleAgents(db, CurrentUser.From(current)).Select(a => a.Id).ToListAsync(ct);
        var agents = await RelevantAgents(db, id).Include(a => a.Runner).Include(a => a.Owner)
            .Where(a => userId == null || a.OwnerId == userId).ToListAsync(ct);
        var rows = agents.Select(a => new AccountUsageDto(visible.Contains(a.Id) ? a.Id : null, visible.Contains(a.Id) ? a.Name : null,
            a.Owner?.DisplayName ?? "User", a.Runner?.Name ?? "Runner", a.State is AgentState.Running or AgentState.Starting, a.LoginStopPending)).ToList();
        return new(rows, manager ? await db.AccountCleanups.CountAsync(c => c.AccountId == id, ct) : 0);
    }

    public async Task SetGrantAsync(CurrentUser actor, Guid id, Guid userId, AccountAccess? level, CancellationToken ct)
    {
        if (level is not (null or AccountAccess.Use or AccountAccess.Manage)) throw new ArgumentException("Choose Use or Manage.");
        using var lease = await AccountCoordination.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var selfLeave = userId == actor.Id && level == null;
        Account a;
        if (selfLeave)
        {
            a = await db.Accounts.Include(x => x.Grants).ThenInclude(g => g.User).SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new KeyNotFoundException("Harness login not found.");
            if (!a.Grants.Any(g => g.UserId == actor.Id && !g.User.Disabled)) throw new KeyNotFoundException("Harness login not found.");
        }
        else (a, _) = await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        var target = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && (!u.Disabled || level == null), ct)
            ?? throw new ArgumentException("Choose an enabled user.");
        var existing = a.Grants.FirstOrDefault(g => g.UserId == userId);
        if (existing?.Access == level) return;
        if (existing?.Access == AccountAccess.Manage && level != AccountAccess.Manage
            && !a.Grants.Any(g => g.UserId != userId && g.Access == AccountAccess.Manage && !g.User.Disabled))
            throw new InvalidOperationException("Add another enabled manager before removing the last manager.");
        if (level == null)
        {
            var affected = await RelevantAgents(db, id).Where(x => x.OwnerId == userId).ToListAsync(ct);
            Block(affected);
            if (existing is not null) db.AccountGrants.Remove(existing);
            db.UserHarnessDefaults.RemoveRange(await db.UserHarnessDefaults.Where(p => p.AccountId == id && p.UserId == userId).ToListAsync(ct));
            db.AgentLoginGrants.RemoveRange(await db.AgentLoginGrants.Where(g => g.AccountId == id && g.OwnerId == userId).ToListAsync(ct));
        }
        else if (existing is not null) existing.Access = level.Value;
        else a.Grants.Add(new() { UserId = userId, Access = level.Value, GrantedById = actor.Id, GrantedAt = DateTime.UtcNow });
        a.AccessVersion++;
        await db.SaveChangesAsync(ct);
        await ChangedAsync(actor, id, "LoginAccessChanged", target.Username + ": " + (level?.ToString() ?? "removed"), ct);
        await EnforceLockedAsync(ct);
    }

    public async Task RemoveLegacyGrantAsync(CurrentUser actor, Guid id, Guid agentId, CancellationToken ct)
    {
        using var lease = await AccountCoordination.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        var grant = await db.AgentLoginGrants.SingleOrDefaultAsync(g => g.AccountId == id && g.AgentId == agentId, ct)
            ?? throw new KeyNotFoundException("Agent-specific grant not found.");
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is not null && !await db.AccountGrants.AnyAsync(g => g.AccountId == id && g.UserId == agent.OwnerId, ct)) Block([agent]);
        db.AgentLoginGrants.Remove(grant);
        await db.SaveChangesAsync(ct);
        await ChangedAsync(actor, id, "LoginAgentGrantRemoved", agentId.ToString(), ct);
        await EnforceLockedAsync(ct);
    }

    public async Task SetDefaultAsync(CurrentUser actor, Guid id, CancellationToken ct)
    {
        using var lease = await AccountCoordination.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (a, _) = await RequireAsync(db, actor, id, AccountAccess.Use, ct);
        var pref = await db.UserHarnessDefaults.FindAsync([actor.Id, a.HarnessId], ct);
        if (pref is null) db.UserHarnessDefaults.Add(new() { UserId = actor.Id, HarnessId = a.HarnessId, AccountId = id });
        else pref.AccountId = id;
        await db.SaveChangesAsync(ct);
        events.Publish(new(ManagerEventKind.SettingsChanged, UserId: actor.Id));
    }

    public async Task RenameAsync(CurrentUser actor, Guid id, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) throw new ArgumentException("Use a name of 1–120 characters.");
        using var lease = await AccountCoordination.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (a, _) = await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        a.Name = name.Trim(); a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await ChangedAsync(actor, id, "LoginRenamed", null, ct);
    }

    public async Task ReplaceSecretAsync(CurrentUser actor, Guid id, AccountKind kind, string secret, CancellationToken ct)
    {
        using var lease = await AccountCoordination.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        await ReplaceSecretAsync(id, kind, secret, ct);
        await ChangedAsync(actor, id, "LoginReconnected", null, ct);
    }

    public async Task SetEnabledAsync(CurrentUser actor, Guid id, bool enabled, CancellationToken ct)
    {
        using var lease = await AccountCoordination.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (a, _) = await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        if (enabled && !a.Grants.Any(g => g.Access == AccountAccess.Manage && !g.User.Disabled))
            throw new InvalidOperationException("Assign an enabled manager first.");
        a.Enabled = enabled; a.AccessVersion++;
        if (enabled) db.AccountCleanups.RemoveRange(await db.AccountCleanups.Where(c => c.AccountId == id).ToListAsync(ct));
        if (!enabled)
        {
            Block(await RelevantAgents(db, id).ToListAsync(ct));
            db.UserHarnessDefaults.RemoveRange(await db.UserHarnessDefaults.Where(p => p.AccountId == id).ToListAsync(ct));
            await QueueCleanupAsync(db, id, ct);
        }
        await db.SaveChangesAsync(ct);
        await ChangedAsync(actor, id, enabled ? "LoginEnabled" : "LoginDisabled", null, ct);
        await EnforceLockedAsync(ct);
    }

    public async Task DeleteAsync(CurrentUser actor, Guid id, CancellationToken ct)
    {
        using var lease = await AccountCoordination.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (a, _) = await RequireAsync(db, actor, id, AccountAccess.Manage, ct);
        Block(await RelevantAgents(db, id).ToListAsync(ct));
        await QueueCleanupAsync(db, id, ct);
        db.Accounts.Remove(a);
        await db.SaveChangesAsync(ct);
        await ChangedAsync(actor, id, "LoginDeleted", null, ct);
        await EnforceLockedAsync(ct);
    }

    private static void Block(IEnumerable<Agent> agents)
    {
        foreach (var a in agents)
        {
            a.LoginBlocked = true;
            a.LoginStopPending |= a.State is AgentState.Running or AgentState.Starting;
            a.StateMessage = "Harness login access ended. Choose a permitted login.";
        }
    }

    private static async Task QueueCleanupAsync(ManagerDbContext db, Guid id, CancellationToken ct)
    {
        var queued = await db.AccountCleanups.Where(c => c.AccountId == id).Select(c => c.RunnerId).ToListAsync(ct);
        foreach (var runner in await db.Runners.Select(r => r.Id).ToListAsync(ct))
            if (!queued.Contains(runner)) db.AccountCleanups.Add(new() { AccountId = id, RunnerId = runner });
    }

    private async Task ChangedAsync(CurrentUser actor, Guid id, string action, string? detail, CancellationToken ct)
    {
        if (audit is not null) await audit.RecordAsync(actor, action, "Harness login " + id, detail, ct);
        // Contains no login identity or credential. Invalidates selectors and rechecks live connections.
        events.Publish(new(ManagerEventKind.SettingsChanged));
        events.Publish(new(ManagerEventKind.AccessChanged, UserId: actor.Id));
    }

    public async Task EnforceAsync(CancellationToken ct)
    {
        using var lease = await AccountCoordination.LockAsync(ct);
        await EnforceLockedAsync(ct);
    }

    private async Task EnforceLockedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agents = await db.Agents.Include(a => a.Runner).Where(a => a.LoginStopPending
            || (a.State == AgentState.Running || a.State == AgentState.Starting) && a.RunningAccountId != null).ToListAsync(ct);
        foreach (var a in agents)
        {
            if (!a.LoginStopPending && a.RunningAccountId is { } id && (
                !await db.Accounts.AnyAsync(x => x.Id == id && x.Enabled, ct) || !await CanUseForAgentAsync(db, a, id, ct)))
            {
                Block([a]);
                await db.SaveChangesAsync(ct); // durable before remote work
            }
            if (!a.LoginStopPending || a.Runner?.State != RunnerState.Ready) continue;
            try
            {
                var target = ShellQuote.Quote("=" + a.TmuxSession + ":");
                var stop = AgentService.BuildStopScript(target) + $"\nif tmux has-session -t {target} 2>/dev/null && [ \"$(tmux display-message -p -t {target} '#{{pane_dead}}')\" != 1 ]; then exit 1; fi";
                (await shell.RunAsync(a.Runner, stop, TimeSpan.FromSeconds(30), ct))
                    .EnsureSuccess("Stopping agent after login access ended");
                a.State = AgentState.Stopped; a.LoginStopPending = false; a.RunningAccountId = null; a.Working = false;
                await db.SaveChangesAsync(ct);
                events.AgentChanged(a.RunnerId, a.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Login access ended; stopping agent {Agent} is pending on Runner {Runner}", a.Id, a.RunnerId);
            }
        }
        foreach (var work in await db.AccountCleanups.ToListAsync(ct))
        {
            var runner = await db.Runners.FindAsync([work.RunnerId], ct);
            if (runner?.State != RunnerState.Ready) continue;
            try
            {
                (await shell.RunAsync(runner, $"rm -rf \"{Folder(work.AccountId)}\"", TimeSpan.FromSeconds(20), ct)).EnsureSuccess("Removing login copies");
                db.AccountCleanups.Remove(work);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Login cleanup pending on Runner {Runner}", runner.Id); }
        }
    }
}
