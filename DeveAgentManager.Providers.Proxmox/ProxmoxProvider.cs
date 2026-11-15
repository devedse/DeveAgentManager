using System.Text.Json;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using DeveAgentManager.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Providers.Proxmox;

/// <summary>
/// Creates Runners as full clones of a Proxmox template VM, with the Runner Account injected by cloud-init, or as
/// unprivileged Debian containers (LXC), where the Manager creates the Runner Account over SSH as root
/// (<see cref="AccountBootstrapUser"/>). Uses the Corsinvest Proxmox VE client.
/// </summary>
public sealed partial class ProxmoxProvider(ProxmoxOptions options, IHttpClientFactory httpClientFactory, ILogger<ProxmoxProvider> logger) : IRunnerProvider
{
    public const string ProviderId = "proxmox";
    public const string HttpClientName = "proxmox";
    public const string Tag = "deveagentmanager";

    /// <summary>The template id of container Runners, and the machine type stored in their provider data.</summary>
    public const string ContainerTemplateId = "container";

    private static readonly SemaphoreSlim AllocationLock = new(1, 1);

    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromMinutes(5);

    public string Id => ProviderId;

    public string DisplayName => "Proxmox VE";

    public bool IsConfigured => options.IsConfigured;

    public RunnerProviderCapabilities Capabilities { get; } = new(
        CanStartStop: true,
        CanDestroy: true,
        SupportsTemplates: true,
        SupportsSizing: true,
        RequiresConnectionDetails: false);

    public IReadOnlyList<RunnerTemplate> GetTemplates()
    {
        var templates = new List<RunnerTemplate>();
        if (options.HeadlessTemplateVmId is { } headless)
        {
            templates.Add(new RunnerTemplate("headless", "Headless", RunnerKind.Headless, $"Template VM {headless}, no desktop"));
        }

        if (options.DesktopTemplateVmId is { } desktop)
        {
            templates.Add(new RunnerTemplate("desktop", "Desktop", RunnerKind.Desktop, $"Template VM {desktop}, with a desktop"));
        }

        if (options.ContainerTemplate is { } container)
        {
            var source = container.Contains(':') ? container[(container.LastIndexOf('/') + 1)..] : $"the newest Debian template in {container}";
            templates.Add(new RunnerTemplate(ContainerTemplateId, "Container", RunnerKind.Headless, $"Debian container from {source}, no desktop; can use a GPU of the host"));
        }

        return templates;
    }

    public IReadOnlyList<ProviderSetting> GetSettings() =>
    [
        new("PROXMOX_URL", options.Url),
        new("PROXMOX_TOKEN_ID", options.TokenId),
        new("PROXMOX_TOKEN_SECRET", string.IsNullOrEmpty(options.TokenSecret) ? null : "••••••••", IsSecret: true),
        new("PROXMOX_VERIFY_TLS", options.VerifyTls ? "true" : "false"),
        new("PROXMOX_NODE", options.Node ?? "(the template's node)"),
        new("PROXMOX_POOL", options.Pool),
        new("PROXMOX_STORAGE", options.Storage ?? "(the template's storage)"),
        new("PROXMOX_BRIDGE", options.Bridge ?? "(the template's bridge)"),
        new("PROXMOX_VMID_RANGE", $"{options.VmIdStart}-{options.VmIdEnd}"),
        new("PROXMOX_FULL_CLONE", options.FullClone ? "true" : "false"),
        new("TEMPLATE_HEADLESS_VMID", options.HeadlessTemplateVmId?.ToString()),
        new("TEMPLATE_DESKTOP_VMID", options.DesktopTemplateVmId?.ToString()),
        new("TEMPLATE_CONTAINER", options.ContainerTemplate),
        new("PROXMOX_CONTAINER_NODE", options.ContainerNode ?? (options.ContainerTemplate is null ? null : options.Node ?? "(the template's node)")),
    ];

    public async Task<IReadOnlyList<ProviderCheck>> CheckAsync(CancellationToken cancellationToken)
    {
        var checks = new List<ProviderCheck>();
        if (!options.IsConfigured)
        {
            checks.Add(new ProviderCheck("Configuration", CheckStatus.Failed, "Set PROXMOX_URL, PROXMOX_TOKEN_ID, PROXMOX_TOKEN_SECRET and TEMPLATE_HEADLESS_VMID (or TEMPLATE_CONTAINER) in the compose file."));
            return checks;
        }

        var client = CreateClient();
        try
        {
            var version = await client.Version.GetAsync();
            checks.Add(new ProviderCheck("API connection", CheckStatus.Ok, $"Connected to Proxmox VE {version.Version} at {options.Host}:{options.Port}."));
        }
        catch (Exception ex)
        {
            checks.Add(new ProviderCheck("API connection", CheckStatus.Failed, $"{ex.Message} Check the URL, the token and PROXMOX_VERIFY_TLS."));
            return checks;
        }

        List<ClusterResource> vms;
        try
        {
            vms = await GetGuestsAsync(client);
        }
        catch (Exception ex)
        {
            checks.Add(new ProviderCheck("List VMs", CheckStatus.Failed, ex.Message));
            return checks;
        }

        checks.Add(await CheckPermissionsAsync(client));

        foreach (var template in GetTemplates().Where(t => t.Id != ContainerTemplateId))
        {
            checks.Add(await CheckTemplateAsync(client, vms, template));
        }

        if (options.ContainerTemplate is not null)
        {
            checks.Add(await CheckContainerTemplateAsync(client, vms));
        }

        if (options.Pool is null)
        {
            checks.Add(new ProviderCheck("Pool", CheckStatus.Warning, "PROXMOX_POOL is not set. Runners are created outside a pool, which needs broader permissions."));
        }
        else
        {
            try
            {
                var pools = await client.Pools.GetAsync();
                checks.Add(pools.Any(p => p.Id == options.Pool)
                    ? new ProviderCheck("Pool", CheckStatus.Ok, $"Pool '{options.Pool}' exists.")
                    : new ProviderCheck("Pool", CheckStatus.Failed, $"Pool '{options.Pool}' was not found or the token can't see it (needs Pool.Audit)."));
            }
            catch (Exception ex)
            {
                checks.Add(new ProviderCheck("Pool", CheckStatus.Failed, ex.Message));
            }
        }

        if (options.Storage is not null)
        {
            var node = options.Node ?? vms.FirstOrDefault(v => v.VmId == (options.HeadlessTemplateVmId ?? options.DesktopTemplateVmId))?.Node;
            try
            {
                var storage = await client.Nodes[node].Storage[options.Storage].Status.GetAsync();
                checks.Add(new ProviderCheck("Storage", CheckStatus.Ok, $"Storage '{options.Storage}' on {node}: {storage.Available / 1024 / 1024 / 1024} GB free."));
            }
            catch (Exception ex)
            {
                checks.Add(new ProviderCheck("Storage", CheckStatus.Failed, $"Storage '{options.Storage}' on {node}: {ex.Message}"));
            }
        }

        int? freeVmId = null;
        try
        {
            var free = await FindFreeVmIdAsync(client, vms, reserve: false);
            freeVmId = free;
            checks.Add(new ProviderCheck("VMID range", CheckStatus.Ok, $"Next free VMID in {options.VmIdStart}-{options.VmIdEnd}: {free}."));
        }
        catch (Exception ex)
        {
            checks.Add(new ProviderCheck("VMID range", CheckStatus.Failed, ex.Message));
        }

        if (options.DesktopTemplateVmId is not null && freeVmId is { } desktopVmId)
        {
            const string name = "Desktop display permission";
            try
            {
                checks.Add(await HasDesktopHardwarePermissionAsync(client, desktopVmId)
                    ? new ProviderCheck(name, CheckStatus.Ok, "VM.Config.HWType is granted for new desktop Runners.")
                    : new ProviderCheck(name, CheckStatus.Failed, DesktopHardwarePermissionHint));
            }
            catch (Exception ex)
            {
                checks.Add(new ProviderCheck(name, CheckStatus.Warning, ex.Message));
            }
        }

        return checks;
    }

    public async Task<MachineHandle> CreateMachineAsync(MachineCreateRequest request, Func<MachineHandle, Task> onAllocated, IRunnerLog log, CancellationToken cancellationToken)
    {
        if (request.TemplateId == ContainerTemplateId)
        {
            return await CreateContainerAsync(request, onAllocated, log, cancellationToken);
        }

        var templateVmId = request.TemplateId switch
        {
            "desktop" => options.DesktopTemplateVmId,
            _ => options.HeadlessTemplateVmId ?? options.DesktopTemplateVmId,
        } ?? throw new InvalidOperationException("No Runner Template is configured.");

        var client = CreateClient();
        var vms = await GetGuestsAsync(client);
        var template = vms.FirstOrDefault(v => v.VmId == templateVmId && v.VmType == VmType.Qemu)
            ?? throw new InvalidOperationException(vms.Count == 0
                ? $"The API token can't see any VM, so template {templateVmId} can't be cloned. {PrivilegeSeparationHint}"
                : $"Template VM {templateVmId} was not found, or the token has no permissions on /vms/{templateVmId}.");
        var targetNode = options.Node ?? template.Node;

        int vmId;
        // Held until the clone task exists, so two Runners created at once never pick the same VMID.
        var locked = true;
        await AllocationLock.WaitAsync(cancellationToken);
        try
        {
            vmId = await FindFreeVmIdAsync(client, vms, reserve: true);
            if (request.TemplateId == "desktop" && !await HasDesktopHardwarePermissionAsync(client, vmId))
            {
                throw new InvalidOperationException(DesktopHardwarePermissionHint);
            }
            log.Info($"{(options.FullClone ? "Full" : "Linked")} clone of template {templateVmId} ({template.Name}) on {template.Node} to VM {vmId} on {targetNode}");
            var clone = await client.Nodes[template.Node].Qemu[templateVmId].Clone.CloneVm(
                newid: vmId,
                name: request.Name,
                full: options.FullClone,
                pool: options.Pool,
                storage: options.FullClone ? options.Storage : null,
                target: targetNode != template.Node ? targetNode : null,
                description: "Runner managed by DeveAgentManager");
            var upid = EnsureTask(clone, $"Cloning template {templateVmId}");

            // From here the VM exists (locked while cloning), so remember it for cleanup even if the clone fails.
            var handle = new MachineHandle(vmId.ToString(), JsonSerializer.Serialize(new ProxmoxMachineData(targetNode)));
            await onAllocated(handle);
            AllocationLock.Release();
            locked = false;

            await WaitForTaskAsync(client, upid, "Clone", CloneTimeout, log, cancellationToken);
            log.Success($"VM {vmId} cloned");
            await ConfigureAsync(client, targetNode, vmId, request, log);
            return handle;
        }
        finally
        {
            if (locked)
            {
                AllocationLock.Release();
            }
        }
    }

    public async Task StartMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var guest = await LocateAsync(client, machine);
        var (node, vmId) = (guest.Node, guest.VmId);
        var running = guest.IsContainer ? guest.IsRunning : (await client.Nodes[node].Qemu[vmId].Status.Current.GetAsync()).IsRunning;
        if (running)
        {
            log.Info($"{guest.Noun} {vmId} is already running");
            return;
        }

        log.Info($"Starting {guest.Noun} {vmId} on {node}");
        var start = guest.IsContainer
            ? await client.Nodes[node].Lxc[vmId].Status.Start.VmStart()
            : await client.Nodes[node].Qemu[vmId].Status.Start.VmStart();
        var upid = EnsureTask(start, $"Starting {guest.Noun} {vmId}");
        await WaitForTaskAsync(client, upid, "Start", TaskTimeout, log, cancellationToken);
    }

    public async Task StopMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var guest = await LocateAsync(client, machine);
        var (node, vmId) = (guest.Node, guest.VmId);
        log.Info($"Shutting down {guest.Noun} {vmId} (forced stop after 2 minutes)");
        var shutdown = guest.IsContainer
            ? await client.Nodes[node].Lxc[vmId].Status.Shutdown.VmShutdown(forceStop: true, timeout: 120)
            : await client.Nodes[node].Qemu[vmId].Status.Shutdown.VmShutdown(forceStop: true, timeout: 120);
        var upid = EnsureTask(shutdown, $"Shutting down {guest.Noun} {vmId}");
        await WaitForTaskAsync(client, upid, "Shutdown", TaskTimeout, log, cancellationToken);
    }

    public async Task DestroyMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var vmId = ParseVmId(machine);
        var vm = (await GetGuestsAsync(client)).FirstOrDefault(v => v.VmId == vmId);
        if (vm is null)
        {
            log.Warning($"VM {vmId} does not exist (anymore); nothing to delete on Proxmox.");
            return;
        }

        EnsureOwned(vm);
        var guest = new Guest(vm);
        if (guest.IsContainer != IsContainer(machine))
        {
            throw new InvalidOperationException($"Refusing to delete {guest.Noun} {vmId}: this Runner was a {(guest.IsContainer ? "VM" : "container")}.");
        }

        if (vm.IsRunning)
        {
            log.Info($"Stopping {guest.Noun} {vmId}");
            var stopped = guest.IsContainer
                ? await client.Nodes[vm.Node].Lxc[vmId].Status.Stop.VmStop()
                : await client.Nodes[vm.Node].Qemu[vmId].Status.Stop.VmStop();
            var stop = EnsureTask(stopped, $"Stopping {guest.Noun} {vmId}");
            await WaitForTaskAsync(client, stop, "Stop", TaskTimeout, log, cancellationToken);
        }

        log.Info($"Deleting {guest.Noun} {vmId} and its disks");
        var destroyed = guest.IsContainer
            ? await client.Nodes[vm.Node].Lxc[vmId].DestroyVm(destroy_unreferenced_disks: true, purge: true)
            : await client.Nodes[vm.Node].Qemu[vmId].DestroyVm(destroy_unreferenced_disks: true, purge: true);
        var destroy = EnsureTask(destroyed, $"Deleting {guest.Noun} {vmId}");
        await WaitForTaskAsync(client, destroy, "Delete", TaskTimeout, log, cancellationToken);
        log.Success($"{guest.Noun} {vmId} deleted");
    }

    public async Task<MachineStatus> GetStatusAsync(RunnerMachine machine, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var vmId = ParseVmId(machine);
        var vm = (await GetGuestsAsync(client)).FirstOrDefault(v => v.VmId == vmId);
        if (vm is null || new Guest(vm).IsContainer != IsContainer(machine))
        {
            return new MachineStatus(MachinePowerState.Missing);
        }

        if (!vm.IsRunning)
        {
            return new MachineStatus(MachinePowerState.Stopped, Detail: vm.Status);
        }

        string? address = null;
        try
        {
            address = await ReadAddressAsync(client, new Guest(vm));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Network interfaces of {Type} {VmId} could not be read", vm.Type, vmId);
        }

        // CPU and memory are read inside the Runner (GuestUsage): Proxmox sees the VM from the host.
        return new MachineStatus(MachinePowerState.Running, address, Uptime: TimeSpan.FromSeconds(vm.Uptime), Detail: vm.Node);
    }

    public async Task<string> WaitForAddressAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var guest = await LocateAsync(client, machine);
        var vmId = guest.VmId;
        log.Info(guest.IsContainer
            ? $"Waiting for container {vmId} to get an IP address"
            : $"Waiting for the QEMU guest agent of VM {vmId} to report an IP address");
        var started = DateTime.UtcNow;
        var lastHint = started;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var address = await ReadAddressAsync(client, guest);
                if (address is not null)
                {
                    log.Success($"{guest.Noun} {vmId} has IP {address}");
                    return address;
                }
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - lastHint > TimeSpan.FromSeconds(30))
                {
                    log.Info($"Still waiting for {(guest.IsContainer ? "the container's network" : "the guest agent")} ({ex.Message.Trim()})");
                    lastHint = DateTime.UtcNow;
                }
            }

            if (DateTime.UtcNow - started > AddressTimeout)
            {
                throw new TimeoutException(guest.IsContainer
                    ? $"Container {vmId} did not get an IP address within {AddressTimeout.TotalMinutes:0} minutes. Is DHCP available on its network?"
                    : $"VM {vmId} did not report an IP address within {AddressTimeout.TotalMinutes:0} minutes. Is qemu-guest-agent installed in the template and is DHCP available?");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private static async Task<string?> ReadAddressAsync(PveClient client, Guest guest) => guest.IsContainer
        ? SelectContainerAddress(await client.Nodes[guest.Node].Lxc[guest.VmId].Interfaces.GetAsync())
        : SelectAddress(await client.Nodes[guest.Node].Qemu[guest.VmId].Agent.NetworkGetInterfaces.GetAsync());

    public string? AccountBootstrapUser(RunnerMachine machine) => IsContainer(machine) ? "root" : null;

    public bool SupportsGpuPassthrough(RunnerMachine machine) => IsContainer(machine);

    public string GpuAttachCommand(RunnerMachine machine, string renderDevice, int renderGroupId)
    {
        // Shown to someone who runs it as root on the host: only ever a plain render node and a numeric group.
        if (!RenderDevice().IsMatch(renderDevice) || renderGroupId is < 1 or > 65535)
        {
            throw new ArgumentException("Not a render node and group this command can be built from.");
        }

        return $"pct set {ParseVmId(machine)} --dev0 {renderDevice},gid={renderGroupId}";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^/dev/dri/renderD[0-9]{3}$")]
    private static partial System.Text.RegularExpressions.Regex RenderDevice();

    private static bool IsContainer(RunnerMachine machine)
    {
        try
        {
            return machine.ProviderData is { } data && JsonSerializer.Deserialize<ProxmoxMachineData>(data)?.Type == ContainerTemplateId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Picks the address the Manager should SSH to: the first global IPv4 address on a physical-looking interface.</summary>
    internal static string? SelectAddress(VmQemuAgentNetworkGetInterfaces? interfaces)
    {
        if (interfaces?.Result is null)
        {
            return null;
        }

        string[] ignoredPrefixes = ["lo", "docker", "br-", "veth", "virbr", "cni", "flannel", "cali", "tailscale", "zt", "wg"];
        return interfaces.Result
            .Where(i => i.Name is not null && !ignoredPrefixes.Any(p => i.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(i => i.IpAddresses ?? [])
            .Where(ip => string.Equals(ip.IpAddressType, "ipv4", StringComparison.OrdinalIgnoreCase)
                         && ip.IpAddress is not null
                         && !ip.IpAddress.StartsWith("127.", StringComparison.Ordinal)
                         && !ip.IpAddress.StartsWith("169.254.", StringComparison.Ordinal))
            .Select(ip => ip.IpAddress)
            .FirstOrDefault();
    }

    /// <summary>The same choice for a container, from the interfaces Proxmox reads inside it (no guest agent needed).</summary>
    internal static string? SelectContainerAddress(IEnumerable<VmLxcInterface>? interfaces)
    {
        string[] ignoredPrefixes = ["lo", "docker", "br-", "veth", "virbr", "cni", "flannel", "cali", "tailscale", "zt", "wg"];
        return (interfaces ?? [])
            .Where(i => i.Name is not null && !ignoredPrefixes.Any(p => i.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(i => i.Inet?.Split('/')[0])
            .FirstOrDefault(ip => ip is { Length: > 0 } && !ip.StartsWith("127.", StringComparison.Ordinal) && !ip.StartsWith("169.254.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A container's first NIC on the template VM's network: its bridge (or <paramref name="bridge"/>), VLAN tag, firewall
    /// and rate limit, with DHCP. Null when there is no bridge to use.
    /// </summary>
    internal static string? BuildContainerNetwork(string? vmNetwork, string? bridge)
    {
        var settings = (vmNetwork ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        bridge ??= settings.GetValueOrDefault("bridge");
        if (string.IsNullOrEmpty(bridge))
        {
            return null;
        }

        var parts = new List<string> { "name=eth0", $"bridge={bridge}" };
        foreach (var key in (string[])["firewall", "tag", "rate"])
        {
            if (settings.TryGetValue(key, out var value))
            {
                parts.Add($"{key}={value}");
            }
        }

        parts.Add("ip=dhcp");
        return string.Join(',', parts);
    }

    /// <summary>The newest Debian standard template among these volumes (by Debian release, then template version).</summary>
    internal static string? PickNewestDebianTemplate(IEnumerable<string?> volumes) => volumes
        .Select(v => (Volume: v, Match: v is null ? null : DebianTemplate().Match(v)))
        .Where(t => t.Match is { Success: true })
        .OrderByDescending(t => int.Parse(t.Match!.Groups["major"].Value))
        .ThenByDescending(t => t.Match!.Groups["version"].Value.Split('.').Select(int.Parse).Aggregate(0L, (sum, part) => sum * 10_000 + part))
        .ThenByDescending(t => int.Parse(t.Match!.Groups["release"].Value))
        .Select(t => t.Volume)
        .FirstOrDefault();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^:]+:vztmpl/debian-(?<major>[0-9]+)-standard_(?<version>[0-9]+(\.[0-9]+)*)-(?<release>[0-9]+)_amd64\.tar\.(zst|gz|xz)$")]
    private static partial System.Text.RegularExpressions.Regex DebianTemplate();

    private async Task<MachineHandle> CreateContainerAsync(MachineCreateRequest request, Func<MachineHandle, Task> onAllocated, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var guests = await GetGuestsAsync(client);
        var node = ContainerNode(guests) ?? throw new InvalidOperationException("Set PROXMOX_NODE: container Runners need a node, and there is no template VM to take it from.");
        var (network, storage) = await ContainerPlacementAsync(client, guests);
        var template = await ResolveContainerTemplateAsync(client, node);

        var locked = true;
        await AllocationLock.WaitAsync(cancellationToken);
        try
        {
            var vmId = await FindFreeVmIdAsync(client, guests, reserve: true);
            log.Info($"Creating container {vmId} on {node} from {template}: {request.Cores} cores, {request.MemoryMb} MB RAM, {request.DiskGb} GB on {storage}");
            var create = await client.Nodes[node].Lxc.CreateVm(
                ostemplate: template,
                vmid: vmId,
                hostname: request.Name,
                cores: request.Cores,
                memory: request.MemoryMb,
                rootfs: $"{storage}:{request.DiskGb}",
                netN: new Dictionary<int, string> { [0] = network },
                // Unprivileged, with only nesting: the one feature an API token may set on a container (systemd needs it).
                unprivileged: true,
                features: "nesting=1",
                // Only root exists. The Manager logs in once to create the Runner Account, then removes this key.
                ssh_public_keys: request.Account.SshPublicKey.Trim() + "\n",
                pool: options.Pool,
                // No tags here: Proxmox checks them against /vms/<id> only, ignoring the pool, so a token is refused
                // until the container exists in the pool. They are set right after.
                description: "Runner managed by DeveAgentManager",
                start: false);
            var upid = EnsureTask(create, $"Creating container {vmId}");

            var handle = new MachineHandle(vmId.ToString(), JsonSerializer.Serialize(new ProxmoxMachineData(node, ContainerTemplateId)));
            await onAllocated(handle);
            AllocationLock.Release();
            locked = false;

            await WaitForTaskAsync(client, upid, "Create container", CloneTimeout, log, cancellationToken);
            EnsureSuccess(await client.Nodes[node].Lxc[vmId].Config.UpdateVm(tags: Tag), $"Tagging container {vmId}");
            log.Success($"Container {vmId} created");
            return handle;
        }
        finally
        {
            if (locked)
            {
                AllocationLock.Release();
            }
        }
    }

    /// <summary>
    /// PROXMOX_CONTAINER_NODE (where the GPU is), else PROXMOX_NODE, else where the template VMs are.
    /// </summary>
    private string? ContainerNode(List<ClusterResource> guests) =>
        options.ContainerNode ?? options.Node ?? guests.FirstOrDefault(v => v.VmType == VmType.Qemu && v.VmId == (options.HeadlessTemplateVmId ?? options.DesktopTemplateVmId))?.Node;

    /// <summary>The template VM's network and disk storage (PROXMOX_BRIDGE and PROXMOX_STORAGE win).</summary>
    private async Task<(string Network, string Storage)> ContainerPlacementAsync(PveClient client, List<ClusterResource> guests)
    {
        var source = guests.FirstOrDefault(v => v.VmType == VmType.Qemu && v.VmId == (options.HeadlessTemplateVmId ?? options.DesktopTemplateVmId));
        var config = source is null ? null : await client.Nodes[source.Node].Qemu[source.VmId].Config.GetAsync();
        var network = BuildContainerNetwork(config?.Networks.FirstOrDefault(n => n.Id == "net0")?.RawDefinition, options.Bridge)
            ?? throw new InvalidOperationException("Set PROXMOX_BRIDGE: there is no template VM network to connect the container to.");
        var storage = options.Storage ?? (config is null ? null : FindBootDisk(config)?.Storage)
            ?? throw new InvalidOperationException("Set PROXMOX_STORAGE: there is no template VM disk storage to put the container on.");
        return (network, storage);
    }

    private async Task<string> ResolveContainerTemplateAsync(PveClient client, string node)
    {
        var configured = options.ContainerTemplate ?? throw new InvalidOperationException("TEMPLATE_CONTAINER is not set.");
        var storage = configured.Contains(':') ? configured[..configured.IndexOf(':')] : configured;
        IEnumerable<string?> volumes;
        try
        {
            volumes = (await client.Nodes[node].Storage[storage].Content.GetAsync("vztmpl")).Select(c => c.Volume).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The container templates on storage '{storage}' of {node} can't be read ({ex.Message.Trim()}). The token needs Datastore.Audit on /storage/{storage}.");
        }

        if (configured.Contains(':'))
        {
            return volumes.Contains(configured) ? configured
                : throw new InvalidOperationException($"Container template '{configured}' was not found on {node}.");
        }

        return PickNewestDebianTemplate(volumes)
            ?? throw new InvalidOperationException($"Storage '{storage}' on {node} has no Debian standard container template. Download one: {node} → {storage} → CT Templates → Templates → debian-13-standard.");
    }

    private async Task<ProviderCheck> CheckContainerTemplateAsync(PveClient client, List<ClusterResource> guests)
    {
        const string name = "Container template";
        var node = ContainerNode(guests);
        if (node is null)
        {
            return new ProviderCheck(name, CheckStatus.Failed, "Set PROXMOX_NODE: container Runners need a node, and there is no template VM to take it from.");
        }

        try
        {
            var template = await ResolveContainerTemplateAsync(client, node);
            var (network, storage) = await ContainerPlacementAsync(client, guests);
            // Lists only storages that are on this node, enabled and hold containers ("rootdir").
            var accepts = await client.Nodes[node].Storage.GetAsync(content: "rootdir", storage: storage);
            if (!accepts.Any(s => s.Storage == storage))
            {
                return new ProviderCheck(name, CheckStatus.Failed,
                    $"Storage '{storage}' takes no containers on {node}. Datacenter → Storage → {storage}: add Container to its content, and {node} to its nodes if it lists any.");
            }

            return new ProviderCheck(name, CheckStatus.Ok, $"{template[(template.LastIndexOf('/') + 1)..]} on {node}; disk on {storage}; network {network}.");
        }
        catch (Exception ex)
        {
            return new ProviderCheck(name, CheckStatus.Failed, ex.Message);
        }
    }

    private async Task ConfigureAsync(PveClient client, string node, int vmId, MachineCreateRequest request, IRunnerLog log)
    {
        var vm = client.Nodes[node].Qemu[vmId];
        var config = await vm.Config.GetAsync();

        log.Info($"Configuring VM {vmId}: {request.Cores} cores, {request.MemoryMb} MB RAM, cloud-init user '{request.Account.Username}'");
        Dictionary<int, string>? networks = null;
        if (options.Bridge is not null && config.Networks.FirstOrDefault(n => n.Id == "net0") is { } net0 && net0.Bridge != options.Bridge)
        {
            networks = new() { [0] = ReplaceBridge(net0.RawDefinition, options.Bridge) };
        }

        var tags = string.Join(';', (config.Tags ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries).Append(Tag).Distinct());
        Task<Result> Update(bool? ciupgrade) => vm.Config.UpdateVm(
            cores: request.Cores,
            memory: request.MemoryMb.ToString(),
            ciuser: request.Account.Username,
            cipassword: request.Account.Password,
            // Proxmox expects the keys URL-encoded (and Corsinvest sends JSON, so encode exactly once).
            sshkeys: Uri.EscapeDataString(request.Account.SshPublicKey.Trim() + "\n"),
            ipconfigN: new Dictionary<int, string> { [0] = "ip=dhcp" },
            // No package upgrade on first boot: it holds the apt lock and the Manager's setup updates packages anyway.
            ciupgrade: ciupgrade,
            agent: config.AgentEnabled ? null : "1",
            vga: request.TemplateId == "desktop" ? "std,memory=64" : null,
            netN: networks,
            tags: tags,
            description: "Runner managed by DeveAgentManager");

        var update = await Update(false);
        if (update.InError() && update.GetError().Contains("ciupgrade", StringComparison.OrdinalIgnoreCase))
        {
            // Proxmox before 8.1 doesn't know the option.
            update = await Update(null);
        }

        EnsureSuccess(update, $"Configuring VM {vmId}");

        var bootDisk = FindBootDisk(config);
        var requested = (long)request.DiskGb * 1024 * 1024 * 1024;
        if (bootDisk is not null && requested > bootDisk.SizeBytes)
        {
            log.Info($"Growing disk {bootDisk.Id} from {bootDisk.Size} to {request.DiskGb}G");
            var resize = await vm.Resize.ResizeVm(bootDisk.Id, $"{request.DiskGb}G");
            EnsureSuccess(resize, $"Resizing disk {bootDisk.Id}");
            if (TryGetTask(resize) is { } upid)
            {
                await WaitForTaskAsync(client, upid, "Resize", TaskTimeout, log, CancellationToken.None);
            }
        }
    }

    internal static VmDisk? FindBootDisk(VmConfigQemu config)
    {
        var disks = config.Disks.Where(d => !d.Id.StartsWith("efidisk", StringComparison.Ordinal) && !d.Id.StartsWith("tpmstate", StringComparison.Ordinal)).ToList();
        var order = (config.Boot ?? "").Split(',').FirstOrDefault(p => p.StartsWith("order=", StringComparison.Ordinal))?[6..].Split(';') ?? [];
        return order.Select(id => disks.FirstOrDefault(d => d.Id == id)).FirstOrDefault(d => d is not null)
            ?? disks.FirstOrDefault(d => d.Id == config.BootDisk)
            ?? disks.FirstOrDefault();
    }

    internal static string ReplaceBridge(string rawDefinition, string bridge)
    {
        var parts = rawDefinition.Split(',').Where(p => !p.StartsWith("bridge=", StringComparison.Ordinal)).ToList();
        parts.Insert(Math.Min(1, parts.Count), $"bridge={bridge}");
        return string.Join(',', parts);
    }

    private const string PrivilegeSeparationHint =
        "Most likely Privilege Separation is on for the token (Datacenter > Permissions > API Tokens > Edit > untick it), " +
        "or the permissions for the user are missing (Datacenter > Permissions should list /pool/..., /vms/<template>, /storage/... and /sdn/zones/...).";

    private const string DesktopHardwarePermissionHint =
        "Desktop Runners need VM.Config.HWType on their pool or VM to set the display. " +
        "Add it to the Manager's Proxmox role under Datacenter > Permissions > Roles.";

    private async Task<bool> HasDesktopHardwarePermissionAsync(PveClient client, int vmId)
    {
        string[] paths = options.Pool is { } pool ? [$"/pool/{pool}", $"/vms/{vmId}"] : [$"/vms/{vmId}"];
        foreach (var path in paths)
        {
            var result = await client.Access.Permissions.Permissions(path: path);
            EnsureSuccess(result, "Reading desktop display permissions");
            if (result.ToData() is IDictionary<string, object> data
                && data.TryGetValue(path, out var privileges)
                && privileges is IDictionary<string, object> map
                // Values say whether a privilege propagates; zero still grants it on this exact path.
                && map.ContainsKey("VM.Config.HWType"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Shows which paths the token has privileges on, which is the quickest way to spot a permission mistake.</summary>
    private static async Task<ProviderCheck> CheckPermissionsAsync(PveClient client)
    {
        const string name = "Token permissions";
        try
        {
            var result = await client.Access.Permissions.Permissions();
            EnsureSuccess(result, "Reading the token's permissions");
            var paths = new List<string>();
            if (result.ToData() is IDictionary<string, object> data)
            {
                foreach (var (path, privileges) in data)
                {
                    var granted = privileges is IDictionary<string, object> map ? map.Count(p => Convert.ToInt32(p.Value) == 1) : 0;
                    if (granted > 0)
                    {
                        paths.Add($"{path} ({granted})");
                    }
                }
            }

            return paths.Count == 0
                ? new ProviderCheck(name, CheckStatus.Failed, $"The token has no permissions at all. {PrivilegeSeparationHint}")
                : new ProviderCheck(name, CheckStatus.Ok, $"Privileges on {string.Join(", ", paths.Order())}.");
        }
        catch (Exception ex)
        {
            return new ProviderCheck(name, CheckStatus.Warning, ex.Message);
        }
    }

    private async Task<ProviderCheck> CheckTemplateAsync(PveClient client, List<ClusterResource> vms, RunnerTemplate template)
    {
        var vmId = template.Kind == RunnerKind.Desktop ? options.DesktopTemplateVmId!.Value : options.HeadlessTemplateVmId!.Value;
        var name = $"{template.DisplayName} template ({vmId})";
        var vm = vms.FirstOrDefault(v => v.VmId == vmId && v.VmType == VmType.Qemu);
        if (vm is null)
        {
            return new ProviderCheck(name, CheckStatus.Failed, $"VM {vmId} not found. It must exist and the token needs VM.Audit and VM.Clone on /vms/{vmId}.");
        }

        try
        {
            var config = await client.Nodes[vm.Node].Qemu[vmId].Config.GetAsync();
            var problems = new List<string>();
            if (!vm.IsTemplate)
            {
                problems.Add("it is not a template (full clones of a normal VM work, but converting it to a template is recommended)");
            }

            if (!config.DisksAll.Any(d => d.Kind == VmDiskKind.CloudInit))
            {
                problems.Add("it has no cloud-init drive (Hardware > Add > CloudInit Drive)");
            }

            if (!config.AgentEnabled)
            {
                problems.Add("the QEMU Guest Agent option is off (the Manager turns it on for Runners, but qemu-guest-agent must be installed)");
            }

            return problems.Count == 0
                ? new ProviderCheck(name, CheckStatus.Ok, $"'{vm.Name}' on {vm.Node}, cloud-init drive and guest agent present.")
                : new ProviderCheck(name, vm.IsTemplate && config.DisksAll.Any(d => d.Kind == VmDiskKind.CloudInit) ? CheckStatus.Warning : CheckStatus.Failed, $"'{vm.Name}' on {vm.Node}: {string.Join("; ", problems)}.");
        }
        catch (Exception ex)
        {
            return new ProviderCheck(name, CheckStatus.Failed, ex.Message);
        }
    }

    private async Task<int> FindFreeVmIdAsync(PveClient client, List<ClusterResource> vms, bool reserve)
    {
        var used = vms.Select(v => v.VmId).ToHashSet();
        for (var candidate = options.VmIdStart; candidate <= options.VmIdEnd; candidate++)
        {
            if (used.Contains(candidate))
            {
                continue;
            }

            // The token may not see every VM, so let Proxmox confirm the id is free.
            var result = await client.Cluster.Nextid.Nextid(candidate);
            if (result.IsSuccessStatusCode && !result.InError())
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No free VMID left in {options.VmIdStart}-{options.VmIdEnd}.");
    }

    private async Task<Guest> LocateAsync(PveClient client, RunnerMachine machine)
    {
        var vmId = ParseVmId(machine);
        var vm = (await GetGuestsAsync(client)).FirstOrDefault(v => v.VmId == vmId)
            ?? throw new InvalidOperationException($"VM {vmId} was not found on Proxmox.");
        var guest = new Guest(vm);
        if (guest.IsContainer != IsContainer(machine))
        {
            // The id was reused for a different kind of guest: never start or stop something that isn't this Runner.
            throw new InvalidOperationException($"VMID {vmId} is now a {(guest.IsContainer ? "container" : "VM")}, not this Runner's {(guest.IsContainer ? "VM" : "container")}.");
        }

        return guest;
    }

    /// <summary>Never touch a VM the Manager didn't create: it must be in the VMID range, not a template, and in the pool (or tagged).</summary>
    private void EnsureOwned(ClusterResource vm)
    {
        var inRange = vm.VmId >= options.VmIdStart && vm.VmId <= options.VmIdEnd;
        var tagged = (vm.Tags ?? "").Split([';', ','], StringSplitOptions.TrimEntries).Contains(Tag);
        var inPool = options.Pool is not null && vm.Pool == options.Pool;
        if (!inRange || vm.IsTemplate || !(inPool || tagged))
        {
            throw new InvalidOperationException(
                $"Refusing to delete {new Guest(vm).Noun} {vm.VmId} ('{vm.Name}'): it does not look like a DeveAgentManager Runner (VMID range {options.VmIdStart}-{options.VmIdEnd}, pool '{options.Pool}', tag '{Tag}').");
        }
    }

    /// <summary>VMs and containers: both are Runners, and both take VMIDs from the range.</summary>
    private static async Task<List<ClusterResource>> GetGuestsAsync(PveClient client) =>
        (await client.Cluster.Resources.GetAsync(ClusterResourceType.Vm)).Where(r => r.VmType is VmType.Qemu or VmType.Lxc).ToList();

    private sealed record Guest(ClusterResource Resource)
    {
        public string Node => Resource.Node;
        public int VmId => (int)Resource.VmId;
        public bool IsContainer => Resource.VmType == VmType.Lxc;
        public bool IsRunning => Resource.IsRunning;

        /// <summary>What Proxmox calls it in its own task list.</summary>
        public string Noun => IsContainer ? "CT" : "VM";
    }

    private static int ParseVmId(RunnerMachine machine) =>
        int.TryParse(machine.MachineId, out var vmId) ? vmId : throw new InvalidOperationException($"Runner '{machine.Name}' has no Proxmox VMID.");

    private async Task WaitForTaskAsync(PveClient client, string upid, string what, TimeSpan timeout, IRunnerLog log, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var lastProgress = started;
        while (await client.TaskIsRunningAsync(upid))
        {
            if (DateTime.UtcNow - started > timeout)
            {
                throw new TimeoutException($"{what} task did not finish within {timeout.TotalMinutes:0} minutes ({upid}).");
            }

            if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(30))
            {
                log.Info($"{what} still running ({(DateTime.UtcNow - started).TotalSeconds:0}s)");
                lastProgress = DateTime.UtcNow;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var exitStatus = await client.GetExitStatusTaskAsync(upid);
        // Proxmox reports "OK", or "WARNINGS: n" for tasks that succeeded with warnings.
        if (exitStatus != "OK" && !exitStatus.StartsWith("WARNINGS", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what} failed on Proxmox: {exitStatus}");
        }
    }

    private static string EnsureTask(Result result, string what)
    {
        EnsureSuccess(result, what);
        return TryGetTask(result) ?? throw new InvalidOperationException($"{what}: Proxmox did not return a task id.");
    }

    private static string? TryGetTask(Result result) =>
        result.ResponseHasData && result.ToData() is string upid && upid.StartsWith("UPID:", StringComparison.Ordinal) ? upid : null;

    private static void EnsureSuccess(Result result, string what)
    {
        if (!result.IsSuccessStatusCode || result.InError())
        {
            var details = result.InError() ? result.GetError() : "";
            throw new InvalidOperationException($"{what} failed: {(int)result.StatusCode} {result.ReasonPhrase} {details}".Trim());
        }
    }

    private PveClient CreateClient() =>
        new(options.Host, options.Port, httpClientFactory.CreateClient(HttpClientName)) { ApiToken = options.ApiToken };

    /// <param name="Type">"container" for container Runners; null for VMs (and every Runner from before containers).</param>
    private sealed record ProxmoxMachineData(string Node, string? Type = null);
}
