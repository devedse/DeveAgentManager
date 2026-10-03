// Short in-memory requests: cancelling them with the test run adds nothing.
#pragma warning disable xUnit1051

using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// The Manager's HTTP surface: everything needs the login; requests that change something need the Portal's header and
/// our origin; sockets refuse other origins; secrets entered through the API never come back out of it.
/// </summary>
public sealed class ApiSecurityTests : IClassFixture<ApiSecurityTests.Manager>
{
    private const string Password = "test-password-1234";

    private readonly Manager _manager;

    public ApiSecurityTests(Manager manager) => _manager = manager;

    public sealed class Manager : WebApplicationFactory<DeveAgentManager.Api.DtoMapper>
    {
        private readonly string _data = Directory.CreateTempSubdirectory("dam-api-").FullName;

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _data);
            builder.UseSetting("ADMIN_USERNAME", "admin");
            builder.UseSetting("ADMIN_PASSWORD", Password);
            builder.UseSetting("PROXMOX_URL", "");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try
            {
                Directory.Delete(_data, true);
            }
            catch (IOException)
            {
                // Still in use by a background service; it's a temp folder.
            }
        }
    }

    private HttpClient Anonymous() => _manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> SignedInAsync()
    {
        var client = Anonymous();
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "admin", ["password"] = Password, ["returnUrl"] = "/" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
        return client;
    }

    [Theory]
    [InlineData("/api/info")]
    [InlineData("/api/agents/attention")]
    [InlineData("/api/runners")]
    [InlineData("/api/runners/00000000-0000-0000-0000-000000000001/usage")]
    [InlineData("/api/runners/00000000-0000-0000-0000-000000000001/timezone")]
    [InlineData("/api/settings/timezone")]
    [InlineData("/api/settings/timezones")]
    [InlineData("/api/agents/00000000-0000-0000-0000-000000000001/limit")]
    [InlineData("/api/accounts")]
    [InlineData("/api/settings/profile")]
    public async Task Api_WithoutLogin_Is401(string path) => Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync(path)).StatusCode);

    [Fact]
    public async Task Hub_WithoutLogin_Is401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsync(LiveHub.Path + "/negotiate?negotiateVersion=1", null)).StatusCode);

    [Fact]
    public async Task Pages_WithoutLogin_RedirectToLogin()
    {
        var response = await Anonymous().GetAsync("/agents");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://localhost/login", response.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync("/login")).StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_IsRefused()
    {
        var client = Anonymous();
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "admin", ["password"] = "wrong", ["returnUrl"] = "/agents" }));
        Assert.Contains("failed=1", response.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/info")).StatusCode);
    }

    [Fact]
    public async Task Login_ThenApiWorks_AndLogoutEndsIt()
    {
        var client = await SignedInAsync();
        var info = await client.GetFromJsonAsync<ManagerInfoDto>("/api/info", ApiJson.Options);
        Assert.Equal("admin", info!.AdminUsername);
        Assert.Contains(info.Harnesses, h => h.Id == "pi");

        await client.PostAsync("/account/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/info")).StatusCode);
    }

    [Fact]
    public async Task Changes_NeedThePortalHeader()
    {
        var client = await SignedInAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/agents/{Guid.NewGuid()}/seen", new MarkAgentSeenRequest(1), ApiJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/agents/{Guid.NewGuid()}/attention/dismiss", new DismissAgentAttentionRequest(new string('A', 64)), ApiJson.Options)).StatusCode);
        var request = new AddAccountRequest("claude", "x", AccountKind.ApiKey, "sk-ant-test");
        var response = await client.PostAsJsonAsync("/api/accounts", request, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DefaultTimezoneIsValidatedAndStored_AndSchedulingNeedsPortalHeader()
    {
        var client = await SignedInAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/agents/{Guid.NewGuid()}/limit/continue", new ScheduleContinueRequest(), ApiJson.Options)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/timezone", new TimeZoneRequest("Not/AZone"), ApiJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings/timezone", new TimeZoneRequest("Europe/Amsterdam"), ApiJson.Options)).StatusCode);
        Assert.Equal("Europe/Amsterdam", (await client.GetFromJsonAsync<TimeZoneConfigDto>("/api/settings/timezone", ApiJson.Options))!.DefaultTimeZoneId);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings/timezone", new TimeZoneRequest("Etc/UTC"), ApiJson.Options)).StatusCode);
    }

    [Fact]
    public async Task ManualScheduleDoesNotRequireADetectedLimitTimestamp()
    {
        var client = await SignedInAsync();
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        var path = $"/api/agents/{Guid.NewGuid()}/limit/continue";
        var manual = await client.PostAsJsonAsync(path, new ScheduleContinueRequest(ContinueAfterMinutes: 1, Manual: true), ApiJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, manual.StatusCode);
        Assert.Contains("Agent not found", await manual.Content.ReadAsStringAsync());
        var detected = await client.PostAsJsonAsync(path, new ScheduleContinueRequest(ContinueAfterMinutes: 1), ApiJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, detected.StatusCode);
        Assert.Contains("Agent not found", await detected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Changes_FromAnotherOrigin_AreRefused()
    {
        var client = await SignedInAsync();
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/accounts") { Content = JsonContent.Create(new AddAccountRequest("claude", "x", AccountKind.ApiKey, "sk-ant-test"), options: ApiJson.Options) };
        message.Headers.Add(ApiHeaders.Request, "1");
        message.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(message)).StatusCode);
    }

    [Fact]
    public async Task Secrets_GoIn_ButNeverComeOut()
    {
        var client = await SignedInAsync();
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        var added = await client.PostAsJsonAsync("/api/accounts", new AddAccountRequest("claude", "Work", AccountKind.Subscription, "sk-ant-oat01-very-secret-token"), ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        foreach (var path in new[] { "/api/accounts", "/api/info", "/api/runners", "/api/agents", "/api/sessions" })
        {
            var body = await client.GetStringAsync(path);
            Assert.DoesNotContain("very-secret", body);
            Assert.DoesNotContain("protectedSecret", body, StringComparison.OrdinalIgnoreCase);
        }

        var accounts = await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts", ApiJson.Options);
        Assert.Contains(accounts!, a => a.Name == "Work" && a.IsDefault);
    }

    [Fact]
    public async Task ApiErrors_AreProblemDetails_WithTheMessage()
    {
        var client = await SignedInAsync();
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        var response = await client.PostAsJsonAsync("/api/accounts", new AddAccountRequest("claude", "x", AccountKind.ApiKey, "  "), ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The login is empty.", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/runners/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Sockets_FromAnotherOrigin_AreRefused()
    {
        var client = await SignedInAsync();
        var hub = new HttpRequestMessage(HttpMethod.Post, LiveHub.Path + "/negotiate?negotiateVersion=1");
        hub.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(hub)).StatusCode);

        var cookie = await LoginCookieAsync();
        var sockets = _manager.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "https://evil.example";
        };
        await Assert.ThrowsAnyAsync<Exception>(() => sockets.ConnectAsync(new Uri($"ws://localhost{TerminalSocket.Path}?kind=shell&id={Guid.NewGuid()}"), CancellationToken.None));
    }

    [Fact]
    public async Task TerminalSocket_WithLogin_OpensAndReportsProblems()
    {
        var cookie = await LoginCookieAsync();
        var sockets = _manager.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "http://localhost";
        };
        using var socket = await sockets.ConnectAsync(new Uri($"ws://localhost{TerminalSocket.Path}?kind=shell&id={Guid.NewGuid()}"), CancellationToken.None);
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count);
        Assert.Contains("\"type\":\"closed\"", text);
        Assert.Contains("Runner not found", text);
    }

    [Fact]
    public async Task TerminalSocket_WithoutLogin_IsRefused()
    {
        var sockets = _manager.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() => sockets.ConnectAsync(new Uri($"ws://localhost{TerminalSocket.Path}?kind=shell&id={Guid.NewGuid()}"), CancellationToken.None));
    }

    private async Task<string> LoginCookieAsync()
    {
        var response = await Anonymous().PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "admin", ["password"] = Password }));
        return string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }
}
