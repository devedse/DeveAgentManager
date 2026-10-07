using System.Net;
using DeveAgentManager.Core.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Api;

/// <summary>Trusted forwarding and exact origin routing precede authentication and all endpoint dispatch.</summary>
public sealed class DeploymentBoundary
{
    private const string LocalKey = "dam:local";
    private const string EditorKey = "dam:editor";
    private readonly ManagerOptions _options;
    private readonly Uri[] _portals;
    private readonly HashSet<IPAddress> _proxies;

    public DeploymentBoundary(ManagerOptions options)
    {
        _options = options;
        _portals = options.PublicOrigins.Select(ParseOrigin).Distinct().ToArray();
        _proxies = options.TrustedProxies.Select(IPAddress.Parse).Select(Normalize).ToHashSet();
        if (_portals.Length > 0)
        {
            if (options.EditorUrl is null || !options.EditorUrl.Contains("{runner}", StringComparison.Ordinal))
                throw new ArgumentException("Public deployment requires EDITOR_URL with {runner}, e.g. https://{runner}.code.{host}.");
            foreach (var portal in _portals)
            {
                var editor = ParseOrigin(options.EditorUrl.Replace("{host}", portal.Host).Replace("{runner}", "runner"));
                var marker = "damrunnerplaceholder";
                var pattern = ParseOrigin(options.EditorUrl.Replace("{host}", portal.Host).Replace("{runner}", marker));
                if (!pattern.Host.StartsWith(marker + ".", StringComparison.Ordinal) || pattern.Host[(marker.Length + 1)..].Contains(marker, StringComparison.Ordinal))
                    throw new ArgumentException("EDITOR_URL must use {runner} as the first complete hostname label, so one wildcard covers all Runners.");
                var suffix = pattern.Host[marker.Length..];
                if (_portals.Any(p => p.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("A Portal hostname cannot belong to the editor hostname namespace.");
            }
        }
    }

    public void ConfigureForwarding(ForwardedHeadersOptions forwarding)
    {
        forwarding.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarding.ForwardLimit = 1;
        forwarding.KnownIPNetworks.Clear();
        forwarding.KnownProxies.Clear();
        // ForwardedHeaders treats empty lists as trust-all: retain a sentinel when none are configured.
        foreach (var address in _proxies.Count > 0 ? _proxies : [IPAddress.None]) forwarding.KnownProxies.Add(address);
    }

    public static bool IsLocal(HttpContext context) => context.Items[LocalKey] is true;
    public static bool IsEditor(HttpContext context) => context.Items[EditorKey] is true;
    public bool IsTrustedProxy(IPAddress? address) => address is not null && _proxies.Contains(Normalize(address));
    public static string Origin(HttpRequest request) => new UriBuilder(request.Scheme, request.Host.Host, request.Host.Port ?? (request.IsHttps ? 443 : 80)).Uri.GetLeftPart(UriPartial.Authority);

    public string EditorOrigin(HttpContext context, string runner)
    {
        if (IsLocal(context))
            return _options.PublicOrigins.Length == 0 && _options.EditorUrl is { } template && !template.Contains("{host}", StringComparison.Ordinal)
                ? template.Replace("{runner}", runner, StringComparison.Ordinal)
                : new UriBuilder(context.Request.Scheme, context.Request.Host.Host, _options.EditorPort).Uri.GetLeftPart(UriPartial.Authority);
        var portal = _portals.Single(p => SameOrigin(p, context.Request));
        return new Uri(_options.EditorUrl!.Replace("{host}", portal.Host, StringComparison.Ordinal).Replace("{runner}", runner, StringComparison.Ordinal)).GetLeftPart(UriPartial.Authority);
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var fromProxy = IsTrustedProxy(context.Connection.RemoteIpAddress);
        // Remember the socket peer before forwarded middleware changes RemoteIpAddress.
        var forwardingPresent = request.Headers.Keys.Any(h => h.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || h.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));
        context.Items[LocalKey] = !fromProxy && !forwardingPresent && _options.AllowLocalAccess && (IsLocalHost(request.Host.Host) || LocalEditorMatches(context))
            && (context.Connection.RemoteIpAddress is null || IsPrivate(context.Connection.RemoteIpAddress));
        context.Items[EditorKey] = IsLocal(context) && context.Connection.LocalPort == _options.EditorPort;
        await next(context);
    }

    public async Task RouteAsync(HttpContext context, RequestDelegate next)
    {
        if (IsLocal(context)) { await next(context); return; }
        if (_portals.Any(p => SameOrigin(p, context.Request))) { await next(context); return; }
        if (context.Request.IsHttps && _portals.Any(p => MatchesEditor(context.Request, p)))
        {
            await using var db = await context.RequestServices.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync(context.RequestAborted);
            var names = await db.Runners.Select(r => r.Name).ToListAsync(context.RequestAborted);
            if (!names.Any(name => _portals.Any(p => SameOrigin(ParseOrigin(_options.EditorUrl!.Replace("{host}", p.Host).Replace("{runner}", name)), context.Request))))
            { context.Response.StatusCode = 404; return; }
            context.Items[EditorKey] = true;
            await next(context);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
    }

    private bool LocalEditorMatches(HttpContext context)
    {
        if (_options.PublicOrigins.Length > 0 || context.Connection.LocalPort != _options.EditorPort || _options.EditorUrl is null || _options.EditorUrl.Contains("{host}", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(_options.EditorUrl.Replace("{runner}", "damrunnerplaceholder"), UriKind.Absolute, out var sample)) return false;
        var parts = sample.Host.Split("damrunnerplaceholder", StringSplitOptions.None);
        return sample.Scheme == context.Request.Scheme && sample.Port == (context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80))
            && (parts.Length == 1 ? sample.Host == context.Request.Host.Host : parts.Length == 2 && parts[1].Length > 0
                && context.Request.Host.Host.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
                && context.Request.Host.Host.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesEditor(HttpRequest request, Uri portal)
    {
        var template = _options.EditorUrl!.Replace("{host}", portal.Host, StringComparison.Ordinal);
        var marker = "damrunnerplaceholder";
        var sample = ParseOrigin(template.Replace("{runner}", marker, StringComparison.Ordinal));
        var parts = sample.Host.Split(marker, StringSplitOptions.None);
        return parts.Length == 2 && parts[1].Length > 0 && request.Host.Host.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
            && request.Host.Host.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase)
            && request.Host.Host.Length > parts[0].Length + parts[1].Length
            && !request.Host.Host[parts[0].Length..^parts[1].Length].Contains('.')
            && (request.Host.Port ?? 443) == sample.Port;
    }

    internal static bool SameOrigin(Uri origin, HttpRequest request) => origin.Scheme == request.Scheme
        && string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
        && origin.Port == (request.Host.Port ?? (request.IsHttps ? 443 : 80));

    private static Uri ParseOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/"
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.HostNameType != UriHostNameType.Dns)
            throw new ArgumentException("Public origins must be HTTPS hostnames without a path, query or credentials.");
        return uri;
    }
    private static bool IsLocalHost(string host) => host == "localhost" || IPAddress.TryParse(host, out var ip) && IsPrivate(ip);
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    private static bool IsPrivate(IPAddress address)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            : (bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal;
    }
}
