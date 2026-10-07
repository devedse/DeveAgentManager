#pragma warning disable xUnit1051
using System.Net;
using DeveAgentManager.Api;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class InternetSecurityTests : IClassFixture<InternetSecurityTests.Manager>
{
    private readonly Manager _manager;
    private const string Password = "test-password-1234";
    public InternetSecurityTests(Manager manager) => _manager = manager;
    public sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }
    public sealed class Manager : WebApplicationFactory<DtoMapper>
    {
        private readonly string _data = Directory.CreateTempSubdirectory("dam-public-").FullName;
        public Clock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _data).UseSetting("ADMIN_PASSWORD", Password).UseSetting("PROXMOX_URL", "")
                .UseSetting("PUBLIC_ORIGINS", "https://dam.test;https://alternate.test")
                .UseSetting("EDITOR_URL", "https://{runner}.code.{host}").UseSetting("TRUSTED_PROXIES", "10.0.0.2");
            builder.ConfigureServices(s => { s.AddSingleton<TimeProvider>(Clock); s.AddSingleton<IStartupFilter, Peer>(); });
        }
    }
    private sealed class Peer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((c, n) => { c.Connection.RemoteIpAddress = IPAddress.Parse(c.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "198.51.100.9"); if (int.TryParse(c.Request.Headers["X-Test-Port"], out var port)) c.Connection.LocalPort = port; return n(c); });
            next(app);
        };
    }
    private HttpClient Client(string origin = "https://dam.test")
    {
        var client = _manager.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(origin), AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
        return client;
    }
    private async Task<string> LoginAsync(HttpClient client, string username = "admin", string password = Password)
    {
        var result = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string> { ["username"] = username, ["password"] = password }));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        var cookie = result.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-DAM.Auth="));
        Assert.Contains("secure", cookie); Assert.Contains("path=/", cookie); Assert.DoesNotContain("domain=", cookie);
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        return cookie.Split(';')[0];
    }
    private async Task<Guid> RunnerAsync(string name)
    {
        await using var db = await _manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync();
        var runner = new Runner { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = name, ProviderId = "existing", Tools = "vscode", State = RunnerState.Ready, Username = "test", Host = "127.0.0.1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Runners.Add(runner); await db.SaveChangesAsync(); return runner.Id;
    }
    [Theory]
    [InlineData("https://unexpected.test")]
    [InlineData("http://dam.test")]
    [InlineData("https://code.dam.test")]
    public async Task UnknownOriginsNeverServePortal(string origin) => Assert.Equal((HttpStatusCode)421, (await Client(origin).GetAsync("/login")).StatusCode);

    [Fact]
    public async Task ForwardingOnlyFromExplicitProxy_AndNeverActivatesLocalException()
    {
        var untrusted = Client("http://dam.test");
        untrusted.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        untrusted.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal((HttpStatusCode)421, (await untrusted.GetAsync("/login")).StatusCode);
        var trusted = Client("http://dam.test");
        trusted.DefaultRequestHeaders.Add("X-Test-Peer", "10.0.0.2");
        trusted.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.OK, (await trusted.GetAsync("/login")).StatusCode);
        var spoofedLocal = Client("http://127.0.0.1");
        spoofedLocal.DefaultRequestHeaders.Add("X-Test-Peer", "10.0.0.2");
        spoofedLocal.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal((HttpStatusCode)421, (await spoofedLocal.GetAsync("/login")).StatusCode);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://alpha.code.dam.test")]
    [InlineData("https://attacker.test")]
    public async Task AccountFormsRejectMissingNullAndOtherOrigins(string? origin)
    {
        var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["username"] = "admin", ["password"] = Password }) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }
    [Fact]
    public async Task CorrectOriginStillNeedsAntiforgeryToken()
    {
        var client = Client(); client.DefaultRequestHeaders.Add("Origin", "https://dam.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
    }
    [Fact]
    public async Task LocalWorkflowWorksAlongsidePublicSettingsAndCannotAuthenticatePublicEndpoints()
    {
        var runner = await RunnerAsync("local-" + Guid.NewGuid().ToString("N")[..8]);
        var local = Client("http://localhost"); local.DefaultRequestHeaders.Add("X-Test-Peer", "127.0.0.1");
        var response = await local.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string> { ["username"] = "admin", ["password"] = Password }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("DeveAgentManager.Auth=")).Split(';')[0];
        local.DefaultRequestHeaders.Add("Cookie", cookie);
        var link = (await local.GetAsync($"/editor/runners/{runner}")).Headers.Location!;
        Assert.Equal("localhost", link.Host); Assert.Equal(8081, link.Port);
        var editor = Client("http://localhost:8081"); editor.DefaultRequestHeaders.Add("X-Test-Peer", "127.0.0.1"); editor.DefaultRequestHeaders.Add("X-Test-Port", "8081");
        Assert.Equal(HttpStatusCode.Redirect, (await editor.ExchangeEditorAsync(link)).StatusCode);
        var publicClient = Client(); publicClient.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await publicClient.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task TicketsAreAtomicOriginBoundAndSessionRevocable()
    {
        var name = "public-" + Guid.NewGuid().ToString("N")[..8];
        var runner = await RunnerAsync(name);
        var portal = Client(); var cookie = await LoginAsync(portal);
        var link = (await portal.GetAsync($"/editor/runners/{runner}")).Headers.Location!;
        Assert.Equal($"{name}.code.dam.test", link.Host); Assert.Equal("", link.Query); Assert.Contains("ticket=", link.Fragment);
        var editor = Client(link.GetLeftPart(UriPartial.Authority));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => editor.ExchangeEditorAsync(link)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Redirect);
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
        var editorCookie = results.Single(r => r.StatusCode == HttpStatusCode.Redirect).Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-DAM.Editor=", editorCookie); Assert.Contains("path=/", editorCookie);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync("/login")).StatusCode);
        var copied = Client(); copied.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await portal.PostAccountAsync("/account/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/me")).StatusCode);
        editor.DefaultRequestHeaders.Add("Cookie", editorCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await editor.GetAsync($"/runners/{runner}/code/")).StatusCode);
    }
    [Fact]
    public async Task TwoUsersCannotGetTicketsForEachOthersRunners_AndHostsCannotBeSwapped()
    {
        var users = _manager.Services.GetRequiredService<UserService>();
        var first = await users.CreateAsync(TestUsers.Admin, "first-" + Guid.NewGuid().ToString("N")[..8], null, Password, UserRoles.None);
        var second = await users.CreateAsync(TestUsers.Admin, "second-" + Guid.NewGuid().ToString("N")[..8], null, Password, UserRoles.None);
        await users.ChangeOwnPasswordAsync(first.Id, Password, Password + "-own");
        await users.ChangeOwnPasswordAsync(second.Id, Password, Password + "-own");
        var name1 = "first-" + Guid.NewGuid().ToString("N")[..8];
        var name2 = "second-" + Guid.NewGuid().ToString("N")[..8];
        var runner1 = await RunnerAsync(name1); var runner2 = await RunnerAsync(name2);
        await using (var db = await _manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
        {
            await db.Runners.Where(r => r.Id == runner1).ExecuteUpdateAsync(u => u.SetProperty(r => r.OwnerId, first.Id));
            await db.Runners.Where(r => r.Id == runner2).ExecuteUpdateAsync(u => u.SetProperty(r => r.OwnerId, second.Id));
        }
        var portal1 = Client(); await LoginAsync(portal1, first.Username, Password + "-own");
        var portal2 = Client(); await LoginAsync(portal2, second.Username, Password + "-own");
        Assert.Equal(HttpStatusCode.NotFound, (await portal1.GetAsync($"/editor/runners/{runner2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await portal2.GetAsync($"/editor/runners/{runner1}")).StatusCode);
        var link = (await portal1.GetAsync($"/editor/runners/{runner1}")).Headers.Location!;
        var wrongHost = new Uri(link.ToString().Replace(name1, name2));
        Assert.Equal(HttpStatusCode.NotFound, (await Client().ExchangeEditorAsync(wrongHost)).StatusCode);
        var alternate = new Uri(link.ToString().Replace("dam.test", "alternate.test"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().ExchangeEditorAsync(alternate)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Client().ExchangeEditorAsync(link)).StatusCode);
    }

    [Fact]
    public async Task RevocationClosesRegisteredLiveConnections()
    {
        var sessions = _manager.Services.GetRequiredService<SignInSessions>();
        var users = _manager.Services.GetRequiredService<UserService>();
        var user = (await users.GetAsync(User.BootstrapId))!;
        var session = await sessions.CreateAsync(user, false, "test", "test");
        var closed = false;
        using var registration = _manager.Services.GetRequiredService<OpenConnections>().Register(CurrentUser.From(user), user.SecurityStamp,
            (_, _) => Task.FromResult(true), () => closed = true, session.Id);
        await sessions.RevokeAsync(user.Id, session.Id);
        Assert.True(closed);
    }

    [Fact]
    public async Task ExpiryClosesLiveConnectionsWithoutAnAccessChangeEvent()
    {
        var sessions = _manager.Services.GetRequiredService<SignInSessions>();
        var user = (await _manager.Services.GetRequiredService<UserService>().GetAsync(User.BootstrapId))!;
        var session = await sessions.CreateAsync(user, false, "test", "test");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _manager.Services.GetRequiredService<OpenConnections>().Register(CurrentUser.From(user), user.SecurityStamp,
            (_, _) => Task.FromResult(true), () => closed.TrySetResult(), session.Id);
        _manager.Clock.Advance(TimeSpan.FromDays(8));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task SensitiveActionsRequireRecentAuthentication()
    {
        var portal = Client(); await LoginAsync(portal);
        _manager.Clock.Advance(TimeSpan.FromMinutes(11));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        { Content = new StringContent("{\"username\":\"recent-auth-test\",\"password\":\"test-password-1234\",\"roles\":0}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", "https://dam.test"); request.Headers.Add(DeveAgentManager.Contracts.ApiHeaders.Request, "1");
        var result = await portal.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Contains("Confirm your identity", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TicketExpiryAndSessionAbsoluteExpiry()
    {
        var sessions = _manager.Services.GetRequiredService<SignInSessions>();
        var user = (await _manager.Services.GetRequiredService<UserService>().GetAsync(User.BootstrapId))!;
        var session = await sessions.CreateAsync(user, false, "test", "test");
        var ticket = await sessions.IssueEditorTicketAsync(session.Id, Guid.Empty, "https://test");
        _manager.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await sessions.ConsumeEditorTicketAsync(ticket, Guid.Empty, "https://test"));
        for (var i = 0; i < 5; i++) { _manager.Clock.Advance(TimeSpan.FromDays(6)); await sessions.ValidateAsync(session.Id, user.Id, user.SecurityStamp, touch: true); }
        Assert.Null(await sessions.ValidateAsync(session.Id, user.Id, user.SecurityStamp));
    }
    [Fact]
    public async Task TotpReplayAndRecoveryAreSingleUse()
    {
        var users = _manager.Services.GetRequiredService<UserService>();
        var user = await users.CreateAsync(TestUsers.Admin, "totp-" + Guid.NewGuid().ToString("N")[..8], null, Password, UserRoles.None);
        var factors = _manager.Services.GetRequiredService<SecondFactors>();
        var secret = await factors.BeginAsync(user.Id);
        var code = SecondFactors.Code(secret, _manager.Clock.GetUtcNow().ToUnixTimeSeconds() / 30);
        var recovery = await factors.ConfirmAsync(user.Id, code);
        Assert.False(await factors.VerifyAsync(user.Id, code));
        _manager.Clock.Advance(TimeSpan.FromSeconds(30));
        code = SecondFactors.Code(secret, _manager.Clock.GetUtcNow().ToUnixTimeSeconds() / 30);
        var attempts = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => factors.VerifyAsync(user.Id, code)));
        Assert.Single(attempts, v => v);
        var recoveries = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => factors.VerifyAsync(user.Id, recovery[0])));
        Assert.Single(recoveries, v => v);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factors.EnforceAsync("everyone"));
    }
    [Fact]
    public async Task FailedSecondFactorDoesNotIssueCredentialsOrRecordSuccessfulSignIn()
    {
        using var manager = new Manager();
        var users = manager.Services.GetRequiredService<UserService>();
        var user = await users.CreateAsync(TestUsers.Admin, "factor-login", null, Password, UserRoles.None);
        user = await users.ChangeOwnPasswordAsync(user.Id, Password, Password + "-own");
        var factors = manager.Services.GetRequiredService<SecondFactors>();
        var secret = await factors.BeginAsync(user.Id);
        var recovery = await factors.ConfirmAsync(user.Id, SecondFactors.Code(secret, manager.Clock.GetUtcNow().ToUnixTimeSeconds() / 30));
        var client = manager.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://dam.test"), AllowAutoRedirect = false, HandleCookies = false });
        var wrong = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string>
            { ["username"] = user.Username, ["password"] = Password + "-own" }));
        Assert.Contains("failed=1", wrong.Headers.Location?.ToString());
        Assert.False(wrong.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("__Host-DAM.Auth=")));
        Assert.Null((await users.GetAsync(user.Id))!.LastSignInAt);
        var success = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string>
            { ["username"] = user.Username, ["password"] = Password + "-own", ["code"] = recovery[0] }));
        Assert.Equal("/", success.Headers.Location?.ToString());
        Assert.NotNull((await users.GetAsync(user.Id))!.LastSignInAt);
    }

    [Fact]
    public async Task GainingAnEnforcedRoleClosesAnUnenrolledUsersLiveConnections()
    {
        using var manager = new Manager();
        var users = manager.Services.GetRequiredService<UserService>();
        var factors = manager.Services.GetRequiredService<SecondFactors>();
        var sessions = manager.Services.GetRequiredService<SignInSessions>();
        var secret = await factors.BeginAsync(User.BootstrapId);
        await factors.ConfirmAsync(User.BootstrapId, SecondFactors.Code(secret, manager.Clock.GetUtcNow().ToUnixTimeSeconds() / 30));
        var plain = await users.CreateAsync(TestUsers.Admin, "unenrolled", null, Password, UserRoles.None);
        plain = await users.ChangeOwnPasswordAsync(plain.Id, Password, Password + "-own");
        await factors.EnforceAsync("roles");
        var session = await sessions.CreateAsync(plain, false, "test", "test");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = manager.Services.GetRequiredService<OpenConnections>().Register(CurrentUser.From(plain), plain.SecurityStamp,
            (_, _) => Task.FromResult(true), () => closed.TrySetResult(), session.Id);
        await users.UpdateAsync(TestUsers.Admin, plain.Id, null, UserRoles.RunnerCreator, false);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ThrottleHasConcurrencyAndAccountBounds()
    {
        var throttle = new LoginThrottle(new Clock());
        var leases = Enumerable.Range(0, 4).Select(i => throttle.TryEnter("ip", "account")).ToArray();
        Assert.All(leases, l => Assert.NotNull(l)); Assert.Null(throttle.TryEnter("another", "another"));
        foreach (var lease in leases) lease!.Dispose();
        for (var i = 4; i < 20; i++) { using var lease = throttle.TryEnter("ip-" + i, "account"); Assert.NotNull(lease); }
        Assert.Null(throttle.TryEnter("new", "account"));
    }
    [Fact]
    public void TotpMatchesRfc6238Vector() => Assert.Equal("287082", SecondFactors.Code("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", 1));
}
