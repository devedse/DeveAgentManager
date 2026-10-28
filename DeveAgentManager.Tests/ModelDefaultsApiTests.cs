using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeveAgentManager.Api;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// My settings → Models over the API: each user's own default model and effort, next to the Manager's. Its own Manager,
/// so its sign-ins don't count against the sign-in limit of the other API tests.
/// </summary>
public sealed class ModelDefaultsApiTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    private const string Path = "/api/me/settings/models";
    private const string TemporaryPassword = "test-password-1234";
    private const string Password = "test-only-models-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModelDefaults_AreEachUsersOwn_NextToTheManagersDefault()
    {
        var settings = manager.Services.GetRequiredService<SettingsStore>();
        var shared = await settings.GetSetupProfileAsync(Ct);
        shared.GetHarness("codex").DefaultModel = "gpt-6.1-sol";
        shared.GetHarness("codex").DefaultThinkingLevel = "xhigh";
        shared.Files.Add(new SetupFile { Path = "~/.example/secret", Content = "test-only-secret-value" });
        await settings.SaveSetupProfileAsync(shared, Ct);
        var (client, userId) = await SignedInAsNewUserAsync();

        var response = await client.GetAsync(Path, Ct);
        response.EnsureSuccessStatusCode();
        // Only the default model fields of the shared Setup Profile.
        Assert.DoesNotContain("test-only-secret-value", await response.Content.ReadAsStringAsync(Ct));
        var codex = (await response.Content.ReadFromJsonAsync<List<ModelDefaultsDto>>(ApiJson.Options, Ct))!.Single(m => m.HarnessId == "codex");
        Assert.Equal(("gpt-6.1-sol", "xhigh", (string?)null, (string?)null), (codex.ManagerModel, codex.ManagerEffort, codex.Model, codex.Effort));

        var saved = await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault> { ["codex"] = new() { Effort = "high" } }, ApiJson.Options, Ct);
        saved.EnsureSuccessStatusCode();
        Assert.Equal("high", (await saved.Content.ReadFromJsonAsync<List<ModelDefaultsDto>>(ApiJson.Options, Ct))!.Single(m => m.HarnessId == "codex").Effort);
        // The user's own: saved under their id, nobody else's.
        var store = manager.Services.GetRequiredService<UserSettingsStore>();
        Assert.Equal("high", (await store.GetModelDefaultsAsync(userId, Ct))["codex"].Effort);
        Assert.Empty(await store.GetModelDefaultsAsync(User.BootstrapId, Ct));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault> { ["nothing"] = new() { Model = "x" } }, ApiJson.Options, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault> { ["codex"] = new() { Model = "gpt 6" } }, ApiJson.Options, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync(Path, Ct)).StatusCode);
    }

    [Fact]
    public async Task PiScopeRoundTripsInheritanceAllAndOrderedPatterns()
    {
        var settings = manager.Services.GetRequiredService<SettingsStore>();
        var shared = await settings.GetSetupProfileAsync(Ct);
        shared.GetHarness("pi").ScopedModels = ["claude-code/*:high", "dam-codex/*"];
        await settings.SaveSetupProfileAsync(shared, Ct);
        var (client, userId) = await SignedInAsNewUserAsync();
        async Task<ModelDefaultsDto> Pi() => (await client.GetFromJsonAsync<List<ModelDefaultsDto>>(Path, ApiJson.Options, Ct))!.Single(m => m.HarnessId == "pi");
        Assert.Equal(["claude-code/*:high", "dam-codex/*"], (await Pi()).ManagerScopedModels);
        Assert.Null((await Pi()).ScopedModels);
        var save = await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault> { ["pi"] = new() { ScopedModels = [] } }, ApiJson.Options, Ct);
        save.EnsureSuccessStatusCode();
        Assert.Empty((await Pi()).ScopedModels!);
        var store = manager.Services.GetRequiredService<UserSettingsStore>();
        Assert.Empty((await store.GetModelDefaultsAsync(userId, Ct))["pi"].ScopedModels!);
        save = await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault> { ["pi"] = new() { ScopedModels = ["dam-codex/*", "claude-code/default:high"] } }, ApiJson.Options, Ct);
        save.EnsureSuccessStatusCode();
        Assert.Equal(["dam-codex/*", "claude-code/default:high"], (await Pi()).ScopedModels);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault> { ["pi"] = new() { ScopedModels = ["bad pattern"] } }, ApiJson.Options, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault> { ["codex"] = new() { ScopedModels = [] } }, ApiJson.Options, Ct)).StatusCode);
        save = await client.PutAsJsonAsync(Path, new Dictionary<string, ModelDefault>(), ApiJson.Options, Ct);
        save.EnsureSuccessStatusCode();
        Assert.Null((await Pi()).ScopedModels);
        Assert.Equal(["claude-code/*:high", "dam-codex/*"], (await Pi()).ManagerScopedModels);
    }

    /// <summary>A new user, signed in, past choosing their own password, as the Portal (its header and origin).</summary>
    private async Task<(HttpClient Client, Guid Id)> SignedInAsNewUserAsync()
    {
        var user = await manager.Services.GetRequiredService<UserService>().CreateAsync(TestUsers.Admin, $"u{Guid.NewGuid():N}"[..16], null, TemporaryPassword, UserRoles.None);
        var client = Anonymous();
        var login = await client.PostAccountAsync("/account/login", Form(("username", user.Username), ("password", TemporaryPassword), ("returnUrl", "/")), Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        await client.PostAccountAsync("/account/password", Form(("currentPassword", TemporaryPassword), ("newPassword", Password), ("confirmPassword", Password), ("returnUrl", "/")), Ct);
        return (client, user.Id);
    }

    private HttpClient Anonymous() => manager.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
}
