using DeveAgentManager.Api;
using DeveAgentManager.Components;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using DeveAgentManager.Providers.Proxmox;
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
builder.Services.AddHttpForwarder();
builder.Services.AddHostedService<LiveUpdatesBridge>();
builder.Services.AddProblemDetails();

builder.Services.AddManagerSignIn();
builder.Services.AddSingleton<OpenConnections>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OpenConnections>());
builder.Services.AddHealthChecks();
// Open terminals and VS Code are closed as soon as the Manager stops (OpenConnections); this only bounds anything else
// still running. Database saves are transactions: a stop part-way rolls them back, it never leaves half a change.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));

var boundary = new DeploymentBoundary(managerOptions);
builder.Services.AddSingleton(boundary);
builder.Services.Configure<ForwardedHeadersOptions>(boundary.ConfigureForwarding);

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

await app.Services.GetRequiredService<UserService>().EnsureAdminAsync();
await app.Services.GetRequiredService<UserSettingsStore>().MoveSettingsFromBeforeUsersAsync();
await app.Services.GetRequiredService<DeveAgentManager.Core.Sessions.SessionService>().BackfillMessageTimesAsync();

// Settings from before users existed: say so, instead of silently ignoring them.
foreach (var (setting, instead) in new[]
{
    ("RUNNER_PASSWORD", "every Runner gets a password of its own (shown on its page)"),
    ("GITHUB_PAT", "everyone enters their own GitHub token under My settings → Git and GitHub"),
    ("GIT_USER_NAME", "everyone enters their own git identity under My settings → Git and GitHub"),
    ("GIT_USER_EMAIL", "everyone enters their own git identity under My settings → Git and GitHub"),
})
{
    if (!string.IsNullOrWhiteSpace(app.Configuration[setting]))
    {
        app.Logger.LogWarning("{Setting} is no longer used: {Instead}. Remove it from the compose file.", setting, instead);
    }
}

app.Use(boundary.InvokeAsync);
app.UseForwardedHeaders();
app.Use(boundary.RouteAsync);

// The editor port serves VS Code and nothing else (see EditorEndpoints).
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseEditorOrigin();

app.UsePortalSecurityHeaders();

// Error and "not found" pages are for pages; the API answers with problem details.
app.UseWhen(context => !RequestSecurity.IsMachinePath(context.Request.Path), pages =>
{
    if (!app.Environment.IsDevelopment())
    {
        pages.UseExceptionHandler("/Error", createScopeForErrors: true);
    }

    pages.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
});

app.UseAuthentication();
app.UsePasswordChangeFirst();
app.UseAuthorization();
app.UseSameOriginForSockets();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapAccountEndpoints();
app.MapSecurityForms();
app.MapManagerApi();
app.MapTerminalSocket();
app.MapEditorLinks();
app.MapHub<LiveUpdatesHub>(LiveHub.Path);

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(DeveAgentManager.Client.Routes).Assembly);

app.Run();
