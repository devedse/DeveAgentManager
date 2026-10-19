using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Diagnostics;

namespace DeveAgentManager.Api;

internal static class CpuTraceEndpoints
{
    public static void MapCpuTraceEndpoints(this RouteGroupBuilder api)
    {
        var captures = api.MapGroup("/diagnostics/cpu");
        captures.MapGet("/", (HttpContext context, ManagerCpuTraces traces) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return traces.Status();
        }).RequireRole(UserRoles.SettingsManager);
        captures.MapPost("/", async (ManagerCpuTraces traces, CurrentUser user, AuditLog audit) =>
        {
            var capture = traces.Start();
            await audit.RecordAsync(user, AuditActions.ManagerCpuTraceStarted, capture.Id.ToString(), $"60-second Manager capture ({capture.Method})");
            return Results.Accepted($"/api/diagnostics/cpu/{capture.Id}/download", capture);
        }).RequireRole(UserRoles.SettingsManager);
        captures.MapPost("/{id:guid}/cancel", (Guid id, ManagerCpuTraces traces) =>
        {
            traces.Cancel(id);
            return Results.NoContent();
        }).RequireRole(UserRoles.SettingsManager);
        captures.MapDelete("/{id:guid}", (Guid id, ManagerCpuTraces traces) =>
        {
            traces.Delete(id);
            return Results.NoContent();
        }).RequireRole(UserRoles.SettingsManager);
        captures.MapGet("/{id:guid}/download", (Guid id, HttpContext context, ManagerCpuTraces traces) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(traces.Download(id), "application/zip", $"manager-cpu-{id:N}.zip", enableRangeProcessing: true);
        }).RequireRole(UserRoles.SettingsManager);
        captures.MapGet("/symbols/download", (HttpContext context, ManagerCpuTraces traces) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(traces.DownloadSymbols(), "application/zip", "manager-cpu-symbols.zip", enableRangeProcessing: true);
        }).RequireRole(UserRoles.SettingsManager);
    }
}
