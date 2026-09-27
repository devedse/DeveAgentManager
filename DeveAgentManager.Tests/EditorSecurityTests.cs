// Short in-memory requests: cancelling them with the test run adds nothing.
#pragma warning disable xUnit1051

using System.Net;
using System.Text.Json;
using DeveAgentManager.Api;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Editor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// VS Code's own origin (the editor port): it serves VS Code and nothing of the Portal, needs a ticket from the Portal,
/// and keeps the Manager's login cookie away from the Runner (and the Runner's cookies away from the browser).
/// </summary>
public sealed class EditorSecurityTests : IClassFixture<EditorSecurityTests.Manager>, IAsyncLifetime
{
    private const string Password = "test-password-1234";
    private const string PortHeader = "X-Test-Local-Port";
    private const int EditorPort = 18081;

    private readonly Manager _manager;
    private readonly Guid _runnerId = Guid.NewGuid();
    private WebApplication? _codeServer;

    public EditorSecurityTests(Manager manager) => _manager = manager;

    public sealed class Manager : WebApplicationFactory<DtoMapper>
    {
        private readonly string _data = Directory.CreateTempSubdirectory("dam-editor-").FullName;

        public FakeUpstreams Upstreams { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _data);
            builder.UseSetting("ADMIN_USERNAME", "admin");
            builder.UseSetting("ADMIN_PASSWORD", Password);
            builder.UseSetting("PROXMOX_URL", "");
            builder.UseSetting("EDITOR_PORT", EditorPort.ToString());
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IEditorUpstreams>(Upstreams);
                // The test server has no ports: a request says which port it came in on.
                services.AddSingleton<IStartupFilter, LocalPortFromHeader>();
            });
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

    private sealed class LocalPortFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (int.TryParse(context.Request.Headers[PortHeader], out var port))
                {
                    context.Connection.LocalPort = port;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>Stands in for the tunnels: code-server is <see cref="_codeServer"/>, with a known session cookie.</summary>
    public sealed class FakeUpstreams : IEditorUpstreams
    {
        public int Port { get; set; }

        public int Forgotten { get; private set; }

        public Task<EditorUpstream> GetAsync(Runner runner, CancellationToken cancellationToken) =>
            Task.FromResult(new EditorUpstream(Port, "code-server-session=runner-session"));

        public void Forget(Guid runnerId) => Forgotten++;

        public IDisposable Use(Guid runnerId) => new MemoryStream();
    }

    public async ValueTask InitializeAsync()
    {
        // A fake code-server: echoes what it got, sets a cookie, and sends /login-please to its login page.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _codeServer = builder.Build();
        _codeServer.Map("/{**path}", (HttpContext context) =>
        {
            if (context.Request.Path == "/login-please")
            {
                return Results.Redirect("./login");
            }

            context.Response.Headers.SetCookie = "stolen=1; Path=/";
            return Results.Json(new
            {
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                host = context.Request.Headers.Host.ToString(),
                cookie = context.Request.Headers.Cookie.ToString(),
            });
        });
        await _codeServer.StartAsync();
        _manager.Upstreams.Port = new Uri(_codeServer.Urls.First()).Port;

        await using var db = await _manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync();
        db.Runners.Add(new Runner
        {
            Id = _runnerId, Name = $"editor-{_runnerId:N}"[..20], ProviderId = "existing", State = RunnerState.Ready, Host = "127.0.0.1",
            Username = "test", Tools = VsCodeTool.ToolId, HomeDirectory = "/home/test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_codeServer is not null)
        {
            await _codeServer.DisposeAsync();
        }
    }

    private HttpClient Client(int? port = null)
    {
        var client = _manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (port is not null)
        {
            client.DefaultRequestHeaders.Add(PortHeader, port.ToString());
        }

        return client;
    }

    private async Task<string> ManagerCookieAsync()
    {
        var response = await Client().PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "admin", ["password"] = Password }));
        return string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }

    /// <summary>Goes through the Portal's link and the editor's sign-in; returns the editor cookie.</summary>
    private async Task<string> EditorCookieAsync(string managerCookie)
    {
        var portal = Client();
        portal.DefaultRequestHeaders.Add("Cookie", managerCookie);
        var link = await portal.GetAsync($"/editor/runners/{_runnerId}");
        Assert.Equal(HttpStatusCode.Redirect, link.StatusCode);
        var auth = link.Headers.Location!;
        Assert.Equal(EditorPort, auth.Port);
        Assert.Equal($"/runners/{_runnerId}/auth", auth.AbsolutePath);

        var signIn = await Client(EditorPort).GetAsync(auth.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal($"/runners/{_runnerId}/code/?folder=%2Fhome%2Ftest", signIn.Headers.Location?.ToString());
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains($"path=/runners/{_runnerId}/", cookie);
        Assert.Contains("httponly", cookie);
        return cookie.Split(';')[0];
    }

    [Theory]
    [InlineData("/api/info")]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/hubs/live/negotiate")]
    [InlineData("/ws/terminal")]
    [InlineData("/editor/ping")]
    public async Task EditorPort_ServesNothingOfThePortal(string path) =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client(EditorPort).GetAsync(path)).StatusCode);

    [Fact]
    public async Task PortalPort_DoesNotServeTheEditor()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("Cookie", await ManagerCookieAsync());
        var response = await client.GetAsync($"/runners/{_runnerId}/code/");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("runner-session", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EditorLink_NeedsTheManagerLogin()
    {
        var response = await Client().GetAsync($"/editor/runners/{_runnerId}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Editor_WithoutItsCookie_OrWithABadTicket_IsRefused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(EditorPort).GetAsync($"/runners/{_runnerId}/code/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(EditorPort).GetAsync($"/runners/{_runnerId}/auth?ticket=made-up")).StatusCode);

        // The Manager's own login cookie is not an editor cookie.
        var client = Client(EditorPort);
        client.DefaultRequestHeaders.Add("Cookie", await ManagerCookieAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/runners/{_runnerId}/code/")).StatusCode);
    }

    [Fact]
    public async Task Ticket_IsForOneRunner()
    {
        var portal = Client();
        portal.DefaultRequestHeaders.Add("Cookie", await ManagerCookieAsync());
        var auth = (await portal.GetAsync($"/editor/runners/{_runnerId}")).Headers.Location!;
        var otherRunner = auth.PathAndQuery.Replace(_runnerId.ToString(), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(EditorPort).GetAsync(otherRunner)).StatusCode);
    }

    [Fact]
    public async Task Proxy_SendsOnlyTheSessionCookie_AndDropsTheRunnersCookies()
    {
        var managerCookie = await ManagerCookieAsync();
        var editorCookie = await EditorCookieAsync(managerCookie);
        var client = Client(EditorPort);
        // A browser sends every cookie of the host, whatever the port.
        client.DefaultRequestHeaders.Add("Cookie", $"{managerCookie}; {editorCookie}");

        var response = await client.GetAsync($"/runners/{_runnerId}/code/static/app.js?v=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upstream = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("/static/app.js", upstream.GetProperty("path").GetString());
        Assert.Equal("?v=1", upstream.GetProperty("query").GetString());
        Assert.Equal("code-server-session=runner-session", upstream.GetProperty("cookie").GetString());
        Assert.Equal("localhost", upstream.GetProperty("host").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Proxy_LogsInAgain_WhenCodeServerAsksForItsLogin()
    {
        var client = Client(EditorPort);
        client.DefaultRequestHeaders.Add("Cookie", await EditorCookieAsync(await ManagerCookieAsync()));
        var before = _manager.Upstreams.Forgotten;

        var response = await client.GetAsync($"/runners/{_runnerId}/code/login-please");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/runners/{_runnerId}/code/login-please", response.Headers.Location?.ToString());
        Assert.Equal(before + 1, _manager.Upstreams.Forgotten);
    }

    [Fact]
    public async Task EditorPath_WithoutSlash_GetsIt() =>
        Assert.Equal($"/runners/{_runnerId}/code/?x=1", (await Client(EditorPort).GetAsync($"/runners/{_runnerId}/code?x=1")).Headers.Location?.ToString());
}
