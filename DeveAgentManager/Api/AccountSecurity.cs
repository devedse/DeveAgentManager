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

    /// <summary>
    /// The forms on /security. The authenticator is optional: setting it up or turning it off asks for the password (and to
    /// turn it off, a code), on the same form. Every password and code check shares the sign-in guessing limits.
    /// </summary>
    public static void MapSecurityForms(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/security/{action}", async (string action, HttpContext context, CurrentUser user,
            UserService users, SecondFactors factors, SignInSessions sessions, LoginThrottle throttle, AuditLog audit) =>
        {
            if (!await SignIn.ValidateFormAsync(context)) return Results.StatusCode(403);
            var form = await context.Request.ReadFormAsync();
            var ct = context.RequestAborted;
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (form["password"].ToString().Length > UserService.MaxPasswordLength || form["code"].ToString().Length > 100)
                return Results.StatusCode(400);
            using var lease = action is "begin" or "confirm" or "disable" ? throttle.TryEnter(ip, user.Username) : null;
            if (action is "begin" or "confirm" or "disable" && lease is null)
            { context.Response.Headers.RetryAfter = "300"; return Results.StatusCode(429); }
            try
            {
                switch (action)
                {
                    case "begin":
                        await RequirePasswordAsync();
                        // The secret is shown on /security only to this recently authenticated session, until it's confirmed.
                        await sessions.ReauthenticateAsync(SignIn.SessionId(context), ct);
                        await factors.BeginAsync(user.Id, ct);
                        return Results.LocalRedirect("/security");
                    case "confirm":
                        if (!await RecentAsync(context)) throw new ArgumentException("That took too long. Set up the authenticator again.");
                        var codes = await factors.ConfirmAsync(user.Id, form["code"].ToString(), ct);
                        await SignOutOtherDevicesAsync();
                        await audit.RecordAsync(user, AuditActions.AccountSecurityChanged, "Authenticator turned on", $"IP: {ip}", ct);
                        // Shown once, in this response: never in a URL, cookie or log.
                        context.Response.Headers.CacheControl = "no-store";
                        return Results.Content(RecoveryCodesPage(context, codes), "text/html; charset=utf-8");
                    case "cancel":
                        if (!await factors.EnabledAsync(user.Id, ct)) await factors.DisableAsync(user.Id, ct);
                        return Results.LocalRedirect("/security");
                    case "disable":
                        await RequirePasswordAsync();
                        if (!await factors.VerifyAsync(user.Id, form["code"].ToString(), ct))
                            throw new ArgumentException("Enter a current code from your authenticator app, or a recovery code.");
                        await factors.DisableAsync(user.Id, ct);
                        await SignOutOtherDevicesAsync();
                        await audit.RecordAsync(user, AuditActions.AccountSecurityChanged, "Authenticator turned off", $"IP: {ip}", ct);
                        return Results.LocalRedirect("/security");
                    case "revoke":
                        if (!Guid.TryParse(form["id"], out var id)) throw new ArgumentException("Invalid session.");
                        await sessions.RevokeAsync(user.Id, id, ct);
                        await audit.RecordAsync(user, AuditActions.AccountSecurityChanged, "Signed out a device", $"IP: {ip}", ct);
                        return Results.LocalRedirect("/security");
                    default:
                        return Results.NotFound();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.LocalRedirect("/security?error=" + Uri.EscapeDataString(ex.Message));
            }

            async Task RequirePasswordAsync()
            {
                if (await users.SignInAsync(user.Username, form["password"].ToString(), ct) is null)
                    throw new ArgumentException("That password is incorrect.");
            }

            async Task SignOutOtherDevicesAsync()
            {
                foreach (var other in await sessions.ListAsync(user.Id, ct))
                    if (other.Id != SignIn.SessionId(context)) await sessions.RevokeAsync(user.Id, other.Id, ct);
            }
        });
    }

    /// <summary>Light or dark like the Portal (My settings → Appearance), before anything paints.</summary>
    public static readonly string ThemeScript = BrowserScripts.Read("theme-startup");

    private static string RecoveryCodesPage(HttpContext context, string[] codes) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="referrer" content="no-referrer"><meta name="theme-color" content="#0d1017"><title>Recovery codes · DeveAgentManager</title>
        <script nonce="{{context.Items[SecurityHeaders.NonceKey]}}">{{ThemeScript}}</script><link rel="stylesheet" href="/app.css"></head>
        <body><div class="login-page"><div class="login-glow"></div><div class="login-card security-card">
        <div class="login-title">Authenticator turned on</div>
        <p class="login-subtitle">Save these recovery codes somewhere safe, such as a password manager. Each one works once instead of a code
        from your app, if you lose your phone. They aren't shown again.</p>
        <pre class="security-codes">{{string.Join("\n", codes)}}</pre>
        <a class="login-button" href="/security">I saved them</a>
        </div></div></body></html>
        """;
}
