using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Background work and requests write at the same time; in SQLite's default mode a write locks readers out.</summary>
public sealed class DatabaseModeTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    [Fact]
    public async Task TheDatabaseUsesWriteAheadLogging()
    {
        using var client = manager.CreateClient();
        await using var db = await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
