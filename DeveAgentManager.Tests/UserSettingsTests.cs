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
    public async Task CodexVoiceModeDefaultsToBrowserAndStaysPrivateToEachUser()
    {
        var other = Guid.NewGuid();
        Assert.False(await _store.GetCodexTerminalVoiceAsync(User.BootstrapId, Ct));
        Assert.False(await _store.GetCodexTerminalVoiceAsync(other, Ct));
        await _store.SaveCodexTerminalVoiceAsync(User.BootstrapId, true, Ct);
        Assert.True(await _store.GetCodexTerminalVoiceAsync(User.BootstrapId, Ct));
        Assert.False(await _store.GetCodexTerminalVoiceAsync(other, Ct));
        await _store.SaveCodexTerminalVoiceAsync(User.BootstrapId, false, Ct);
        Assert.False(await _store.GetCodexTerminalVoiceAsync(User.BootstrapId, Ct));
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

    [Fact]
    public async Task ModelDefaults_KeepOnlyWhatTheUserChose()
    {
        Assert.Empty(await _store.GetModelDefaultsAsync(User.BootstrapId, Ct));
        await _store.SaveModelDefaultsAsync(User.BootstrapId, new Dictionary<string, ModelDefault>
        {
            ["codex"] = new() { Model = " gpt-6.1-sol ", Effort = "" },
            ["claude"] = new() { Model = "  ", Effort = null },
        }, Ct);
        var saved = Assert.Single(await _store.GetModelDefaultsAsync(User.BootstrapId, Ct));
        Assert.Equal(("codex", "gpt-6.1-sol", (string?)null), (saved.Key, saved.Value.Model, saved.Value.Effort));

        // Back to the Manager's default everywhere.
        await _store.SaveModelDefaultsAsync(User.BootstrapId, new Dictionary<string, ModelDefault>(), Ct);
        Assert.Empty(await _store.GetModelDefaultsAsync(User.BootstrapId, Ct));
    }

    [Fact]
    public void ModelDefaults_FollowTheManagerForWhatTheUserLeftEmpty()
    {
        var manager = new HarnessConfig { DefaultProvider = "anthropic", DefaultModel = "claude-opus-5-5", DefaultThinkingLevel = "xhigh" };
        Assert.Equal(new EffectiveModel("anthropic", "claude-opus-5-5", "xhigh"), EffectiveModel.Resolve(manager, null));
        Assert.Equal(new EffectiveModel("anthropic", "claude-opus-5-5", "low"), EffectiveModel.Resolve(manager, new() { Effort = "low" }));
        // The Manager's provider belongs to the Manager's model.
        Assert.Equal(new EffectiveModel(null, "openai/gpt-6", "xhigh"), EffectiveModel.Resolve(manager, new() { Model = "openai/gpt-6", Effort = " " }));
        Assert.Equal(new EffectiveModel(null, null, null), EffectiveModel.Resolve(new HarnessConfig { DefaultModel = " ", DefaultProvider = "anthropic" }, new()));
    }

    [Theory]
    [InlineData("opus[1m]", null)]
    [InlineData("anthropic/claude-opus-5-5:high", null)]
    [InlineData("gpt 6", "One model name, without spaces.")]
    public void ModelDefaults_TakeOneModelName(string model, string? problem) => Assert.Equal(problem, ModelDefault.ModelProblem(model));

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
