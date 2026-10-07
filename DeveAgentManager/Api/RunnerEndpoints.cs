using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>/api/runners: Runners, their operations, desktop, access and password.</summary>
internal static class RunnerEndpoints
{
    public static void MapRunnerEndpoints(this RouteGroupBuilder api)
    {
        var runners = api.MapGroup("/runners");

        runners.MapGet("/", async (CurrentUser user, RunnerService service, DtoMapper map, CancellationToken ct) =>
            (await service.ListVisibleAsync(user, ct)).Select(r => map.Runner(r, user)).ToList()).ForAnyUser();

        runners.MapGet("/{runnerId:guid}", async (Guid runnerId, CurrentUser user, RunnerService service, DtoMapper map, CancellationToken ct) =>
            await service.GetAsync(runnerId, ct) is { } runner ? Results.Ok(map.Runner(runner, user)) : Results.NotFound())
            .RequireRunner(RunnerAccess.Use);

        runners.MapPost("/", async (CreateRunnerRequest request, CurrentUser user, RunnerService service, DtoMapper map, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(user, request, ct);
            return map.Runner(await service.GetAsync(created.Id, ct) ?? throw new InvalidOperationException("The Runner disappeared."), user);
        }).RequireRole(UserRoles.RunnerCreator);

        runners.MapGet("/{runnerId:guid}/usage", (Guid runnerId, int? minutes, RunnerStatusCache status) =>
            status.History(runnerId, minutes == 1 ? 1 : 60)).RequireRunner(RunnerAccess.Use);

        runners.MapPost("/{runnerId:guid}/start", (Guid runnerId, RunnerService service) => ManagerApi.Accepted(() => service.Start(runnerId)))
            .RequireRunner(RunnerAccess.Manage);
        runners.MapPost("/{runnerId:guid}/stop", (Guid runnerId, RunnerService service) => ManagerApi.Accepted(() => service.Stop(runnerId)))
            .RequireRunner(RunnerAccess.Manage);
        runners.MapPost("/{runnerId:guid}/setup", (Guid runnerId, RunnerService service) => ManagerApi.Accepted(() => service.RerunSetup(runnerId)))
            .RequireRunner(RunnerAccess.Manage);
        runners.MapPost("/{runnerId:guid}/cancel", (Guid runnerId, RunnerService service) => ManagerApi.Accepted(() => service.Cancel(runnerId)))
            .RequireRunner(RunnerAccess.Manage);
        runners.MapDelete("/{runnerId:guid}", (Guid runnerId, bool? destroyMachine, CurrentUser user, RunnerService service) =>
            ManagerApi.Accepted(() => service.Delete(user, runnerId, destroyMachine ?? true))).RequireRunner(RunnerAccess.Owner);

        runners.MapPost("/{runnerId:guid}/forget-host-key", async (Guid runnerId, RunnerService service, CancellationToken ct) =>
        {
            await service.ForgetHostKeyAsync(runnerId, ct);
            return Results.NoContent();
        }).RequireRunner(RunnerAccess.Manage);

        runners.MapPost("/{runnerId:guid}/harnesses/{harnessId}/update", (Guid runnerId, string harnessId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            ManagerApi.Track(runnerId, service, notifier, "update", () => service.UpdateHarness(runnerId, harnessId), ct)).RequireRunner(RunnerAccess.Manage);

        runners.MapPost("/{runnerId:guid}/harnesses/{harnessId}/install", (Guid runnerId, string harnessId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            ManagerApi.Track(runnerId, service, notifier, "install", () => service.InstallHarness(runnerId, harnessId), ct)).RequireRunner(RunnerAccess.Manage);

        // A tool's install script also updates it.
        runners.MapPost("/{runnerId:guid}/tools/{toolId}/install", (Guid runnerId, string toolId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            ManagerApi.Track(runnerId, service, notifier, "install", () => service.InstallTool(runnerId, toolId), ct)).RequireRunner(RunnerAccess.Manage);

        runners.MapPost("/{runnerId:guid}/tools/{toolId}/update", (Guid runnerId, string toolId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            ManagerApi.Track(runnerId, service, notifier, "update", () => service.InstallTool(runnerId, toolId, update: true), ct)).RequireRunner(RunnerAccess.Manage);

        // The agents' state from tmux right now (e.g. after a terminal closed), instead of at the monitor's next round.
        runners.MapPost("/{runnerId:guid}/refresh", async (Guid runnerId, RunnerService service, AgentService agents, CancellationToken ct) =>
        {
            if (await service.GetAsync(runnerId, ct) is { State: RunnerState.Ready } runner)
            {
                await agents.RefreshRunnerAsync(runner, ct);
            }

            return Results.NoContent();
        }).RequireRunner(RunnerAccess.Use);

        // The Details panel's refresh: the installed versions and the agents' state, read from the Runner now.
        runners.MapPost("/{runnerId:guid}/refresh-details", async (Guid runnerId, RunnerService service, AgentService agents, CancellationToken ct) =>
        {
            await service.RefreshVersionsAsync(runnerId, ct);
            if (await service.GetAsync(runnerId, ct) is { State: RunnerState.Ready } runner)
            {
                await agents.RefreshRunnerAsync(runner, ct);
            }

            return Results.NoContent();
        }).RequireRunner(RunnerAccess.Use);

        runners.MapGet("/{runnerId:guid}/log", (Guid runnerId, RunnerLogStore logs) => logs.GetRecent(runnerId).TakeLast(2000).ToList())
            .RequireRunner(RunnerAccess.Use);

        runners.MapGet("/{runnerId:guid}/activity", async (Guid runnerId, RunnerService service, CancellationToken ct) =>
            (await service.ListActivityAsync(runnerId, ct)).Select(a => new RunnerActivityDto(a.Id, a.Name, a.StartedAt, a.FinishedAt, a.Outcome, a.Error)).ToList())
            .RequireRunner(RunnerAccess.Use);

        runners.MapGet("/{runnerId:guid}/timezone", (Guid runnerId, RunnerTimeZones zones, CancellationToken ct) => zones.GetAsync(runnerId, ct))
            .RequireRunner(RunnerAccess.Use);
        runners.MapPut("/{runnerId:guid}/timezone", async (Guid runnerId, TimeZoneRequest request, RunnerTimeZones zones, CancellationToken ct) =>
        {
            await zones.SaveAsync(runnerId, request.TimeZoneId, ct);
            return await zones.GetAsync(runnerId, ct);
        }).RequireRunner(RunnerAccess.Manage);

        MapDesktop(runners);
        MapAccess(runners);
    }

    private static void MapDesktop(RouteGroupBuilder runners)
    {
        runners.MapGet("/{runnerId:guid}/desktop", async (Guid runnerId, RunnerService service, RunnerDesktop desktop, CancellationToken ct) =>
            await service.GetAsync(runnerId, ct) is { } runner ? Results.Ok(await desktop.ReadAsync(runner, ct)) : Results.NotFound())
            .RequireRunner(RunnerAccess.Use);

        runners.MapGet("/{runnerId:guid}/desktop/frame", async (Guid runnerId, HttpContext context, RunnerService service, RunnerDesktop desktop, CancellationToken ct) =>
        {
            var runner = await service.GetAsync(runnerId, ct);
            if (runner is null) return Results.NotFound();
            var frame = await desktop.CaptureAsync(runner, ct);
            context.Response.Headers["X-DAM-Captured-At"] = frame.CapturedAt.ToString("O");
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(frame.Data, "image/jpeg");
        }).RequireRunner(RunnerAccess.Use);

        runners.MapPost("/{runnerId:guid}/desktop/snapshot", async (Guid runnerId, SaveDesktopSnapshotRequest request, CurrentUser user, RunnerService service,
            RunnerDesktop desktop, SessionImages images, CancellationToken ct) =>
        {
            var runner = await service.GetAsync(runnerId, ct);
            if (runner is null) return Results.NotFound();
            var frame = await desktop.CaptureAsync(runner, ct);
            return Results.Ok(await images.SaveSnapshotAsync(user, runnerId, request.SessionId, frame.Data, request.Caption, ct));
        }).RequireRunner(RunnerAccess.Use);
    }

    private static void MapAccess(RouteGroupBuilder runners)
    {
        runners.MapGet("/{runnerId:guid}/members", async (Guid runnerId, RunnerMembers members, CancellationToken ct) =>
            (await members.ListAsync(runnerId, ct)).Select(m => new RunnerMemberDto(m.UserId, m.User!.Username, m.User.DisplayName, m.Access, m.GrantedAt)).ToList())
            .RequireRunner(RunnerAccess.Use);

        runners.MapPut("/{runnerId:guid}/members/{userId:guid}", async (Guid runnerId, Guid userId, RunnerAccessRequest request, CurrentUser user, RunnerMembers members, CancellationToken ct) =>
        {
            await members.SetAsync(user, runnerId, userId, request.Access, ct);
            return Results.NoContent();
        }).RequireRunner(RunnerAccess.Manage);

        // People who manage the Runner take someone's access away; anyone can leave.
        runners.MapDelete("/{runnerId:guid}/members/{userId:guid}", async (Guid runnerId, Guid userId, CurrentUser user, RunnerMembers members, CancellationToken ct) =>
        {
            await members.RemoveAsync(user, runnerId, userId, ct);
            return Results.NoContent();
        }).RequireRunner(RunnerAccess.Use);

        runners.MapPut("/{runnerId:guid}/owner", async (Guid runnerId, ChangeOwnerRequest request, CurrentUser user, RunnerMembers members, CancellationToken ct) =>
        {
            await members.ChangeOwnerAsync(user, runnerId, request.UserId, ct);
            return Results.NoContent();
        }).RequireRole(UserRoles.RunnerManager).RequireRunner(RunnerAccess.Owner);

        runners.MapGet("/{runnerId:guid}/password", async (Guid runnerId, CurrentUser user, RunnerPasswords passwords, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return new RunnerPasswordDto(await passwords.RevealAsync(user, runnerId, ct));
        }).RequireRunner(RunnerAccess.Manage);

        runners.MapPut("/{runnerId:guid}/password", async (Guid runnerId, SetPasswordRequest request, CurrentUser user, RunnerPasswords passwords, CancellationToken ct) =>
        {
            await passwords.SetAsync(user, runnerId, request.Password, ct);
            return Results.NoContent();
        }).RequireRunner(RunnerAccess.Manage);

        runners.MapPut("/{runnerId:guid}/keys-only", async (Guid runnerId, KeysOnlyRequest request, CurrentUser user, RunnerPasswords passwords, CancellationToken ct) =>
        {
            await passwords.SetKeysOnlyAsync(user, runnerId, request.KeysOnly, ct);
            return Results.NoContent();
        }).RequireRunner(RunnerAccess.Manage);
    }
}
