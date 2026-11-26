using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Diagnostics;

namespace DeveAgentManager.Api;

internal static class PortalFreezeEndpoints
{
    /// <summary>The Portal stopped answering in a browser: its worker (freeze-watch.ts) reports it to the Manager's log.</summary>
    public static void MapPortalFreezeEndpoints(this RouteGroupBuilder api) =>
        api.MapPost("/diagnostics/portal-freeze", (PortalFreezeReport report, CurrentUser user, PortalFreezeLog log) =>
            log.Record(user, report) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status429TooManyRequests)).ForAnyUser();
}
