using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>/api/agents: agents, their terminals' viewers, usage limits, icons, conversations and shares.</summary>
internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this RouteGroupBuilder api)
    {
        var agents = api.MapGroup("/agents");

        agents.MapGet("/", async (CurrentUser user, AgentService service, CancellationToken ct) =>
            (await service.ListVisibleAsync(user, ct)).Select(a => DtoMapper.Agent(a, user)).ToList()).ForAnyUser();

        agents.MapGet("/{agentId:guid}", async (Guid agentId, CurrentUser user, AgentService service, CancellationToken ct) =>
            await service.GetAsync(agentId, ct) is { Runner: not null } agent ? Results.Ok(DtoMapper.Agent(agent, user)) : Results.NotFound())
            .RequireAgent(AgentAccess.Listed);

        agents.MapPost("/", async (CreateAgentRequest request, CurrentUser user, AgentService service, CancellationToken ct) =>
        {
            var agent = await service.CreateAsync(user, request, ct);
            var created = await service.GetAsync(agent.Id, ct) ?? throw new InvalidOperationException("The agent disappeared.");
            return DtoMapper.Agent(created, user);
        }).ForAnyUser();

        // Workspaces: independent clones next to the project's Main folder (see docs/project-workspaces.md).
        agents.MapPost("/{agentId:guid}/workspaces", async (Guid agentId, CreateWorkspaceRequest request, CurrentUser user, AgentService service, CancellationToken ct) =>
        {
            var agent = await service.CreateWorkspaceAsync(user, agentId, request, ct);
            return DtoMapper.Agent((await service.GetAsync(agent.Id, ct))!, user);
        }).RequireAgent(AgentAccess.Owner);
        agents.MapGet("/{agentId:guid}/workspaces/estimate", (Guid agentId, WorkspaceStart? start, AgentService service, CancellationToken ct) =>
            service.EstimateWorkspaceAsync(agentId, start ?? WorkspaceStart.MainBranch, ct)).RequireAgent(AgentAccess.Owner);
        agents.MapPost("/{agentId:guid}/workspaces/branches", (Guid agentId, AgentService service, CancellationToken ct) =>
            service.RefreshBranchesAsync(agentId, ct)).RequireAgent(AgentAccess.Read);
        agents.MapGet("/{agentId:guid}/workspace/changes", (Guid agentId, AgentService service, CancellationToken ct) =>
            service.GetWorkspaceChangesAsync(agentId, ct)).RequireAgent(AgentAccess.Write);
        agents.MapPost("/{agentId:guid}/workspace/remove", async (Guid agentId, RemoveWorkspaceRequest request, AgentService service, CancellationToken ct) =>
        {
            await service.RemoveWorkspaceAsync(agentId, request.KeepFiles, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Owner);
        agents.MapGet("/{agentId:guid}/kept-folders", (Guid agentId, AgentService service, CancellationToken ct) =>
            service.ListKeptFoldersAsync(agentId, ct)).RequireAgent(AgentAccess.Read);
        agents.MapDelete("/{agentId:guid}/kept-folders", async (Guid agentId, string path, AgentService service, CancellationToken ct) =>
        {
            await service.DeleteKeptFolderAsync(agentId, path, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Owner);

        agents.MapPost("/{agentId:guid}/start", async (Guid agentId, bool? restart, AgentService service, CancellationToken ct) =>
        {
            await service.StartAsync(agentId, restart ?? false, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);

        agents.MapPost("/{agentId:guid}/stop", async (Guid agentId, AgentService service, CancellationToken ct) =>
        {
            await service.StopAsync(agentId, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);

        // What deleting would affect (its folder, unsaved work, conversations), then the agent alone or with its folder.
        agents.MapGet("/{agentId:guid}/deletion", (Guid agentId, AgentService service, CancellationToken ct) =>
            service.GetDeletionAsync(agentId, ct)).RequireAgent(AgentAccess.Owner);
        agents.MapGet("/{agentId:guid}/folder-size", (Guid agentId, AgentService service, CancellationToken ct) =>
            service.MeasureFolderAsync(agentId, ct)).RequireAgent(AgentAccess.Owner);
        agents.MapDelete("/{agentId:guid}", async (Guid agentId, bool? deleteFolder, AgentService service, CancellationToken ct) =>
        {
            if (deleteFolder == true) await service.DeleteWithFolderAsync(agentId, ct);
            else await service.DeleteAsync(agentId, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Owner);

        agents.MapPost("/{agentId:guid}/switch", async (Guid agentId, SwitchAgentRequest request, CurrentUser user, AgentService service, CancellationToken ct) =>
        {
            await service.SwitchAsync(user, agentId, request.HarnessId, request.AccountId, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);

        // Read access suffices to hand your own identity back to the owner; the service checks the rest.
        agents.MapPost("/{agentId:guid}/git-identity", async (Guid agentId, SetGitIdentityRequest request, CurrentUser user, AgentService service, CancellationToken ct) =>
        {
            await service.SetGitIdentityAsync(user, agentId, request.UserId, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Read);

        agents.MapPost("/{agentId:guid}/move", async (Guid agentId, MoveAgentRequest request, CurrentUser user, AgentService service, CancellationToken ct) =>
        {
            await service.RequestMoveAsync(user, agentId, request.RunnerId, ct, request.StopProcesses);
            return Results.Accepted();
        }).RequireAgent(AgentAccess.Owner);
        agents.MapGet("/{agentId:guid}/move/processes", (Guid agentId, CurrentUser user, AgentService service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return service.ListMoveProcessesAsync(user, agentId, ct);
        }).RequireAgent(AgentAccess.Owner);
        agents.MapPost("/{agentId:guid}/move/recover", async (Guid agentId, CurrentUser user, AgentService service, CancellationToken ct) =>
        {
            await service.RecoverMoveAsync(user, agentId, ct);
            return Results.Accepted();
        }).RequireAgent(AgentAccess.Owner);

        agents.MapGet("/reply-previews", (CurrentUser user, SessionService sessions, CancellationToken ct) => sessions.ReplyPreviewsAsync(user, ct)).ForAnyUser();
        agents.MapGet("/attention", (CurrentUser user, AgentAttentionService attention, CancellationToken ct) => attention.ListAsync(user, ct)).ForAnyUser();
        agents.MapPost("/{agentId:guid}/attention/dismiss", async (Guid agentId, DismissAgentAttentionRequest request, AgentAttentionService attention, CancellationToken ct) =>
        {
            await attention.DismissAsync(agentId, request.Token, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Read);
        agents.MapPost("/{agentId:guid}/seen", async (Guid agentId, MarkAgentSeenRequest request, AgentAttentionService attention, CancellationToken ct) =>
        {
            await attention.MarkSeenAsync(agentId, request.CompletionSequence, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Read);

        agents.MapGet("/{agentId:guid}/diagnostics", (Guid agentId, AgentDiagnostics diagnostics) => diagnostics.Read(agentId)).RequireAgent(AgentAccess.Read);
        agents.MapGet("/{agentId:guid}/commands", async (Guid agentId, AgentService service, CancellationToken ct) =>
            await service.GetAsync(agentId, ct) is { } agent ? Results.Ok(AgentQuickCommands.For(agent.HarnessId)) : Results.NotFound())
            .RequireAgent(AgentAccess.Read);
        agents.MapPost("/{agentId:guid}/commands", async (Guid agentId, AgentQuickCommandRequest request, AgentQuickCommands commands, CancellationToken ct) =>
        {
            await commands.SendAsync(agentId, request, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);

        agents.MapGet("/{agentId:guid}/viewers", (Guid agentId, AgentService service, CancellationToken ct) => service.ListViewersAsync(agentId, ct))
            .RequireAgent(AgentAccess.Read);
        // Everyone who can watch the terminal can copy from it.
        agents.MapGet("/{agentId:guid}/terminal-text", (Guid agentId, int? lines, AgentService service, CancellationToken ct) =>
            service.ReadTerminalTextAsync(agentId, lines ?? AgentService.TerminalTextLines, ct)).RequireAgent(AgentAccess.Read);
        agents.MapPost("/{agentId:guid}/viewers/disconnect", async (Guid agentId, DisconnectViewersRequest request, AgentService service, CancellationToken ct) =>
        {
            await service.DisconnectViewersAsync(agentId, request.Tty, request.KeepTty, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);

        agents.MapPost("/{agentId:guid}/session", async (Guid agentId, SelectSessionRequest request, AgentService service, CancellationToken ct) =>
        {
            await service.SelectSessionAsync(agentId, request.SessionId, request.Title, ct, request.Update);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);
        agents.MapGet("/{agentId:guid}/session", async (Guid agentId, CurrentUser user, SessionService sessions, CancellationToken ct) =>
            await sessions.GetForAgentAsync(agentId, ct) is { } session && await sessions.GetAsync(session.Id, ct) is { } full
                ? Results.Ok(DtoMapper.Session(full, user))
                : Results.NotFound()).RequireAgent(AgentAccess.Read);

        // The agent's task list (dam-tools tasks): everyone who reads the agent sees it; clearing it needs read-write.
        agents.MapGet("/{agentId:guid}/tasks", (Guid agentId, AgentTasks tasks, CancellationToken ct) => tasks.GetAsync(agentId, ct))
            .RequireAgent(AgentAccess.Read);
        agents.MapDelete("/{agentId:guid}/tasks", async (Guid agentId, AgentTasks tasks, CancellationToken ct) =>
        {
            await tasks.ClearAsync(agentId, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);

        MapLimits(agents);
        MapIcons(agents);
        MapShares(agents);
    }

    private static void MapLimits(RouteGroupBuilder agents)
    {
        agents.MapGet("/{agentId:guid}/limit/automatic", (Guid agentId, AgentContinuations limits, CancellationToken ct) => limits.GetAutomaticAsync(agentId, ct))
            .RequireAgent(AgentAccess.Read);
        agents.MapPut("/{agentId:guid}/limit/automatic", async (Guid agentId, AutoContinueRequest request, AgentContinuations limits, CancellationToken ct) =>
        {
            await limits.SetAutomaticAsync(agentId, request.Enabled, request.Remaining, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);
        // No usage limit is an answer (null), not a 404: browsers show every 404 as an error.
        agents.MapGet("/{agentId:guid}/limit", (Guid agentId, AgentContinuations limits, CancellationToken ct) => limits.GetAsync(agentId, ct))
            .RequireAgent(AgentAccess.Read);
        agents.MapPost("/{agentId:guid}/limit/continue", async (Guid agentId, ScheduleContinueRequest request, AgentContinuations limits, CancellationToken ct) =>
        {
            await limits.ScheduleAsync(agentId, request.ResetAtUtc, ct, request.ExpectedDetectedAt, request.ContinueAfterMinutes, request.Manual);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);
        agents.MapDelete("/{agentId:guid}/limit/continue", async (Guid agentId, AgentContinuations limits, CancellationToken ct) =>
        {
            await limits.CancelAsync(agentId, "Cancelled by you.", ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);
    }

    private static void MapIcons(RouteGroupBuilder agents)
    {
        // The project's icon. The URL has the icon's version, so browsers keep it until it changes.
        agents.MapGet("/{agentId:guid}/icon", async (Guid agentId, HttpContext context, AgentIconService icons, CancellationToken ct) =>
            await icons.GetAsync(agentId, ct) is { } icon ? ManagerApi.Image(context, icon.Data, icon.ContentType, TimeSpan.FromDays(365)) : Results.NotFound())
            .RequireAgent(AgentAccess.Listed);

        agents.MapGet("/{agentId:guid}/icon/choices", async (Guid agentId, AgentIconService icons, CancellationToken ct) =>
        {
            var (images, current, chosenByUser) = await icons.ListAsync(agentId, ct);
            var automatic = images.FirstOrDefault(i => i.Eligible)?.File.Path;
            // The same file in several folders (an .ico copied into every project) is shown once, where it ranks best
            // (as the one in use, when that is one of them).
            return new AgentIconChoicesDto(
                images.GroupBy(i => (Path.GetFileName(i.File.Path).ToLowerInvariant(), i.File.Bytes, i.File.Width, i.File.Height))
                    .Select(g => g.FirstOrDefault(i => i.File.Path == current) ?? g.First())
                    .Take(60).Select(i => new AgentIconImageDto(i.File.Path, i.File.Bytes, i.File.Width, i.File.Height, i.Score, i.File.Path == automatic)).ToList(),
                current, chosenByUser);
        }).RequireAgent(AgentAccess.Write);

        agents.MapGet("/{agentId:guid}/icon/image", async (Guid agentId, string path, HttpContext context, AgentIconService icons, CancellationToken ct) =>
            await icons.ReadAsync(agentId, path, ct) is { } image ? ManagerApi.Image(context, image.Data, image.ContentType, TimeSpan.FromMinutes(5)) : Results.NotFound())
            .RequireAgent(AgentAccess.Write);

        agents.MapPut("/{agentId:guid}/icon", async (Guid agentId, SetAgentIconRequest request, AgentIconService icons, CancellationToken ct) =>
        {
            if (request.Automatic)
            {
                await icons.DetectAsync(agentId, ct);
            }
            else
            {
                await icons.ChooseAsync(agentId, request.Path, ct);
            }

            return Results.NoContent();
        }).RequireAgent(AgentAccess.Write);
    }

    private static void MapShares(RouteGroupBuilder agents)
    {
        agents.MapGet("/{agentId:guid}/shares", async (Guid agentId, Sharing sharing, CancellationToken ct) =>
            (await sharing.ListAgentSharesAsync(agentId, ct)).Select(s => new AgentShareDto(s.UserId, s.User!.Username, s.User.DisplayName, s.Access, s.GrantedAt)).ToList())
            .RequireAgent(AgentAccess.Read);

        agents.MapPut("/{agentId:guid}/shares/{userId:guid}", async (Guid agentId, Guid userId, ShareAgentRequest request, CurrentUser user, Sharing sharing, CancellationToken ct) =>
        {
            await sharing.ShareAgentAsync(user, agentId, userId, request.Access, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Owner);

        // The owner ends anyone's share; anyone can leave a share they got.
        agents.MapDelete("/{agentId:guid}/shares/{userId:guid}", async (Guid agentId, Guid userId, CurrentUser user, Sharing sharing, CancellationToken ct) =>
        {
            await sharing.UnshareAgentAsync(user, agentId, userId, ct);
            return Results.NoContent();
        }).RequireAgent(AgentAccess.Read);
    }
}
