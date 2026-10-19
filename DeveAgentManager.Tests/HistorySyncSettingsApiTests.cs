using System.Net;
using System.Net.Http.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveAgentManager.Tests;

public class HistorySyncSettingsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Endpoint = "/api/settings/history-sync";

    [Fact]
    public async Task ModeRequiresSettingsRoleAndValidRequestsAndPersistsAcrossManagerRestarts()
    {
        var folder = Directory.CreateTempSubdirectory("dam-history-settings-").FullName;
        try
        {
            await using (var manager = new Manager(folder))
            {
                using var anonymous = manager.CreateClient(new() { AllowAutoRedirect = false });
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Endpoint, Ct)).StatusCode);
                using var viewer = await Login(manager, UserRoles.LogViewer);
                Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Endpoint, Ct)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(Endpoint, new HistorySyncSettings(HistorySyncMode.Full), ApiJson.Options, Ct)).StatusCode);
                using var admin = await Login(manager, UserRoles.SettingsManager);
                Assert.Equal(HistorySyncMode.Sampled, (await admin.GetFromJsonAsync<HistorySyncSettings>(Endpoint, ApiJson.Options, Ct))!.Mode);
                admin.DefaultRequestHeaders.Remove(ApiHeaders.Request);
                Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync(Endpoint, new HistorySyncSettings(HistorySyncMode.Full), ApiJson.Options, Ct)).StatusCode);
                admin.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
                admin.DefaultRequestHeaders.Add("Origin", "https://unrelated.test");
                Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync(Endpoint, new HistorySyncSettings(HistorySyncMode.Full), ApiJson.Options, Ct)).StatusCode);
                admin.DefaultRequestHeaders.Remove("Origin");
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Endpoint, new HistorySyncSettings(HistorySyncMode.Full), ApiJson.Options, Ct)).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Endpoint, new { mode = 999 }, Ct)).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Endpoint, new { mode = "unrecognized" }, Ct)).StatusCode);
                Assert.Equal(HistorySyncMode.Full, (await admin.GetFromJsonAsync<HistorySyncSettings>(Endpoint, ApiJson.Options, Ct))!.Mode);
            }
            await using (var restarted = new Manager(folder))
            {
                using var admin = await Login(restarted, UserRoles.SettingsManager);
                Assert.Equal(HistorySyncMode.Full, (await admin.GetFromJsonAsync<HistorySyncSettings>(Endpoint, ApiJson.Options, Ct))!.Mode);
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Endpoint, new HistorySyncSettings(HistorySyncMode.Shadow), ApiJson.Options, Ct)).StatusCode);
                Assert.Equal(HistorySyncMode.Shadow, (await restarted.Services.GetRequiredService<HistorySyncSettingsStore>().GetAsync(Ct)).Mode);
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Manager(string folder) : WebApplicationFactory<DeveAgentManager.Api.DtoMapper>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("DATA_DIR", folder).UseSetting("ADMIN_PASSWORD", "test-only-history-password-1234")
            .UseSetting("CPU_TRACE_STARTUP", "false").UseSetting("PROXMOX_URL", "");
    }

    private static async Task<HttpClient> Login(Manager manager, UserRoles roles)
    {
        const string password = "test-only-history-password-1234", next = "test-only-history-password-5678";
        var user = await manager.Services.GetRequiredService<UserService>().CreateAsync(TestUsers.Admin, "history-" + Guid.NewGuid().ToString("N")[..12], null, password, roles, Ct);
        var client = manager.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["username"] = user.Username, ["password"] = password, ["returnUrl"] = "/" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAccountAsync("/account/password", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["currentPassword"] = password, ["newPassword"] = next, ["confirmPassword"] = next, ["returnUrl"] = "/" }))).StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        return client;
    }
}
