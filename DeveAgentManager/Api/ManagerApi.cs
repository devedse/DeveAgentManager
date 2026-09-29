using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using Microsoft.AspNetCore.Mvc;

namespace DeveAgentManager.Api;

/// <summary>
/// The Manager's HTTP API (/api), used by the browser app. Everything needs the login cookie (the fallback policy);
/// requests that change something need <see cref="ApiHeaders.Request"/> (see <see cref="RequestSecurity"/>).
/// Errors come back as problem details whose <c>detail</c> is the message to show.
/// </summary>
public static class ManagerApi
{
    public const long MaxImportBytes = 200L * 1024 * 1024;

    public static void MapManagerApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(RequestSecurity.RequireApiRequest).AddEndpointFilter(TranslateErrors).DisableAntiforgery();

        api.MapGet("/info", (DtoMapper map, CancellationToken ct) => map.InfoAsync(ct));

        MapRunners(api.MapGroup("/runners"));
        MapAgents(api.MapGroup("/agents"));
        MapSessions(api.MapGroup("/sessions"));
        MapAccounts(api.MapGroup("/accounts"));
        MapSettings(api.MapGroup("/settings"));

        api.MapGet("/providers/{providerId}/check", async (string providerId, RunnerService runners, CancellationToken ct) =>
        {
            var provider = runners.FindProvider(providerId) ?? throw new KeyNotFoundException("Provider not found.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                return await provider.CheckAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return [new ProviderCheck("Test", CheckStatus.Failed, ex.Message)];
            }
        });

        api.MapGet("/harnesses/{harnessId}/models", async (string harnessId, RunnerSetupService setup, RunnerService runners, CancellationToken ct) =>
            await setup.ListModelsAsync(harnessId, await runners.ListAsync(ct), ct));
    }

    private static void MapRunners(RouteGroupBuilder runners)
    {
        runners.MapGet("/", async (RunnerService service, DtoMapper map, CancellationToken ct) =>
        {
            var names = await map.AccountNamesAsync(ct);
            return (await service.ListAsync(ct)).Select(r => map.Runner(r, names)).ToList();
        });

        runners.MapGet("/{runnerId:guid}", async (Guid runnerId, RunnerService service, DtoMapper map, CancellationToken ct) =>
            await service.GetAsync(runnerId, ct) is { } runner ? Results.Ok(map.Runner(runner, await map.AccountNamesAsync(ct))) : Results.NotFound());

        runners.MapGet("/{runnerId:guid}/usage", async (Guid runnerId, RunnerService service, RunnerStatusCache status, CancellationToken ct) =>
            await service.GetAsync(runnerId, ct) is not null ? Results.Ok(status.History(runnerId)) : Results.NotFound());

        runners.MapPost("/", async (CreateRunnerRequest request, RunnerService service, DtoMapper map, CancellationToken ct) =>
            map.Runner(await service.CreateAsync(request, ct), new Dictionary<Guid, string>()));

        runners.MapPost("/{runnerId:guid}/start", (Guid runnerId, RunnerService service) => Accepted(() => service.Start(runnerId)));
        runners.MapPost("/{runnerId:guid}/stop", (Guid runnerId, RunnerService service) => Accepted(() => service.Stop(runnerId)));
        runners.MapPost("/{runnerId:guid}/setup", (Guid runnerId, RunnerService service) => Accepted(() => service.RerunSetup(runnerId)));
        runners.MapPost("/{runnerId:guid}/cancel", (Guid runnerId, RunnerService service) => Accepted(() => service.Cancel(runnerId)));
        runners.MapDelete("/{runnerId:guid}", (Guid runnerId, bool? destroyMachine, RunnerService service) =>
            Accepted(() => service.Delete(runnerId, destroyMachine ?? true)));

        runners.MapPost("/{runnerId:guid}/forget-host-key", async (Guid runnerId, RunnerService service, CancellationToken ct) =>
        {
            await service.ForgetHostKeyAsync(runnerId, ct);
            return Results.NoContent();
        });

        runners.MapPost("/{runnerId:guid}/harnesses/{harnessId}/update", async (Guid runnerId, string harnessId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            await Track(runnerId, service, notifier, "update", () => service.UpdateHarness(runnerId, harnessId), ct));

        runners.MapPost("/{runnerId:guid}/harnesses/{harnessId}/install", async (Guid runnerId, string harnessId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            await Track(runnerId, service, notifier, "install", () => service.InstallHarness(runnerId, harnessId), ct));

        // A tool's install script also updates it.
        runners.MapPost("/{runnerId:guid}/tools/{toolId}/install", async (Guid runnerId, string toolId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            await Track(runnerId, service, notifier, "install", () => service.InstallTool(runnerId, toolId), ct));

        runners.MapPost("/{runnerId:guid}/tools/{toolId}/update", async (Guid runnerId, string toolId, RunnerService service, OperationNotifier notifier, CancellationToken ct) =>
            await Track(runnerId, service, notifier, "update", () => service.InstallTool(runnerId, toolId, update: true), ct));

        // The agents' state from tmux right now (e.g. after a terminal closed), instead of at the monitor's next round.
        runners.MapPost("/{runnerId:guid}/refresh", async (Guid runnerId, RunnerService service, AgentService agents, CancellationToken ct) =>
        {
            if (await service.GetAsync(runnerId, ct) is { State: RunnerState.Ready } runner)
            {
                await agents.RefreshRunnerAsync(runner, ct);
            }

            return Results.NoContent();
        });

        // The Details panel's refresh: the installed versions and the agents' state, read from the Runner now.
        runners.MapPost("/{runnerId:guid}/refresh-details", async (Guid runnerId, RunnerService service, AgentService agents, CancellationToken ct) =>
        {
            await service.RefreshVersionsAsync(runnerId, ct);
            if (await service.GetAsync(runnerId, ct) is { State: RunnerState.Ready } runner)
            {
                await agents.RefreshRunnerAsync(runner, ct);
            }

            return Results.NoContent();
        });

        runners.MapGet("/{runnerId:guid}/log", (Guid runnerId, RunnerLogStore logs) => logs.GetRecent(runnerId).TakeLast(2000).ToList());

        runners.MapGet("/{runnerId:guid}/activity", async (Guid runnerId, RunnerService service, CancellationToken ct) =>
            (await service.ListActivityAsync(runnerId, ct)).Select(a => new RunnerActivityDto(a.Id, a.Name, a.StartedAt, a.FinishedAt, a.Outcome, a.Error)).ToList());
    }

    private static void MapAgents(RouteGroupBuilder agents)
    {
        agents.MapGet("/", async (AgentService service, DtoMapper map, CancellationToken ct) =>
        {
            var names = await map.AccountNamesAsync(ct);
            return (await service.ListAsync(cancellationToken: ct)).Where(a => a.Runner is not null).Select(a => DtoMapper.Agent(a, a.Runner!, names)).ToList();
        });

        agents.MapGet("/{agentId:guid}", async (Guid agentId, AgentService service, DtoMapper map, CancellationToken ct) =>
            await service.GetAsync(agentId, ct) is { Runner: { } runner } agent
                ? Results.Ok(DtoMapper.Agent(agent, runner, await map.AccountNamesAsync(ct)))
                : Results.NotFound());

        agents.MapPost("/", async (CreateAgentRequest request, AgentService service, DtoMapper map, CancellationToken ct) =>
        {
            var agent = await service.CreateAsync(request, ct);
            var created = await service.GetAsync(agent.Id, ct) ?? throw new InvalidOperationException("The agent disappeared.");
            return DtoMapper.Agent(created, created.Runner!, await map.AccountNamesAsync(ct));
        });

        agents.MapPost("/{agentId:guid}/start", async (Guid agentId, bool? restart, AgentService service, CancellationToken ct) =>
        {
            await service.StartAsync(agentId, restart ?? false, ct);
            return Results.NoContent();
        });

        agents.MapPost("/{agentId:guid}/stop", async (Guid agentId, AgentService service, CancellationToken ct) =>
        {
            await service.StopAsync(agentId, ct);
            return Results.NoContent();
        });

        agents.MapDelete("/{agentId:guid}", async (Guid agentId, AgentService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(agentId, ct);
            return Results.NoContent();
        });

        agents.MapPost("/{agentId:guid}/switch", async (Guid agentId, SwitchAgentRequest request, AgentService service, CancellationToken ct) =>
        {
            await service.SwitchAsync(agentId, request.HarnessId, request.AccountId, ct);
            return Results.NoContent();
        });

        agents.MapGet("/{agentId:guid}/viewers", (Guid agentId, AgentService service, CancellationToken ct) => service.ListViewersAsync(agentId, ct));

        // The project's icon. The URL has the icon's version, so browsers keep it until it changes.
        agents.MapGet("/{agentId:guid}/icon", async (Guid agentId, HttpContext context, AgentIconService icons, CancellationToken ct) =>
            await icons.GetAsync(agentId, ct) is { } icon ? Image(context, icon.Data, icon.ContentType, TimeSpan.FromDays(365)) : Results.NotFound());

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
        });

        agents.MapGet("/{agentId:guid}/icon/image", async (Guid agentId, string path, HttpContext context, AgentIconService icons, CancellationToken ct) =>
            await icons.ReadAsync(agentId, path, ct) is { } image ? Image(context, image.Data, image.ContentType, TimeSpan.FromMinutes(5)) : Results.NotFound());

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
        });

        agents.MapPost("/{agentId:guid}/viewers/disconnect", async (Guid agentId, DisconnectViewersRequest request, AgentService service, CancellationToken ct) =>
        {
            await service.DisconnectViewersAsync(agentId, request.Tty, request.KeepTty, ct);
            return Results.NoContent();
        });

        agents.MapGet("/{agentId:guid}/session", async (Guid agentId, SessionService sessions, DtoMapper map, CancellationToken ct) =>
            await sessions.GetForAgentAsync(agentId, ct) is { } session && await sessions.GetAsync(session.Id, ct) is { } full
                ? Results.Ok(DtoMapper.Session(full, await map.AccountNamesAsync(ct)))
                : Results.NotFound());
    }

    private static void MapSessions(RouteGroupBuilder sessions)
    {
        sessions.MapGet("/", async (SessionService service, DtoMapper map, CancellationToken ct) =>
        {
            var names = await map.AccountNamesAsync(ct);
            return (await service.ListAsync(ct)).Select(s => DtoMapper.Session(s, names)).ToList();
        });

        sessions.MapGet("/{sessionId:guid}", async (Guid sessionId, SessionService service, DtoMapper map, CancellationToken ct) =>
            await service.GetAsync(sessionId, ct) is { } session ? Results.Ok(DtoMapper.Session(session, await map.AccountNamesAsync(ct))) : Results.NotFound());

        // The conversation itself, as a DeveSession (see docs/session-formats.md).
        sessions.MapGet("/{sessionId:guid}/content", async (Guid sessionId, SessionService service, CancellationToken ct) =>
            Results.Text((await service.LoadAsync(sessionId, ct)).ToJson(), "application/json", Encoding.UTF8));

        sessions.MapGet("/{sessionId:guid}/export", async (Guid sessionId, string format, SessionService service, CancellationToken ct) =>
        {
            var export = await service.ExportAsync(sessionId, format, ct);
            return Results.File(Encoding.UTF8.GetBytes(export.Content), export.ContentType, export.FileName);
        });

        sessions.MapPost("/import", async (IFormFile file, SessionService service, DtoMapper map, CancellationToken ct) =>
        {
            if (file.Length > MaxImportBytes)
            {
                throw new ArgumentException("Session files can be up to 200 MB.");
            }

            using var reader = new StreamReader(file.OpenReadStream());
            var session = await service.ImportAsync(file.FileName, await reader.ReadToEndAsync(ct), ct);
            return DtoMapper.Session(session, await map.AccountNamesAsync(ct));
        });

        sessions.MapDelete("/{sessionId:guid}", async (Guid sessionId, SessionService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sessionId, ct);
            return Results.NoContent();
        });
    }

    private static void MapAccounts(RouteGroupBuilder accounts)
    {
        accounts.MapGet("/", async (AccountService service, CancellationToken ct) =>
        {
            var usage = await service.CountAgentsAsync(ct);
            return (await service.ListAsync(cancellationToken: ct)).Select(a => DtoMapper.Account(a, usage.GetValueOrDefault(a.Id))).ToList();
        });

        accounts.MapPost("/", async (AddAccountRequest request, AccountService service, CancellationToken ct) =>
            DtoMapper.Account(await service.AddAsync(request.HarnessId, request.Name ?? "", request.Kind, request.Secret, ct), 0));

        accounts.MapPut("/{accountId:guid}/secret", async (Guid accountId, ReplaceAccountSecretRequest request, AccountService service, CancellationToken ct) =>
        {
            await service.ReplaceSecretAsync(accountId, request.Kind, request.Secret, ct);
            return Results.NoContent();
        });

        accounts.MapPut("/{accountId:guid}/name", async (Guid accountId, RenameRequest request, AccountService service, CancellationToken ct) =>
        {
            await service.RenameAsync(accountId, request.Name, ct);
            return Results.NoContent();
        });

        accounts.MapPost("/{accountId:guid}/default", async (Guid accountId, AccountService service, CancellationToken ct) =>
        {
            await service.SetDefaultAsync(accountId, ct);
            return Results.NoContent();
        });

        accounts.MapDelete("/{accountId:guid}", async (Guid accountId, AccountService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(accountId, ct);
            return Results.NoContent();
        });
    }

    private static void MapSettings(RouteGroupBuilder settings)
    {
        // The Setup Profile holds secrets the admin enters (API keys, login files). They are shown and edited here,
        // by the one admin; they never leave through any other endpoint.
        settings.MapGet("/profile", (SettingsStore store, CancellationToken ct) => store.GetSetupProfileAsync(ct));

        settings.MapPut("/profile", async (SetupProfile profile, SettingsStore store, CancellationToken ct) =>
        {
            await store.SaveSetupProfileAsync(profile, ct);
            return await store.GetSetupProfileAsync(ct);
        });

        settings.MapPost("/profile/import", async (ImportHarnessFilesRequest request, RunnerService runners, RunnerSetupService setup, CancellationToken ct) =>
        {
            var runner = await runners.GetAsync(request.RunnerId, ct) ?? throw new KeyNotFoundException("Runner not found.");
            return await setup.ImportHarnessFilesAsync(runner, request.HarnessId, ct);
        });

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
        });

        settings.MapPut("/github-pat", async (GitHubPatRequest request, SettingsStore store, CancellationToken ct) =>
        {
            if (store.GitHubPatFromCompose)
            {
                throw new InvalidOperationException("The token is set with GITHUB_PAT in the compose file.");
            }

            await store.SaveGitHubPatAsync(request.Pat, ct);
            return Results.NoContent();
        });
    }

    /// <summary>Starts a Runner operation whose result is reported through <see cref="OperationNotifier"/>.</summary>
    private static async Task<IResult> Track(Guid runnerId, RunnerService service, OperationNotifier notifier, string operation, Func<Task<string?>> start, CancellationToken ct)
    {
        var runner = await service.GetAsync(runnerId, ct) ?? throw new KeyNotFoundException("Runner not found.");
        notifier.Track(runner.Id, runner.Name, operation, start());
        return Results.Accepted(value: new OperationStartedDto([runner.Id]));
    }

    /// <summary>
    /// An image from a Runner. An SVG can hold script: shown as an image it doesn't run, and opened by itself the sandbox
    /// keeps it from running as the Portal.
    /// </summary>
    private static IResult Image(HttpContext context, byte[] data, string contentType, TimeSpan maxAge)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        headers.CacheControl = $"private, max-age={(int)maxAge.TotalSeconds}";
        return Results.File(data, contentType);
    }

    private static IResult Accepted(Action start)
    {
        start();
        return Results.Accepted();
    }

    /// <summary>The services report problems as exceptions with a message meant for the user.</summary>
    private static async ValueTask<object?> TranslateErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            var status = ex switch
            {
                KeyNotFoundException => StatusCodes.Status404NotFound,
                ArgumentException or FormatException or BadHttpRequestException => StatusCodes.Status400BadRequest,
                InvalidOperationException or AgentNotRunningException => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError,
            };
            if (status == StatusCodes.Status500InternalServerError)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DeveAgentManager.Api")
                    .LogError(ex, "{Method} {Path} failed", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
            }

            return Results.Problem(detail: ex.Message, statusCode: status);
        }
    }
}
