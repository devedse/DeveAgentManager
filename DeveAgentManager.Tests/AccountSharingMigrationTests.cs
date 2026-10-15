using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DeveAgentManager.Tests;

public class AccountSharingMigrationTests
{
    [Fact]
    public async Task OwnershipDefaultsCredentialsAndLegacyAgentDelegationSurvive()
    {
        var temp = Directory.CreateTempSubdirectory("dam-sharing-migration-");
        try
        {
            await using var db = new ManagerDbContext(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={Path.Combine(temp.FullName, "test.db")}").Options);
            var ct = TestContext.Current.CancellationToken;
            await db.GetService<IMigrator>().MigrateAsync("20261011180000_LogViewerRole", ct);
            var user = new User { Id = Guid.NewGuid(), Username = "test-user", DisplayName = "Test" };
            db.Users.Add(user); await db.SaveChangesAsync(ct);
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var agent = Guid.NewGuid(); var runner = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Accounts(Id,OwnerId,HarnessId,Name,Kind,ProtectedSecret,IsDefault,Version,CreatedAt,UpdatedAt,ManagedRenewal)
                VALUES({a},{User.BootstrapId},'claude','First','Subscription','encrypted-test-only',1,7,{now},{now},0),
                      ({b},{User.BootstrapId},'claude','Second','ApiKey','encrypted-test-only-2',0,2,{now},{now},0);
                """, ct);
            db.Runners.Add(new Runner { Id = runner, Name = "test-runner", OwnerId = User.BootstrapId, State = RunnerState.Stopped });
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Agents(Id,RunnerId,OwnerId,Name,HarnessId,WorkingDirectory,TmuxSession,HarnessSessionId,State,CreatedAt,UpdatedAt,AccountId,Working,SeenCompletionSequence)
                VALUES({agent},{runner},{user.Id},'test-agent','claude','/work','test','session','Running',{now},{now},{a},0,0);
                """, ct);
            await db.Database.MigrateAsync(ct);
            db.ChangeTracker.Clear();
            var first = await db.Accounts.SingleAsync(x => x.Id == a, ct);
            Assert.Equal("encrypted-test-only", first.ProtectedSecret);
            Assert.Equal(7, first.Version);
            Assert.Equal(User.BootstrapId, first.CreatedById);
            Assert.True(first.Enabled);
            Assert.True((await db.Accounts.SingleAsync(x => x.Id == b, ct)).Enabled);
            Assert.Equal(a, (await db.UserHarnessDefaults.SingleAsync(ct)).AccountId);
            Assert.Equal(AccountAccess.Manage, (await db.AccountGrants.FirstAsync(g => g.AccountId == a, ct)).Access);
            Assert.Equal(user.Id, (await db.AgentLoginGrants.SingleAsync(ct)).OwnerId);
            Assert.Equal(a, (await db.Agents.SingleAsync(ct)).RunningAccountId);
            Assert.False(await db.AccountGrants.AnyAsync(g => g.UserId == user.Id, ct));
            Assert.Equal(UserRoles.Admin, (await db.Users.SingleAsync(u => u.Id == User.BootstrapId, ct)).Roles);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); temp.Delete(true); }
    }
}
