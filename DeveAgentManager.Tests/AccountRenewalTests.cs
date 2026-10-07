using System.Text;
using System.Text.Json;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AccountRenewalTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dam-renewal-{Guid.NewGuid():N}.db");
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private Factory _factory = null!;
    private AccountService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new Factory(_path);
        await using var db = _factory.CreateDbContext();
        await db.Database.MigrateAsync(Ct);
        _service = new AccountService(_factory, _protector, null!, [new CodexHarness(), new ClaudeHarness()],
            new ManagerEvents(), NullLogger<AccountService>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RenewalIsEncryptedAndAvailableForNextRunnerWithoutChangingLoginGeneration()
    {
        var original = await AddAsync();
        var renewed = Secret(2);
        await _service.SaveRenewalAsync(original, renewed, Ct);
        var stored = await GetAsync(original.Id);
        Assert.Equal(renewed, _protector.Unprotect(stored.ProtectedSecret));
        Assert.DoesNotContain("refresh-2", stored.ProtectedSecret);
        Assert.Equal(original.Version, stored.Version);
        await _service.SaveRenewalAsync(original, Secret(1), Ct);
        Assert.Equal(renewed, _protector.Unprotect((await GetAsync(original.Id)).ProtectedSecret));
    }

    [Fact]
    public async Task ConcurrentRenewalsKeepNewestCredential()
    {
        var original = await AddAsync();
        await _service.SaveRenewalAsync(original, Secret(3), Ct);
        await _service.SaveRenewalAsync(original, Secret(2), Ct);
        Assert.Equal(Secret(3), _protector.Unprotect((await GetAsync(original.Id)).ProtectedSecret));
        await _service.SaveRenewalAsync(original, Secret(4), Ct);
        Assert.Equal(Secret(4), _protector.Unprotect((await GetAsync(original.Id)).ProtectedSecret));
    }

    [Fact]
    public async Task InFlightRenewalCannotUndoReconnectOrAccountDeletion()
    {
        var original = await AddAsync();
        await _service.ReplaceSecretAsync(original.Id, AccountKind.Subscription, Secret(2), Ct);
        await _service.SaveRenewalAsync(original, Secret(4), Ct);
        Assert.Equal(Secret(2), _protector.Unprotect((await GetAsync(original.Id)).ProtectedSecret));
        await using var db = _factory.CreateDbContext();
        await db.Accounts.Where(a => a.Id == original.Id).ExecuteDeleteAsync(Ct);
        await _service.SaveRenewalAsync(original, Secret(5), Ct);
        Assert.False(await db.Accounts.AnyAsync(a => a.Id == original.Id, Ct));
    }

    [Theory]
    [InlineData("other-account", "user")]
    [InlineData("account", "other-user")]
    public async Task AnotherSubscriptionIdentityCannotReplaceCredential(string identity, string subject)
    {
        var original = await AddAsync();
        await _service.SaveRenewalAsync(original, Secret(2, identity, subject), Ct);
        Assert.Equal(original.ProtectedSecret, (await GetAsync(original.Id)).ProtectedSecret);
    }

    [Fact]
    public void InvalidIncompleteFutureAndApiCredentialsAreIgnored()
    {
        var harness = new CodexHarness();
        foreach (var candidate in new[] { "", "not json", "[]", "{\"tokens\":{}}", Secret(1), Secret(0),
                     Secret(2).Replace("refresh-2", ""), Secret(2).Replace("access-2", ""),
                     Secret(2).Replace("2026-01-02T00:00:00Z", "2099-01-02T00:00:00Z"),
                     Secret(2).Replace("chatgpt", "apikey"), Secret(2).Replace("account_id", "unknown") })
            Assert.False(harness.IsNewerAccountSecret(Secret(1), candidate));
        Assert.False(((IHarness)new ClaudeHarness()).IsNewerAccountSecret("claude-token", Secret(2)));
        Assert.True(harness.IsNewerAccountSecret(Secret(1), Secret(2)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunnerReplacementPreservesAConcurrentRenewalOrReconnect(bool renewedAgain, bool reconnected)
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Directory.CreateTempSubdirectory("dam-renewal-script-").FullName;
        try
        {
            var account = new Account { Id = Guid.NewGuid(), Version = 1, Kind = AccountKind.Subscription };
            var folder = Path.Combine(home, ".deveagentmanager", "accounts", account.Id.ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "codex"));
            var original = Secret(1);
            var installed = renewedAgain ? Secret(3) : original;
            var auth = Path.Combine(folder, "codex", "auth.json");
            var temporary = Path.Combine(folder, "codex", "pending");
            await File.WriteAllTextAsync(auth, installed, Ct);
            await File.WriteAllTextAsync(temporary, Secret(2), Ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "version"), reconnected ? "2-Subscription\n" : "1-Subscription\n", Ct);
            var script = AccountService.BuildRenewalInstallScript(account, "codex/auth.json", "codex/pending", original)
                .Replace("$HOME", home);
            var start = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(script);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync(Ct);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(renewedAgain || reconnected ? installed : Secret(2), await File.ReadAllTextAsync(auth, Ct));
            Assert.False(File.Exists(temporary));
        }
        finally { Directory.Delete(home, true); }
    }

    private Task<Account> AddAsync() => _service.AddAsync(User.BootstrapId, "codex", "Personal", AccountKind.Subscription, Secret(1), Ct);
    private async Task<Account> GetAsync(Guid id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
    }

    private static string Secret(int day, string identity = "account", string subject = "user") => JsonSerializer.Serialize(new
    {
        auth_mode = "chatgpt",
        last_refresh = $"2026-01-{day:00}T00:00:00Z",
        tokens = new
        {
            account_id = identity,
            id_token = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = subject })))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature",
            access_token = $"access-{day}", refresh_token = $"refresh-{day}",
        },
    });

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path}").Options);
        public Task<ManagerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
