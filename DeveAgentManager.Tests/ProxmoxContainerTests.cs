using System.Net;
using System.Text;
using System.Text.Json;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Runners;
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
        // Tagged only once it exists: Proxmox checks tags at creation against /vms/<id> alone, where a token has no rights.
        Assert.DoesNotContain("tags", body.Keys);
        var tag = Assert.Single(api.Calls, c => c.Method == HttpMethod.Put && c.Path.EndsWith("/nodes/test-node/lxc/30000/config"));
        Assert.Equal(ProxmoxProvider.Tag, Parameters(tag.Body)["tags"]);
        Assert.True(api.Calls.IndexOf(tag) > api.Calls.FindIndex(c => c.Path.Contains("/tasks/")), "Tagged after the create task finished");
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

    [Fact]
    public void TheHostCommandFindsAChosenCardsRenderNodeOnTheHost() =>
        Assert.Equal("pct set 30000 --dev0 \"$(readlink -e /dev/dri/by-path/pci-0000:0b:00.0-render)\",gid=993",
            Provider(new Api()).GpuAttachCommand(Machine("""{"Node":"test-node","Type":"container"}"""), "0000:0b:00.0", 993));

    [Fact]
    public async Task ListsTheGraphicsCardsOfTheContainersNodeWithACommandEach()
    {
        using var api = new Api { Guests = [.. Api.Templates, Container] };
        var provider = Provider(api);
        var machine = Machine("""{"Node":"test-node","Type":"container"}""");

        var cards = await provider.GetHostGpusAsync(machine, Ct);
        Assert.Equal(["0000:00:02.0", "0000:0b:00.0", "0000:c1:00.0"], cards.Select(c => c.Address));
        Assert.Equal("Intel Corporation Battlemage G21 [Intel Graphics]", cards[1].Name);
        Assert.Equal("Advanced Micro Devices, Inc. [AMD/ATI] 0x7480", cards[2].Name);
        Assert.Contains(api.Calls, c => c.Path.EndsWith("/nodes/test-node/hardware/pci"));

        // What the GPU panel gets: every card with its own command, and the first GPU's command kept as the fallback.
        var gpu = await RunnerGpu.WithHostGpusAsync(new("not-attached", "No GPU is passed into this Runner yet.", AttachCommand: "fallback"), provider, machine, 992, Ct);
        Assert.Equal("fallback", gpu.AttachCommand);
        Assert.Null(gpu.HostGpusProblem);
        Assert.Equal("pct set 30000 --dev0 \"$(readlink -e /dev/dri/by-path/pci-0000:0b:00.0-render)\",gid=992",
            Assert.Single(gpu.HostGpus!, c => c.Address == "0000:0b:00.0").AttachCommand);
    }

    [Fact]
    public async Task SaysWhatTheTokenNeedsWhenItMayNotListTheCards()
    {
        using var api = new Api { Guests = [.. Api.Templates, Container], PciForbidden = true };
        var provider = Provider(api);

        var gpu = await RunnerGpu.WithHostGpusAsync(new("not-attached", "No GPU is passed into this Runner yet.", AttachCommand: "fallback"), provider,
            Machine("""{"Node":"test-node","Type":"container"}"""), 992, Ct);
        Assert.Null(gpu.HostGpus);
        Assert.Equal("fallback", gpu.AttachCommand);
        Assert.Contains("needs Sys.Audit on /", gpu.HostGpusProblem);

        var check = Assert.Single(await provider.CheckAsync(Ct), c => c.Name == "Graphics cards");
        Assert.Equal(CheckStatus.Warning, check.Status);
        Assert.Contains("Sys.Audit on /", check.Message);
    }

    [Theory]
    [InlineData("/dev/dri/renderD128; reboot", 993)]
    [InlineData("0000:0b:00.0; reboot", 993)]
    [InlineData("0000:0b:00.0-render", 993)]
    [InlineData("0b:00.0", 993)]
    [InlineData("0000:0b:00.0", 0)]
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

    [Theory]
    [InlineData("lxc", """{"Node":"test-node","Type":"container"}""")]
    [InlineData("qemu", """{"Node":"test-node"}""")]
    public async Task ARunnerJustStoppedIsNotTakenAsRunningFromTheLaggingClusterList(string type, string data)
    {
        // Right after a shutdown, /cluster/resources still says running for a few seconds. The monitor would take that as
        // started outside the Manager and start the Runner again; the guest's own node knows better.
        using var api = new Api
        {
            Guests = [.. Api.Templates, new { type, vmid = 30000, node = "test-node", name = "test-runner", status = "running", tags = ProxmoxProvider.Tag }],
            CurrentStatus = "stopped",
        };
        var provider = Provider(api);
        var machine = Machine(data);

        Assert.Equal(MachinePowerState.Stopped, (await provider.GetStatusAsync(machine, Ct)).PowerState);
        await provider.StartMachineAsync(machine, NullRunnerLog.Instance, Ct);
        Assert.Contains(api.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith($"/{type}/30000/status/start"));
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
        var cards = Assert.Single(checks, c => c.Name == "Graphics cards");
        Assert.Equal(CheckStatus.Ok, cards.Status);
        Assert.Contains("Battlemage G21 [Intel Graphics] (0000:0b:00.0)", cards.Message);
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

    private static readonly object Container = new { type = "lxc", vmid = 30000, node = "test-node", name = "test-runner", status = "running", tags = ProxmoxProvider.Tag };

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

        /// <summary>The token lacks Sys.Audit on / (listing PCI devices).</summary>
        public bool PciForbidden { get; init; }

        /// <summary>What the guest's node says (status/current); null: the same as the cluster list.</summary>
        public string? CurrentStatus { get; init; }

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
            else if (path.EndsWith("/hardware/pci") && PciForbidden)
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "Permission check failed (/, Sys.Audit|Sys.Modify)" };
            else if (path.EndsWith("/hardware/pci")) data = new object[]
            {
                new { id = "0000:0b:00.0", @class = "0x030000", vendor = "0x8086", device = "0xe212", vendor_name = "Intel Corporation", device_name = "Battlemage G21 [Intel Graphics]", iommugroup = 20 },
                new { id = "0000:00:02.0", @class = "0x030000", vendor = "0x8086", device = "0xa7a0", vendor_name = "Intel Corporation", device_name = "Raptor Lake-P [Iris Xe Graphics]", iommugroup = 2 },
                new { id = "0000:c1:00.0", @class = "0x038000", vendor = "0x1002", device = "0x7480", vendor_name = "Advanced Micro Devices, Inc. [AMD/ATI]", device_name = "", iommugroup = 30 },
                new { id = "0000:03:00.0", @class = "0x020000", vendor = "0x8086", device = "0x125c", vendor_name = "Intel Corporation", device_name = "Ethernet Controller I226-V", iommugroup = 15 },
                new { id = "bad id", @class = "0x030000", vendor = "0x1234", device = "0x1111", vendor_name = "Bochs", device_name = "VGA", iommugroup = 1 },
            };
            else if (path.EndsWith("/status/current"))
            {
                var guest = JsonSerializer.SerializeToElement(Guests).EnumerateArray().First(g => path.Contains($"/{g.GetProperty("vmid").GetInt32()}/"));
                var status = CurrentStatus ?? (guest.TryGetProperty("status", out var listed) ? listed.GetString() : "stopped");
                data = new { status, uptime = status == "running" ? 60 : 0, vmid = guest.GetProperty("vmid").GetInt32() };
            }
            else if (path.EndsWith("/lxc/30000/interfaces")) data = new object[]
            {
                new { name = "lo", inet = "127.0.0.1/8", hwaddr = "00:00:00:00:00:00" },
                new { name = "eth0", inet = "192.0.2.10/24", hwaddr = "BC:24:11:00:00:02" },
            };
            else if (path.Contains("/tasks/")) data = new { status = "stopped", exitstatus = "OK" };
            else if (request.Method == HttpMethod.Post && path.EndsWith("/lxc") && (await request.Content!.ReadAsStringAsync(cancellationToken)).Contains("tags"))
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "Permission check failed (/vms/30000, VM.Config.Options)" };
            else if (request.Method == HttpMethod.Put && path.EndsWith("/config")) data = null;
            else if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Delete) data = task;
            else throw new InvalidOperationException($"Unexpected test request: {request.Method} {path}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
