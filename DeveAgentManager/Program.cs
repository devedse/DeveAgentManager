using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Components;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Harnesses.Pi;
using DeveAgentManager.Providers.Proxmox;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

var managerOptions = ManagerOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddDeveAgentManagerCore(managerOptions);
builder.Services.AddProxmoxProvider(builder.Configuration);
builder.Services.AddPiHarness();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = true;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/login";
        cookie.Cookie.Name = "DeveAgentManager.Auth";
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
        cookie.SlidingExpiration = true;
    });
// Everything needs a login (pages, the Blazor connection, terminals) unless it is explicitly anonymous.
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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
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

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
