#pragma warning disable xUnit1051
using System.Net;
using System.Net.Http.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DeveAgentManager.Tests;
public sealed class ManagerLogAccessTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    [Fact]
    public async Task ManagerLogsRequireTheDedicatedRole()
    {
        var plain = await SignedInAsNewUserAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/logs")).StatusCode);
        var viewer = await SignedInAsNewUserAsync(UserRoles.LogViewer);
        manager.Services.GetRequiredService<DeveAgentManager.Logging.ManagerLogStore>()
            .Append(Microsoft.Extensions.Logging.LogLevel.Warning, "deployment-test", "test warning");
        var response = await viewer.GetAsync("/api/logs?minimum=Warning&search=deployment-test");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var entries = await response.Content.ReadFromJsonAsync<List<ManagerLogEntry>>(ApiJson.Options);
        Assert.Contains(entries!, e => e.Message == "test warning");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
    }

    private async Task<HttpClient> SignedInAsNewUserAsync(UserRoles roles = UserRoles.None)
    {
        const string first = "test-password-1234", next = "test-logs-password-1";
        var users = manager.Services.GetRequiredService<UserService>();
        var user = await users.CreateAsync(TestUsers.Admin, "logs-" + Guid.NewGuid().ToString("N")[..10], null, first, roles);
        var client = manager.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string> { ["username"] = user.Username, ["password"] = first, ["returnUrl"] = "/" }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var changed = await client.PostAccountAsync("/account/password", new FormUrlEncodedContent(new Dictionary<string,string> { ["currentPassword"] = first, ["newPassword"] = next, ["confirmPassword"] = next, ["returnUrl"] = "/" }));
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        return client;
    }
}
