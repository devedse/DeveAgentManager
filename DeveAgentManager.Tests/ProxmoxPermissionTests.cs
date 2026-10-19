using System.Net;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Providers.Proxmox;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class ProxmoxPermissionTests
{
    [Fact]
    public async Task MissingDesktopHardwarePermissionFailsBeforeCloning()
    {
        using var api = new Api();
        var allocated = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(api).CreateMachineAsync(
            Request("desktop"), _ => { allocated = true; return Task.CompletedTask; }, NullRunnerLog.Instance,
            TestContext.Current.CancellationToken));

        Assert.Contains("VM.Config.HWType", error.Message);
        Assert.Contains("Proxmox role", error.Message);
        Assert.False(allocated);
        Assert.DoesNotContain(api.Calls, call => call.Method != HttpMethod.Get);
        Assert.Equal(["/pool/test-runners", "/vms/30000"], api.PermissionPaths);
    }

    [Theory]
    [InlineData("/pool/test-runners", "test-runners", 1)]
    [InlineData("/vms/30000", "test-runners", 0)]
    [InlineData("/vms/30000", null, 1)]
    public async Task EffectivePoolOrVmPermissionAllowsDesktopCreation(string path, string? pool, int propagate)
    {
        using var api = new Api { GrantedPath = path, Propagate = propagate };
        MachineHandle? allocated = null;
        var handle = await Provider(api, pool).CreateMachineAsync(Request("desktop"), machine =>
        {
            allocated = machine;
            return Task.CompletedTask;
        }, NullRunnerLog.Instance, TestContext.Current.CancellationToken);

        Assert.Equal("30000", handle.MachineId);
        Assert.Equal(handle, allocated);
        var clone = Assert.Single(api.Calls, call => call.Path.EndsWith("/clone"));
        var configure = Assert.Single(api.Calls, call => call.Method == HttpMethod.Put && call.Path.EndsWith("/config"));
        Assert.True(api.Calls.IndexOf(clone) < api.Calls.IndexOf(configure));
        Assert.Contains("std,memory=64", configure.Body);
    }

    [Fact]
    public async Task HeadlessCreationDoesNotRequireDesktopHardwarePermission()
    {
        using var api = new Api();
        await Provider(api).CreateMachineAsync(Request("headless"), _ => Task.CompletedTask,
            NullRunnerLog.Instance, TestContext.Current.CancellationToken);

        Assert.Empty(api.PermissionPaths);
        Assert.Contains(api.Calls, call => call.Path.EndsWith("/clone"));
        Assert.DoesNotContain(api.Calls, call => call.Body.Contains("std,memory=64"));
    }

    [Fact]
    public async Task ConnectionTestReportsMissingDesktopPermissionDespiteOtherGrants()
    {
        using var api = new Api();
        var checks = await Provider(api).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Ok, Assert.Single(checks, check => check.Name == "Token permissions").Status);
        var hardware = Assert.Single(checks, check => check.Name == "Desktop display permission");
        Assert.Equal(CheckStatus.Failed, hardware.Status);
        Assert.Contains("VM.Config.HWType", hardware.Message);
    }

    [Fact]
    public async Task PermissionLookupFailureDoesNotCloneOrClaimMissingPrivilege()
    {
        using var api = new Api { PermissionStatus = HttpStatusCode.ServiceUnavailable };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(api).CreateMachineAsync(
            Request("desktop"), _ => Task.CompletedTask, NullRunnerLog.Instance, TestContext.Current.CancellationToken));

        Assert.Contains("503", error.Message);
        Assert.DoesNotContain("Add it", error.Message);
        Assert.DoesNotContain(api.Calls, call => call.Method != HttpMethod.Get);
    }

    private static ProxmoxProvider Provider(Api api, string? pool = "test-runners") => new(new ProxmoxOptions
    {
        Host = "proxmox.test", TokenId = "test@pve!manager", TokenSecret = "test-only-token", Pool = pool,
        DesktopTemplateVmId = 10120, HeadlessTemplateVmId = 10100
    }, api, NullLogger<ProxmoxProvider>.Instance);

    private static MachineCreateRequest Request(string template) => new("test-runner", template, 4, 8192, 32,
        new RunnerAccount("test-runner", "test-only-password", "ssh-ed25519 test-only-key"), null);

    private sealed record Call(HttpMethod Method, string Path, string Body);

    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public string? GrantedPath { get; init; }
        public int Propagate { get; init; } = 1;
        public HttpStatusCode PermissionStatus { get; init; } = HttpStatusCode.OK;
        public List<Call> Calls { get; } = [];
        public List<string> PermissionPaths { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(new Call(request.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            object? data;
            if (path.EndsWith("/access/permissions"))
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var scope = query.TryGetValue("path", out var value) ? value.ToString() : "";
                PermissionPaths.Add(scope);
                if (PermissionStatus != HttpStatusCode.OK)
                    return new HttpResponseMessage(PermissionStatus) { Content = new StringContent("{\"data\":null}") };
                var privileges = new Dictionary<string, int> { ["VM.Audit"] = 1, ["VM.Config.CPU"] = 1 };
                if (scope == GrantedPath) privileges["VM.Config.HWType"] = Propagate;
                data = new Dictionary<string, object> { [scope == "" ? "/pool/test-runners" : scope] = privileges };
            }
            else if (path.EndsWith("/version")) data = new { version = "9.1", release = "test-only" };
            else if (path.EndsWith("/cluster/resources")) data = new[]
            {
                new { type = "qemu", vmid = 10120, node = "test-node", name = "desktop-template", template = 1 },
                new { type = "qemu", vmid = 10100, node = "test-node", name = "headless-template", template = 1 }
            };
            else if (path.EndsWith("/cluster/nextid")) data = 30000;
            else if (path.EndsWith("/pools")) data = new[] { new { poolid = "test-runners" } };
            else if (path.EndsWith("/clone")) data = "UPID:test-node:00000001:00000001:00000001:qmclone:10120:test@pve:";
            else if (path.Contains("/tasks/")) data = new { status = "stopped", exitstatus = "OK" };
            else if (path.EndsWith("/config") && request.Method == HttpMethod.Get) data = new
            {
                agent = "1", scsi0 = "example-storage:vm-10120-disk-0,size=32G", ide2 = "example-storage:cloudinit,media=cdrom", boot = "order=scsi0"
            };
            else if (path.EndsWith("/config") && request.Method == HttpMethod.Put) data = null;
            else throw new InvalidOperationException($"Unexpected test request: {request.Method} {path}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json")
            };
        }
    }
}
