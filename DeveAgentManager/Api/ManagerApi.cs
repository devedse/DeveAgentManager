using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>
/// The Manager's HTTP API (/api), used by the browser app. Every endpoint needs a sign-in and declares who may call it
/// (see <see cref="EndpointAccess"/>); requests that change something need <see cref="ApiHeaders.Request"/> (see
/// <see cref="RequestSecurity"/>). Errors come back as problem details whose <c>detail</c> is the message to show.
/// </summary>
public static class ManagerApi
{
    public static void MapManagerApi(this IEndpointRouteBuilder app)
    {
        // The access check runs before the errors are translated: a mistake in it is the Manager's (500), not the user's.
        var api = app.MapGroup("/api")
            .AddEndpointFilter(RequestSecurity.RequireApiRequest)
            .AddEndpointFilter(EndpointAccess.CheckAsync)
            .AddEndpointFilter(TranslateErrors)
            .DisableAntiforgery();

        api.MapGet("/info", (CurrentUser user, DtoMapper map) => map.Info(user)).ForAnyUser();
        api.MapGet("/build", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return new AppBuildDto(DtoMapper.BuildId, DtoMapper.Version);
        }).ForAnyUser();

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
        }).RequireRole(UserRoles.SettingsManager | UserRoles.RunnerCreator);

        // The harness's own model catalog, read on one of the user's Runners that has it.
        api.MapGet("/harnesses/{harnessId}/models", async (string harnessId, CurrentUser user, RunnerSetupService setup, RunnerService runners, CancellationToken ct) =>
            await setup.ListModelsAsync(harnessId, await runners.ListVisibleAsync(user, ct), ct)).ForAnyUser();

        // The Portal's password prompt for admin actions (see EndpointAccess.NeedsRecentPassword). Shares the sign-in limits.
        api.MapPost("/me/confirm-password", async (ConfirmPasswordRequest request, HttpContext context, CurrentUser user, UserService users,
            SecondFactors factors, SignInSessions sessions, LoginThrottle throttle, AuditLog audit, CancellationToken ct) =>
        {
            if (request.Password is not { Length: > 0 } password || password.Length > UserService.MaxPasswordLength || request.Code?.Length > 100)
                throw new ArgumentException("Enter your password.");
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            using var lease = throttle.TryEnter(ip, user.Username);
            if (lease is null)
            {
                context.Response.Headers.RetryAfter = "300";
                return Results.Problem("Too many attempts. Wait a few minutes.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (await users.SignInAsync(user.Username, password, ct) is null)
                throw new ArgumentException("That password is incorrect.");
            if (await factors.EnabledAsync(user.Id, ct) && !await factors.VerifyAsync(user.Id, request.Code ?? "", ct))
                throw new ArgumentException("That code is incorrect.");
            await sessions.ReauthenticateAsync(SignIn.SessionId(context), ct);
            await audit.RecordAsync(user, AuditActions.IdentityConfirmed, "Portal session", $"IP: {ip}", ct);
            return Results.NoContent();
        }).ForAnyUser();

        api.MapGet("/logs", (HttpContext context, string? minimum, string? search, int? take, DeveAgentManager.Logging.ManagerLogStore logs) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var level = Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(minimum, true, out var parsed) ? parsed : Microsoft.Extensions.Logging.LogLevel.Information;
            return logs.Read(level, search, take ?? 300);
        }).RequireRole(UserRoles.LogViewer);

        api.MapUserEndpoints();
        api.MapRunnerEndpoints();
        api.MapAgentEndpoints();
        api.MapSessionEndpoints();
        api.MapAccountEndpoints();
        api.MapSettingsEndpoints();
    }

    /// <summary>Starts a Runner operation whose result is reported through <see cref="OperationNotifier"/>.</summary>
    internal static async Task<IResult> Track(Guid runnerId, RunnerService service, OperationNotifier notifier, string operation, Func<Task<string?>> start, CancellationToken ct)
    {
        var runner = await service.GetAsync(runnerId, ct) ?? throw new KeyNotFoundException("Runner not found.");
        notifier.Track(runner.Id, runner.Name, operation, start());
        return Results.Accepted(value: new OperationStartedDto([runner.Id]));
    }

    /// <summary>
    /// An image from a Runner. An SVG can hold script: shown as an image it doesn't run, and opened by itself the sandbox
    /// keeps it from running as the Portal.
    /// </summary>
    internal static IResult Image(HttpContext context, byte[] data, string contentType, TimeSpan maxAge)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        headers.CacheControl = $"private, max-age={(int)maxAge.TotalSeconds}";
        return Results.File(data, contentType);
    }

    internal static IResult Accepted(Action start)
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
                UnauthorizedAccessException => StatusCodes.Status403Forbidden,
                ArgumentException or FormatException or BadHttpRequestException => StatusCodes.Status400BadRequest,
                InvalidOperationException or AgentNotRunningException => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError,
            };
            if (status == StatusCodes.Status500InternalServerError)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DeveAgentManager.Api")
                    .LogError(ex, "{Method} {Path} failed", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
            }

            return Results.Problem(detail: status == 500 ? "The request failed. Check the Manager log." : ex.Message, statusCode: status);
        }
    }
}
