using System.Security.Cryptography;
using DeveAgentManager.Core.Configuration;

namespace DeveAgentManager.Api;

public static class SecurityHeaders
{
    public const string NonceKey = "dam:csp-nonce";
    public static IApplicationBuilder UsePortalSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        context.Items[NonceKey] = nonce;
        var options = context.RequestServices.GetRequiredService<ManagerOptions>();
        var frames = options.PublicOrigins.Select(p => DeploymentBoundary.EditorFrameSource(options.EditorUrl!.Replace("{host}", new Uri(p).Host))).ToList();
        if (DeploymentBoundary.IsLocal(context))
            frames.Add(options.PublicOrigins.Length == 0 && options.EditorUrl is { } template && !template.Contains("{host}", StringComparison.Ordinal)
                ? DeploymentBoundary.EditorFrameSource(template)
                : new UriBuilder(context.Request.Scheme, context.Request.Host.Host, options.EditorPort).Uri.GetLeftPart(UriPartial.Authority));
        context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' {string.Join(' ', frames)}; frame-src 'self' {string.Join(' ', frames)}; frame-ancestors 'self'; object-src 'none'; base-uri 'self'; form-action 'self'";
        context.Response.Headers.XFrameOptions = "SAMEORIGIN";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=()";
        if (context.Request.IsHttps) context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
        if (context.Request.Path.StartsWithSegments("/account") || context.Request.Path.StartsWithSegments("/login"))
            context.Response.Headers.CacheControl = "no-store";
        await next(context);
    });
}
