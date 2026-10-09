using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Harnesses.Codex;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>The Manager is the only one renewing a Codex login: one renewal per account, saved before use, never undone.</summary>
public sealed class AccountRenewalTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dam-renewal-{Guid.NewGuid():N}.db");
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly TokenEndpoint _endpoint = new();
    private Factory _factory = null!;
    private AccountService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new Factory(_path);
        await using var db = _factory.CreateDbContext();
        await db.Database.MigrateAsync(Ct);
        _service = new AccountService(_factory, _protector, null!, [new CodexHarness(_endpoint)], new ManagerEvents(), NullLogger<AccountService>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void RunnerCopyHasNoRefreshTokenAndCodexNeverRenewsIt()
    {
        var copy = JsonNode.Parse(new CodexHarness().RunnerAccountCopy(Login("refresh-1", TimeSpan.FromDays(5))))!;
        Assert.Equal("chatgptAuthTokens", copy["auth_mode"]!.GetValue<string>());
        Assert.Equal("", copy["tokens"]!["refresh_token"]!.GetValue<string>());
        Assert.Equal("acct", copy["tokens"]!["account_id"]!.GetValue<string>());
        Assert.NotEmpty(copy["tokens"]!["access_token"]!.GetValue<string>());
        Assert.DoesNotContain("refresh-1", copy.ToJsonString());
    }

    [Fact]
    public async Task ManyCallersExpiringTogether_OneRenewal_SavedEncrypted()
    {
        var account = await _service.AddAsync(User.BootstrapId, "codex", "", AccountKind.Subscription, Login("refresh-1", TimeSpan.FromHours(1)), Ct);
        Assert.True(account.ManagedRenewal);
        _endpoint.Hold = new TaskCompletionSource();
        var callers = Enumerable.Range(0, 6).Select(_ => _service.RenewIfDueAsync(account, Ct)).ToArray();
        await _endpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _endpoint.Hold.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(["refresh-1"], _endpoint.Used);
        var stored = await GetAsync(account.Id);
        Assert.All(results, r => Assert.Equal(stored.ProtectedSecret, r!.ProtectedSecret));
        Assert.Contains("refresh-2", _protector.Unprotect(stored.ProtectedSecret));
        Assert.DoesNotContain("refresh-2", stored.ProtectedSecret);
        Assert.Equal(account.Version, stored.Version);
        Assert.Null(stored.RenewalProblem);

        // Fresh again: no more renewals until it is due.
        await _service.RenewIfDueAsync(stored, Ct);
        Assert.Single(_endpoint.Used);
    }

    [Fact]
    public async Task ReconnectDuringRenewal_RenewalIsDropped()
    {
        var account = await _service.AddAsync(User.BootstrapId, "codex", "", AccountKind.Subscription, Login("refresh-1", TimeSpan.FromHours(1)), Ct);
        _endpoint.Hold = new TaskCompletionSource();
        var renewal = _service.RenewIfDueAsync(account, Ct);
        await _endpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await _service.ReplaceSecretAsync(account.Id, AccountKind.Subscription, Login("reconnected", TimeSpan.FromDays(9)), Ct);
        _endpoint.Hold.SetResult();
        await renewal;
        Assert.Contains("reconnected", _protector.Unprotect((await GetAsync(account.Id)).ProtectedSecret));
    }

    [Fact]
    public async Task SpentRefreshToken_NeedsReconnect_NetworkTrouble_IsRetriedLater()
    {
        var account = await _service.AddAsync(User.BootstrapId, "codex", "", AccountKind.Subscription, Login("refresh-1", TimeSpan.FromHours(1)), Ct);
        _endpoint.Reply = (HttpStatusCode.InternalServerError, """{"error":"busy"}""");
        await _service.RenewIfDueAsync(account, Ct);
        Assert.Contains("tries again", (await GetAsync(account.Id)).RenewalProblem);

        _endpoint.Reply = (HttpStatusCode.Unauthorized, """{"error":{"code":"refresh_token_reused","message":"used"}}""");
        var ex = await Assert.ThrowsAsync<AccountRenewalException>(() => _service.RenewIfDueAsync(account, Ct));
        Assert.True(ex.Permanent);
        var stored = await GetAsync(account.Id);
        Assert.Contains("Connect it again", stored.RenewalProblem);
        Assert.Contains("refresh-1", _protector.Unprotect(stored.ProtectedSecret)); // Kept: running agents still use its access.
    }

    [Fact]
    public async Task OlderLoginsAndApiKeysAreNeverRenewedByTheManager()
    {
        var legacy = await _service.AddAsync(User.BootstrapId, "codex", "", AccountKind.Subscription, Login("refresh-1", TimeSpan.FromHours(1)), Ct);
        await using (var db = _factory.CreateDbContext())
        {
            await db.Accounts.Where(a => a.Id == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ManagedRenewal, false), Ct);
        }

        var key = await _service.AddAsync(User.BootstrapId, "codex", "", AccountKind.ApiKey, "sk-test", Ct);
        Assert.False(key.ManagedRenewal);
        await _service.RenewIfDueAsync(legacy, Ct);
        await _service.RenewIfDueAsync(key, Ct);
        Assert.Empty(_endpoint.Used);
        Assert.Equal("1-Subscription", AccountService.Marker((await GetAsync(legacy.Id))));
    }

    private async Task<Account> GetAsync(Guid id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
    }

    private static string Jwt(TimeSpan validFor)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("""{"alg":"none"}""") + "." + B64($$"""{"exp":{{DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds()}}}""") + ".sig";
    }

    private static string Login(string refreshToken, TimeSpan validFor) => new JsonObject
    {
        ["auth_mode"] = "chatgpt",
        ["OPENAI_API_KEY"] = null,
        ["tokens"] = new JsonObject { ["id_token"] = Jwt(validFor), ["access_token"] = Jwt(validFor), ["refresh_token"] = refreshToken, ["account_id"] = "acct" },
        ["last_refresh"] = "2026-10-01T00:00:00Z",
    }.ToJsonString();

    /// <summary>A fake auth.openai.com that rotates: every refresh token works once.</summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public List<string> Used { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold { get; set; }
        public (HttpStatusCode Status, string Body)? Reply { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(CodexHarness.TokenUrl, request.RequestUri!.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Assert.Equal(CodexHarness.ClientId, body["client_id"]!.GetValue<string>());
            var token = body["refresh_token"]!.GetValue<string>();
            lock (Used)
            {
                Used.Add(token);
            }

            Started.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.Task;
            }

            var (status, text) = Reply ?? (Used.Count(t => t == token) > 1
                ? (HttpStatusCode.Unauthorized, """{"error":{"code":"refresh_token_reused"}}""")
                : (HttpStatusCode.OK, new JsonObject { ["access_token"] = Jwt(TimeSpan.FromDays(10)), ["refresh_token"] = "refresh-2" }.ToJsonString()));
            return new HttpResponseMessage(status) { Content = new StringContent(text) };
        }
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path}").Options);
        public Task<ManagerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
