using DeveAgentManager.Contracts;

namespace DeveAgentManager.Api;

/// <summary>
/// The browser app talks to the Manager with the login cookie. What keeps other sites from using that cookie:
/// <list type="bullet">
/// <item>The cookie is SameSite=Lax, so browsers don't send it with requests other sites make in the background.</item>
/// <item>Every API request that changes something must carry <see cref="ApiHeaders.Request"/>. A page on another site
/// can only add a header like that with a CORS preflight, which the Manager doesn't allow.</item>
/// <item>Requests that say where they come from (the Origin header, which browsers always send for fetch, WebSockets and
/// cross-site forms) must come from the Manager itself: that covers the live hub and terminal WebSockets.</item>
/// </list>
/// </summary>
public static class RequestSecurity
{
    /// <summary>API, live hub and terminal paths: they answer 401/403 instead of redirecting to the login page.</summary>
    public static bool IsMachinePath(PathString path) =>
        path.StartsWithSegments("/account") || path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || path.StartsWithSegments("/ws");

    /// <summary>True when the request has no Origin, or one that is this Manager (same scheme, host and port).</summary>
    public static bool IsSameOrigin(HttpRequest request, bool requireOrigin = false)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return !requireOrigin;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        var host = request.Host;
        var port = host.Port ?? (request.IsHttps ? 443 : 80);
        return string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase)
               && uri.Port == port;
    }

    /// <summary>Refuses hub and WebSocket connections that other sites open (cross-site WebSocket hijacking).</summary>
    public static IApplicationBuilder UseSameOriginForSockets(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if ((path.StartsWithSegments("/hubs") || path.StartsWithSegments("/ws")) && !IsSameOrigin(context.Request, requireOrigin: !DeploymentBoundary.IsLocal(context)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next();
    });

    /// <summary>For the API group: requests that change something need the header and, when they say, our origin.</summary>
    public static async ValueTask<object?> RequireApiRequest(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
        {
            if (request.Headers[ApiHeaders.Request] != "1" || !IsSameOrigin(request))
            {
                return Results.Problem("This request needs to come from the Manager's own pages.", statusCode: StatusCodes.Status403Forbidden);
            }
        }

        return await next(context);
    }
}
