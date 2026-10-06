using System.Security.Claims;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace DeveAgentManager.Api;

/// <summary>
/// Signing in with a username and password (a cookie), and who a request acts for.
/// <list type="bullet">
/// <item>The cookie holds the user's id and security stamp. Every request checks both against the user (cached), so a
/// disabled user, a changed password or a reset ends every sign-in at once.</item>
/// <item>Each request's <see cref="CurrentUser"/> is loaded fresh (roles and access always as they are now).</item>
/// <item>A user who must choose a new password (it was set by a user manager, or is the default admin password) gets
/// only the change-password page until they did; the API, live hub and terminals refuse them.</item>
/// </list>
/// </summary>
public static class SignIn
{
    public const string StampClaim = "dam:stamp";
    private const string UserItem = "dam:user";

    public static IServiceCollection AddManagerSignIn(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.LoginPath = "/login";
                cookie.Cookie.Name = "DeveAgentManager.Auth";
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
                cookie.SlidingExpiration = true;
                cookie.Events.OnValidatePrincipal = ValidateAsync;
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
        // Everything needs a sign-in (pages, the API, the live hub, terminals) unless it is explicitly anonymous.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddScoped(provider => Current(provider.GetRequiredService<IHttpContextAccessor>().HttpContext)
            ?? throw new UnauthorizedAccessException("Sign in first."));
        return services;
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
            && !path.StartsWithSegments("/account") && !path.StartsWithSegments("/_framework") && !path.StartsWithSegments("/_content")
            && Path.GetExtension(path.Value) is null or "")
        {
            context.Response.Redirect($"/account/password?returnUrl={Uri.EscapeDataString(path + context.Request.QueryString)}");
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
            var form = await context.Request.ReadFormAsync();
            var returnUrl = LocalUrl(form["returnUrl"]);
            var user = await users.SignInAsync(form["username"].ToString(), form["password"].ToString(), context.RequestAborted);
            if (user is null)
            {
                // Slows down password guessing (on top of the lockout).
                await Task.Delay(TimeSpan.FromSeconds(1));
                return Results.LocalRedirect($"/login?failed=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            await SignInAsync(context, user.Id, user.Username, user.SecurityStamp, persistent: form["remember"] == "on");
            return Results.LocalRedirect(user.MustChangePassword ? $"/account/password?returnUrl={Uri.EscapeDataString(returnUrl)}" : returnUrl);
        }).AllowAnonymous();

        app.MapPost("/account/password", async (HttpContext context, CurrentUser user, UserService users) =>
        {
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

            return Results.LocalRedirect($"/account/password?error={Uri.EscapeDataString(error)}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        });

        app.MapPost("/account/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync();
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
        if (user is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.HttpContext.Items[UserItem] = user;
    }

    private static Task SignInAsync(HttpContext context, Guid userId, string username, string stamp, bool persistent)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, username), new Claim(StampClaim, stamp)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = persistent });
    }

    /// <summary>A path on this site to go back to; anything else (another site) becomes the start page.</summary>
    private static string LocalUrl(string? url) =>
        string.IsNullOrEmpty(url) || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("/\\", StringComparison.Ordinal) ? "/" : url;
}
