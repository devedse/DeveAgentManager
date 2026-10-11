using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
namespace DeveAgentManager.Tests;
public sealed class LogViewerMigrationTests
{
    [Fact] public async Task ExistingAdminsGainLogAccessButOrdinaryRolesDoNot()
    {
        var folder = Directory.CreateTempSubdirectory("dam-log-role-");
        try
        {
            await using var db = new ManagerDbContext(new DbContextOptionsBuilder<ManagerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(folder.FullName, "test.db")}").Options);
            await db.GetService<IMigrator>().MigrateAsync("20261007222017_CentralAccountRenewal", TestContext.Current.CancellationToken);
            db.Users.Add(new User { Id = Guid.NewGuid(), Username = "test-viewer", DisplayName = "Test viewer", Roles = UserRoles.UserManager });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();
            Assert.Equal(UserRoles.Admin, (await db.Users.SingleAsync(u => u.Id == User.BootstrapId, TestContext.Current.CancellationToken)).Roles);
            Assert.Equal(UserRoles.UserManager, (await db.Users.SingleAsync(u => u.Username == "test-viewer", TestContext.Current.CancellationToken)).Roles);
        }
        finally { folder.Delete(true); }
    }
}
