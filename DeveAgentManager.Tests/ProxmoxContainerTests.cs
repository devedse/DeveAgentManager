using System.Net;
using System.Text;
using System.Text.Json;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Providers.Proxmox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Container Runners against a fake Proxmox API: what is created, how it is found and what it may do.</summary>
public sealed class ProxmoxContainerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatesAnUnprivilegedContainerFromTheNewestDebianTemplateOnTheTemplatesNetwork()
    {
        using var api = new Api();
        MachineHandle? allocated = null;
        var handle = await Provider(api).CreateMachineAsync(Request(), machine => { allocated = machine; return Task.CompletedTask; }, NullRunnerLog.Instance, Ct);

        Assert.Equal("30000", handle.MachineId);
        Assert.Equal(handle, allocated);
        var create = Assert.Single(api.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith("/nodes/test-node/lxc"));
        var body = Parameters(create.Body);
        Assert.Equal("local:vztmpl/debian-13-standard_13.10-1_amd64.tar.zst", body["ostemplate"]);
        Assert.Equal("30000", body["vmid"]);
        Assert.Equal("test-runner", body["hostname"]);
        Assert.Equal("4", body["cores"]);
        Assert.Equal("8192", body["memory"]);
        Assert.Equal("example-storage:32", body["rootfs"]);
        Assert.Equal("name=eth0,bridge=vmbr0,firewall=1,tag=28,ip=dhcp", body["net0"]);
        Assert.Contains(body["unprivileged"], new[] { "1", "True", "true" });
        Assert.Equal("nesting=1", body["features"]);
        Assert.Equal("ssh-ed25519 test-only-key\n", body["ssh-public-keys"]);
        Assert.Equal("test-runners", body["pool"]);
        Assert.Equal(ProxmoxProvider.Tag, body["tags"]);
        // Nothing that only root@pam may set, and the Manager starts it itself after creating it.
        Assert.DoesNotContain(body.Keys, key => key.StartsWith("dev", StringComparison.Ordinal) || key == "password");
        Assert.Contains(body["start"], new[] { "0", "False", "false" });
        Assert.DoesNotContain(api.Calls, c => c.Path.Contains("/qemu/") && c.Method != HttpMethod.Get);
    }

    [Fact]
    public void ContainerRunnersAreBootstrappedAsRootAndCanGetAGpu_VmRunnersNot()
    {
        var provider = Provider(new Api());
        var container = Machine("""{"Node":"test-node","Type":"container"}""");
        var vm = Machine("""{"Node":"test-node"}""");

        Assert.Equal("root", provider.AccountBootstrapUser(container));
        Assert.True(provider.SupportsGpuPassthrough(container));
        Assert.Equal("pct set 30000 --dev0 /dev/dri/renderD129,gid=993", provider.GpuAttachCommand(container, "/dev/dri/renderD129", 993));

        foreach (var machine in (RunnerMachine[])[vm, Machine(null), Machine("not json")])
        {
            Assert.Null(provider.AccountBootstrapUser(machine));
            Assert.False(provider.SupportsGpuPassthrough(machine));
        }
    }

    [Theory]
    [InlineData("/dev/dri/renderD128; reboot", 993)]
    [InlineData("/dev/dri/card0", 993)]
    [InlineData("/dev/dri/by-path/pci-0000:01:00.0-render", 993)]
    [InlineData("/dev/dri/renderD128", 0)]
    [InlineData("/dev/dri/renderD128", 70000)]
    public void TheHostCommandIsOnlyBuiltFromARenderNodeAndAGroupId(string device, int group)
    {
        // Someone runs this as root on the hypervisor: nothing from inside the container may end up in it unchecked.
        Assert.Throws<ArgumentException>(() => Provider(new Api()).GpuAttachCommand(Machine("""{"Node":"test-node","Type":"container"}"""), device, group));
    }

    [Fact]
    public async Task StartsStopsAndFindsTheAddressOfAContainerThroughTheContainerApi()
    {
        using var api = new Api { Guests = [.. Api.Templates, new { type = "lxc", vmid = 30000, node = "test-node", name = "test-runner", status = "stopped", tags = ProxmoxProvider.Tag }] };
        var provider = Provider(api);
        var machine = Machine("""{"Node":"test-node","Type":"container"}""");

        await provider.StartMachineAsync(machine, NullRunnerLog.Instance, Ct);
        Assert.Equal("192.0.2.10", await provider.WaitForAddressAsync(machine, NullRunnerLog.Instance, Ct));
        await provider.StopMachineAsync(machine, NullRunnerLog.Instance, Ct);

        Assert.Contains(api.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith("/lxc/30000/status/start"));
        Assert.Contains(api.Calls, c => c.Method == HttpMethod.Get && c.Path.EndsWith("/lxc/30000/interfaces"));
        Assert.Contains(api.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith("/lxc/30000/status/shutdown"));
        Assert.DoesNotContain(api.Calls, c => c.Path.Contains("/qemu/30000"));
    }

    [Fact]
    public async Task DeletesItsOwnContainerWithItsDisks()
    {
        using var api = new Api { Guests = [.. Api.Templates, new { type = "lxc", vmid = 30000, node = "test-node", name = "test-runner", status = "running", tags = ProxmoxProvider.Tag }] };
        await Provider(api).DestroyMachineAsync(Machine("""{"Node":"test-node","Type":"container"}"""), NullRunnerLog.Instance, Ct);

        Assert.Contains(api.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith("/lxc/30000/status/stop"));
        var delete = Assert.Single(api.Calls, c => c.Method == HttpMethod.Delete);
        Assert.EndsWith("/lxc/30000", delete.Path);
        Assert.Contains("purge", delete.Query + delete.Body);
    }

    [Fact]
    public async Task NeverActsOnAGuestOfTheOtherKindThatReusedTheVmid()
    {
        // The Runner was a VM; VMID 30000 is now someone's container (and the other way around).
        using var api = new Api { Guests = [.. Api.Templates, new { type = "lxc", vmid = 30000, node = "test-node", name = "other", status = "stopped", tags = ProxmoxProvider.Tag }] };
        var provider = Provider(api);
        var vm = Machine("""{"Node":"test-node"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartMachineAsync(vm, NullRunnerLog.Instance, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DestroyMachineAsync(vm, NullRunnerLog.Instance, Ct));
        Assert.Equal(MachinePowerState.Missing, (await provider.GetStatusAsync(vm, Ct)).PowerState);
        Assert.DoesNotContain(api.Calls, c => c.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ExplainsHowToGetATemplateWhenTheStorageHasNone()
    {
        using var api = new Api { TemplateVolumes = ["local:vztmpl/ubuntu-24.04-standard_24.04-2_amd64.tar.zst"] };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(api).CreateMachineAsync(Request(), _ => Task.CompletedTask, NullRunnerLog.Instance, Ct));

        Assert.Contains("CT Templates", error.Message);
        Assert.DoesNotContain(api.Calls, c => c.Method != HttpMethod.Get);

        var configured = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(api, "local:vztmpl/debian-13-standard_13.6-1_amd64.tar.zst")
            .CreateMachineAsync(Request(), _ => Task.CompletedTask, NullRunnerLog.Instance, Ct));
        Assert.Contains("was not found", configured.Message);
    }

    [Fact]
    public async Task ConnectionTestChecksTheContainerTemplateNetworkAndStorage()
    {
        using var api = new Api();
        var checks = await Provider(api).CheckAsync(Ct);

        var container = Assert.Single(checks, c => c.Name == "Container template");
        Assert.Equal(CheckStatus.Ok, container.Status);
        Assert.Contains("debian-13-standard_13.10-1", container.Message);
        Assert.Contains("vmbr0", container.Message);
        Assert.Contains("example-storage", container.Message);
        // The container template is not mistaken for a template VM.
        Assert.DoesNotContain(checks, c => c.Name.StartsWith("Container template (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatesContainersOnTheContainerNodeWhereTheGpuIs()
    {
        // The template VMs are on test-node; the GPU is in gpu-node.
        using var api = new Api();
        var handle = await Provider(api, containerNode: "gpu-node").CreateMachineAsync(Request(), _ => Task.CompletedTask, NullRunnerLog.Instance, Ct);

        Assert.Single(api.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith("/nodes/gpu-node/lxc"));
        Assert.Contains(api.Calls, c => c.Path.EndsWith("/nodes/gpu-node/storage/local/content"));
        Assert.DoesNotContain(api.Calls, c => c.Method == HttpMethod.Post && c.Path.Contains("/nodes/test-node/"));
        Assert.Contains("\"gpu-node\"", handle.ProviderData);
    }

    [Fact]
    public async Task ConnectionTestSaysSoWhenTheStorageTakesNoContainers()
    {
        using var api = new Api { ContainerStorages = ["local"] };
        var checks = await Provider(api, containerNode: "gpu-node").CheckAsync(Ct);

        var container = Assert.Single(checks, c => c.Name == "Container template");
        Assert.Equal(CheckStatus.Failed, container.Status);
        Assert.Contains("'example-storage' takes no containers on gpu-node", container.Message);
        Assert.Contains(api.Calls, c => c.Path.EndsWith("/nodes/gpu-node/storage") && c.Query.Contains("content=rootdir"));
    }

    [Fact]
    public void PicksTheNewestDebianStandardTemplate()
    {
        Assert.Equal("local:vztmpl/debian-13-standard_13.10-1_amd64.tar.zst", ProxmoxProvider.PickNewestDebianTemplate(
        [
            "local:vztmpl/debian-12-standard_12.12-1_amd64.tar.zst",
            "local:vztmpl/debian-13-standard_13.6-1_amd64.tar.zst",
            "local:vztmpl/debian-13-standard_13.10-1_amd64.tar.zst",
            "local:vztmpl/debian-13-standard_13.6-2_amd64.tar.zst",
            "local:vztmpl/ubuntu-26.04-standard_26.04-1_amd64.tar.zst",
            "local:iso/debian-13-standard_14.0-1_amd64.tar.zst",
            null,
        ]));
        Assert.Null(ProxmoxProvider.PickNewestDebianTemplate(["local:vztmpl/alpine-3.22-default_20250617_amd64.tar.xz"]));
    }

    [Theory]
    [InlineData("virtio=BC:24:11:00:00:01,bridge=vmbr0,firewall=1,tag=28", null, "name=eth0,bridge=vmbr0,firewall=1,tag=28,ip=dhcp")]
    [InlineData("virtio=BC:24:11:00:00:01,bridge=vmbr0,mtu=1,rate=50", "vmbr1", "name=eth0,bridge=vmbr1,rate=50,ip=dhcp")]
    [InlineData(null, "vmbr2", "name=eth0,bridge=vmbr2,ip=dhcp")]
    [InlineData(null, null, null)]
    public void ContainerNetworkFollowsTheTemplateVm(string? vmNetwork, string? bridge, string? expected) =>
        Assert.Equal(expected, ProxmoxProvider.BuildContainerNetwork(vmNetwork, bridge));

    [Fact]
    public void ContainerAddressSkipsLoopbackAndBridges() =>
        Assert.Equal("192.0.2.10", ProxmoxProvider.SelectContainerAddress(
        [
            new VmLxcInterface { Name = "lo", Inet = "127.0.0.1/8" },
            new VmLxcInterface { Name = "docker0", Inet = "172.17.0.1/16" },
            new VmLxcInterface { Name = "eth0", Inet = "192.0.2.10/24" },
        ]));

    private static ProxmoxProvider Provider(Api api, string container = "local", string? containerNode = null) => new(new ProxmoxOptions
    {
        Host = "proxmox.test", TokenId = "test@pve!manager", TokenSecret = "test-only-token", Pool = "test-runners",
        HeadlessTemplateVmId = 10100, ContainerTemplate = container, ContainerNode = containerNode,
    }, api, NullLogger<ProxmoxProvider>.Instance);

    private static MachineCreateRequest Request() => new("test-runner", ProxmoxProvider.ContainerTemplateId, 4, 8192, 32,
        new RunnerAccount("test-runner", "test-only-password", "ssh-ed25519 test-only-key"), null);

    private static RunnerMachine Machine(string? data) => new(Guid.NewGuid(), "test-runner", "30000", data, null, 22);

    /// <summary>The parameters of a request, whether the client sent them as JSON or as a form.</summary>
    private static Dictionary<string, string> Parameters(string body)
    {
        if (body.TrimStart().StartsWith('{'))
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!
                .ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString());
        }

        return body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
    }

    private sealed record Call(HttpMethod Method, string Path, string Query, string Body);

    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public static readonly object[] Templates = [new { type = "qemu", vmid = 10100, node = "test-node", name = "headless-template", template = 1 }];

        public object[] Guests { get; init; } = Templates;

        public string[] TemplateVolumes { get; init; } =
        [
            "local:vztmpl/debian-12-standard_12.12-1_amd64.tar.zst",
            "local:vztmpl/debian-13-standard_13.6-1_amd64.tar.zst",
            "local:vztmpl/debian-13-standard_13.10-1_amd64.tar.zst",
        ];

        /// <summary>The storages whose content includes containers.</summary>
        public string[] ContainerStorages { get; init; } = ["local", "example-storage"];

        public List<Call> Calls { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(new Call(request.Method, path, request.RequestUri.Query, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            const string task = "UPID:test-node:00000001:00000001:00000001:vzcreate:30000:test@pve:";
            object? data;
            if (path.EndsWith("/access/permissions")) data = new Dictionary<string, object> { ["/pool/test-runners"] = new Dictionary<string, int> { ["VM.Allocate"] = 1 } };
            else if (path.EndsWith("/version")) data = new { version = "9.2", release = "test-only" };
            else if (path.EndsWith("/cluster/resources")) data = Guests;
            else if (path.EndsWith("/cluster/nextid")) data = 30000;
            else if (path.EndsWith("/pools")) data = new[] { new { poolid = "test-runners" } };
            else if (path.EndsWith("/storage") && path.Contains("/nodes/")) data = ContainerStorages.Select(s => new { storage = s, content = "rootdir,images", enabled = 1, active = 1, type = "rbd" }).ToArray();
            else if (path.EndsWith("/storage/local/content")) data = TemplateVolumes.Select(v => new { volid = v, content = "vztmpl", format = "tzst", size = 1 }).ToArray();
            else if (path.EndsWith("/qemu/10100/config")) data = new
            {
                agent = "1", net0 = "virtio=BC:24:11:00:00:01,bridge=vmbr0,firewall=1,tag=28",
                scsi0 = "example-storage:base-10100-disk-0,size=32G", ide2 = "example-storage:cloudinit,media=cdrom", boot = "order=scsi0",
            };
            else if (path.EndsWith("/lxc/30000/interfaces")) data = new object[]
            {
                new { name = "lo", inet = "127.0.0.1/8", hwaddr = "00:00:00:00:00:00" },
                new { name = "eth0", inet = "192.0.2.10/24", hwaddr = "BC:24:11:00:00:02" },
            };
            else if (path.Contains("/tasks/")) data = new { status = "stopped", exitstatus = "OK" };
            else if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Delete) data = task;
            else throw new InvalidOperationException($"Unexpected test request: {request.Method} {path}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
