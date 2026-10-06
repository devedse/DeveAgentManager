using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>
/// /api/settings: the Manager's settings, the same for everyone (settings managers change them), and /api/me/settings:
/// the signed-in user's own (My settings).
/// </summary>
internal static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings");

        settings.MapGet("/timezones", () => RunnerTimeZones.Ids).ForAnyUser();
        settings.MapGet("/timezone", (RunnerTimeZones zones, CancellationToken ct) => zones.GetAsync(null, ct)).ForAnyUser();
        settings.MapPut("/timezone", async (TimeZoneRequest request, RunnerTimeZones zones, CancellationToken ct) =>
        {
            await zones.SaveAsync(null, request.TimeZoneId, ct);
            return await zones.GetAsync(null, ct);
        }).RequireRole(UserRoles.SettingsManager);

        // The shared Setup Profile holds secrets settings managers enter (login files). They are shown and edited here,
        // and never leave through any other endpoint.
        settings.MapGet("/profile", (SettingsStore store, CancellationToken ct) => store.GetSetupProfileAsync(ct)).RequireRole(UserRoles.SettingsManager);

        settings.MapPut("/profile", async (SetupProfile profile, SettingsStore store, CancellationToken ct) =>
        {
            await store.SaveSetupProfileAsync(profile, ct);
            return await store.GetSetupProfileAsync(ct);
        }).RequireRole(UserRoles.SettingsManager);

        // Copies a harness's settings files from one of the settings manager's Runners into the shared Setup Profile.
        settings.MapPost("/profile/import", async (ImportHarnessFilesRequest request, CurrentUser user, AccessControl access, RunnerService runners,
            RunnerSetupService setup, CancellationToken ct) =>
        {
            if (await access.RunnerAsync(user, request.RunnerId, ct) < RunnerAccess.Use)
            {
                throw new KeyNotFoundException("Runner not found.");
            }

            var runner = await runners.GetAsync(request.RunnerId, ct) ?? throw new KeyNotFoundException("Runner not found.");
            return await setup.ImportHarnessFilesAsync(runner, request.HarnessId, ct);
        }).RequireRole(UserRoles.SettingsManager);

        // Applies a harness's configuration on every ready Runner that has it; each result arrives as OperationFinished.
        settings.MapPost("/harnesses/{harnessId}/apply", async (string harnessId, RunnerService runners, OperationNotifier notifier, CancellationToken ct) =>
        {
            var targets = (await runners.ListAsync(ct)).Where(r => r.State == RunnerState.Ready && r.HarnessIds.Contains(harnessId)).ToList();
            foreach (var runner in targets)
            {
                Task<string?> result;
                try
                {
                    result = runners.ApplyHarnessConfig(runner.Id, harnessId);
                }
                catch (InvalidOperationException ex)
                {
                    result = Task.FromResult<string?>(ex.Message);
                }

                notifier.Track(runner.Id, runner.Name, "apply", result);
            }

            return new OperationStartedDto(targets.Select(r => r.Id).ToList());
        }).RequireRole(UserRoles.SettingsManager);

        var mine = api.MapGroup("/me/settings");

        mine.MapGet("/", async (CurrentUser user, UserSettingsStore store, CancellationToken ct) =>
        {
            var compose = await store.ComposeSettingsAsync(user.Id, ct);
            return new MySettingsDto(await store.GetProfileAsync(user.Id, ct), await store.GetGitHubTokenAsync(user.Id, ct) is not null,
                compose.GitHubToken is not null, compose.GitUserName is not null || compose.GitUserEmail is not null);
        }).ForAnyUser();

        // Their Runners get the change in the background (and before their next agent or shell starts there).
        mine.MapPut("/profile", async (UserProfile profile, CurrentUser user, UserSettingsStore store, RunnerUsers runnerUsers, CancellationToken ct) =>
        {
            await store.SaveProfileAsync(user.Id, profile, ct);
            runnerUsers.SyncForUser(user.Id);
            return await store.GetProfileAsync(user.Id, ct);
        }).ForAnyUser();

        mine.MapPut("/github-token", async (GitHubTokenRequest request, CurrentUser user, UserSettingsStore store, RunnerUsers runnerUsers, CancellationToken ct) =>
        {
            await store.SaveGitHubTokenAsync(user.Id, request.Token, ct);
            runnerUsers.SyncForUser(user.Id);
            return Results.NoContent();
        }).ForAnyUser();
    }
}
