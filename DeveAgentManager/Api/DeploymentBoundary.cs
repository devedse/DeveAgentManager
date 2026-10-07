using System.Net;
using System.Text.RegularExpressions;
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
    private const string Marker = "damrunnerplaceholder";
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
                // {runner} lies within the first label (`{runner}.code.{host}` or `dam-vscode-{runner}.example.com`), so one
                // wildcard (DNS, certificate, proxy) covers every Runner and a Runner name never spans labels.
                var (prefix, suffix, _) = EditorPattern(portal);
                if (prefix.Contains('.') || !suffix.StartsWith('.') || suffix.Length < 2 || suffix.Contains(Marker, StringComparison.Ordinal))
                    throw new ArgumentException("EDITOR_URL must have {runner} once, in the first hostname label, e.g. https://{runner}.code.{host} or https://dam-vscode-{runner}.example.com.");
                if (_portals.Any(p => EditorRunner(p.Host, portal) is not null))
                    throw new ArgumentException("A Portal hostname cannot match the editor hostname pattern.");
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
        var name = context.Request.IsHttps
            ? _portals.Select(p => (context.Request.Host.Port ?? 443) == EditorPattern(p).Port ? EditorRunner(context.Request.Host.Host, p) : null).FirstOrDefault(n => n is not null)
            : null;
        if (name is not null)
        {
            await using var db = await context.RequestServices.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync(context.RequestAborted);
            if (!await db.Runners.AnyAsync(r => r.Name == name, context.RequestAborted))
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

    /// <summary>The editor hostname around {runner} for a Portal origin: what comes before and after it, and the port.</summary>
    private (string Prefix, string Suffix, int Port) EditorPattern(Uri portal)
    {
        var sample = ParseOrigin(_options.EditorUrl!.Replace("{host}", portal.Host, StringComparison.Ordinal).Replace("{runner}", Marker, StringComparison.Ordinal));
        var parts = sample.Host.Split(Marker, 2);
        return (parts[0], parts.Length == 2 ? parts[1] : "", sample.Port);
    }

    /// <summary>The Runner name an editor hostname names (lowercase, a single label), or null when it isn't one.</summary>
    internal string? EditorRunner(string host, Uri portal)
    {
        var (prefix, suffix, _) = EditorPattern(portal);
        host = host.ToLowerInvariant();
        if (host.Length <= prefix.Length + suffix.Length || !host.StartsWith(prefix, StringComparison.Ordinal) || !host.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var name = host[prefix.Length..^suffix.Length];
        return name.Contains('.') ? null : name;
    }

    /// <summary>A CSP source for every Runner's editor: the label holding {runner} becomes the one wildcard CSP allows.</summary>
    internal static string EditorFrameSource(string editorUrl) => Regex.Replace(editorUrl, @"//[^./]*\{runner\}[^./]*", "//*");

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
