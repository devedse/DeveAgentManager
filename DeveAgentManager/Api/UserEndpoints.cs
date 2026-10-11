using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>/api/me (the signed-in user), /api/users (user managers) and /api/audit (the audit log).</summary>
internal static class UserEndpoints
{
    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", (CurrentUser user) => new CurrentUserDto(user.Id, user.Username, user.DisplayName, user.Roles, user.MustChangePassword))
            .ForAnyUser().AllowBeforePasswordChange();

        var users = api.MapGroup("/users");

        // Everyone to share with or give access to.
        users.MapGet("/directory", async (UserService service, CancellationToken ct) =>
            (await service.ListAsync(ct)).Where(u => !u.Disabled).Select(DtoMapper.Summary).ToList()).ForAnyUser();

        users.MapGet("/", async (UserService service, CancellationToken ct) => (await service.ListAsync(ct)).Select(DtoMapper.User).ToList())
            .RequireRole(UserRoles.UserManager);

        users.MapPost("/", async (CreateUserRequest request, CurrentUser user, UserService service, CancellationToken ct) =>
            DtoMapper.User(await service.CreateAsync(user, request.Username, request.DisplayName, request.Password, request.Roles, ct)))
            .RequireRole(UserRoles.UserManager);

        users.MapPut("/{userId:guid}", async (Guid userId, UpdateUserRequest request, CurrentUser user, UserService service, CancellationToken ct) =>
            DtoMapper.User(await service.UpdateAsync(user, userId, request.DisplayName, request.Roles, request.Disabled, ct)))
            .RequireRole(UserRoles.UserManager);

        users.MapPut("/{userId:guid}/password", async (Guid userId, SetPasswordRequest request, CurrentUser user, UserService service, CancellationToken ct) =>
        {
            await service.ResetPasswordAsync(user, userId, request.Password, ct);
            return Results.NoContent();
        }).RequireRole(UserRoles.UserManager);

        // What the user owns goes to transferTo; every Runner loses their files and harness logins, and the ones they had
        // access to their SSH keys.
        users.MapDelete("/{userId:guid}", async (Guid userId, Guid transferTo, CurrentUser user, UserService service, RunnerUsers runnerUsers,
            CancellationToken ct) =>
        {
            await runnerUsers.RemoveDeletedUserAsync(userId, await service.DeleteAsync(user, userId, transferTo, ct), ct);
            return Results.NoContent();
        }).RequireRole(UserRoles.UserManager);

        api.MapGet("/audit", async (int? skip, int? take, AuditLog audit, CancellationToken ct) =>
            (await audit.ListAsync(skip ?? 0, take ?? 100, ct)).Select(a => new AuditEventDto(a.Id, a.At, a.ActorName, a.Action, a.Target, a.Detail)).ToList())
            .RequireRole(UserRoles.UserManager);
    }
}
