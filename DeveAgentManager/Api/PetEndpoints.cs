using DeveAgentManager.Contracts.Pets;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Pets;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

internal static class PetEndpoints
{
    public static void MapPetEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me/pet", (CurrentUser user, PetService pets, CancellationToken ct) => pets.MineAsync(user, ct)).ForAnyUser();
        api.MapPut("/me/pet", async (PetPreferences request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.SavePreferencesAsync(user, request, ct); return await pets.MineAsync(user, ct);
        }).ForAnyUser();
        api.MapPost("/me/pet/adopt", async (PetAdoption request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.AdoptAsync(user, request, ct); return await pets.MineAsync(user, ct);
        }).ForAnyUser();
        api.MapPost("/me/pet/retire", async (PetRetirement request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.RetireAsync(user, request, ct); return await pets.MineAsync(user, ct);
        }).ForAnyUser();
        api.MapPost("/me/pet/interact", async (PetInteractionRequest request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.InteractAsync(user, request.Action, 0, ct); return Results.NoContent();
        }).ForAnyUser();
        api.MapPost("/me/pet/snooze", async (PetSnoozeRequest request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.InteractAsync(user, "snooze", request.Minutes, ct); return Results.NoContent();
        }).ForAnyUser();
        api.MapGet("/pets/settings", (PetService pets, CancellationToken ct) => pets.GetOptionsAsync(ct)).RequireRole(UserRoles.DamagochiManager);
        api.MapPut("/pets/settings", async (PetOptions request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.SaveOptionsAsync(user, request, ct); return request;
        }).RequireRole(UserRoles.DamagochiManager);
        api.MapGet("/pets/people", (CurrentUser user, PetService pets, CancellationToken ct) => pets.PeopleAsync(user, ct)).RequireRole(UserRoles.DamagochiManager);
        api.MapPut("/pets/people/{userId:guid}", async (Guid userId, PetAdjustment request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.AdjustAsync(user, userId, request, ct); return Results.NoContent();
        }).RequireRole(UserRoles.DamagochiManager);
        api.MapPost("/pets/people", async (PetBulkRequest request, CurrentUser user, PetService pets, CancellationToken ct) => {
            await pets.BulkAsync(user, request.Enabled, ct); return Results.NoContent();
        }).RequireRole(UserRoles.DamagochiManager);
    }
}
