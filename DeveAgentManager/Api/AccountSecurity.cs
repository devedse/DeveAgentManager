using DeveAgentManager.Core.Users;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Api;

public static class AccountSecurity
{
    public static async Task<bool> RecentAsync(HttpContext context)
    {
        var user = SignIn.Current(context);
        if (user is null) return false;
        var session = await context.RequestServices.GetRequiredService<SignInSessions>().ValidateAsync(
            SignIn.SessionId(context), user.Id, SignIn.SecurityStamp(context), ct: context.RequestAborted);
        return session is not null && session.AuthenticatedAt > context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime - SignInSessions.RecentLifetime;
    }

    public static void MapSecurityForms(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/security/{action}", async (string action, HttpContext context, CurrentUser user,
            UserService users, SecondFactors factors, SignInSessions sessions, LoginThrottle throttle) =>
        {
            if (!await SignIn.ValidateFormAsync(context)) return Results.StatusCode(403);
            var form = await context.Request.ReadFormAsync();
            var ct = context.RequestAborted;
            if (form["password"].ToString().Length > UserService.MaxPasswordLength || form["code"].ToString().Length > 100)
                return Results.StatusCode(400);
            try
            {
                if (action == "reauth")
                {
                    using var lease = throttle.TryEnter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", user.Username);
                    if (lease is null) { context.Response.Headers.RetryAfter = "300"; return Results.StatusCode(429); }
                    if (await users.SignInAsync(user.Username, form["password"].ToString(), ct) is null
                        || await factors.EnabledAsync(user.Id, ct) && !await factors.VerifyAsync(user.Id, form["code"].ToString(), ct))
                    {
                        await context.RequestServices.GetRequiredService<AuditLog>().RecordAsync(user, AuditActions.SignInFailed,
                            "Identity confirmation", $"IP: {context.Connection.RemoteIpAddress}", ct);
                        return Results.LocalRedirect("/security?error=Authentication%20failed");
                    }
                    await sessions.ReauthenticateAsync(SignIn.SessionId(context), ct);
                    await context.RequestServices.GetRequiredService<AuditLog>().RecordAsync(user, AuditActions.IdentityConfirmed,
                        "Portal session", $"IP: {context.Connection.RemoteIpAddress}", ct);
                    return Results.LocalRedirect("/security");
                }
                if (!await RecentAsync(context)) return Results.LocalRedirect("/security?error=Confirm%20your%20password%20first");
                // Enrollment and removal verify short codes too. They must not bypass the guessing budget simply
                // because the browser has a recent session; coordinate them with login and password verification.
                using var factorLease = action is "confirm" or "disable"
                    ? throttle.TryEnter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", user.Username)
                    : null;
                if ((action is "confirm" or "disable") && factorLease is null)
                { context.Response.Headers.RetryAfter = "300"; return Results.StatusCode(429); }
                switch (action)
                {
                    case "begin":
                        await factors.BeginAsync(user.Id, ct);
                        break;
                    case "confirm":
                        var codes = await factors.ConfirmAsync(user.Id, form["code"].ToString(), ct);
                        foreach (var other in await sessions.ListAsync(user.Id, ct))
                            if (other.Id != SignIn.SessionId(context)) await sessions.RevokeAsync(user.Id, other.Id, ct);
                        await context.RequestServices.GetRequiredService<AuditLog>().RecordAsync(user, AuditActions.AccountSecurityChanged,
                            "Authenticator enabled", $"IP: {context.Connection.RemoteIpAddress}", ct);
                        // A POST response renders codes once; never place them in URLs, cookies or logs.
                        context.Response.Headers.CacheControl = "no-store";
                        return Results.Content("<!doctype html><html><head><meta name=referrer content=no-referrer><title>Recovery codes</title></head><body><h1>Save these recovery codes</h1><p>Each can be used once instead of an authenticator code. Store them somewhere safe.</p><pre>"
                            + string.Join('\n', codes) + "</pre><a href=/security>Back to account security</a></body></html>", "text/html");
                    case "disable":
                        if (await factors.RequiredAsync(user, ct)) throw new InvalidOperationException("Your account requires a second factor. A user manager must relax enforcement before you disable it.");
                        if (!await factors.VerifyAsync(user.Id, form["code"].ToString(), ct)) throw new ArgumentException("Enter a current authenticator or recovery code.");
                        await factors.DisableAsync(user.Id, ct);
                        foreach (var other in await sessions.ListAsync(user.Id, ct))
                            if (other.Id != SignIn.SessionId(context)) await sessions.RevokeAsync(user.Id, other.Id, ct);
                        break;
                    case "revoke":
                        if (!Guid.TryParse(form["id"], out var id)) throw new ArgumentException("Invalid session.");
                        await sessions.RevokeAsync(user.Id, id, ct);
                        break;
                    case "enforce":
                        if (!await factors.CanUseAsync(user, ct)) return Results.StatusCode(403);
                        if ((user.Roles & DeveAgentManager.Core.Data.UserRoles.UserManager) == 0) return Results.StatusCode(403);
                        await factors.EnforceAsync(form["mode"].ToString(), ct);
                        break;
                    default: return Results.NotFound();
                }
                await context.RequestServices.GetRequiredService<AuditLog>().RecordAsync(user, AuditActions.AccountSecurityChanged, action, $"IP: {context.Connection.RemoteIpAddress}", ct);
                return Results.LocalRedirect("/security");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { return Results.LocalRedirect("/security?error=" + Uri.EscapeDataString(ex.Message)); }
        });
    }

    public static IApplicationBuilder UseEnrollmentRequirement(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (SignIn.Current(context) is { MustChangePassword: false } user
            && context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null
            && !context.Request.Path.StartsWithSegments("/security") && !context.Request.Path.StartsWithSegments("/account")
            && !context.Request.Path.StartsWithSegments("/logout")
            && !await context.RequestServices.GetRequiredService<SecondFactors>().CanUseAsync(user, context.RequestAborted))
        {
            if (RequestSecurity.IsMachinePath(context.Request.Path)) context.Response.StatusCode = 403;
            else context.Response.Redirect("/security?error=Enroll%20an%20authenticator%20to%20continue");
            return;
        }
        await next(context);
    });
}
