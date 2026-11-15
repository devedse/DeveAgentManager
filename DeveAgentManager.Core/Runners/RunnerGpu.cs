using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Core.Runners;

/// <summary>
/// A GPU that the host passes into a Runner (Proxmox containers): what the Runner sees of it, and installing its drivers.
/// Passing the device in is up to someone with root on the host (<see cref="IRunnerProvider.GpuAttachCommand"/>).
/// </summary>
public sealed class RunnerGpu(RunnerShell shell, IEnumerable<IRunnerProvider> providers)
{
    /// <summary>The render node of a host's first GPU; the help next to the command explains how to pick another.</summary>
    public const string DefaultRenderDevice = "/dev/dri/renderD128";

    private readonly Dictionary<string, IRunnerProvider> _providers = providers.ToDictionary(p => p.Id);

    // As the Runner Account. vulkaninfo runs through sudo -u so it has the account's groups as they are now: a pooled SSH
    // connection keeps the groups it logged in with.
    internal const string ReadScript = """
        user=$(id -un)
        printf 'gid=%s\n' "$(getent group render | cut -d: -f3)"
        id -nG "$user" | tr ' ' '\n' | grep -qx render && echo member=1
        for device in /dev/dri/renderD*; do
          [ -c "$device" ] && printf 'device=%s %s %s\n' "$device" "$(stat -c %g "$device")" "$(stat -c %a "$device")"
        done
        if command -v vulkaninfo >/dev/null 2>&1; then
          echo drivers=1
          sudo -n -u "$user" vulkaninfo --summary 2>/dev/null | sed -n 's/^[[:space:]]*deviceName[[:space:]]*=[[:space:]]*/vulkan=/p' | head -4
        fi
        true
        """;

    /// <summary>Mesa's Vulkan, VA-API and OpenCL for Intel (and AMD through Mesa), and the render group for the account.</summary>
    internal const string InstallScript = """
        set -euo pipefail
        apt_get() {
          sudo -n env DEBIAN_FRONTEND=noninteractive apt-get -q -y -o DPkg::Lock::Timeout=600 "$@"
        }
        user=$(id -un)
        getent group render >/dev/null || sudo -n groupadd --system render
        sudo -n usermod -aG render,video "$user"
        apt_get update
        apt_get install --no-install-recommends libvulkan1 mesa-vulkan-drivers vulkan-tools intel-media-va-driver vainfo clinfo
        apt_get install --no-install-recommends intel-opencl-icd || echo "intel-opencl-icd is not available here; OpenCL is skipped"
        echo "Vulkan devices:"
        sudo -n -u "$user" vulkaninfo --summary 2>/dev/null | sed -n 's/^[[:space:]]*deviceName[[:space:]]*=[[:space:]]*/  /p'
        """;

    public bool Supports(Runner runner) =>
        _providers.GetValueOrDefault(runner.ProviderId)?.SupportsGpuPassthrough(runner.ToMachine()) == true;

    /// <param name="manage">Include the command for the host (only for people who manage the Runner).</param>
    public async Task<RunnerGpuDto> ReadAsync(Runner runner, bool manage, CancellationToken ct = default)
    {
        if (!Supports(runner)) return new("unavailable", "This Runner can't get a GPU from its host.");
        if (runner.State != RunnerState.Ready) return new("unavailable", "The Runner is not ready.");
        try
        {
            var result = await shell.RunAsync(runner, ReadScript, TimeSpan.FromSeconds(30), ct);
            if (!result.Succeeded) return new("unavailable", "The GPU couldn't be read. Check the Runner connection.");
            var seen = Parse(result.Output);
            var command = manage && seen.RenderGroup is { } group
                ? _providers[runner.ProviderId].GpuAttachCommand(runner.ToMachine(), DefaultRenderDevice, group)
                : null;
            return Interpret(seen, command);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("unavailable", "The GPU couldn't be read. Check the Runner connection.");
        }
    }

    public async Task InstallAsync(Runner runner, IRunnerLog log, CancellationToken ct)
    {
        if (!Supports(runner)) throw new InvalidOperationException("This Runner can't get a GPU from its host.");
        log.Step("Installing the GPU drivers (Mesa Vulkan, VA-API, OpenCL) and adding the Runner Account to the render group");
        var exit = await shell.RunStreamingAsync(runner, InstallScript, log.Output, TimeSpan.FromMinutes(20), ct);
        if (exit != 0) throw new RunnerCommandException($"Setting up the GPU failed (exit code {exit}). See the setup log.");
        var gpu = await ReadAsync(runner, manage: false, ct);
        if (gpu.State == "ready") log.Success($"GPU ready: {gpu.DeviceName}. Restart running agents to give them the render group.");
        else log.Warning(gpu.Message);
    }

    internal sealed record Device(string Path, int Group, string Mode);

    internal sealed record Observation(int? RenderGroup, bool Member, IReadOnlyList<Device> Devices, bool Drivers, IReadOnlyList<string> VulkanDevices);

    internal static Observation Parse(string output)
    {
        int? group = null;
        var member = false;
        var drivers = false;
        var devices = new List<Device>();
        var vulkan = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = line.IndexOf('=');
            var (key, value) = at > 0 ? (line[..at], line[(at + 1)..]) : (line, "");
            switch (key)
            {
                case "gid" when int.TryParse(value, out var gid):
                    group = gid;
                    break;
                case "member":
                    member = true;
                    break;
                case "drivers":
                    drivers = true;
                    break;
                case "device" when value.Split(' ') is [var path, var groupText, var mode] && int.TryParse(groupText, out var deviceGroup):
                    devices.Add(new(path, deviceGroup, mode));
                    break;
                case "vulkan" when value.Length > 0:
                    vulkan.Add(value.Length > 120 ? value[..120] : value);
                    break;
            }
        }

        return new(group, member, devices, drivers, vulkan);
    }

    internal static RunnerGpuDto Interpret(Observation seen, string? command)
    {
        if (seen.Devices.FirstOrDefault() is not { } device)
            return new("not-attached", "No GPU is passed into this Runner yet.", AttachCommand: command);

        // Proxmox passes the node in owned by the group in the command; mode 666 would let everyone use it.
        var usable = device.Group == seen.RenderGroup || device.Mode.EndsWith('6');
        if (!usable)
            return new("no-access", $"{device.Path} belongs to group {device.Group}, not to the render group ({seen.RenderGroup?.ToString() ?? "missing"}). Run the command again.",
                RenderDevice: device.Path, AttachCommand: command);
        if (!seen.Drivers || !seen.Member)
            return new("drivers-missing", $"{device.Path} is passed in. Set up GPU installs its drivers.", RenderDevice: device.Path, AttachCommand: command);

        var gpu = seen.VulkanDevices.FirstOrDefault(name => !name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase));
        return gpu is null
            ? new("not-detected", "Vulkan only sees the software renderer. Check that the render node is the right card, then use Set up GPU again.",
                RenderDevice: device.Path, AttachCommand: command)
            : new("ready", gpu, gpu, device.Path, command);
    }
}
