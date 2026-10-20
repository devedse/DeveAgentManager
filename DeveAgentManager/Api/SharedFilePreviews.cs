using System.Security.Cryptography;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;

namespace DeveAgentManager.Api;

/// <summary>
/// Streams an isolated website and its published assets. Opaque sandbox origins cannot use Portal cookies, so each
/// preview gets a short-lived, resource-scoped capability bound to its revocable browser sign-in.
/// </summary>
public sealed class SharedFilePreviews(IDataProtectionProvider protection, UserService users, SignInSessions signIns,
    AccessControl access, AgentArtifacts artifacts, SessionFileStore files, SessionImageStore images, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("DeveAgentManager.SharedFilePreview.v1");
    private static readonly FileExtensionContentTypeProvider Types = new();
    private sealed record Grant(Guid User, Guid BrowserSession, string Stamp, Guid Session, Guid File, DateTimeOffset Expires);

    public SessionFilePreview WithUrls(HttpContext context, Guid session, Guid file, string name, SessionFilePreview preview)
    {
        var user = SignIn.Current(context) ?? throw new UnauthorizedAccessException();
        var grant = new Grant(user.Id, SignIn.SessionId(context), SignIn.SecurityStamp(context) ?? "", session, file, clock.GetUtcNow().AddHours(2));
        var token = _protector.Protect(JsonSerializer.Serialize(grant));
        var prefix = $"{context.Request.PathBase}/shared-preview/{token}";
        return preview with
        {
            SourceUrl = SessionFilePreviewReader.IsPdf(name) ? null : $"{prefix}/text/{Uri.EscapeDataString(name)}",
            HtmlUrl = SessionFilePreviewReader.IsHtml(name) ? $"{prefix}/view/{Uri.EscapeDataString(name)}" : null,
            PdfUrl = SessionFilePreviewReader.IsPdf(name) ? $"{prefix}/view/{Uri.EscapeDataString(name)}" : null
        };
    }

    public static void Map(IEndpointRouteBuilder app) => app.MapGet("/shared-preview/{token}/{mode}/{**name}",
        (HttpContext context, string token, string mode, string? name, SharedFilePreviews previews, CancellationToken ct) =>
            previews.ServeAsync(context, token, mode, name, ct)).AllowAnonymous();

    private async Task<IResult> ServeAsync(HttpContext context, string token, string mode, string? name, CancellationToken ct)
    {
        SetHeaders(context);
        if (token.Length > 4096 || mode is not ("view" or "text") || string.IsNullOrWhiteSpace(name)
            || name.Length > 2048 || name.Contains('\\') || name.Split('/').Any(s => s is ".." or ".")) return Results.NotFound();
        Grant? grant;
        try { grant = JsonSerializer.Deserialize<Grant>(_protector.Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return Results.NotFound(); }
        if (grant is null || grant.Expires <= clock.GetUtcNow()) return Results.NotFound();
        var user = await users.ValidateAsync(grant.User, grant.Stamp, ct);
        if (user is null || user.MustChangePassword
            || await signIns.ValidateAsync(grant.BrowserSession, grant.User, grant.Stamp, local: DeploymentBoundary.IsLocal(context), ct: ct) is null
            || await access.SessionAsync(user, grant.Session, ct) < SessionAccess.Read) return Results.NotFound();
        var root = await artifacts.FileAsync(grant.Session, grant.File, user, ct);
        if (root is null) return Results.NotFound();
        // The original file is addressed exactly; relative dependencies use published filenames, never OS paths.
        if (name == root.Value.File.FileName)
            return await ContentAsync(root.Value.Path, root.Value.File.FileName, mode == "text", ct);
        if (mode != "view" || !SessionFilePreviewReader.IsHtml(root.Value.File.FileName)) return Results.NotFound();
        var leaf = name.Split('/').Last();
        var siblings = await artifacts.ListFilesAsync(grant.Session, user, ct);
        var candidates = siblings.Where(f => f.FileName == leaf).ToList();
        // Prefer the page's publishing session when two sessions published a resource with the same filename.
        if (Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(root.Value.Path)), "N", out var source))
        {
            var original = (await files.ListAsync(source, ct)).FirstOrDefault(f => f.FileName == leaf);
            if (original is not null) return await ContentAsync(files.DataPath(source, original.Id), leaf, false, ct);
            var image = (await images.ListAsync(source, ct)).FirstOrDefault(i => i.FileName == leaf);
            if (image is not null && await images.ReadAsync(source, image.Id, ct) is { } found)
                return Results.File(found.Data, found.Image.ContentType);
        }
        if (candidates.Count == 1 && await artifacts.FileAsync(grant.Session, candidates[0].Id, user, ct) is { } resource)
            return await ContentAsync(resource.Path, leaf, false, ct);
        if (candidates.Count > 1) return Results.NotFound();
        var gallery = (await artifacts.ListImagesAsync(grant.Session, user, ct)).Where(i => i.FileName == leaf).ToList();
        if (gallery.Count == 1 && await artifacts.ImageAsync(grant.Session, gallery[0].Id, user, ct) is { } sharedImage)
            return Results.File(sharedImage.Data, sharedImage.Image.ContentType);
        return Results.NotFound();
    }

    private static void SetHeaders(HttpContext context)
    {
        var origin = $"{context.Request.Scheme}://{context.Request.Host}";
        var headers = context.Response.Headers;
        headers.CacheControl = "private, no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        // Scoped GET resources may be used by the opaque frame, including fonts, modules and JSON fetches.
        headers.AccessControlAllowOrigin = "*";
        headers.ContentSecurityPolicy = $"sandbox allow-scripts allow-downloads allow-popups; default-src 'none'; "
            + "script-src 'unsafe-inline' https: http: data: blob:; style-src 'unsafe-inline' https: http:; "
            + "img-src https: http: data: blob:; font-src https: http: data:; media-src https: http: data: blob:; "
            + $"connect-src https: http:; frame-src 'none'; frame-ancestors {origin}; object-src 'none'; base-uri 'none'; form-action 'none'";
    }

    private static async Task<IResult> ContentAsync(string path, string name, bool text, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Open and determine encoding before returning a response, including deletion races.
            var start = new byte[4];
            try { await stream.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, cancellationToken: ct); stream.Position = 0; }
            catch { await stream.DisposeAsync(); throw; }
            var mime = text ? $"text/plain; charset={SessionFilePreviewReader.Charset(start)}"
                : SessionFilePreviewReader.IsHtml(name) ? "text/html"
                : Types.TryGetContentType(name, out var type) ? type : "application/octet-stream";
            return Results.Stream(stream, mime, enableRangeProcessing: true);
        }
        catch (FileNotFoundException) { return Results.NotFound(); }
        catch (DirectoryNotFoundException) { return Results.NotFound(); }
    }
}
