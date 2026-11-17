# Container Runners (Proxmox)

On Proxmox, a Runner can be a Debian container (LXC) instead of a VM. A container starts in seconds, has almost no
overhead and can use a GPU that other containers on the same host use too. It is less isolated than a VM: it shares
the host's kernel. The Manager therefore only creates unprivileged containers.

## Enable it

1. Download a Debian standard template on the node that will run the containers: **node → local → CT Templates →
   Templates → debian-13-standard**.
2. Set `TEMPLATE_CONTAINER` in the compose file and restart the Manager:
   - a storage name, for example `local`: the Manager uses the newest Debian standard template in it, or
   - one template, for example `local:vztmpl/debian-13-standard_13.6-1_amd64.tar.zst`.
3. If the GPU is in another node than the template VMs, set `PROXMOX_CONTAINER_NODE` to that node. VM Runners keep
   being created on `PROXMOX_NODE` (or the template's node).
4. Let the token read the templates on that storage:
   ```bash
   pveum acl modify /storage/local --users deveagentmanager@pve --roles DeveAgentManager
   ```
5. **Manager settings → Providers → Test connection** checks the template, the network and the storage. The storage
   must take containers on that node: **Datacenter → Storage → (storage) → Content** includes **Container**.

**New Runner** then offers **Container** and **Container desktop** next to Headless and Desktop. A container desktop
is a container with an XFCE desktop you open in the browser, on the GPU once one is passed in; see
[Browser desktops](runner-desktop.md#browser-desktops). **Add desktop** under Operations gives an existing container one.

## What the Manager creates

- The same VMID range, pool, tag, naming and size as VM Runners, on `PROXMOX_CONTAINER_NODE`, `PROXMOX_NODE` or the
  template VMs' node (the first one set). The root disk goes to `PROXMOX_STORAGE`, or else to the storage of the
  headless template's disk.
- The network of the headless template VM: its bridge (or `PROXMOX_BRIDGE`), VLAN tag and firewall setting, with
  DHCP.
- Unprivileged, with only the `nesting` feature, which an API token may set on unprivileged containers. Docker needs
  it (as does systemd), and nothing more: `keyctl` isn't needed, since Docker's runc works without it in a container.

Containers have no cloud-init. Only root gets the Manager's SSH key when the container is created. The Manager logs in
as root once, installs `sudo` and `python3`, creates the Runner Account (`RUNNER_USERNAME`) with its password,
passwordless sudo and the `render` and `video` groups, and checks that it can log in as that account. Then it removes
its key from root. From there, setup, agents, Start, Stop, Restart and Delete work exactly as for VMs. The Manager
reads the container's IP from Proxmox, so it needs no guest agent.

## GPU

Proxmox lets only `root@pam` pass devices into a container; no API token can, whatever its role. The Runner page's
**GPU** panel shows the command to run once as root on the container's node. It lists the node's graphics cards; with
more than one, choose the card to pass in. The command names it by its PCI address and finds its render node on the
host, for example:

```bash
pct set 30003 --dev0 "$(readlink -e /dev/dri/by-path/pci-0000:0b:00.0-render)",gid=993
```

The group ID is the container's `render` group, which the Manager reads from the container. The container gets the
`renderD` node itself, under the same path as on the host, since drivers only look for `/dev/dri/renderD*`.

Listing the cards needs `Sys.Audit` on `/`, which the token doesn't have from the setup in the README. To allow only
that, without passing it on to anything below `/`:

```bash
pveum acl modify / --users deveagentmanager@pve --roles PVEAuditor --propagate 0
```

**Test connection** lists the cards it finds. Without that permission, the panel shows the command for the host's
first GPU (`/dev/dri/renderD128`), and `ls -l /dev/dri/by-path/` on the host shows which `renderD` node belongs to
which card.

Restart the Runner, then use **Set up GPU**. It installs the Mesa Vulkan and VA-API drivers, and the panel then names
the GPU that Vulkan reports. On Debian, Mesa comes from Debian's own backports (on Debian 13: Mesa 26.1 instead of
25.0), which recent GPUs need: an Intel Arc Pro B50 only has the 64-bit image atomics of Unreal's Nanite with it. Mesa
then keeps updating from there with Update all. Agents can use it right away: the Runner Account has been in the `render` group since the
container was created. Mesa covers Intel and AMD GPUs; NVIDIA's own driver is not installed.

OpenCL comes from Intel's `intel-opencl-icd`, which Debian 12 packages but Debian 13 doesn't. On Debian 13, Set up GPU
skips it; Intel's compute runtime releases on GitHub provide it (and Level Zero) for compute workloads.

## Limits

- A container's desktop is a browser desktop (KasmVNC): containers have no screen of their own.
- Docker in a container Runner needs Proxmox VE with lxc-pve 6.0.5-2 or later (Proxmox VE 9.1, or 8.4.16): earlier
  versions block the runc security update of November 2025 ("open sysctl net.ipv4.ip_unprivileged_port_start file:
  ... permission denied"). An existing container without `nesting` gets Docker installed but not running, with a
  warning in the setup log: turn on **Options → Features → Nesting** and restart it.
