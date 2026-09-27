using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Api;
using DeveAgentManager.Components;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using DeveAgentManager.Providers.Proxmox;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var managerOptions = ManagerOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddDeveAgentManagerCore(managerOptions);
builder.Services.AddProxmoxProvider(builder.Configuration);
builder.Services.AddPiHarness();
builder.Services.AddClaudeHarness();
builder.Services.AddCodexHarness();

// The Portal (DeveAgentManager.Client) runs in the browser; the Manager serves it and its API, live hub and terminals.
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
builder.Services.ConfigureHttpJsonOptions(json => ApiJson.Configure(json.SerializerOptions));
builder.Services.AddSignalR().AddJsonProtocol(json => ApiJson.Configure(json.PayloadSerializerOptions));
builder.Services.AddSingleton<DtoMapper>();
builder.Services.AddSingleton<OperationNotifier>();
builder.Services.AddHostedService<LiveUpdatesBridge>();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/login";
        cookie.Cookie.Name = "DeveAgentManager.Auth";
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
        cookie.SlidingExpiration = true;
        // The API, the live hub and terminals answer 401/403; only pages redirect to the login page.
        cookie.Events.OnRedirectToLogin = context =>
        {
            if (RequestSecurity.IsMachinePath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        cookie.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
// Everything needs a login (pages, the API, the live hub, terminals) unless it is explicitly anonymous.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHealthChecks();

builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    // The Portal normally runs behind a reverse proxy that terminates HTTPS.
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

if (managerOptions.UsesDefaultAdminPassword)
{
    app.Logger.LogWarning("ADMIN_PASSWORD is not set: the Portal login is {User} / {Password}. Set ADMIN_PASSWORD in the compose file.", managerOptions.AdminUsername, ManagerOptions.DefaultAdminPassword);
}

app.UseForwardedHeaders();

// Error and "not found" pages are for pages; the API answers with problem details.
app.UseWhen(context => !RequestSecurity.IsMachinePath(context.Request.Path), pages =>
{
    if (!app.Environment.IsDevelopment())
    {
        pages.UseExceptionHandler("/Error", createScopeForErrors: true);
    }

    pages.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();
app.UseSameOriginForSockets();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapPost("/account/login", async (HttpContext context, ManagerOptions options) =>
{
    var form = await context.Request.ReadFormAsync();
    var returnUrl = form["returnUrl"].ToString();
    if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
    {
        returnUrl = "/";
    }

    if (!FixedTimeEquals(form["username"].ToString(), options.AdminUsername) | !FixedTimeEquals(form["password"].ToString(), options.AdminPassword))
    {
        // Slow down password guessing a little.
        await Task.Delay(TimeSpan.FromSeconds(1));
        return Results.LocalRedirect($"/login?failed=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, options.AdminUsername)], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = form["remember"] == "on" });
    return Results.LocalRedirect(returnUrl);
}).AllowAnonymous();

app.MapPost("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.LocalRedirect("/login");
});

app.MapManagerApi();
app.MapTerminalSocket();
app.MapVSCodeProxy();
app.MapHub<LiveUpdatesHub>(LiveHub.Path);

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(DeveAgentManager.Client.Routes).Assembly);

app.Run();

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
