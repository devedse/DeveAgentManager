using DeveAgentManager.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class ConversationReaderMigrationTests
{
    [Fact]
    public async Task ExistingAdminsAreUpgradedOnceAndPartialRolesRemainUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new ManagerDbContext(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261007131119_InternetAuthentication", ct);
        var bootstrap = await db.Users.SingleAsync(u => u.Id == User.BootstrapId, ct);
        bootstrap.Roles = UserRoles.UserManager;
        var admin = new User { Id = Guid.NewGuid(), Username = "admin-test", DisplayName = "Admin", Roles = (UserRoles)15, SecurityStamp = "old-admin" };
        var manager = new User { Id = Guid.NewGuid(), Username = "manager-test", DisplayName = "Manager", Roles = UserRoles.RunnerManager, SecurityStamp = "old-manager" };
        db.Users.AddRange(admin, manager); await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
        await db.Database.MigrateAsync(ct);
        var updated = await db.Users.AsNoTracking().SingleAsync(u => u.Id == admin.Id, ct);
        Assert.Equal(UserRoles.Admin, updated.Roles);
        Assert.NotEqual("old-admin", updated.SecurityStamp);
        var untouched = await db.Users.AsNoTracking().SingleAsync(u => u.Id == manager.Id, ct);
        Assert.Equal(UserRoles.UserManager, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == User.BootstrapId, ct)).Roles);
        Assert.Equal(UserRoles.RunnerManager, untouched.Roles);
        Assert.Equal("old-manager", untouched.SecurityStamp);
        await db.Database.MigrateAsync(ct);
        Assert.Equal(updated.SecurityStamp, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == admin.Id, ct)).SecurityStamp);
    }
}
