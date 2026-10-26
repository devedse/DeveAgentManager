using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Users;

/// <summary>
/// The people who sign in: passwords, roles, disabling and deleting. Who may change whom:
/// <list type="bullet">
/// <item>Managing users needs <see cref="UserRoles.UserManager"/>.</item>
/// <item>You can only grant or remove roles you have yourself, and only manage users whose roles you all have (so a
/// user manager can't take over an admin by resetting their password).</item>
/// <item>There is always an enabled user manager left, and nobody disables or deletes themselves.</item>
/// </list>
/// </summary>
public sealed partial class UserService(
    IDbContextFactory<ManagerDbContext> dbFactory,
    AuditLog audit,
    ManagerEvents events,
    ManagerOptions options,
    ILogger<UserService> logger,
    DeveAgentManager.Core.Pets.PetService? pets = null)
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 256;

    private readonly PasswordHasher<User> _hasher = new();

    // What every request checks (enabled, security stamp, roles), without a query each time. Dropped on every change.
    private readonly ConcurrentDictionary<Guid, User?> _cache = new();

    /// <summary>
    /// The user a sign-in cookie belongs to, if it still counts: the user exists, isn't disabled and the cookie's
    /// security stamp is the current one (it changes with the password and when the user is disabled).
    /// </summary>
    public async Task<CurrentUser?> ValidateAsync(Guid userId, string? securityStamp, CancellationToken cancellationToken = default)
    {
        var user = await FindCachedAsync(userId, cancellationToken);
        return user is { Disabled: false } && securityStamp is not null && user.SecurityStamp == securityStamp ? CurrentUser.From(user) : null;
    }

    /// <summary>Checks a username and password; null when they don't match or the user can't sign in now.</summary>
    public async Task<User?> SignInAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (username.Length > 64 || password.Length > MaxPasswordLength) return null;
        var name = username.Trim().ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == name, cancellationToken);
        var now = DateTime.UtcNow;
        if (user is null || user.PasswordHash.Length == 0)
        {
            // As slow as a real check, so the answer doesn't tell which usernames exist.
            _hasher.VerifyHashedPassword(new User(), DummyHash.Value, password);
            await audit.RecordAsync(null, AuditActions.SignInFailed, $"User {Clip(name)}", "Unknown username", cancellationToken);
            return null;
        }

        if (user.Disabled)
        {
            _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            await audit.RecordAsync(null, AuditActions.SignInFailed, $"User {user.Username}", user.Disabled ? "The user is disabled" : "Locked after too many wrong passwords", cancellationToken);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            await audit.RecordAsync(null, AuditActions.SignInFailed, $"User {user.Username}", "Wrong password", cancellationToken);
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
        }

        user.FailedSignIns = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);
        Invalidate(user.Id);

        return user;
    }

    /// <summary>Only a completed password/second-factor sign-in updates the displayed last sign-in time.</summary>
    public async Task RecordSignInAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSignInAt, now), ct);
        Invalidate(userId);
    }

    public async Task<List<User>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(cancellationToken);
    }

    public async Task<User?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }

    public async Task<User> CreateAsync(CurrentUser actor, string username, string? displayName, string password, UserRoles roles, CancellationToken cancellationToken = default)
    {
        RequireUserManager(actor);
        RequireCanGrant(actor, UserRoles.None, roles);
        var name = ValidateUsername(username);
        ValidatePassword(password);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Users.AnyAsync(u => u.Username == name, cancellationToken))
        {
            throw new ArgumentException($"The username '{name}' is taken.");
        }

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = name,
            DisplayName = ValidateDisplayName(displayName, name),
            SecurityStamp = NewStamp(),
            Roles = roles,
            // The user manager chose the password: the new user picks their own when they first sign in.
            MustChangePassword = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = _hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        if (pets is not null) await pets.InitializeAsync(user.Id, cancellationToken);
        await audit.RecordAsync(actor, AuditActions.UserCreated, $"User {user.Username}", $"Roles: {Roles.Describe(roles)}", cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.UserChanged, UserId: user.Id));
        return user;
    }

    public async Task<List<DeveAgentManager.Contracts.LoginDependencyDto>> LoginDependenciesAsync(CurrentUser actor, Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        RequireCanManage(actor, await RequireUserAsync(db, userId, ct));
        return await db.Accounts.Where(a => a.Grants.Any(g => g.UserId == userId && g.Access == AccountAccess.Manage)
            && !a.Grants.Any(g => g.UserId != userId && g.Access == AccountAccess.Manage && !g.User.Disabled))
            .Select(a => new DeveAgentManager.Contracts.LoginDependencyDto(a.Id, a.Name, a.HarnessId,
                (actor.Roles & UserRoles.HarnessLoginManager) != 0 || a.Grants.Any(g => g.UserId == actor.Id && g.Access == AccountAccess.Manage))).ToListAsync(ct);
    }

    /// <summary>Changes a user's name, roles or whether they can sign in. Disabling signs them out everywhere.</summary>
    public async Task<User> UpdateAsync(CurrentUser actor, Guid userId, string? displayName, UserRoles roles, bool disabled, CancellationToken cancellationToken = default)
    {
        using var loginLease = await AccountCoordination.LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireUserAsync(db, userId, cancellationToken);
        RequireCanManage(actor, user);
        RequireCanGrant(actor, user.Roles, roles);
        if (disabled && user.Id == actor.Id)
        {
            throw new InvalidOperationException("You can't disable yourself.");
        }

        var removesUserManager = !user.Disabled && (user.Roles & UserRoles.UserManager) != 0 && (disabled || (roles & UserRoles.UserManager) == 0);
        if (removesUserManager)
        {
            await RequireAnotherUserManagerAsync(db, user.Id, cancellationToken);
        }

        if (disabled && !user.Disabled) await AccountService.RequireManagersRemainAsync(db, userId, cancellationToken);

        var changes = new List<string>();
        var name = ValidateDisplayName(displayName, user.Username);
        if (name != user.DisplayName)
        {
            changes.Add($"name '{user.DisplayName}' → '{name}'");
            user.DisplayName = name;
        }

        if (roles != user.Roles)
        {
            changes.Add($"roles {Roles.Describe(user.Roles)} → {Roles.Describe(roles)}");
            user.Roles = roles;
        }

        if (disabled != user.Disabled)
        {
            changes.Add(disabled ? "disabled" : "enabled");
            user.Disabled = disabled;
            if (disabled)
                foreach (var agent in await db.Agents.Where(a => a.OwnerId == userId && a.RunningAccountId != null).ToListAsync(cancellationToken))
                {
                    agent.LoginBlocked = true;
                    agent.LoginStopPending = agent.State is AgentState.Running or AgentState.Starting;
                    agent.StateMessage = "The user running this harness login was disabled. Choose a permitted login.";
                }
            if (disabled)
                // A disabled user's git identity is no longer used: back to each agent's owner (running harnesses stop).
                await db.Agents.Where(a => a.GitIdentityUserId == userId).ExecuteUpdateAsync(s => s.SetProperty(a => a.GitIdentityUserId, (Guid?)null), cancellationToken);
            user.SecurityStamp = NewStamp();
        }

        if (changes.Count == 0)
        {
            return user;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        Invalidate(user.Id);
        await audit.RecordAsync(actor, AuditActions.UserUpdated, $"User {user.Username}", string.Join("; ", changes), cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.UserChanged, UserId: user.Id));
        return user;
    }

    /// <summary>Sets a password the user manager chose; the user picks their own at the next sign-in. Signs them out everywhere.</summary>
    public async Task ResetPasswordAsync(CurrentUser actor, Guid userId, string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireUserAsync(db, userId, cancellationToken);
        RequireCanManage(actor, user);
        SetPassword(user, password, mustChange: user.Id != actor.Id);
        await db.SaveChangesAsync(cancellationToken);
        Invalidate(user.Id);
        await audit.RecordAsync(actor, AuditActions.UserPasswordReset, $"User {user.Username}", cancellationToken: cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.UserChanged, UserId: user.Id));
    }

    /// <summary>
    /// The signed-in user picks a new password. Other sign-ins of theirs end; returns the user with the new security
    /// stamp, to renew the current sign-in with.
    /// </summary>
    public async Task<User> ChangeOwnPasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireUserAsync(db, userId, cancellationToken);
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            throw new ArgumentException("The current password is wrong.");
        }

        ValidatePassword(newPassword);
        if (newPassword == currentPassword)
        {
            throw new ArgumentException("Choose a password that differs from the current one.");
        }

        SetPassword(user, newPassword, mustChange: false);
        await db.SaveChangesAsync(cancellationToken);
        Invalidate(user.Id);
        await audit.RecordAsync(CurrentUser.From(user), AuditActions.PasswordChanged, $"User {user.Username}", cancellationToken: cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.UserChanged, UserId: user.Id));
        return user;
    }

    /// <summary>
    /// Deletes a user. What they own (Runners, agents and conversations) goes to <paramref name="transferToId"/>, with
    /// their access to other people's Runners (where those agents are). Their harness logins, settings and shares are
    /// removed. Returns the Runners whose access changed, so the files and keys on them can be brought up to date, and their
    /// harness logins, whose copies on the Runners have to go too.
    /// </summary>
    public async Task<DeletedUser> DeleteAsync(CurrentUser actor, Guid userId, Guid transferToId, CancellationToken cancellationToken = default)
    {
        if (userId == actor.Id)
        {
            throw new InvalidOperationException("You can't delete yourself.");
        }

        if (transferToId == userId)
        {
            throw new ArgumentException("Choose someone else to get what this user owns.");
        }

        using var loginLease = await AccountCoordination.LockAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireUserAsync(db, userId, cancellationToken);
        RequireCanManage(actor, user);
        var heir = await db.Users.FirstOrDefaultAsync(u => u.Id == transferToId && !u.Disabled, cancellationToken)
            ?? throw new ArgumentException("Choose an enabled user to get what this user owns.");
        if (!user.Disabled && (user.Roles & UserRoles.UserManager) != 0)
        {
            await RequireAnotherUserManagerAsync(db, user.Id, cancellationToken);
        }

        var runners = await db.Runners.Where(r => r.OwnerId == userId || r.Members.Any(m => m.UserId == userId)).Select(r => r.Id).ToListAsync(cancellationToken);
        await AccountService.RequireManagersRemainAsync(db, userId, cancellationToken);
        var accounts = new List<Guid>();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // The heir owns these Runners now, so a membership of theirs on them would be a second, weaker access.
            await db.RunnerMembers.Where(m => m.UserId == heir.Id && db.Runners.Any(r => r.Id == m.RunnerId && r.OwnerId == userId)).ExecuteDeleteAsync(cancellationToken);
            await db.Runners.Where(r => r.OwnerId == userId).ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerId, heir.Id), cancellationToken);
            // The agents the heir gets on other people's Runners need access there: the heir takes over the user's.
            foreach (var membership in await db.RunnerMembers.Where(m => m.UserId == userId).ToListAsync(cancellationToken))
            {
                var heirHasAccess = await db.Runners.AnyAsync(r => r.Id == membership.RunnerId && r.OwnerId == heir.Id, cancellationToken)
                    || await db.RunnerMembers.AnyAsync(m => m.RunnerId == membership.RunnerId && m.UserId == heir.Id, cancellationToken);
                if (!heirHasAccess)
                {
                    db.RunnerMembers.Add(new RunnerMember
                    {
                        RunnerId = membership.RunnerId, UserId = heir.Id, Access = membership.Access, GrantedById = actor.Id, GrantedAt = DateTime.UtcNow,
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await db.Projects.Where(p => p.OwnerId == userId).ExecuteUpdateAsync(s => s.SetProperty(p => p.OwnerId, heir.Id), cancellationToken);
            await db.AgentShares.Where(s => s.UserId == heir.Id && db.Agents.Any(a => a.Id == s.AgentId && a.OwnerId == userId)).ExecuteDeleteAsync(cancellationToken);
            foreach (var agent in await db.Agents.Where(a => a.OwnerId == userId).ToListAsync(cancellationToken))
            {
                agent.OwnerId = heir.Id;
                if (agent.RunningAccountId is not null || agent.AccountId is not null)
                {
                    agent.LoginBlocked = true;
                    agent.LoginStopPending = agent.State is AgentState.Running or AgentState.Starting;
                    agent.StateMessage = "Agent ownership changed. Choose a login permitted for the new owner.";
                }
            }
            // Agents that committed as them go back to their owners; harnesses still running with it are stopped.
            await db.Agents.Where(a => a.GitIdentityUserId == userId).ExecuteUpdateAsync(s => s.SetProperty(a => a.GitIdentityUserId, (Guid?)null), cancellationToken);
            await db.SessionShares.Where(s => s.UserId == heir.Id && db.Sessions.Any(x => x.Id == s.SessionId && x.OwnerId == userId)).ExecuteDeleteAsync(cancellationToken);
            await db.Sessions.Where(s => s.OwnerId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, heir.Id), cancellationToken);
            db.AgentLoginGrants.RemoveRange(await db.AgentLoginGrants.Where(g => g.OwnerId == userId).ToListAsync(cancellationToken));
            // Logins are independent resources. Only this user's grants and personal defaults cascade.
            // The enforcement pass rechecks transferred agents against their new owner.
            db.Users.Remove(user);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        Invalidate(user.Id);
        await audit.RecordAsync(actor, AuditActions.UserDeleted, $"User {user.Username}", $"What they owned went to {heir.Username}", cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.UserRemoved, UserId: user.Id));
        return new DeletedUser(runners, accounts);
    }

    /// <summary>
    /// At start-up: gives the user the first migration created (owner of everything from before users existed) its
    /// username and password from ADMIN_USERNAME and ADMIN_PASSWORD; with RESET_ADMIN_PASSWORD, resets that user's
    /// password as a way back in.
    /// </summary>
    public async Task EnsureAdminAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var name = ValidUsername().IsMatch(options.AdminUsername.ToLowerInvariant()) ? options.AdminUsername.ToLowerInvariant() : "admin";
        var bootstrap = await db.Users.FirstOrDefaultAsync(u => u.Id == User.BootstrapId, cancellationToken);
        if (bootstrap is { PasswordHash.Length: > 0, MustChangePassword: true }
            && _hasher.VerifyHashedPassword(bootstrap, bootstrap.PasswordHash, ManagerOptions.DefaultAdminPassword) != PasswordVerificationResult.Failed)
        {
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            SetPassword(bootstrap, code, mustChange: true);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Replaced legacy default password. SETUP CODE for {User}: {Code}", bootstrap.Username, code);
        }
        if (bootstrap is { PasswordHash.Length: 0 })
        {
            bootstrap.Username = name;
            bootstrap.DisplayName = options.AdminUsername;
            var initialPassword = options.UsesDefaultAdminPassword ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : options.AdminPassword;
            SetPassword(bootstrap, initialPassword, mustChange: options.UsesDefaultAdminPassword);
            if (options.UsesDefaultAdminPassword)
                logger.LogWarning("FIRST-RUN SETUP CODE for {User}: {Code}. Use this as the initial password, then choose your own. This code is never shown in the UI.", name, initialPassword);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created the first user, {User}, from ADMIN_USERNAME and ADMIN_PASSWORD", name);
        }

        if (!options.ResetAdminPassword)
        {
            return;
        }

        var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == name, cancellationToken);
        if (admin is null)
        {
            var now = DateTime.UtcNow;
            admin = new User { Id = Guid.NewGuid(), Username = name, DisplayName = options.AdminUsername, CreatedAt = now };
            db.Users.Add(admin);
        }

        admin.Roles = UserRoles.Admin;
        admin.Disabled = false;
        admin.LockedUntil = null;
        admin.FailedSignIns = 0;
        var resetPassword = options.UsesDefaultAdminPassword ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : options.AdminPassword;
        SetPassword(admin, resetPassword, mustChange: true);
        // Operator recovery explicitly removes the second factor and revokes existing credentials.
        await db.UserSecondFactors.Where(f => f.UserId == admin.Id).ExecuteDeleteAsync(cancellationToken);
        if (options.UsesDefaultAdminPassword) logger.LogWarning("ADMIN RECOVERY CODE for {User}: {Code}", name, resetPassword);
        await db.SaveChangesAsync(cancellationToken);
        Invalidate(admin.Id);
        await audit.RecordAsync(null, AuditActions.AdminPasswordReset, $"User {admin.Username}", "RESET_ADMIN_PASSWORD was set at start-up", cancellationToken);
        logger.LogWarning("RESET_ADMIN_PASSWORD: {User} has all roles again and the password from ADMIN_PASSWORD. Remove RESET_ADMIN_PASSWORD from the compose file.", name);
    }

    public static string ValidateUsername(string username)
    {
        var name = username.Trim().ToLowerInvariant();
        return ValidUsername().IsMatch(name)
            ? name
            : throw new ArgumentException("Use 1 to 32 lowercase letters, digits, dots, dashes and underscores for the username, starting with a letter or digit.");
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            throw new ArgumentException($"Use a password of at least {MinPasswordLength} characters.");
        }
    }

    private void SetPassword(User user, string password, bool mustChange)
    {
        user.PasswordHash = _hasher.HashPassword(user, password);
        user.MustChangePassword = mustChange;
        user.SecurityStamp = NewStamp();
        user.FailedSignIns = 0;
        user.LockedUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<User?> FindCachedAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        _cache[userId] = user;
        return user;
    }

    private void Invalidate(Guid userId) => _cache.TryRemove(userId, out _);

    private static async Task<User> RequireUserAsync(ManagerDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken) ?? throw new KeyNotFoundException("User not found.");

    private static void RequireUserManager(CurrentUser actor)
    {
        if (!actor.Has(UserRoles.UserManager))
        {
            throw new UnauthorizedAccessException("This needs the User manager role.");
        }
    }

    private static void RequireCanManage(CurrentUser actor, User user)
    {
        RequireUserManager(actor);
        if ((user.Roles & ~actor.Roles) != 0)
        {
            throw new UnauthorizedAccessException($"{user.Username} has roles you don't have ({Roles.Describe(user.Roles & ~actor.Roles)}), so only someone with those roles can change them.");
        }
    }

    private static void RequireCanGrant(CurrentUser actor, UserRoles before, UserRoles after)
    {
        var changed = before ^ after;
        if ((changed & ~actor.Roles) != 0)
        {
            throw new UnauthorizedAccessException($"You can only grant or remove roles you have yourself (not {Roles.Describe(changed & ~actor.Roles)}).");
        }
    }

    private static async Task RequireAnotherUserManagerAsync(ManagerDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var others = await db.Users.Where(u => u.Id != userId && !u.Disabled).Select(u => u.Roles).ToListAsync(cancellationToken);
        if (!others.Any(r => (r & UserRoles.UserManager) != 0))
        {
            throw new InvalidOperationException("Someone else needs the User manager role first: otherwise nobody can manage users any more.");
        }
    }

    private static string ValidateDisplayName(string? displayName, string username)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();
        return name.Length <= 100 ? name : throw new ArgumentException("Use a display name of at most 100 characters.");
    }

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string Clip(string text) => text.Length <= 64 ? text : text[..64];

    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<User>().HashPassword(new User(), Guid.NewGuid().ToString()));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,31}$")]
    private static partial Regex ValidUsername();
}

/// <summary>What <see cref="UserService.DeleteAsync"/> leaves to clean up on the Runners.</summary>
/// <param name="Runners">The Runners they owned or were a member of: whose access changed.</param>
/// <param name="Accounts">Their harness logins, now deleted.</param>
public sealed record DeletedUser(IReadOnlyList<Guid> Runners, IReadOnlyList<Guid> Accounts);
