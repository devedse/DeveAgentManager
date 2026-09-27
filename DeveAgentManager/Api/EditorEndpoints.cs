using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Editor;
using DeveAgentManager.Core.Runners;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;

namespace DeveAgentManager.Api;

/// <summary>
/// VS Code in the browser (code-server on a Runner), on an origin of its own: the Manager's second port
/// (<see cref="ManagerOptions.EditorPort"/>). VS Code runs code that comes from the Runner (extensions; and whatever an
/// agent there could put in code-server's place), so it must never run as the Portal, which can do anything the admin
/// can. On its own origin, the Portal's API refuses it like any other site (see <see cref="RequestSecurity"/>).
/// <list type="bullet">
/// <item>Portal origin: <c>/editor/runners/{id}</c> and <c>/editor/agents/{id}</c> need the login and send the browser on
/// to the editor origin with a ticket for that one Runner, valid for two minutes.</item>
/// <item>Editor origin: <c>/runners/{id}/auth</c> turns the ticket into a cookie for that Runner's path only;
/// <c>/runners/{id}/code/</c> is its code-server. It serves nothing else.</item>
/// <item>The browser sends the Manager's login cookie to the editor port as well (cookies don't care about ports): it is
/// removed before a request goes to the Runner, and cookies code-server sets never reach the browser.</item>
/// <item>A page on the editor port counts as the same site as the Portal, so its frames get the login cookie: the links
/// only answer the Portal itself (Sec-Fetch-Site), or a page there could get tickets for every Runner.</item>
/// </list>
/// All Runners share the editor origin unless <see cref="ManagerOptions.EditorUrl"/> has a <c>{runner}</c> placeholder
/// (one host per Runner, e.g. <c>https://{runner}.code.example.com</c> with a wildcard DNS name). On a shared origin,
/// a Runner whose VS Code is open could reach the VS Code of other Runners opened in the same browser.
/// </summary>
public static class EditorEndpoints
{
    public const string CookieName = "DeveAgentManager.Editor";

    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
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
        context.Connection.LocalPort == context.RequestServices.GetRequiredService<ManagerOptions>().EditorPort;

    /// <summary>The editor origin: requests to the editor port go here and nowhere else.</summary>
    public static IApplicationBuilder UseEditorOrigin(this IApplicationBuilder app) =>
        app.MapWhen(IsEditorRequest, editor => editor.Run(HandleEditorRequestAsync));

    /// <summary>The Portal's links to VS Code (they need the login).</summary>
    public static void MapEditorLinks(this IEndpointRouteBuilder app)
    {
        var links = app.MapGroup("/editor");

        links.MapGet("/runners/{runnerId:guid}", async (Guid runnerId, HttpContext context, RunnerService runners, CancellationToken ct) =>
            await runners.GetAsync(runnerId, ct) is { } runner
                ? Open(context, runner, runner.HomeDirectory)
                : Page(StatusCodes.Status404NotFound, "This Runner doesn't exist (any more)."));

        links.MapGet("/agents/{agentId:guid}", async (Guid agentId, HttpContext context, AgentService agents, CancellationToken ct) =>
            await agents.GetAsync(agentId, ct) is { Runner: { } runner } agent
                ? Open(context, runner, agent.WorkingDirectory)
                : Page(StatusCodes.Status404NotFound, "This agent doesn't exist (any more)."));

        // Lets the Portal find out whether a Runner's editor origin can be reached from the browser.
        links.MapGet("/ping", async (Guid? runner, HttpContext context, RunnerService runners, CancellationToken ct) =>
            NoStore(context, Results.Redirect($"{EditorBase(context, runner is { } id ? (await runners.GetAsync(id, ct))?.Name : null)}/ping")));
    }

    /// <summary>
    /// Where the browser reaches a Runner's editor origin: EDITOR_URL (with the Runner's name for <c>{runner}</c>), or the
    /// Portal's host on the editor port.
    /// </summary>
    internal static string EditorBase(HttpContext context, string? runnerName)
    {
        var options = context.RequestServices.GetRequiredService<ManagerOptions>();
        return options.EditorUrl?.Replace("{runner}", runnerName ?? "runner", StringComparison.Ordinal)
            ?? new UriBuilder(context.Request.Scheme, context.Request.Host.Host, options.EditorPort).Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// The Portal's own pages (or an address typed in the browser), not a page on another port of this host: browsers say
    /// where a request comes from in Sec-Fetch-Site. Browsers without it are let through.
    /// </summary>
    private static bool FromThePortal(HttpRequest request) => request.Headers["Sec-Fetch-Site"].ToString() is "" or "same-origin" or "none";

    private static IResult Open(HttpContext context, Runner runner, string? folder)
    {
        if (!FromThePortal(context.Request))
        {
            return Page(StatusCodes.Status403Forbidden, "Open VS Code from the Manager's own pages.");
        }

        if (!runner.ToolIds.Contains(VsCodeTool.ToolId))
        {
            return Page(StatusCodes.Status409Conflict, $"VS Code is not installed on '{runner.Name}'. Install it on the Runner page, under Operations.");
        }

        var ticket = Protectors(context).Ticket.Protect(runner.Id.ToString("N"), TicketLifetime);
        var query = QueryString.Create("ticket", ticket);
        if (!string.IsNullOrEmpty(folder))
        {
            query = query.Add("folder", folder);
        }

        return NoStore(context, Results.Redirect($"{EditorBase(context, runner.Name)}/runners/{runner.Id}/auth{query}"));
    }

    private static async Task HandleEditorRequestAsync(HttpContext context)
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var path = context.Request.Path.Value ?? "";
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
            await WriteAsync(context, SignIn(context, runnerId));
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
        var template = context.RequestServices.GetRequiredService<ManagerOptions>().EditorUrl;
        if (template is null || !template.Contains("{runner}", StringComparison.Ordinal))
        {
            return true;
        }

        var runner = await context.RequestServices.GetRequiredService<RunnerService>().GetAsync(runnerId, context.RequestAborted);
        return runner is not null && Uri.TryCreate(template.Replace("{runner}", runner.Name, StringComparison.Ordinal), UriKind.Absolute, out var own)
            && string.Equals(own.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns a ticket from the Portal into this origin's cookie for the one Runner, then opens VS Code.</summary>
    private static IResult SignIn(HttpContext context, Guid runnerId)
    {
        var protectors = Protectors(context);
        if (Unprotect(protectors.Ticket, context.Request.Query["ticket"]) != runnerId.ToString("N"))
        {
            return Page(StatusCodes.Status403Forbidden, "This link to VS Code has expired. Open VS Code from the Manager again.");
        }

        context.Response.Cookies.Append(CookieName, protectors.Cookie.Protect(runnerId.ToString("N"), CookieLifetime), new CookieOptions
        {
            Path = $"/runners/{runnerId}/",
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            MaxAge = CookieLifetime,
            IsEssential = true,
        });
        var folder = context.Request.Query["folder"].ToString();
        var query = folder.Length > 0 ? QueryString.Create("folder", folder) : QueryString.Empty;
        return NoStore(context, Results.Redirect($"/runners/{runnerId}/code/{query}"));
    }

    private static async Task ProxyAsync(HttpContext context, Guid runnerId, PathString path)
    {
        if (Unprotect(Protectors(context).Cookie, context.Request.Cookies[CookieName]) != runnerId.ToString("N"))
        {
            await WriteAsync(context, Page(StatusCodes.Status401Unauthorized, "Open VS Code from the Manager (the Runner page, or an agent's terminal)."));
            return;
        }

        var services = context.RequestServices;
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
            await WriteAsync(context, Page(StatusCodes.Status502BadGateway, $"Could not open VS Code on '{runner.Name}': {ex.Message}"));
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

    private static (ITimeLimitedDataProtector Ticket, ITimeLimitedDataProtector Cookie) Protectors(HttpContext context)
    {
        var provider = context.RequestServices.GetRequiredService<IDataProtectionProvider>();
        return (provider.CreateProtector("DeveAgentManager.Editor.Ticket").ToTimeLimitedDataProtector(),
            provider.CreateProtector("DeveAgentManager.Editor.Cookie").ToTimeLimitedDataProtector());
    }

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
