using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>/api/sessions: stored conversations, their images and files, exports and shares.</summary>
internal static class SessionEndpoints
{
    public const long MaxImportBytes = 200L * 1024 * 1024;

    public static void MapSessionEndpoints(this RouteGroupBuilder api)
    {
        var sessions = api.MapGroup("/sessions");

        sessions.MapGet("/", async (CurrentUser user, SessionService service, CancellationToken ct) =>
            (await service.ListVisibleAsync(user, ct)).Select(s => DtoMapper.Session(s, user)).ToList()).ForAnyUser();

        sessions.MapGet("/{sessionId:guid}", async (Guid sessionId, CurrentUser user, SessionService service, CancellationToken ct) =>
            await service.GetAsync(sessionId, ct) is { } session ? Results.Ok(DtoMapper.Session(session, user)) : Results.NotFound())
            .RequireSession(SessionAccess.Read);

        // The conversation itself, as a DeveSession (see docs/session-formats.md).
        sessions.MapGet("/{sessionId:guid}/content", async (Guid sessionId, SessionService service, CancellationToken ct) =>
            Results.Text((await service.LoadAsync(sessionId, ct)).ToJson(), "application/json", Encoding.UTF8)).RequireSession(SessionAccess.Read);

        sessions.MapGet("/{sessionId:guid}/export", async (Guid sessionId, string format, SessionService service, CancellationToken ct) =>
        {
            var export = await service.ExportAsync(sessionId, format, ct);
            return Results.File(Encoding.UTF8.GetBytes(export.Content), export.ContentType, export.FileName);
        }).RequireSession(SessionAccess.Read);

        sessions.MapPost("/import", async (IFormFile file, CurrentUser user, SessionService service, CancellationToken ct) =>
        {
            if (file.Length > MaxImportBytes)
            {
                throw new ArgumentException("Session files can be up to 200 MB.");
            }

            using var reader = new StreamReader(file.OpenReadStream());
            var session = await service.ImportAsync(user.Id, file.FileName, await reader.ReadToEndAsync(ct), ct);
            return DtoMapper.Session(await service.GetAsync(session.Id, ct) ?? session, user);
        }).ForAnyUser();

        sessions.MapPut("/{sessionId:guid}/title", async (Guid sessionId, RenameSessionRequest request, SessionService service, CancellationToken ct) =>
        {
            await service.RenameAsync(sessionId, request.Title, ct);
            return Results.NoContent();
        }).RequireSession(SessionAccess.Owner);

        sessions.MapDelete("/{sessionId:guid}", async (Guid sessionId, SessionService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sessionId, ct);
            return Results.NoContent();
        }).RequireSession(SessionAccess.Owner);

        sessions.MapGet("/{sessionId:guid}/images", (Guid sessionId, AgentArtifacts images, CurrentUser user, CancellationToken ct) => images.ListImagesAsync(sessionId, user, ct))
            .RequireSession(SessionAccess.Read);
        sessions.MapGet("/{sessionId:guid}/images/{imageId:guid}", async (Guid sessionId, Guid imageId, HttpContext context, AgentArtifacts images, CurrentUser user, CancellationToken ct) =>
            await images.ImageAsync(sessionId, imageId, user, ct) is { } image
                ? ManagerApi.Image(context, image.Data, image.Image.ContentType, TimeSpan.FromMinutes(5))
                : Results.NotFound()).RequireSession(SessionAccess.Read);
        sessions.MapGet("/{sessionId:guid}/images/{imageId:guid}/download", async (Guid sessionId, Guid imageId, HttpContext context, AgentArtifacts images, CurrentUser user, CancellationToken ct) =>
        {
            if (await images.ImageAsync(sessionId, imageId, user, ct) is not { } image) return Results.NotFound();
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(image.Data, image.Image.ContentType, image.Image.FileName);
        }).RequireSession(SessionAccess.Read);

        sessions.MapGet("/{sessionId:guid}/files", (Guid sessionId, AgentArtifacts files, CurrentUser user, CancellationToken ct) => files.ListFilesAsync(sessionId, user, ct))
            .RequireSession(SessionAccess.Read);
        sessions.MapGet("/{sessionId:guid}/files/{fileId:guid}/preview", async (Guid sessionId, Guid fileId, HttpContext context, AgentArtifacts files, CurrentUser user, CancellationToken ct) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "private, no-store";
            if (await files.FileAsync(sessionId, fileId, user, ct) is not { } found) return Results.NotFound();
            try { return Results.Ok(await SessionFilePreviewReader.ReadAsync(found.Path, found.File.FileName, ct)); }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (DirectoryNotFoundException) { return Results.NotFound(); }
        }).RequireSession(SessionAccess.Read);
        // Always an attachment, resumable (range requests), never sniffed or cached.
        sessions.MapGet("/{sessionId:guid}/files/{fileId:guid}/download", async (Guid sessionId, Guid fileId, HttpContext context, AgentArtifacts files, CurrentUser user, CancellationToken ct) =>
        {
            if (await files.FileAsync(sessionId, fileId, user, ct) is not { } found) return Results.NotFound();
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "private, no-store";
            return TypedResults.PhysicalFile(found.Path, SessionFileStore.DownloadType(found.File.FileName), found.File.FileName, enableRangeProcessing: true);
        }).RequireSession(SessionAccess.Read);
        sessions.MapDelete("/{sessionId:guid}/files/{fileId:guid}", async (Guid sessionId, Guid fileId, SessionService service, CurrentUser user, CancellationToken ct) =>
            await service.DeleteFileAsync(sessionId, fileId, ct, user) ? Results.NoContent() : Results.NotFound())
            .RequireSession(SessionAccess.Owner);

        sessions.MapGet("/{sessionId:guid}/shares", async (Guid sessionId, Sharing sharing, CancellationToken ct) =>
            (await sharing.ListSessionSharesAsync(sessionId, ct)).Select(s => new SessionShareDto(s.UserId, s.User!.Username, s.User.DisplayName, s.GrantedAt)).ToList())
            .RequireSession(SessionAccess.Read);

        sessions.MapPut("/{sessionId:guid}/shares/{userId:guid}", async (Guid sessionId, Guid userId, CurrentUser user, Sharing sharing, CancellationToken ct) =>
        {
            await sharing.ShareSessionAsync(user, sessionId, userId, ct);
            return Results.NoContent();
        }).RequireSession(SessionAccess.Owner);

        // The owner ends anyone's share; anyone can leave a share they got.
        sessions.MapDelete("/{sessionId:guid}/shares/{userId:guid}", async (Guid sessionId, Guid userId, CurrentUser user, Sharing sharing, CancellationToken ct) =>
        {
            await sharing.UnshareSessionAsync(user, sessionId, userId, ct);
            return Results.NoContent();
        }).RequireSession(SessionAccess.Read);
    }
}
