using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Each user's own settings (UserSettingsStore): what were Manager settings before users existed (the git identity,
/// environment variables and GitHub token) become the first user's, once, and leave the shared Setup Profile.
/// </summary>
public sealed class UserSettingsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dam-user-settings-{Guid.NewGuid():N}.db");
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private Factory _factory = null!;
    private UserSettingsStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new Factory(_path);
        await using var db = _factory.CreateDbContext();
        await db.Database.MigrateAsync(Ct);
        _store = new UserSettingsStore(_factory, _protector, new ManagerEvents());
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SettingsFromBeforeUsers_BecomeTheFirstUsers_Once()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Settings.Add(new SettingEntry
            {
                Key = "setup-profile",
                Value = _protector.Protect("""
                    {"Script":"echo hi","GitUserName":"Dev","GitUserEmail":"dev@example.com",
                     "EnvironmentVariables":[{"Name":"OPENAI_API_KEY","Value":"sk-test"}]}
                    """),
            });
            db.Settings.Add(new SettingEntry { Key = "github-pat", Value = _protector.Protect("ghp_before") });
            await db.SaveChangesAsync(Ct);
        }

        await _store.MoveSettingsFromBeforeUsersAsync(Ct);

        var profile = await _store.GetProfileAsync(User.BootstrapId, Ct);
        Assert.Equal(("Dev", "dev@example.com"), (profile.GitUserName, profile.GitUserEmail));
        var variable = Assert.Single(profile.EnvironmentVariables);
        Assert.Equal(("OPENAI_API_KEY", "sk-test"), (variable.Name, variable.Value));
        Assert.Equal("ghp_before", await _store.GetGitHubTokenAsync(User.BootstrapId, Ct));
        await using (var db = _factory.CreateDbContext())
        {
            var shared = JsonDocument.Parse(_protector.Unprotect((await db.Settings.FindAsync(["setup-profile"], Ct))!.Value)!).RootElement;
            Assert.Equal("echo hi", shared.GetProperty("Script").GetString());
            Assert.False(shared.TryGetProperty("GitUserName", out _));
            Assert.False(shared.TryGetProperty("EnvironmentVariables", out _));
            Assert.Null(await db.Settings.FindAsync(["github-pat"], Ct));
        }

        // What the user changes afterwards stays: moving again finds nothing to move.
        profile.GitUserName = "Dev Changed";
        await _store.SaveProfileAsync(User.BootstrapId, profile, Ct);
        await _store.MoveSettingsFromBeforeUsersAsync(Ct);
        Assert.Equal("Dev Changed", (await _store.GetProfileAsync(User.BootstrapId, Ct)).GitUserName);
        Assert.Equal("ghp_before", await _store.GetGitHubTokenAsync(User.BootstrapId, Ct));
    }

    [Fact]
    public async Task OtherUsers_StartWithoutSettings()
    {
        var someone = Guid.NewGuid();
        Assert.Null((await _store.GetProfileAsync(someone, Ct)).GitUserName);
        Assert.Null(await _store.GetGitHubTokenAsync(someone, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveGitHubTokenAsync(someone, "https://user:token@github.com", Ct));
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
