using System.Collections.Concurrent;
using System.Text.Json;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using DeveAgentManager.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Providers.Proxmox;

/// <summary>
/// Creates Runners as full clones of a Proxmox template and injects the Runner Account with cloud-init.
/// Uses the Corsinvest Proxmox VE client.
/// </summary>
public sealed class ProxmoxProvider(ProxmoxOptions options, IHttpClientFactory httpClientFactory, ILogger<ProxmoxProvider> logger) : IRunnerProvider
{
    public const string ProviderId = "proxmox";
    public const string HttpClientName = "proxmox";
    public const string Tag = "deveagentmanager";

    private static readonly SemaphoreSlim AllocationLock = new(1, 1);
    private readonly ConcurrentDictionary<int, string> _knownNodes = new();

    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromMinutes(5);

    public string Id => ProviderId;

    public string DisplayName => "Proxmox VE";

    public bool IsConfigured => options.IsConfigured;

    public bool SupportsUsageSampling => true;

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
    ];

    public async Task<IReadOnlyList<ProviderCheck>> CheckAsync(CancellationToken cancellationToken)
    {
        var checks = new List<ProviderCheck>();
        if (!options.IsConfigured)
        {
            checks.Add(new ProviderCheck("Configuration", CheckStatus.Failed, "Set PROXMOX_URL, PROXMOX_TOKEN_ID, PROXMOX_TOKEN_SECRET and TEMPLATE_HEADLESS_VMID in the compose file."));
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
            vms = await GetVmsAsync(client);
        }
        catch (Exception ex)
        {
            checks.Add(new ProviderCheck("List VMs", CheckStatus.Failed, ex.Message));
            return checks;
        }

        checks.Add(await CheckPermissionsAsync(client));

        foreach (var template in GetTemplates())
        {
            checks.Add(await CheckTemplateAsync(client, vms, template));
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

        try
        {
            var free = await FindFreeVmIdAsync(client, vms, reserve: false);
            checks.Add(new ProviderCheck("VMID range", CheckStatus.Ok, $"Next free VMID in {options.VmIdStart}-{options.VmIdEnd}: {free}."));
        }
        catch (Exception ex)
        {
            checks.Add(new ProviderCheck("VMID range", CheckStatus.Failed, ex.Message));
        }

        return checks;
    }

    public async Task<MachineHandle> CreateMachineAsync(MachineCreateRequest request, Func<MachineHandle, Task> onAllocated, IRunnerLog log, CancellationToken cancellationToken)
    {
        var templateVmId = request.TemplateId switch
        {
            "desktop" => options.DesktopTemplateVmId,
            _ => options.HeadlessTemplateVmId ?? options.DesktopTemplateVmId,
        } ?? throw new InvalidOperationException("No Runner Template is configured.");

        var client = CreateClient();
        var vms = await GetVmsAsync(client);
        var template = vms.FirstOrDefault(v => v.VmId == templateVmId)
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
        var (node, vmId) = await LocateAsync(client, machine);
        var status = await client.Nodes[node].Qemu[vmId].Status.Current.GetAsync();
        if (status.IsRunning)
        {
            log.Info($"VM {vmId} is already running");
            return;
        }

        log.Info($"Starting VM {vmId} on {node}");
        var upid = EnsureTask(await client.Nodes[node].Qemu[vmId].Status.Start.VmStart(), $"Starting VM {vmId}");
        await WaitForTaskAsync(client, upid, "Start", TaskTimeout, log, cancellationToken);
    }

    public async Task StopMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var (node, vmId) = await LocateAsync(client, machine);
        log.Info($"Shutting down VM {vmId} (forced stop after 2 minutes)");
        var upid = EnsureTask(await client.Nodes[node].Qemu[vmId].Status.Shutdown.VmShutdown(forceStop: true, timeout: 120), $"Shutting down VM {vmId}");
        await WaitForTaskAsync(client, upid, "Shutdown", TaskTimeout, log, cancellationToken);
    }

    public async Task DestroyMachineAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var vmId = ParseVmId(machine);
        var vm = (await GetVmsAsync(client)).FirstOrDefault(v => v.VmId == vmId);
        if (vm is null)
        {
            log.Warning($"VM {vmId} does not exist (anymore); nothing to delete on Proxmox.");
            return;
        }

        EnsureOwned(vm);
        if (vm.IsRunning)
        {
            log.Info($"Stopping VM {vmId}");
            var stop = EnsureTask(await client.Nodes[vm.Node].Qemu[vmId].Status.Stop.VmStop(), $"Stopping VM {vmId}");
            await WaitForTaskAsync(client, stop, "Stop", TaskTimeout, log, cancellationToken);
        }

        log.Info($"Deleting VM {vmId} and its disks");
        var destroy = EnsureTask(await client.Nodes[vm.Node].Qemu[vmId].DestroyVm(destroy_unreferenced_disks: true, purge: true), $"Deleting VM {vmId}");
        await WaitForTaskAsync(client, destroy, "Delete", TaskTimeout, log, cancellationToken);
        log.Success($"VM {vmId} deleted");
    }

    public async Task<MachineStatus> GetStatusAsync(RunnerMachine machine, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var vmId = ParseVmId(machine);
        var vm = (await GetVmsAsync(client)).FirstOrDefault(v => v.VmId == vmId);
        if (vm is null)
        {
            return new MachineStatus(MachinePowerState.Missing);
        }

        _knownNodes[vmId] = vm.Node;

        if (!vm.IsRunning)
        {
            return new MachineStatus(MachinePowerState.Stopped, Detail: vm.Status);
        }

        string? address = null;
        try
        {
            address = SelectAddress(await client.Nodes[vm.Node].Qemu[vmId].Agent.NetworkGetInterfaces.GetAsync());
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Guest agent of VM {VmId} did not answer", vmId);
        }

        return new MachineStatus(
            MachinePowerState.Running,
            address,
            CpuUsage: vm.CpuUsagePercentage * 100,
            MemoryUsedBytes: (long)vm.MemoryUsage,
            MemoryTotalBytes: (long)vm.MemorySize,
            Uptime: TimeSpan.FromSeconds(vm.Uptime),
            Detail: vm.Node);
    }

    public async Task<MachineStatus?> GetUsageAsync(RunnerMachine machine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vmId = ParseVmId(machine);
        using var http = httpClientFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(5);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        using var cancel = budget.Token.Register(http.CancelPendingRequests);
        var client = new PveClient(options.Host, options.Port, http) { ApiToken = options.ApiToken };
        try
        {
            // The stored node is where the VM was created. Full status checks learn its current location after migration.
            var node = _knownNodes.GetValueOrDefault(vmId);
            if (node is null && !string.IsNullOrWhiteSpace(machine.ProviderData))
            {
                try { node = JsonSerializer.Deserialize<ProxmoxMachineData>(machine.ProviderData)?.Node; }
                catch (JsonException) { /* Older runner data may not contain a node. */ }
            }

            // options.Node is a placement preference for new VMs, not the location of existing ones.
            node ??= (await GetVmsAsync(client)).FirstOrDefault(vm => vm.VmId == vmId)?.Node;
            if (node is null) return null;

            budget.Token.ThrowIfCancellationRequested();
            VmQemuStatusCurrent vm;
            try
            {
                vm = await client.Nodes[node].Qemu[vmId].Status.Current.GetAsync();
            }
            catch (PveResultException original) when (!budget.IsCancellationRequested && IsVmLocationError(original, node, vmId))
            {
                // A missing config on this particular node can mean migration. Transport/auth/server failures cannot.
                try
                {
                    var currentNode = (await GetVmsAsync(client)).FirstOrDefault(v => v.VmId == vmId)?.Node;
                    if (currentNode is null || currentNode == node) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
                    budget.Token.ThrowIfCancellationRequested();
                    node = currentNode;
                    vm = await client.Nodes[node].Qemu[vmId].Status.Current.GetAsync();
                }
                catch (Exception secondary) when (!ReferenceEquals(secondary, original))
                {
                    // Preserve the initiating error without putting unfiltered provider responses in diagnostics.
                    throw new InvalidOperationException("VM location recovery failed after a node-specific missing configuration.",
                        new AggregateException(original, secondary));
                }
            }
            budget.Token.ThrowIfCancellationRequested();
            _knownNodes[vmId] = node;
            return vm.IsRunning
                ? new MachineStatus(MachinePowerState.Running, CpuUsage: vm.CpuUsagePercentage * 100,
                    MemoryUsedBytes: (long)vm.MemoryUsage, MemoryTotalBytes: (long)vm.MemorySize)
                : new MachineStatus(MachinePowerState.Stopped);
        }
        catch (Exception ex) when (budget.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Proxmox usage sampling exceeded its five-second budget.", ex);
        }
    }

    internal static bool IsVmLocationError(PveResultException error, string node, int vmId)
    {
        if (error.Result.StatusCode is not (System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.NotFound)) return false;
        var reason = error.Result.ReasonPhrase;
        // Proxmox reports this failure in the HTTP reason; a generic 404/500 is insufficient.
        return reason is { Length: <= 4096 }
            && reason.Contains("Configuration file", StringComparison.OrdinalIgnoreCase)
            && reason.Contains($"nodes/{node}/qemu-server/{vmId}.conf", StringComparison.Ordinal)
            && reason.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> WaitForAddressAsync(RunnerMachine machine, IRunnerLog log, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var (node, vmId) = await LocateAsync(client, machine);
        log.Info($"Waiting for the QEMU guest agent of VM {vmId} to report an IP address");
        var started = DateTime.UtcNow;
        var lastHint = started;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var address = SelectAddress(await client.Nodes[node].Qemu[vmId].Agent.NetworkGetInterfaces.GetAsync());
                if (address is not null)
                {
                    log.Success($"VM {vmId} has IP {address}");
                    return address;
                }
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - lastHint > TimeSpan.FromSeconds(30))
                {
                    log.Info($"Still waiting for the guest agent ({ex.Message.Trim()})");
                    lastHint = DateTime.UtcNow;
                }
            }

            if (DateTime.UtcNow - started > AddressTimeout)
            {
                throw new TimeoutException($"VM {vmId} did not report an IP address within {AddressTimeout.TotalMinutes:0} minutes. Is qemu-guest-agent installed in the template and is DHCP available?");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
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
        var vm = vms.FirstOrDefault(v => v.VmId == vmId);
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

    private async Task<(string Node, int VmId)> LocateAsync(PveClient client, RunnerMachine machine)
    {
        var vmId = ParseVmId(machine);
        var vm = (await GetVmsAsync(client)).FirstOrDefault(v => v.VmId == vmId)
            ?? throw new InvalidOperationException($"VM {vmId} was not found on Proxmox.");
        return (vm.Node, vmId);
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
                $"Refusing to delete VM {vm.VmId} ('{vm.Name}'): it does not look like a DeveAgentManager Runner (VMID range {options.VmIdStart}-{options.VmIdEnd}, pool '{options.Pool}', tag '{Tag}').");
        }
    }

    private static async Task<List<ClusterResource>> GetVmsAsync(PveClient client) =>
        (await client.Cluster.Resources.GetAsync(ClusterResourceType.Vm)).Where(r => r.VmType == VmType.Qemu).ToList();

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

    private sealed record ProxmoxMachineData(string Node);
}
