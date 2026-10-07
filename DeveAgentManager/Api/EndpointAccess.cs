using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>
/// Who may call an API endpoint. Every endpoint under /api declares it, and <see cref="CheckAsync"/> enforces it before
/// the endpoint runs; an endpoint without a declaration answers 500, so a forgotten check can't go unnoticed (the tests
/// call every endpoint).
/// <list type="bullet">
/// <item><see cref="RequireRunner{T}"/>, <see cref="RequireAgent{T}"/>, <see cref="RequireSession{T}"/> and
/// <see cref="RequireAccount{T}"/>: a minimum access to what the route's <c>runnerId</c>, <c>agentId</c>,
/// <c>sessionId</c> or <c>accountId</c> names. Without any access the answer is 404, as if it didn't exist; with too little,
/// 403.</item>
/// <item><see cref="RequireRole{T}"/>: one of the given roles.</item>
/// <item><see cref="ForAnyUser{T}"/>: every signed-in user; the endpoint only touches their own things or filters a list
/// (and checks ids in the request body itself).</item>
/// </list>
/// Users who must choose a new password first can only call endpoints marked <see cref="AllowBeforePasswordChange{T}"/>.
/// </summary>
public static class EndpointAccess
{
    public static TBuilder ForAnyUser<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new Requirement(Kind.AnyUser));

    /// <summary>Any one of <paramref name="roles"/> (flags).</summary>
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, UserRoles roles) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new Requirement(Kind.Role, Roles: roles));

    public static TBuilder RequireRunner<TBuilder>(this TBuilder builder, RunnerAccess level) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new Requirement(Kind.Runner, Level: (int)level));

    public static TBuilder RequireAgent<TBuilder>(this TBuilder builder, AgentAccess level) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new Requirement(Kind.Agent, Level: (int)level));

    public static TBuilder RequireSession<TBuilder>(this TBuilder builder, SessionAccess level) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new Requirement(Kind.Session, Level: (int)level));

    /// <summary>The route's harness login (<c>accountId</c>) is the user's own.</summary>
    public static TBuilder RequireAccount<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new Requirement(Kind.Account));

    public static TBuilder AllowBeforePasswordChange<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new BeforePasswordChange());

    /// <summary>Whether an endpoint declared who may call it (for the tests).</summary>
    public static bool IsDeclared(Endpoint endpoint) => endpoint.Metadata.GetOrderedMetadata<Requirement>().Count > 0;

    /// <summary>The roles an endpoint needs (any one of them), or null when it needs none (for the tests).</summary>
    public static UserRoles? RequiredRoles(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<Requirement>().FirstOrDefault(r => r.Kind == Kind.Role)?.Roles;

    /// <summary>The filter on the /api group.</summary>
    public static async ValueTask<object?> CheckAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var endpoint = http.GetEndpoint();
        var requirements = endpoint?.Metadata.GetOrderedMetadata<Requirement>() ?? [];
        if (requirements.Count == 0)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EndpointAccess))
                .LogError("{Method} {Path} declares no access requirement", http.Request.Method, http.Request.Path);
            return Results.Problem("This endpoint declares no access requirement.", statusCode: StatusCodes.Status500InternalServerError);
        }

        if (SignIn.Current(http) is not { } user)
        {
            return Results.Problem("Sign in first.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (user.MustChangePassword && endpoint!.Metadata.GetMetadata<BeforePasswordChange>() is null)
        {
            return Results.Problem("Choose a new password first.", statusCode: StatusCodes.Status403Forbidden);
        }

        var access = http.RequestServices.GetRequiredService<AccessControl>();
        foreach (var requirement in requirements)
        {
            if (await CheckAsync(requirement, http, user, access) is { } refused)
            {
                return refused;
            }
        }

        if (NeedsRecentPassword(http, requirements) && !await AccountSecurity.RecentAsync(http))
        {
            var withCode = await http.RequestServices.GetRequiredService<SecondFactors>().EnabledAsync(user.Id, http.RequestAborted);
            http.Response.Headers[ApiHeaders.ConfirmPassword] = withCode ? "password+code" : "password";
            return Results.Problem("Enter your password to continue.", statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }

    /// <summary>
    /// Admin actions ask for the password again when it was last entered more than <see cref="SignInSessions.RecentLifetime"/>
    /// ago (the Portal prompts for it): changes only user or settings managers may make, and a Runner's password.
    /// </summary>
    private static bool NeedsRecentPassword(HttpContext http, IReadOnlyList<Requirement> requirements)
    {
        var path = http.Request.Path.Value ?? "";
        if (path.StartsWith("/api/runners/", StringComparison.Ordinal) && path.EndsWith("/password", StringComparison.Ordinal)) return true;
        const UserRoles admin = UserRoles.UserManager | UserRoles.SettingsManager;
        return !HttpMethods.IsGet(http.Request.Method)
            && requirements.FirstOrDefault(r => r.Kind == Kind.Role)?.Roles is { } roles && roles != UserRoles.None && (roles & ~admin) == 0;
    }

    private static async Task<IResult?> CheckAsync(Requirement requirement, HttpContext http, CurrentUser user, AccessControl access)
    {
        var ct = http.RequestAborted;
        switch (requirement.Kind)
        {
            case Kind.AnyUser:
                return null;
            case Kind.Role:
                return (user.Roles & requirement.Roles) != 0 ? null : Forbidden($"This needs the {Roles.Describe(requirement.Roles).Replace(", ", " or ")} role.");
            case Kind.Runner:
                var runner = await access.RunnerAsync(user, RouteId(http, "runnerId"), ct);
                return Check((int)runner, requirement.Level, "Runner", $"{(RunnerAccess)requirement.Level} access to this Runner");
            case Kind.Agent:
                var agent = await access.AgentAsync(user, RouteId(http, "agentId"), ct);
                return Check((int)agent, requirement.Level, "Agent", AgentNeed((AgentAccess)requirement.Level));
            case Kind.Session:
                var session = await access.SessionAsync(user, RouteId(http, "sessionId"), ct);
                return Check((int)session, requirement.Level, "Session", (SessionAccess)requirement.Level == SessionAccess.Owner
                    ? "being the owner of this conversation" : "access to this conversation");
            case Kind.Account:
                return await access.OwnsAccountAsync(user, RouteId(http, "accountId"), ct) ? null : NotFound("Account");
            default:
                throw new InvalidOperationException($"Unknown requirement {requirement.Kind}.");
        }
    }

    private static IResult? Check(int level, int required, string what, string need) =>
        level == 0 ? NotFound(what) : level < required ? Forbidden($"This needs {need}.") : null;

    private static string AgentNeed(AgentAccess level) => level switch
    {
        AgentAccess.Owner => "being the owner of this agent",
        AgentAccess.Write => "read-write access to this agent (and access to its Runner)",
        _ => "access to this agent",
    };

    private static Guid RouteId(HttpContext http, string name) =>
        Guid.TryParse(http.Request.RouteValues[name]?.ToString(), out var id)
            ? id
            : throw new InvalidOperationException($"The endpoint requires access to a {name}, but its route has none.");

    private static IResult NotFound(string what) => Results.Problem($"{what} not found.", statusCode: StatusCodes.Status404NotFound);

    private static IResult Forbidden(string message) => Results.Problem(message, statusCode: StatusCodes.Status403Forbidden);

    private enum Kind
    {
        AnyUser,
        Role,
        Runner,
        Agent,
        Session,
        Account,
    }

    private sealed record Requirement(Kind Kind, int Level = 0, UserRoles Roles = UserRoles.None);

    private sealed record BeforePasswordChange;
}
