#pragma warning disable xUnit1051
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class ManagerCpuTraceApiTests
{
    [Fact]
    public async Task StartupCaptureIsAlreadyRunningWhenTheApiBecomesAvailable()
    {
        await using var manager = new CaptureManager(startup: true);
        using var admin = await LoginAsync(manager, UserRoles.SettingsManager);
        var status = (await admin.GetFromJsonAsync<ManagerCpuTraceStatus>("/api/diagnostics/cpu", ApiJson.Options))!;
        Assert.Equal("startup", status.Active!.Trigger);
        Assert.Equal(300, status.Active.DurationSeconds);
        Assert.Equal("profiling", status.Active.Phase);
        Assert.Single(manager.Recorder.Plans);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/diagnostics/cpu", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/diagnostics/cpu/{status.Active.Id}/cancel", null)).StatusCode);
        var service = manager.Services.GetRequiredService<ManagerCpuTraces>();
        await ManagerCpuTraceTests.WaitAsync(() => service.Status().Active is null);
        Assert.Equal("canceled", service.Status().Captures[0].State);
    }

    [Fact]
    public async Task CaptureAndDownloadRequireSettingsRoleAndMutationsNeedPortalHeaderAndOrigin()
    {
        await using var manager = new CaptureManager();
        using var anonymous = manager.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/diagnostics/cpu")).StatusCode);
        using var viewer = await LoginAsync(manager, UserRoles.LogViewer);
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/diagnostics/cpu")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/diagnostics/cpu", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/diagnostics/cpu/{missing}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/diagnostics/cpu/{missing}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/diagnostics/cpu/{missing}")).StatusCode);

        using var admin = await LoginAsync(manager, UserRoles.SettingsManager);
        admin.DefaultRequestHeaders.Remove(ApiHeaders.Request);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/diagnostics/cpu", null)).StatusCode);
        admin.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        admin.DefaultRequestHeaders.Add("Origin", "https://unrelated.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/diagnostics/cpu", null)).StatusCode);
        admin.DefaultRequestHeaders.Remove("Origin");
        var started = await admin.PostAsync("/api/diagnostics/cpu", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var capture = (await started.Content.ReadFromJsonAsync<ManagerCpuTrace>(ApiJson.Options))!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/diagnostics/cpu", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/diagnostics/cpu/{capture.Id}/download")).StatusCode);
        manager.Recorder.Release.Release();
        var service = manager.Services.GetRequiredService<ManagerCpuTraces>();
        await ManagerCpuTraceTests.WaitAsync(() => service.Status().Active is null);
        var response = await admin.GetAsync($"/api/diagnostics/cpu/{capture.Id}/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        using (var zip = new ZipArchive(await response.Content.ReadAsStreamAsync())) Assert.Equal(3, zip.Entries.Count);
        await admin.PostAccountAsync("/account/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync($"/api/diagnostics/cpu/{capture.Id}/download")).StatusCode);
    }

    private sealed class CaptureManager(bool startup = false) : WebApplicationFactory<DeveAgentManager.Api.DtoMapper>
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("dam-cpu-api-").FullName;
        public ControlledCpuRecorder Recorder { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _folder).UseSetting("ADMIN_PASSWORD", "test-password-1234").UseSetting("PROXMOX_URL", "");
            builder.UseSetting("CPU_TRACE_STARTUP", startup ? "true" : "false");
            builder.ConfigureServices(services => { services.RemoveAll<ICpuTraceRecorder>(); services.AddSingleton<ICpuTraceRecorder>(Recorder); });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); Directory.Delete(_folder, true); }
    }

    private static async Task<HttpClient> LoginAsync(CaptureManager manager, UserRoles roles)
    {
        const string password = "test-password-1234", next = "test-cpu-password-5678";
        var user = await manager.Services.GetRequiredService<UserService>().CreateAsync(TestUsers.Admin, "cpu-" + Guid.NewGuid().ToString("N")[..12], null, password, roles);
        var client = manager.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAccountAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["username"] = user.Username, ["password"] = password, ["returnUrl"] = "/" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAccountAsync("/account/password", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["currentPassword"] = password, ["newPassword"] = next, ["confirmPassword"] = next, ["returnUrl"] = "/" }))).StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
        return client;
    }
}
