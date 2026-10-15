using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this RouteGroupBuilder api)
    {
        var accounts = api.MapGroup("/accounts");
        accounts.MapGet("/", (CurrentUser user, AccountService service, CancellationToken ct) =>
            service.ListVisibleAsync(user, false, ct)).ForAnyUser();
        accounts.MapGet("/all", (CurrentUser user, AccountService service, CancellationToken ct) =>
            service.ListVisibleAsync(user, true, ct)).RequireRole(UserRoles.HarnessLoginManager);

        accounts.MapGet("/pending", (CurrentUser user, AccountService service, CancellationToken ct) =>
            service.PendingAsync(user, ct)).RequireRole(UserRoles.HarnessLoginManager);
        accounts.MapPost("/", async (AddAccountRequest request, CurrentUser user, AccountService service, CancellationToken ct) =>
            DtoMapper.Account(await service.AddAsync(user.Id, request.HarnessId, request.Name ?? "", request.Kind, request.Secret, ct), 0, service.GetHarness(request.HarnessId).RenewsAccounts)).ForAnyUser();
        accounts.MapPut("/{accountId:guid}/secret", async (Guid accountId, ReplaceAccountSecretRequest request, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.ReplaceSecretAsync(user, accountId, request.Kind, request.Secret, ct);
            return Results.NoContent();
        }).RequireAccount();
        accounts.MapPut("/{accountId:guid}/name", async (Guid accountId, RenameRequest request, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.RenameAsync(user, accountId, request.Name, ct);
            return Results.NoContent();
        }).RequireAccount();
        accounts.MapPost("/{accountId:guid}/default", async (Guid accountId, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.SetDefaultAsync(user, accountId, ct);
            return Results.NoContent();
        }).RequireAccount(AccountAccess.Use);
        accounts.MapPut("/{accountId:guid}/enabled", async (Guid accountId, AccountEnabledRequest request, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.SetEnabledAsync(user, accountId, request.Enabled, ct);
            return Results.NoContent();
        }).RequireAccount();
        accounts.MapGet("/{accountId:guid}/grants", (Guid accountId, CurrentUser user, AccountService service, CancellationToken ct) =>
            service.GrantsAsync(user, accountId, ct)).RequireAccount();
        accounts.MapPut("/{accountId:guid}/grants/{userId:guid}", async (Guid accountId, Guid userId, AccountGrantRequest request, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.SetGrantAsync(user, accountId, userId, request.Access, ct);
            return Results.NoContent();
        }).RequireAccount();
        // A recipient may leave their own grant. The service enforces the target and last-manager rules.
        accounts.MapDelete("/{accountId:guid}/grants/{userId:guid}", async (Guid accountId, Guid userId, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.SetGrantAsync(user, accountId, userId, null, ct);
            return Results.NoContent();
        }).ForAnyUser();
        accounts.MapDelete("/{accountId:guid}/agent-grants/{agentId:guid}", async (Guid accountId, Guid agentId, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.RemoveLegacyGrantAsync(user, accountId, agentId, ct);
            return Results.NoContent();
        }).RequireAccount();
        accounts.MapGet("/{accountId:guid}/impact", (Guid accountId, Guid? userId, CurrentUser user, AccountService service, CancellationToken ct) =>
            service.ImpactAsync(user, accountId, userId, ct)).ForAnyUser();
        accounts.MapDelete("/{accountId:guid}", async (Guid accountId, CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(user, accountId, ct);
            return Results.NoContent();
        }).RequireAccount();
    }
}
