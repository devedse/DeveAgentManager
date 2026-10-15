using System.Net;
using System.Net.Http.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AccountSharingTests : IClassFixture<ApiSecurityTests.Manager>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly ApiSecurityTests.Manager _manager;
    private readonly AccountService _accounts;
    private readonly UserService _users;
    private readonly IDbContextFactory<ManagerDbContext> _factory;
    public AccountSharingTests(ApiSecurityTests.Manager manager)
    {
        _manager = manager;
        using var client = manager.CreateClient();
        _accounts = manager.Services.GetRequiredService<AccountService>();
        _users = manager.Services.GetRequiredService<UserService>();
        _factory = manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
    }
    private async Task<CurrentUser> UserAsync(UserRoles role = UserRoles.None) =>
        CurrentUser.From(await _users.CreateAsync(TestUsers.Admin, "u" + Guid.NewGuid().ToString("N")[..12], null, "test-only-password", role, Ct));
    private Task<Account> LoginAsync(CurrentUser user) => _accounts.AddAsync(user.Id, "claude", "Test login", AccountKind.ApiKey, "test-only-secret", Ct);

    [Fact]
    public async Task GrantsExposeNoSecret_IndependentDefaults_AdministrationRequiresExplicitUse()
    {
        var alice = await UserAsync(); var bob = await UserAsync(); var admin = await UserAsync(UserRoles.HarnessLoginManager);
        var a = await LoginAsync(alice); var b = await LoginAsync(bob);
        await _accounts.SetGrantAsync(alice, a.Id, bob.Id, AccountAccess.Use, Ct);
        await _accounts.SetDefaultAsync(bob, a.Id, Ct);
        Assert.Equal(a.Id, (await _accounts.ResolveAsync(new Agent { OwnerId = alice.Id, HarnessId = "claude" }, Ct))!.Id);
        Assert.Equal(a.Id, (await _accounts.ResolveAsync(new Agent { OwnerId = bob.Id, HarnessId = "claude" }, Ct))!.Id);
        await _accounts.SetDefaultAsync(bob, b.Id, Ct);
        Assert.Equal(a.Id, (await _accounts.ListVisibleAsync(alice, false, Ct)).Single().Id);
        Assert.True((await _accounts.ListVisibleAsync(alice, false, Ct)).Single().IsDefault);
        Assert.True((await _accounts.ListVisibleAsync(bob, false, Ct)).Single(x => x.Id == b.Id).IsDefault);
        Assert.False((await _accounts.ListVisibleAsync(bob, false, Ct)).Single(x => x.Id == a.Id).CanManage);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _accounts.RenameAsync(bob, a.Id, "No", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _accounts.ReplaceSecretAsync(bob, a.Id, AccountKind.ApiKey, "other-test-secret", Ct));
        Assert.Empty(await _accounts.ListVisibleAsync(admin, false, Ct));
        Assert.Contains(await _accounts.ListVisibleAsync(admin, true, Ct), x => x.Id == a.Id && x.CanManage && x.Access == AccountAccess.None);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _accounts.SetDefaultAsync(admin, a.Id, Ct));
        await _accounts.SetGrantAsync(admin, a.Id, admin.Id, AccountAccess.Use, Ct);
        await _accounts.SetDefaultAsync(admin, a.Id, Ct);
        var dto = (await _accounts.ListVisibleAsync(admin, false, Ct)).Single();
        Assert.DoesNotContain("test-only-secret", System.Text.Json.JsonSerializer.Serialize(dto));
        Assert.DoesNotContain("ProtectedSecret", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public async Task RevokingDefaultBlocksOnlyAffectedAgents_AndNeverFallsBack()
    {
        var alice = await UserAsync(); var bob = await UserAsync();
        var login = await LoginAsync(alice);
        await _accounts.SetGrantAsync(alice, login.Id, bob.Id, AccountAccess.Use, Ct);
        await _accounts.SetDefaultAsync(bob, login.Id, Ct);
        await using var db = await _factory.CreateDbContextAsync(Ct);
        var runner = new Runner { Name = "r" + Guid.NewGuid().ToString("N"), OwnerId = User.BootstrapId, State = RunnerState.Stopped };
        var affected = new Agent { Id = Guid.NewGuid(), Runner = runner, OwnerId = bob.Id, Name = "affected", HarnessId = "claude", State = AgentState.Running, RunningAccountId = login.Id };
        var other = new Agent { Id = Guid.NewGuid(), Runner = runner, OwnerId = alice.Id, Name = "other", HarnessId = "claude", State = AgentState.Running, RunningAccountId = login.Id };
        db.Agents.AddRange(affected, other); await db.SaveChangesAsync(Ct);
        var summary = (await _accounts.ListVisibleAsync(alice, false, Ct)).Single(x => x.Id == login.Id);
        Assert.Equal(2, summary.AgentCount);
        var hidden = await _accounts.ImpactAsync(alice, login.Id, bob.Id, Ct);
        Assert.Null(hidden.Agents.Single().AgentName); // Login management grants no conversation access.
        await _accounts.SetGrantAsync(alice, login.Id, bob.Id, null, Ct);
        db.ChangeTracker.Clear();
        var blocked = await db.Agents.SingleAsync(a => a.Id == affected.Id, Ct);
        Assert.True(blocked.LoginBlocked);
        Assert.True(blocked.LoginStopPending); // Offline stop is durable.
        Assert.False((await db.Agents.SingleAsync(a => a.Id == other.Id, Ct)).LoginBlocked);
        Assert.False(await db.UserHarnessDefaults.AnyAsync(p => p.UserId == bob.Id, Ct));
        await LoginAsync(bob); // A different default must not restart the revoked binding.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.ResolveAsync(blocked, Ct));
        await _accounts.EnforceAsync(Ct);
        db.ChangeTracker.Clear();
        Assert.True((await db.Agents.SingleAsync(a => a.Id == affected.Id, Ct)).LoginStopPending);
    }

    [Fact]
    public async Task LastManagerRaceAndUserDeletionCannotOrphanOrCascadeSharedLogin()
    {
        var alice = await UserAsync(); var bob = await UserAsync();
        var login = await LoginAsync(alice);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.DeleteAsync(TestUsers.Admin, alice.Id, bob.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.UpdateAsync(TestUsers.Admin, alice.Id, null, UserRoles.None, true, Ct));
        await _accounts.SetGrantAsync(alice, login.Id, bob.Id, AccountAccess.Manage, Ct);
        async Task<bool> Remove(Guid id)
        {
            try { await _accounts.SetGrantAsync(TestUsers.Admin, login.Id, id, null, Ct); return true; }
            catch (InvalidOperationException) { return false; }
        }
        Assert.Equal(1, (await Task.WhenAll(Remove(alice.Id), Remove(bob.Id))).Count(x => x));
        await using var db = await _factory.CreateDbContextAsync(Ct);
        var survivor = await db.AccountGrants.SingleAsync(g => g.AccountId == login.Id && g.Access == AccountAccess.Manage, Ct);
        var leaving = survivor.UserId == alice.Id ? bob : alice;
        var heir = survivor.UserId == alice.Id ? alice : bob;
        var deleted = await _users.DeleteAsync(TestUsers.Admin, leaving.Id, heir.Id, Ct);
        Assert.Empty(deleted.Accounts);
        db.ChangeTracker.Clear();
        Assert.NotNull(await db.Accounts.FindAsync([login.Id], Ct));
        Assert.Single(await db.AccountGrants.Where(g => g.AccountId == login.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task DisabledOrDeletedLoginPersistsCleanupAndBlockedBindings()
    {
        var alice = await UserAsync();
        var login = await LoginAsync(alice);
        await using var db = await _factory.CreateDbContextAsync(Ct);
        var runner = new Runner { Name = "r" + Guid.NewGuid().ToString("N"), OwnerId = User.BootstrapId, State = RunnerState.Stopped };
        db.Runners.Add(runner); await db.SaveChangesAsync(Ct);
        await _accounts.SetEnabledAsync(alice, login.Id, false, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.ResolveAsync(new Agent { OwnerId = alice.Id, AccountId = login.Id, HarnessId = "claude" }, Ct));
        Assert.True(await db.AccountCleanups.AnyAsync(c => c.AccountId == login.Id && c.RunnerId == runner.Id, Ct));
        await _accounts.SetEnabledAsync(alice, login.Id, true, Ct);
        Assert.False(await db.AccountCleanups.AnyAsync(c => c.AccountId == login.Id, Ct));
        await _accounts.DeleteAsync(alice, login.Id, Ct);
        Assert.True(await db.AccountCleanups.AnyAsync(c => c.AccountId == login.Id, Ct));
    }
}
