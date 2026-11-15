# Container Runners (Proxmox)

On Proxmox, a Runner can be a Debian container (LXC) instead of a VM. A container starts in seconds, has almost no
overhead and can use a GPU that other containers on the same host use too. It is less isolated than a VM: it shares
the host's kernel. The Manager therefore only creates unprivileged containers.

## Enable it

1. Download a Debian standard template on the node that creates Runners: **node → local → CT Templates → Templates →
   debian-13-standard**.
2. Set `TEMPLATE_CONTAINER` in the compose file and restart the Manager:
   - a storage name, for example `local`: the Manager uses the newest Debian standard template in it, or
   - one template, for example `local:vztmpl/debian-13-standard_13.6-1_amd64.tar.zst`.
3. Let the token read the templates on that storage:
   ```bash
   pveum acl modify /storage/local --users deveagentmanager@pve --roles DeveAgentManager
   ```
4. **Manager settings → Providers → Test connection** checks the template, the network and the storage.

**New Runner** then offers **Container** next to Headless and Desktop.

## What the Manager creates

- The same VMID range, pool, tag, naming and size as VM Runners. The root disk goes to `PROXMOX_STORAGE`, or else to
  the storage of the headless template's disk.
- The network of the headless template VM: its bridge (or `PROXMOX_BRIDGE`), VLAN tag and firewall setting, with
  DHCP.
- Unprivileged, with only the `nesting` feature, which an API token may set on unprivileged containers.

Containers have no cloud-init. Only root gets the Manager's SSH key when the container is created. The Manager logs in
as root once, installs `sudo` and `python3`, creates the Runner Account (`RUNNER_USERNAME`) with its password,
passwordless sudo and the `render` and `video` groups, and checks that it can log in as that account. Then it removes
its key from root. From there, setup, agents, Start, Stop, Restart and Delete work exactly as for VMs. The Manager
reads the container's IP from Proxmox, so it needs no guest agent.

## GPU

Proxmox lets only `root@pam` pass devices into a container; no API token can, whatever its role. The Runner page's
**GPU** panel shows the command to run once as root on the container's node, for example:

```bash
pct set 30003 --dev0 /dev/dri/renderD128,gid=993
```

The group ID is the container's `render` group, which the Manager reads from the container. With more than one GPU,
`ls -l /dev/dri/by-path/` on the host shows which `renderD` node belongs to which card. Use the `renderD` node itself:
the container sees a device under the same path as the host.

Restart the Runner, then use **Set up GPU**. It installs the Mesa Vulkan, VA-API and OpenCL drivers, and the panel
then names the GPU that Vulkan reports. Agents can use it right away: the Runner Account has been in the `render` group
since the container was created. Mesa covers Intel and AMD GPUs; NVIDIA's own driver is not installed.

## Limits

- Headless only. A desktop container is not supported yet.
- No Docker inside the Runner: that needs the `keyctl` feature, which also only `root@pam` may set.
