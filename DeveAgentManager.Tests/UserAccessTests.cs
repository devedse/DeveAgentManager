// Short in-memory requests: cancelling them with the test run adds nothing.
#pragma warning disable xUnit1051

using System.Net;
using System.Net.Http.Json;
using System.Text;
using DeveAgentManager.Api;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Who may do what (docs/users-plan.md), through the Manager's HTTP API: every endpoint declares its access; someone
/// else's Runners, agents, conversations and harness logins answer 404 and role endpoints 403; lists only show what is
/// yours or shared with you. Sharing, leaving a share and the first sign-in's new password work end to end.
/// </summary>
public sealed class UserAccessTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    private const string AdminPassword = "test-password-1234";
    private const string Password = "ketty-password-1";

    [Fact]
    public void EveryApiEndpoint_DeclaresWhoMayCallIt()
    {
        var endpoints = ApiEndpoints();
        Assert.True(endpoints.Count > 50, $"Only {endpoints.Count} API endpoints were found.");
        Assert.Empty(endpoints.Where(e => !EndpointAccess.IsDeclared(e.Endpoint)).Select(e => $"{e.Method} {e.Endpoint.RoutePattern.RawText}"));
    }

    [Fact]
    public async Task SomeoneElsesThings_AreNotFound_RoleEndpointsForbidden_AndListsDontShowThem()
    {
        var admins = await SeedAsync(User.BootstrapId);
        var ketty = await SignedInAsNewUserAsync();
        var ids = new Dictionary<string, Guid>
        {
            ["runnerId"] = admins.RunnerId,
            ["agentId"] = admins.AgentId,
            ["sessionId"] = admins.SessionId,
            ["accountId"] = admins.AccountId,
        };

        var wrong = new List<string>();
        foreach (var (endpoint, method) in ApiEndpoints())
        {
            var pattern = endpoint.RoutePattern;
            var path = string.Concat(pattern.PathSegments.Select(segment => "/" + string.Concat(segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternSeparatorPart separator => separator.Content,
                RoutePatternParameterPart { Name: "userId" } => User.BootstrapId.ToString(),
                RoutePatternParameterPart parameter => ids.TryGetValue(parameter.Name, out var id) ? id.ToString()
                    : parameter.ParameterPolicies.Any(p => p.Content == "guid") ? Guid.NewGuid().ToString() : "x",
                _ => "",
            }))));
            // Query parameters some endpoints need to bind (the access check runs after binding).
            var request = new HttpRequestMessage(new HttpMethod(method), $"{path}?format=markdown&path=x&transferTo={User.BootstrapId}");
            if (method is "POST" or "PUT" or "PATCH")
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            var response = await ketty.SendAsync(request);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync();
            // A role she lacks is refused first, the same for every id; without access to the thing itself, it isn't there.
            var expected = EndpointAccess.RequiredRoles(endpoint) is not null ? StatusCodes.Status403Forbidden
                : pattern.Parameters.Any(p => ids.ContainsKey(p.Name)) ? StatusCodes.Status404NotFound
                : (int?)null;
            if (expected is { } code ? status != code : status is 401 or 403 or >= 500)
            {
                wrong.Add($"{method} {pattern.RawText}: {status}");
            }
            else if (ids.Values.FirstOrDefault(id => body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)) is var leaked && leaked != Guid.Empty)
            {
                wrong.Add($"{method} {pattern.RawText}: shows {ids.First(i => i.Value == leaked).Key}");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task RunnerManagers_SeeOtherPeoplesAgentsListed_ButCantOpenThem()
    {
        var admins = await SeedAsync(User.BootstrapId);
        var manager = await SignedInAsNewUserAsync(UserRoles.RunnerManager);
        var runner = await manager.GetFromJsonAsync<RunnerDto>($"/api/runners/{admins.RunnerId}", ApiJson.Options);
        Assert.Equal(RunnerAccess.Owner, runner!.Access);
        var agent = Assert.Single(runner.Agents, a => a.Id == admins.AgentId);
        Assert.Equal(AgentAccess.Listed, agent.Access);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/agents/{admins.AgentId}/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/agents/{admins.AgentId}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/sessions/{admins.SessionId}")).StatusCode);
        Assert.DoesNotContain(admins.SessionId.ToString(), await manager.GetStringAsync("/api/sessions"));
    }

    [Theory]
    [InlineData(UserRoles.ConversationReader)]
    [InlineData(UserRoles.Admin)]
    public async Task ConversationReaders_OpenEveryAgentAndSessionWithoutOwnershipOrShares(UserRoles roles)
    {
        var things = await SeedAsync(User.BootstrapId);
        var (reader, readerId) = await SignedInAsNewUserWithIdAsync(roles);
        Assert.Equal(AgentAccess.Read, (await AgentAsync(reader, things.AgentId)).Access);
        Assert.Contains(things.AgentId.ToString(), await reader.GetStringAsync("/api/agents"));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/agents/{things.AgentId}/session")).StatusCode);
        foreach (var id in new[] { things.SessionId, things.ImportedSessionId })
        {
            Assert.Equal(SessionAccess.Read, (await reader.GetFromJsonAsync<SessionDto>($"/api/sessions/{id}", ApiJson.Options))!.Access);
            Assert.Contains(id.ToString(), await reader.GetStringAsync("/api/sessions"));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/sessions/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/sessions/{id}/title", new RenameSessionRequest("changed"))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/agents/{things.AgentId}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/agents/{things.AgentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/accounts/{things.AccountId}")).StatusCode);
        var access = manager.Services.GetRequiredService<AccessControl>();
        Assert.Contains(readerId, (await access.AudienceAsync(new ManagerEvent(ManagerEventKind.AgentChanged, things.RunnerId, things.AgentId)))!);
        Assert.Contains(readerId, (await access.AudienceAsync(new ManagerEvent(ManagerEventKind.SessionChanged, SessionId: things.ImportedSessionId)))!);
        await Users.UpdateAsync(TestUsers.Admin, readerId, null, UserRoles.None, disabled: false);
        Assert.Equal(AgentAccess.None, await access.AgentAsync(CurrentUser.From((await Users.GetAsync(readerId))!), things.AgentId));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/agents/{things.AgentId}")).StatusCode);
    }

    [Fact]
    public async Task SharingAnAgent_ReadOnly_ReadWrite_AndLeaving()
    {
        var admins = await SeedAsync(User.BootstrapId);
        var (ketty, kettyId) = await SignedInAsNewUserWithIdAsync();
        var sharing = manager.Services.GetRequiredService<Sharing>();

        await sharing.ShareAgentAsync(TestUsers.Admin, admins.AgentId, kettyId, AgentAccess.Read);
        Assert.Equal(AgentAccess.Read, (await AgentAsync(ketty, admins.AgentId)).Access);
        Assert.Contains(admins.AgentId.ToString(), await ketty.GetStringAsync("/api/agents"));
        Assert.Equal(HttpStatusCode.Forbidden, (await ketty.PostAsync($"/api/agents/{admins.AgentId}/start", null)).StatusCode);
        // Its workspace's conversations can be read, not renamed, deleted or shared further.
        var session = await ketty.GetFromJsonAsync<SessionDto>($"/api/sessions/{admins.SessionId}", ApiJson.Options);
        Assert.Equal(SessionAccess.Read, session!.Access);
        Assert.Equal(HttpStatusCode.Forbidden, (await ketty.PutAsJsonAsync($"/api/sessions/{admins.SessionId}/title", new RenameSessionRequest("Mine now"), ApiJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ketty.PutAsync($"/api/sessions/{admins.SessionId}/shares/{kettyId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ketty.PutAsJsonAsync($"/api/agents/{admins.AgentId}/shares/{kettyId}", new ShareAgentRequest(AgentAccess.Write), ApiJson.Options)).StatusCode);

        // Read-write needs access to the Runner too; then she can prompt it, with its own login or one of hers.
        await sharing.ShareAgentAsync(TestUsers.Admin, admins.AgentId, kettyId, AgentAccess.Write);
        Assert.Equal(AgentAccess.Read, (await AgentAsync(ketty, admins.AgentId)).Access);
        await manager.Services.GetRequiredService<RunnerMembers>().SetAsync(TestUsers.Admin, admins.RunnerId, kettyId, RunnerAccess.Use);
        var shared = await AgentAsync(ketty, admins.AgentId);
        Assert.Equal((AgentAccess.Write, User.BootstrapId), (shared.Access, shared.LoginOwnerId));
        var start = await ketty.PostAsync($"/api/agents/{admins.AgentId}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode); // The Runner is stopped: past the access check.
        var switching = await ketty.PostAsJsonAsync($"/api/agents/{admins.AgentId}/switch", new SwitchAgentRequest("claude", admins.AccountId), ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, switching.StatusCode);
        Assert.Contains("your own", await switching.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await ketty.DeleteAsync($"/api/agents/{admins.AgentId}")).StatusCode);

        // Leaving: it is gone for her, also from the Runner she can still use (only its managers see other people's agents).
        Assert.Equal(HttpStatusCode.NoContent, (await ketty.DeleteAsync($"/api/agents/{admins.AgentId}/shares/{kettyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ketty.GetAsync($"/api/agents/{admins.AgentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ketty.GetAsync($"/api/sessions/{admins.SessionId}")).StatusCode);
        Assert.DoesNotContain(admins.AgentId.ToString(), await ketty.GetStringAsync($"/api/runners/{admins.RunnerId}"));
    }

    [Fact]
    public async Task SharingAConversation_IsReadOnly_AndOnlyTheOwnerSharesIt()
    {
        var admins = await SeedAsync(User.BootstrapId);
        var (ketty, kettyId) = await SignedInAsNewUserWithIdAsync();
        var (other, otherId) = await SignedInAsNewUserWithIdAsync();
        await manager.Services.GetRequiredService<Sharing>().ShareSessionAsync(TestUsers.Admin, admins.ImportedSessionId, kettyId);

        var session = await ketty.GetFromJsonAsync<SessionDto>($"/api/sessions/{admins.ImportedSessionId}", ApiJson.Options);
        Assert.Equal((SessionAccess.Read, "admin"), (session!.Access, session.OwnerName));
        Assert.Equal(HttpStatusCode.OK, (await ketty.GetAsync($"/api/sessions/{admins.ImportedSessionId}/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ketty.DeleteAsync($"/api/sessions/{admins.ImportedSessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ketty.PutAsync($"/api/sessions/{admins.ImportedSessionId}/shares/{otherId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/sessions/{admins.ImportedSessionId}")).StatusCode);
        // Someone she shares nothing with can't take her share away.
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/sessions/{admins.ImportedSessionId}/shares/{kettyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ketty.GetAsync($"/api/sessions/{admins.ImportedSessionId}")).StatusCode);
    }

    [Fact]
    public async Task NewUsers_ChooseTheirOwnPasswordFirst()
    {
        var username = NewUsername();
        await Users.CreateAsync(TestUsers.Admin, username, null, Password, UserRoles.None);
        var client = await SignInAsync(username, Password, "/agents");

        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/me", ApiJson.Options);
        Assert.True(me!.MustChangePassword);
        var refused = await client.GetAsync("/api/runners");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Choose a new password first", await refused.Content.ReadAsStringAsync());
        var page = await client.GetAsync("/agents");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/password?returnUrl=", page.Headers.Location?.ToString());

        var wrong = await ChangePasswordAsync(client, "not-the-password", "a-new-password-1");
        Assert.Contains("error=", wrong.Headers.Location?.ToString());
        Assert.Contains("error=", (await ChangePasswordAsync(client, Password, Password)).Headers.Location?.ToString());
        var changed = await ChangePasswordAsync(client, Password, "a-new-password-1");
        Assert.Equal("/agents", changed.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/runners")).StatusCode);
        Assert.False((await client.GetFromJsonAsync<CurrentUserDto>("/api/me", ApiJson.Options))!.MustChangePassword);

        var old = await Anonymous().PostAccountAsync("/account/login", Form(("username", username), ("password", Password)));
        Assert.Contains("failed=1", old.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ChangingThePassword_EndsTheUsersOtherSignIns()
    {
        var username = NewUsername();
        var user = await Users.CreateAsync(TestUsers.Admin, username, null, Password, UserRoles.None);
        var first = await SignInAsync(username, Password);
        await ChangePasswordAsync(first, Password, "a-new-password-1");
        var second = await SignInAsync(username, "a-new-password-1");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/runners")).StatusCode);

        await Users.ResetPasswordAsync(TestUsers.Admin, user.Id, "set-by-admin-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/me")).StatusCode);
        await Users.UpdateAsync(TestUsers.Admin, user.Id, null, UserRoles.None, disabled: true);
        Assert.Contains("failed=1", (await Anonymous().PostAccountAsync("/account/login", Form(("username", username), ("password", "set-by-admin-1")))).Headers.Location?.ToString());
    }

    [Fact]
    public async Task UserManagers_OnlyGiveRolesTheyHave_AndOnlyManageUsersWithTheirRoles()
    {
        var userManager = CurrentUser.From(await Users.CreateAsync(TestUsers.Admin, NewUsername(), null, Password, UserRoles.UserManager));
        var plain = await Users.CreateAsync(userManager, NewUsername(), null, Password, UserRoles.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Users.CreateAsync(userManager, NewUsername(), null, Password, UserRoles.RunnerManager));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Users.UpdateAsync(userManager, plain.Id, null, UserRoles.SettingsManager, false));
        // An admin has roles the user manager lacks: taking it over by a new password is refused.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Users.ResetPasswordAsync(userManager, User.BootstrapId, "taken-over-1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Users.DeleteAsync(userManager, User.BootstrapId, plain.Id));
        await Users.UpdateAsync(userManager, plain.Id, "Plain Person", UserRoles.UserManager, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Users.UpdateAsync(userManager, userManager.Id, null, UserRoles.UserManager, disabled: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Users.DeleteAsync(userManager, userManager.Id, plain.Id));

        var plainUser = await Users.GetAsync(plain.Id);
        Assert.Equal(("Plain Person", UserRoles.UserManager), (plainUser!.DisplayName, plainUser.Roles));
    }

    [Fact]
    public async Task WrongPasswords_DoNotLockOutTheAccountOwner()
    {
        var username = NewUsername();
        await Users.CreateAsync(TestUsers.Admin, username, null, Password, UserRoles.None);
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(await Users.SignInAsync(username, "wrong-password"));
        }

        Assert.NotNull(await Users.SignInAsync(username, Password));
        var user = (await Users.ListAsync()).Single(u => u.Username == username);
        Assert.Null(user.LockedUntil);
        Assert.Null(await Users.SignInAsync("nobody-" + username, Password));
    }

    [Fact]
    public async Task DeletingAUser_GivesWhatTheyOwnedToTheHeir_WithTheirAccess()
    {
        var leaving = await Users.CreateAsync(TestUsers.Admin, NewUsername(), null, Password, UserRoles.None);
        var heir = await Users.CreateAsync(TestUsers.Admin, NewUsername(), null, Password, UserRoles.None);
        var theirs = await SeedAsync(leaving.Id);
        var admins = await SeedAsync(User.BootstrapId);
        await using (var db = await DbAsync())
        {
            // An agent of theirs on the admin's Runner, where they have Use access.
            db.RunnerMembers.Add(new RunnerMember { RunnerId = admins.RunnerId, UserId = leaving.Id, Access = RunnerAccess.Use, GrantedAt = DateTime.UtcNow });
            db.Agents.Add(new Agent { Id = Guid.NewGuid(), RunnerId = admins.RunnerId, OwnerId = leaving.Id, Name = $"a{Guid.NewGuid():N}"[..12], HarnessId = "claude", State = AgentState.Stopped });
            await db.SaveChangesAsync();
        }

        var changed = await Users.DeleteAsync(TestUsers.Admin, leaving.Id, heir.Id);

        Assert.Equal(new[] { theirs.RunnerId, admins.RunnerId }.Order(), changed.Order());
        await using var check = await DbAsync();
        Assert.Equal(heir.Id, (await check.Runners.FindAsync(theirs.RunnerId))!.OwnerId);
        Assert.Equal(heir.Id, (await check.Sessions.FindAsync(theirs.SessionId))!.OwnerId);
        Assert.False(await check.Agents.AnyAsync(a => a.OwnerId == leaving.Id));
        Assert.Equal(RunnerAccess.Use, (await check.RunnerMembers.SingleAsync(m => m.RunnerId == admins.RunnerId && m.UserId == heir.Id)).Access);
        Assert.False(await check.Accounts.AnyAsync(a => a.OwnerId == leaving.Id));
        Assert.Null(await check.Users.FindAsync(leaving.Id));
    }

    [Fact]
    public async Task LiveUpdates_GoToThePeopleConcerned()
    {
        var admins = await SeedAsync(User.BootstrapId);
        var ketty = await Users.CreateAsync(TestUsers.Admin, NewUsername(), null, Password, UserRoles.None);
        var watcher = await Users.CreateAsync(TestUsers.Admin, NewUsername(), null, Password, UserRoles.None);
        var access = manager.Services.GetRequiredService<AccessControl>();
        await manager.Services.GetRequiredService<Sharing>().ShareAgentAsync(TestUsers.Admin, admins.AgentId, ketty.Id, AgentAccess.Read);
        await manager.Services.GetRequiredService<RunnerMembers>().SetAsync(TestUsers.Admin, admins.RunnerId, watcher.Id, RunnerAccess.Use);

        var agentChange = await access.AudienceAsync(new ManagerEvent(ManagerEventKind.AgentChanged, admins.RunnerId, admins.AgentId));
        Assert.Contains(User.BootstrapId, agentChange!);
        Assert.Contains(ketty.Id, agentChange!);
        Assert.DoesNotContain(watcher.Id, agentChange!); // Uses the Runner, doesn't manage it: other people's agents aren't theirs to see.
        var sessionChange = await access.AudienceAsync(new ManagerEvent(ManagerEventKind.SessionChanged, SessionId: admins.SessionId));
        Assert.Contains(ketty.Id, sessionChange!);
        Assert.DoesNotContain(watcher.Id, sessionChange!);
        var runnerChange = await access.AudienceAsync(new ManagerEvent(ManagerEventKind.RunnerChanged, admins.RunnerId));
        Assert.Contains(watcher.Id, runnerChange!);
        Assert.DoesNotContain(ketty.Id, runnerChange!);
        Assert.Null(await access.AudienceAsync(new ManagerEvent(ManagerEventKind.AgentRemoved, admins.RunnerId, admins.AgentId)));
    }

    [Fact]
    public void AccessLevels_FollowOwnershipMembershipsAndShares()
    {
        var me = TestUsers.Plain(Guid.NewGuid());
        var other = Guid.NewGuid();
        RunnerMember Member(RunnerAccess level) => new() { UserId = me.Id, Access = level };
        AgentShare Share(AgentAccess level) => new() { UserId = me.Id, Access = level };

        Assert.Equal(RunnerAccess.Owner, AccessControl.ForRunner(me, me.Id, []));
        Assert.Equal(RunnerAccess.Owner, AccessControl.ForRunner(me with { Roles = UserRoles.RunnerManager }, other, []));
        Assert.Equal(RunnerAccess.Manage, AccessControl.ForRunner(me, other, [Member(RunnerAccess.Manage)]));
        Assert.Equal(RunnerAccess.None, AccessControl.ForRunner(me with { Roles = UserRoles.Admin & ~UserRoles.RunnerManager }, other, []));

        Assert.Equal(AgentAccess.Owner, AccessControl.ForAgent(me, me.Id, [], RunnerAccess.Use));
        Assert.Equal(AgentAccess.Write, AccessControl.ForAgent(me, other, [Share(AgentAccess.Write)], RunnerAccess.Use));
        Assert.Equal(AgentAccess.Read, AccessControl.ForAgent(me, other, [Share(AgentAccess.Write)], RunnerAccess.None));
        Assert.Equal(AgentAccess.Listed, AccessControl.ForAgent(me, other, [], RunnerAccess.Manage));
        Assert.Equal(AgentAccess.None, AccessControl.ForAgent(me, other, [], RunnerAccess.Use));
        Assert.Equal(AgentAccess.Read, AccessControl.ForAgent(me, other, [Share(AgentAccess.Read)], RunnerAccess.Owner));

        Assert.Equal(SessionAccess.Owner, AccessControl.ForSession(me, me.Id, [], AgentAccess.None));
        Assert.Equal(SessionAccess.Read, AccessControl.ForSession(me, other, [new SessionShare { UserId = me.Id }], AgentAccess.None));
        Assert.Equal(SessionAccess.Read, AccessControl.ForSession(me, other, [], AgentAccess.Read));
        Assert.Equal(SessionAccess.None, AccessControl.ForSession(me, other, [], AgentAccess.Listed));
    }

    private UserService Users => manager.Services.GetRequiredService<UserService>();

    private List<(RouteEndpoint Endpoint, string Method)> ApiEndpoints()
    {
        _ = manager.Server; // Starts the app, which maps the endpoints.
        return manager.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (e, m)))
            .ToList();
    }

    private async Task<ManagerDbContext> DbAsync() =>
        await manager.Services.GetRequiredService<IDbContextFactory<ManagerDbContext>>().CreateDbContextAsync();

    /// <summary>A stopped Runner, an agent on it with its workspace conversation, an imported conversation and a harness login, all <paramref name="ownerId"/>'s.</summary>
    private async Task<Things> SeedAsync(Guid ownerId)
    {
        var things = new Things(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var name = $"t{Guid.NewGuid():N}"[..12];
        await using var db = await DbAsync();
        db.Runners.Add(new Runner { Id = things.RunnerId, Name = name, OwnerId = ownerId, State = RunnerState.Stopped, Harnesses = "claude" });
        db.Agents.Add(new Agent { Id = things.AgentId, RunnerId = things.RunnerId, OwnerId = ownerId, Name = name, HarnessId = "claude", State = AgentState.Stopped });
        db.Sessions.Add(new StoredSession { Id = things.SessionId, OwnerId = ownerId, Title = name, HarnessId = "claude", AgentId = things.AgentId, WorkspaceAgentId = things.AgentId });
        db.Sessions.Add(new StoredSession { Id = things.ImportedSessionId, OwnerId = ownerId, Title = name + " imported", HarnessId = "pi" });
        db.Accounts.Add(new Account { Id = things.AccountId, OwnerId = ownerId, HarnessId = "claude", Name = name, Kind = AccountKind.ApiKey });
        await db.SaveChangesAsync();
        return things;
    }

    private async Task<AgentDto> AgentAsync(HttpClient client, Guid agentId) =>
        (await client.GetFromJsonAsync<AgentDto>($"/api/agents/{agentId}", ApiJson.Options))!;

    private async Task<HttpClient> SignedInAsNewUserAsync(UserRoles roles = UserRoles.None) => (await SignedInAsNewUserWithIdAsync(roles)).Client;

    /// <summary>A new user, signed in, past choosing their own password, as the Portal (its header and origin).</summary>
    private async Task<(HttpClient Client, Guid Id)> SignedInAsNewUserWithIdAsync(UserRoles roles = UserRoles.None)
    {
        var user = await Users.CreateAsync(TestUsers.Admin, NewUsername(), null, AdminPassword, roles);
        var client = await SignInAsync(user.Username, AdminPassword);
        await ChangePasswordAsync(client, AdminPassword, Password);
        return (client, user.Id);
    }

    private async Task<HttpClient> SignInAsync(string username, string password, string returnUrl = "/")
    {
        var client = Anonymous();
        var response = await client.PostAccountAsync("/account/login", Form(("username", username), ("password", password), ("returnUrl", returnUrl)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("failed=1", response.Headers.Location?.ToString());
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        return client;
    }

    private static Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string current, string next) =>
        client.PostAccountAsync("/account/password", Form(("currentPassword", current), ("newPassword", next), ("confirmPassword", next), ("returnUrl", "/agents")));

    private HttpClient Anonymous() => manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static string NewUsername() => $"u{Guid.NewGuid():N}"[..16];

    private sealed record Things(Guid RunnerId, Guid AgentId, Guid SessionId, Guid ImportedSessionId, Guid AccountId);
}
