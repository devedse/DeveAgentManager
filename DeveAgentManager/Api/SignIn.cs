using System.Security.Claims;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;

namespace DeveAgentManager.Api;

/// <summary>
/// Signing in with a username and password (a cookie), and who a request acts for.
/// <list type="bullet">
/// <item>The cookie holds the user, security stamp and revocable session. Every request validates them, so a
/// disabled user, a changed password or a reset ends every sign-in at once.</item>
/// <item>Each request's <see cref="CurrentUser"/> is loaded fresh (roles and access always as they are now).</item>
/// <item>A user who must choose a new password (it was set by a user manager, or is an initial setup password) gets
/// only the change-password page until they did; the API, live hub and terminals refuse them.</item>
/// </list>
/// </summary>
public static class SignIn
{
    public const string SessionClaim = "dam:session";
    public const string PublicScheme = "Public";
    public const string LocalScheme = "Local";
    public const string StampClaim = "dam:stamp";
    private const string UserItem = "dam:user";

    public static IServiceCollection AddManagerSignIn(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<LoginThrottle>();
        services.AddAntiforgery(o => o.Cookie.Name = "DAM.Antiforgery");
        services.AddAuthentication("Manager")
            .AddPolicyScheme("Manager", null, o => o.ForwardDefaultSelector = c => DeploymentBoundary.IsLocal(c) ? LocalScheme : PublicScheme)
            .AddCookie(LocalScheme, cookie => ConfigureCookie(cookie, "DeveAgentManager.Auth", false))
            .AddCookie(PublicScheme, cookie => ConfigureCookie(cookie, "__Host-DAM.Auth", true));
        // Everything needs a sign-in (pages, the API, the live hub, terminals) unless it is explicitly anonymous.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddScoped(provider => Current(provider.GetRequiredService<IHttpContextAccessor>().HttpContext)
            ?? throw new UnauthorizedAccessException("Sign in first."));
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions cookie, string name, bool secure)
    {
        cookie.LoginPath = "/login";
        cookie.Cookie.Name = name;
        cookie.Cookie.Path = "/";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = SignInSessions.IdleLifetime;
        cookie.SlidingExpiration = true;
        cookie.Events.OnValidatePrincipal = ValidateAsync;
        cookie.Events.OnRedirectToLogin = context =>
        {
            if (RequestSecurity.IsMachinePath(context.Request.Path)) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        cookie.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    }

    public static Guid SessionId(HttpContext context) => Guid.TryParse(context.User.FindFirstValue(SessionClaim), out var id) ? id : Guid.Empty;
    public static string Scheme(HttpContext context) => DeploymentBoundary.IsLocal(context) ? LocalScheme : PublicScheme;

    public static async Task<bool> ValidateFormAsync(HttpContext context)
    {
        if (!RequestSecurity.IsSameOrigin(context.Request, requireOrigin: true)) return false;
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    /// <summary>The user the request acts for, or null when it isn't signed in.</summary>
    public static CurrentUser? Current(HttpContext? context) => context?.Items[UserItem] as CurrentUser;

    /// <summary>The security stamp the request's sign-in has, to notice when it no longer counts (see <see cref="OpenConnections"/>).</summary>
    public static string? SecurityStamp(HttpContext context) => context.User.FindFirstValue(StampClaim);

    /// <summary>
    /// Until a user chose their own password, their pages go to the change-password page (the API, live hub and terminals
    /// refuse them: see <see cref="EndpointAccess"/> and <see cref="UsableUser"/>).
    /// </summary>
    public static IApplicationBuilder UsePasswordChangeFirst(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (Current(context) is { MustChangePassword: true } && HttpMethods.IsGet(context.Request.Method) && !RequestSecurity.IsMachinePath(path)
            && !path.StartsWithSegments("/account") && !path.StartsWithSegments("/password") && !path.StartsWithSegments("/_framework") && !path.StartsWithSegments("/_content")
            && Path.GetExtension(path.Value) is null or "")
        {
            context.Response.Redirect($"/password?returnUrl={Uri.EscapeDataString(path + context.Request.QueryString)}");
            return;
        }

        await next();
    });

    /// <summary>For the live hub and terminals: the signed-in user, unless they first need to choose a new password.</summary>
    public static CurrentUser? UsableUser(HttpContext context) => Current(context) is { MustChangePassword: false } user ? user : null;

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/login", async (HttpContext context, UserService users) =>
        {
            if (!await ValidateFormAsync(context)) return Results.StatusCode(403);
            var form = await context.Request.ReadFormAsync();
            var returnUrl = LocalUrl(form["returnUrl"]);
            if (form["username"].ToString().Length > 64 || form["password"].ToString().Length > UserService.MaxPasswordLength)
                return Results.StatusCode(400);
            using var lease = context.RequestServices.GetRequiredService<LoginThrottle>().TryEnter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", form["username"].ToString());
            if (lease is null) { context.Response.Headers.RetryAfter = "300"; return Results.StatusCode(429); }
            var user = await users.SignInAsync(form["username"].ToString(), form["password"].ToString(), context.RequestAborted);
            var factors = context.RequestServices.GetRequiredService<SecondFactors>();
            if (user is not null && await factors.EnabledAsync(user.Id, context.RequestAborted)
                && !await factors.VerifyAsync(user.Id, form["code"].ToString(), context.RequestAborted)) user = null;
            await context.RequestServices.GetRequiredService<AuditLog>().RecordAsync(user is null ? null : CurrentUser.From(user),
                user is null ? DeveAgentManager.Core.Data.AuditActions.SignInFailed : DeveAgentManager.Core.Data.AuditActions.SignedIn, "Portal sign-in", $"IP: {context.Connection.RemoteIpAddress}", context.RequestAborted);
            if (user is null)
            {
                return Results.LocalRedirect($"/login?failed=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            await users.RecordSignInAsync(user.Id, context.RequestAborted);
            await SignInAsync(context, user.Id, user.Username, user.SecurityStamp, persistent: form["remember"] == "on");
            return Results.LocalRedirect(user.MustChangePassword ? $"/password?returnUrl={Uri.EscapeDataString(returnUrl)}" : returnUrl);
        }).AllowAnonymous();

        app.MapPost("/account/password", async (HttpContext context, CurrentUser user, UserService users) =>
        {
            if (!await ValidateFormAsync(context)) return Results.StatusCode(403);
            var form = await context.Request.ReadFormAsync();
            var returnUrl = LocalUrl(form["returnUrl"]);
            string? error = null;
            if (form["newPassword"] != form["confirmPassword"])
            {
                error = "The new passwords differ.";
            }
            else
            {
                try
                {
                    var changed = await users.ChangeOwnPasswordAsync(user.Id, form["currentPassword"].ToString(), form["newPassword"].ToString(), context.RequestAborted);
                    // The new security stamp ends every other sign-in; this one continues with it.
                    var persistent = (await context.AuthenticateAsync()).Properties?.IsPersistent ?? false;
                    await SignInAsync(context, changed.Id, changed.Username, changed.SecurityStamp, persistent);
                    return Results.LocalRedirect(returnUrl);
                }
                catch (ArgumentException ex)
                {
                    error = ex.Message;
                }
            }

            return Results.LocalRedirect($"/password?error={Uri.EscapeDataString(error)}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        });

        app.MapPost("/account/logout", async (HttpContext context) =>
        {
            if (!await ValidateFormAsync(context)) return Results.StatusCode(403);
            if (Current(context) is { } user)
                await context.RequestServices.GetRequiredService<SignInSessions>().RevokeAsync(user.Id, SessionId(context), context.RequestAborted);
            await context.SignOutAsync(Scheme(context));
            return Results.LocalRedirect("/login");
        });
    }

    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
        var user = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await users.ValidateAsync(id, principal!.FindFirstValue(StampClaim), context.HttpContext.RequestAborted)
            : null;
        var session = user is null ? null : await context.HttpContext.RequestServices.GetRequiredService<SignInSessions>().ValidateAsync(
            Guid.TryParse(principal!.FindFirstValue(SessionClaim), out var sessionId) ? sessionId : Guid.Empty, user.Id, principal!.FindFirstValue(StampClaim), DeploymentBoundary.IsLocal(context.HttpContext),
            touch: true, ct: context.HttpContext.RequestAborted);
        if (user is null || session is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        context.HttpContext.Items[UserItem] = user;
    }

    private static async Task SignInAsync(HttpContext context, Guid userId, string username, string stamp, bool persistent)
    {
        var sessions = context.RequestServices.GetRequiredService<SignInSessions>();
        if (Current(context) is { } previous) await sessions.RevokeAsync(previous.Id, SessionId(context), context.RequestAborted);
        var session = await sessions.CreateAsync(new DeveAgentManager.Core.Data.User { Id = userId, SecurityStamp = stamp },
            DeploymentBoundary.IsLocal(context), context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, username), new Claim(StampClaim, stamp), new Claim(SessionClaim, session.Id.ToString())],
            Scheme(context));
        await context.SignInAsync(Scheme(context), new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = persistent, ExpiresUtc = session.ExpiresAt });
    }

    /// <summary>A path on this site to go back to; anything else (another site) becomes the start page.</summary>
    internal static string LocalUrl(string? url) =>
        string.IsNullOrEmpty(url) || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("/\\", StringComparison.Ordinal) ? "/" : url;
}
