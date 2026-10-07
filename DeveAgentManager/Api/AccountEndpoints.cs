using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>/api/accounts: the signed-in user's own harness logins (subscriptions and API keys).</summary>
internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this RouteGroupBuilder api)
    {
        var accounts = api.MapGroup("/accounts");

        accounts.MapGet("/", async (CurrentUser user, AccountService service, CancellationToken ct) =>
        {
            var usage = await service.CountAgentsAsync(ct);
            return (await service.ListAsync(user.Id, ct)).Select(a => DtoMapper.Account(a, usage.GetValueOrDefault(a.Id), service.GetHarness(a.HarnessId).RenewsAccounts)).ToList();
        }).ForAnyUser();

        accounts.MapPost("/", async (AddAccountRequest request, CurrentUser user, AccountService service, CancellationToken ct) =>
            DtoMapper.Account(await service.AddAsync(user.Id, request.HarnessId, request.Name ?? "", request.Kind, request.Secret, ct), 0, service.GetHarness(request.HarnessId).RenewsAccounts)).ForAnyUser();

        accounts.MapPut("/{accountId:guid}/secret", async (Guid accountId, ReplaceAccountSecretRequest request, AccountService service, CancellationToken ct) =>
        {
            await service.ReplaceSecretAsync(accountId, request.Kind, request.Secret, ct);
            return Results.NoContent();
        }).RequireAccount();

        accounts.MapPut("/{accountId:guid}/name", async (Guid accountId, RenameRequest request, AccountService service, CancellationToken ct) =>
        {
            await service.RenameAsync(accountId, request.Name, ct);
            return Results.NoContent();
        }).RequireAccount();

        accounts.MapPost("/{accountId:guid}/default", async (Guid accountId, AccountService service, CancellationToken ct) =>
        {
            await service.SetDefaultAsync(accountId, ct);
            return Results.NoContent();
        }).RequireAccount();

        accounts.MapDelete("/{accountId:guid}", async (Guid accountId, AccountService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(accountId, ct);
            return Results.NoContent();
        }).RequireAccount();
    }
}
