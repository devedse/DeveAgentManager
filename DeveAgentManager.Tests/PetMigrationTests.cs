using DeveAgentManager.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class PetMigrationTests
{
    [Fact]
    public async Task ExistingNamedPetsAndTokensArePreservedAndUntouchedDefaultsBecomeEggs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new ManagerDbContext(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261026205238_Damagochi", ct);
        var user = User.BootstrapId;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO UserPets (UserId, Enabled, Species, Name, Experience, Happiness, ReducedMotion, Size, Revision, Reaction) VALUES ({user}, 1, 'silkie', 'Byte', 4477262, 100, 0, 120, 2, 'idle')", ct);
        await db.Database.MigrateAsync(ct);
        var pet = await db.UserPets.SingleAsync(ct);
        Assert.Equal("Byte", pet.Name); Assert.Equal("silkie", pet.Species);
        Assert.Equal(4_477_262, pet.Experience); Assert.Equal(4_477_262, pet.LifetimeTokens); Assert.Equal(0, pet.Generation);
        Assert.Equal(1, DeveAgentManager.Pets.PetRules.Level(pet.Experience));
        db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync("20261026205238_Damagochi", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE UserPets SET Enabled = 0, Species = 'snek', Name = 'Snek', Experience = 0 WHERE UserId = {user}", ct);
        await db.Database.MigrateAsync(ct);
        pet = await db.UserPets.SingleAsync(ct);
        Assert.Equal("", pet.Name); Assert.Equal("", pet.Species); Assert.False(pet.Enabled);
    }
}
