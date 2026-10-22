using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Editor;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;

namespace DeveAgentManager.Api;

/// <summary>
/// VS Code runs on a separate origin for each public Runner, dispatched by hostname on the Portal port.
/// Local direct IP access can use the optional editor port. Portal links check Runner Use access and issue
/// short-lived, single-use session tickets. The bootstrap exchanges a URL fragment through a same-origin POST;
/// every editor request and live connection checks the same revocable sign-in session and current Runner access.
/// Runner credentials/content never become Portal credentials/content.
/// </summary>
public static class EditorEndpoints
{
    public const string CookieName = "DeveAgentManager.Editor";
    public const string PublicCookieName = "__Host-DAM.Editor";

    private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(12);

    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        // VS Code keeps a WebSocket open for as long as it is open, and it can be quiet for a while.
        ActivityTimeout = TimeSpan.FromMinutes(30),
    };

    private static readonly HttpMessageInvoker Upstream = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
    });

    public static bool IsEditorRequest(HttpContext context) =>
        DeploymentBoundary.IsEditor(context);

    /// <summary>The editor origin: requests to the editor port go here and nowhere else.</summary>
    public static IApplicationBuilder UseEditorOrigin(this IApplicationBuilder app) =>
        app.MapWhen(IsEditorRequest, editor => editor.Run(HandleEditorRequestAsync));

    /// <summary>The Portal's links to VS Code (they need the login).</summary>
    public static void MapEditorLinks(this IEndpointRouteBuilder app)
    {
        var links = app.MapGroup("/editor");

        links.MapGet("/runners/{runnerId:guid}", async (Guid runnerId, HttpContext context, RunnerService runners, AccessControl access, CancellationToken ct) =>
            SignIn.UsableUser(context) is { } user && await access.RunnerAsync(user, runnerId, ct) >= RunnerAccess.Use && await runners.GetAsync(runnerId, ct) is { } runner
                ? await OpenAsync(context, user, runner, runner.HomeDirectory)
                : Page(StatusCodes.Status404NotFound, "This Runner doesn't exist (any more)."));

        links.MapGet("/agents/{agentId:guid}", async (Guid agentId, HttpContext context, AgentService agents, AccessControl access, CancellationToken ct) =>
            SignIn.UsableUser(context) is { } user && await access.AgentAsync(user, agentId, ct) >= AgentAccess.Write && await agents.GetAsync(agentId, ct) is { Runner: { } runner } agent
                ? await OpenAsync(context, user, runner, agent.WorkingDirectory)
                : Page(StatusCodes.Status404NotFound, "This agent doesn't exist (any more), or you can only read it."));

        // Lets the Portal find out whether a Runner's editor origin can be reached from the browser.
        links.MapGet("/ping", async (Guid? runner, HttpContext context, RunnerService runners, AccessControl access, CancellationToken ct) =>
        {
            var name = runner is { } id && SignIn.UsableUser(context) is { } user && await access.RunnerAsync(user, id, ct) >= RunnerAccess.Use
                ? (await runners.GetAsync(id, ct))?.Name
                : null;
            return NoStore(context, Results.Redirect($"{EditorBase(context, name)}/ping"));
        });
    }

    /// <summary>
    /// Where the browser reaches a Runner's editor origin: EDITOR_URL (with the Runner's name for <c>{runner}</c>), or the
    /// Portal's host on the editor port.
    /// </summary>
    internal static string EditorBase(HttpContext context, string? runnerName)
    {
        return context.RequestServices.GetRequiredService<DeploymentBoundary>().EditorOrigin(context, runnerName ?? "runner");
    }

    /// <summary>
    /// The Portal's own pages (or an address typed in the browser), not a page on another port of this host: browsers say
    /// where a request comes from in Sec-Fetch-Site. Browsers without it are let through.
    /// </summary>
    private static bool FromThePortal(HttpRequest request) => RequestSecurity.IsSameOrigin(request)
        && (request.Headers["Sec-Fetch-Site"].ToString() is "same-origin" or "none"
            || DeploymentBoundary.IsLocal(request.HttpContext) && request.Headers["Sec-Fetch-Site"].Count == 0);

    private static async Task<IResult> OpenAsync(HttpContext context, CurrentUser user, Runner runner, string? folder)
    {
        if (!FromThePortal(context.Request))
        {
            return Page(StatusCodes.Status403Forbidden, "Open VS Code from the Manager's own pages.");
        }

        if (!runner.ToolIds.Contains(VsCodeTool.ToolId))
        {
            return Page(StatusCodes.Status409Conflict, $"VS Code is not installed on '{runner.Name}'. Install it on the Runner page, under Operations.");
        }

        if (await context.RequestServices.GetRequiredService<AccessControl>().RunnerAsync(user, runner.Id, context.RequestAborted) < RunnerAccess.Use)
            return Page(StatusCodes.Status404NotFound, "Runner access is required to open VS Code.");
        var origin = EditorBase(context, runner.Name);
        var ticket = await context.RequestServices.GetRequiredService<SignInSessions>().IssueEditorTicketAsync(
            SignIn.SessionId(context), runner.Id, origin, context.RequestAborted);
        var query = QueryString.Create("ticket", ticket);
        if (!string.IsNullOrEmpty(folder))
        {
            query = query.Add("folder", folder);
        }

        if (context.Request.Query["theme"] == "light")
        {
            query = query.Add("theme", "light");
        }

        return NoStore(context, Results.Redirect($"{origin}/runners/{runner.Id}/auth#{query.ToString().TrimStart('?')}"));
    }

    private static async Task HandleEditorRequestAsync(HttpContext context)
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        // Only the Portal (and VS Code's own webviews) may frame VS Code. A proxy's X-Frame-Options: SAMEORIGIN would
        // otherwise block the Portal's frame; browsers obey frame-ancestors over it. Added when the response starts, as
        // its own policy, so headers copied from code-server can't replace it.
        var portals = context.RequestServices.GetRequiredService<ManagerOptions>().PublicOrigins;
        if (portals.Length > 0)
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.Append(HeaderNames.ContentSecurityPolicy, $"frame-ancestors 'self' {string.Join(' ', portals)}");
                return Task.CompletedTask;
            });
        var mutation = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
        if ((mutation || context.WebSockets.IsWebSocketRequest) && !RequestSecurity.IsSameOrigin(context.Request, requireOrigin: true))
        { context.Response.StatusCode = 403; return; }
        var path = context.Request.Path.Value ?? "";
        if (path == "/auth.js" && HttpMethods.IsGet(context.Request.Method))
        {
            await WriteAsync(context, NoStore(context, Results.Content(AuthScript, "text/javascript")));
            return;
        }
        if (path == "/ping")
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // /runners/{id}/auth, /runners/{id}/code and /runners/{id}/code/...
        var parts = path.Split('/', 4);
        if (parts.Length != 4 || parts[0] != "" || parts[1] != "runners" || !Guid.TryParseExact(parts[2], "D", out var runnerId) || parts[2] != runnerId.ToString())
        {
            await WriteAsync(context, Page(StatusCodes.Status404NotFound, "Nothing here. Open VS Code from the Manager."));
            return;
        }

        if (!await OnItsOwnHostAsync(context, runnerId))
        {
            await WriteAsync(context, Page(StatusCodes.Status404NotFound, "Nothing here. Open VS Code from the Manager."));
            return;
        }

        var prefix = $"/runners/{runnerId}/code";
        if (parts[3] == "auth")
        {
            if (HttpMethods.IsGet(context.Request.Method))
                await WriteAsync(context, NoStore(context, Results.Content(AuthPage, "text/html; charset=utf-8")));
            else if (HttpMethods.IsPost(context.Request.Method) && RequestSecurity.IsSameOrigin(context.Request, requireOrigin: true))
                await WriteAsync(context, await AcceptTicketAsync(context, runnerId));
            else context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        else if (parts[3] == "code")
        {
            // code-server's pages use relative links: they need the trailing slash.
            await WriteAsync(context, Results.Redirect($"{prefix}/{context.Request.QueryString}"));
        }
        else if (parts[3].StartsWith("code/", StringComparison.Ordinal))
        {
            await ProxyAsync(context, runnerId, new PathString(path[prefix.Length..]));
        }
        else
        {
            await WriteAsync(context, Page(StatusCodes.Status404NotFound, "Nothing here. Open VS Code from the Manager."));
        }
    }

    /// <summary>With one host per Runner (EDITOR_URL with <c>{runner}</c>), a Runner is only served on its own host.</summary>
    private static async Task<bool> OnItsOwnHostAsync(HttpContext context, Guid runnerId)
    {
        var options = context.RequestServices.GetRequiredService<ManagerOptions>();
        if (DeploymentBoundary.IsLocal(context) && (options.EditorUrl is null || options.PublicOrigins.Length > 0)) return true;
        var runner = await context.RequestServices.GetRequiredService<RunnerService>().GetAsync(runnerId, context.RequestAborted);
        if (runner is null) return false;
        if (DeploymentBoundary.IsLocal(context))
            return Uri.TryCreate(options.EditorUrl!.Replace("{runner}", runner.Name), UriKind.Absolute, out var own)
                && DeploymentBoundary.SameOrigin(own, context.Request);
        return options.PublicOrigins.Any(portal => Uri.TryCreate(options.EditorUrl!.Replace("{runner}", runner.Name)
            .Replace("{host}", new Uri(portal).Host), UriKind.Absolute, out var own) && DeploymentBoundary.SameOrigin(own, context.Request));
    }

    /// <summary>Turns a ticket from the Portal into this origin's cookie for the one Runner, then opens VS Code.</summary>
    private static async Task<IResult> AcceptTicketAsync(HttpContext context, Guid runnerId)
    {
        if (!context.Request.HasFormContentType) return Results.StatusCode(400);
        var form = await context.Request.ReadFormAsync();
        var session = await context.RequestServices.GetRequiredService<SignInSessions>().ConsumeEditorTicketAsync(
            form["ticket"].ToString(), runnerId, DeploymentBoundary.Origin(context.Request), context.RequestAborted);
        if (session is null || session.Local != DeploymentBoundary.IsLocal(context)
            || await context.RequestServices.GetRequiredService<UserService>().ValidateAsync(session.UserId, session.SecurityStamp, context.RequestAborted) is not { MustChangePassword: false } user
            || await context.RequestServices.GetRequiredService<AccessControl>().RunnerAsync(user, runnerId, context.RequestAborted) < RunnerAccess.Use)
            return Page(StatusCodes.Status403Forbidden, "This link to VS Code has expired. Open VS Code from the Manager again.");
        var protector = CookieProtector(context);
        var pass = new EditorPass(session.UserId, session.SecurityStamp, runnerId, session.Id);
        context.Response.Cookies.Append(DeploymentBoundary.IsLocal(context) ? CookieName : PublicCookieName, protector.Protect(pass.Format(), CookieLifetime), new CookieOptions
        {
            Path = DeploymentBoundary.IsLocal(context) ? $"/runners/{runnerId}/" : "/",
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            MaxAge = CookieLifetime,
            IsEssential = true,
        });
        var folder = form["folder"].ToString();
        var query = folder.Length > 0 ? QueryString.Create("folder", folder) : QueryString.Empty;
        return NoStore(context, Results.Redirect($"/runners/{runnerId}/code/{query}"));
    }

    private static async Task ProxyAsync(HttpContext context, Guid runnerId, PathString path)
    {
        var services = context.RequestServices;
        var access = services.GetRequiredService<AccessControl>();
        if (EditorPass.Parse(Unprotect(CookieProtector(context), context.Request.Cookies[DeploymentBoundary.IsLocal(context) ? CookieName : PublicCookieName])) is not { } pass || pass.RunnerId != runnerId
            || await services.GetRequiredService<UserService>().ValidateAsync(pass.UserId, pass.SecurityStamp, context.RequestAborted) is not { MustChangePassword: false } user
            || await services.GetRequiredService<SignInSessions>().ValidateAsync(pass.SessionId, pass.UserId, pass.SecurityStamp,
                DeploymentBoundary.IsLocal(context), touch: true, ct: context.RequestAborted) is null
            || await access.RunnerAsync(user, runnerId, context.RequestAborted) < RunnerAccess.Use)
        {
            await WriteAsync(context, Page(StatusCodes.Status401Unauthorized, "Open VS Code from the Manager (the Runner page, or an agent's terminal)."));
            return;
        }

        // VS Code keeps a WebSocket open: it closes when the user's access or sign-in ends meanwhile.
        using var watched = services.GetRequiredService<OpenConnections>().Register(user, pass.SecurityStamp,
            async (current, ct) => await access.RunnerAsync(current, runnerId, ct) >= RunnerAccess.Use, context.Abort, pass.SessionId);
        var runner = await services.GetRequiredService<RunnerService>().GetAsync(runnerId, context.RequestAborted);
        if (runner is null || !runner.ToolIds.Contains(VsCodeTool.ToolId))
        {
            await WriteAsync(context, Page(StatusCodes.Status404NotFound, "VS Code is not installed on this Runner (any more)."));
            return;
        }

        if (runner.State != RunnerState.Ready)
        {
            await WriteAsync(context, Page(StatusCodes.Status503ServiceUnavailable, $"'{runner.Name}' is not ready ({runner.State}). Try again when it is."));
            return;
        }

        var upstreams = services.GetRequiredService<IEditorUpstreams>();
        using var inUse = upstreams.Use(runnerId);
        EditorUpstream upstream;
        try
        {
            upstream = await upstreams.GetAsync(runner, context.RequestAborted);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EditorEndpoints)).LogWarning(ex, "Could not open VS Code on {Runner}", runner.Name);
            await WriteAsync(context, Page(StatusCodes.Status502BadGateway, $"Could not open VS Code on '{runner.Name}'. Try again later."));
            return;
        }

        var error = await services.GetRequiredService<IHttpForwarder>().SendAsync(context, $"http://127.0.0.1:{upstream.LocalPort}", Upstream, RequestConfig,
            new Transformer($"/runners/{runnerId}/code/", path, upstream.SessionCookie, () => upstreams.Forget(runnerId)));
        if (error is ForwarderError.Request or ForwarderError.RequestTimedOut or ForwarderError.UpgradeRequestDestination)
        {
            // code-server (or the tunnel to it) is gone: start again on the next request.
            upstreams.Forget(runnerId);
        }
    }

    private sealed class Transformer(string basePath, PathString path, string sessionCookie, Action loggedOut) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, path, httpContext.Request.QueryString);
            // code-server accepts WebSockets only from the page it served itself (Origin must match Host).
            proxyRequest.Headers.Host = httpContext.Request.Host.Value;
            // The browser's cookies for this host include the Manager's login: none of them go to the Runner.
            foreach (var header in proxyRequest.Headers.Select(h => h.Key).ToArray())
            {
                if (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.StartsWith("X-Auth", StringComparison.OrdinalIgnoreCase)
                    || header.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("Remote-User", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("X-Remote-User", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("X-Remote-Email", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("X-User", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("X-Email", StringComparison.OrdinalIgnoreCase)
                    || header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
                    || httpContext.RequestServices.GetRequiredService<ManagerOptions>().ProxyIdentityHeaders.Contains(header, StringComparer.OrdinalIgnoreCase))
                    proxyRequest.Headers.Remove(header);
            }
            proxyRequest.Headers.Remove(HeaderNames.Cookie);
            proxyRequest.Headers.TryAddWithoutValidation(HeaderNames.Cookie, sessionCookie);
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var copyBody = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            // A cookie the Runner sets would be a cookie for the Manager's host (all its ports), and Clear-Site-Data would
            // log the Portal out. code-server's service worker may cover its own path, not the whole origin (every
            // Runner's VS Code, and the tickets).
            httpContext.Response.Headers.Remove(HeaderNames.SetCookie);
            httpContext.Response.Headers.Remove("Clear-Site-Data");
            if (httpContext.Response.Headers.ContainsKey("Service-Worker-Allowed"))
            {
                httpContext.Response.Headers["Service-Worker-Allowed"] = basePath;
            }

            if (proxyResponse is { StatusCode: HttpStatusCode.Found or HttpStatusCode.SeeOther } && proxyResponse.Headers.Location is { } location
                && location.OriginalString.Split('?')[0].EndsWith("login", StringComparison.Ordinal))
            {
                // code-server no longer accepts the session (it restarted with a new password): log in again and retry.
                loggedOut();
                httpContext.Response.StatusCode = HttpMethods.IsGet(httpContext.Request.Method) ? StatusCodes.Status302Found : StatusCodes.Status307TemporaryRedirect;
                httpContext.Response.Headers.Location = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                return false;
            }

            return copyBody;
        }
    }

    // Dark unless the Portal passed theme=light; the colors are the Portal's.
    private const string AuthPage = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="no-referrer"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Opening VS Code</title>
        <style>
        :root{color-scheme:dark;--bg:#0d1017;--fg:#e6ebf5;--muted:#8b93a7;--accent:#8b7cf6;--track:#262c3a}
        :root[data-theme=light]{color-scheme:light;--bg:#f6f7fb;--fg:#1b2230;--muted:#5b6478;--accent:#6d5ae6;--track:#dde1ea}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--fg);font:15px system-ui,sans-serif}
        main{display:grid;justify-items:center;gap:16px;padding:24px;text-align:center}
        .spinner{width:32px;height:32px;border-radius:50%;border:3px solid var(--track);border-top-color:var(--accent);animation:spin .8s linear infinite}
        p{margin:0;color:var(--muted);max-width:34em;line-height:1.5}
        @keyframes spin{to{transform:rotate(360deg)}}
        @media (prefers-reduced-motion:reduce){.spinner{animation-duration:2.4s}}
        </style></head>
        <body><main><div class="spinner"></div><p id="status">Opening VS Code…</p></main><script src="/auth.js"></script></body></html>
        """;

    private static readonly string AuthScript = BrowserScripts.Read("editor-auth");

    private static string? Unprotect(ITimeLimitedDataProtector protector, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static ITimeLimitedDataProtector CookieProtector(HttpContext context) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("DeveAgentManager.Editor.Cookie.v2").ToTimeLimitedDataProtector();

    private static IResult NoStore(HttpContext context, IResult result)
    {
        context.Response.Headers.CacheControl = "no-store";
        return result;
    }

    /// <summary>A small page for the VS Code frame or tab: the editor origin has no Portal to show errors.</summary>
    private static IResult Page(int status, string message) => Results.Content($$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>VS Code</title>
        <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0d1017;color:#e6ebf5;font:15px system-ui,sans-serif}p{max-width:34em;padding:24px;line-height:1.5}</style>
        </head><body><p>{{WebUtility.HtmlEncode(message)}}</p></body></html>
        """, "text/html; charset=utf-8", statusCode: status);

    private static Task WriteAsync(HttpContext context, IResult result) => result.ExecuteAsync(context);
}

/// <summary>Who may use VS Code on which Runner: what the editor ticket and cookie carry (protected).</summary>
internal sealed record EditorPass(Guid UserId, string SecurityStamp, Guid RunnerId, Guid SessionId)
{
    public string Format() => $"{UserId:N}.{RunnerId:N}.{SessionId:N}.{SecurityStamp}";

    public static EditorPass? Parse(string? value) =>
        value?.Split('.', 4) is [var user, var runner, var session, var stamp] && Guid.TryParseExact(user, "N", out var userId) && Guid.TryParseExact(runner, "N", out var runnerId) && Guid.TryParseExact(session, "N", out var sessionId)
            ? new EditorPass(userId, stamp, runnerId, sessionId)
            : null;
}
