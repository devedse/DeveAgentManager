# DeveAgentManager

A self-hosted web portal that creates **Runners** (Proxmox VMs, or Linux machines you already have), sets them up for
coding agents over SSH, and lets you run agents on them from your browser. Agents run in tmux, so they keep working
when you close the tab.

- **Manager**: this app, one Docker container with the web portal.
- **Runner**: a machine that hosts agents. Proxmox Runners are full clones of a template VM.
- **Agent**: a harness (currently [pi](https://pi.dev)) running in its own tmux session and working folder on a Runner.
- **Setup Profile**: what every Runner gets during setup: git identity, API keys, login and settings files, and a script.

See [PLAN.md](PLAN.md) for where this is going (Claude Code and Codex, sessions you can move between harnesses, Jobs API).

## Quick start

1. Create a Proxmox API token and check the template (below).
2. Copy [`docker-compose.yml`](docker-compose.yml) to the Docker host and fill in the values marked `CHANGE ME`.
3. Log in to the GitHub Container Registry once (the image is private, like the repo). Use a
   [personal access token](https://github.com/settings/tokens) with `read:packages`:
   `echo <token> | docker login ghcr.io -u devedse --password-stdin`
4. Run `docker compose up -d` and open `http://<docker-host>:8080`.
5. In **Settings → Providers**, click **Test connection**. Every check should be green.
6. **New Runner** → Headless → Create. Watch the setup log (about 3 to 5 minutes).
7. **New agent** → clone a repository → pi opens in the browser.

pi needs a model provider. Either add an API key (e.g. `ANTHROPIC_API_KEY`) under **Settings → Setup Profile →
Environment variables**, or run `/login` inside an agent's terminal once and then use **Import a login from a
Runner** in the same screen, so every new Runner gets it. Use **Re-run setup** on existing Runners to apply changes.

## Proxmox setup

### API token

The Manager uses an API token that can only touch VMs in its own pool, plus clone the templates. Run on a Proxmox node:

```bash
# A pool for all Runners
pveum pool add deveagents --comment "DeveAgentManager runners"

# A role with only what the Manager needs (Proxmox VE 9)
pveum role add DeveAgentManager --privs "VM.Allocate VM.Clone VM.Audit VM.PowerMgmt \
VM.Config.CPU VM.Config.Memory VM.Config.Disk VM.Config.Network VM.Config.Options VM.Config.Cloudinit \
VM.GuestAgent.Audit Datastore.AllocateSpace Datastore.Audit SDN.Use Sys.Audit Pool.Audit"
#   Proxmox VE 8: use VM.Monitor instead of VM.GuestAgent.Audit (needed to read the Runner's IP).

# A user and a token (copy the secret, it is shown once)
pveum user add deveagentmanager@pve --comment "DeveAgentManager"
pveum user token add deveagentmanager@pve manager --privsep 0

# Where the role applies
pveum acl modify /pool/deveagents          --users deveagentmanager@pve --roles DeveAgentManager
pveum acl modify /vms/10100                --users deveagentmanager@pve --roles DeveAgentManager  # headless template
pveum acl modify /vms/10120                --users deveagentmanager@pve --roles DeveAgentManager  # desktop template
pveum acl modify /storage/ReplicatedPool_2 --users deveagentmanager@pve --roles DeveAgentManager  # disk storage
pveum acl modify /sdn/zones/localnetwork   --users deveagentmanager@pve --roles DeveAgentManager  # network
```

Put the token in the compose file: `PROXMOX_TOKEN_ID=deveagentmanager@pve!manager` and `PROXMOX_TOKEN_SECRET=<secret>`.
Adjust the storage and SDN zone names to yours. Without `SDN.Use` on the zone, cloning fails with a 403 on
`/sdn/zones/...`.

#### The same in the web UI

Everything is under **Datacenter → Permissions**. Do the steps in this order, because the pool and the role have to
exist before you can pick them.

1. **Pools** (a sub-item under *Permissions*) → **Create**. Name `deveagents`, comment `DeveAgentManager runners`.
2. **Roles** → **Create**. Name `DeveAgentManager`. In *Privileges*, type to filter and tick each of these:
   `VM.Allocate`, `VM.Clone`, `VM.Audit`, `VM.PowerMgmt`, `VM.Config.CPU`, `VM.Config.Memory`, `VM.Config.Disk`,
   `VM.Config.Network`, `VM.Config.Options`, `VM.Config.Cloudinit`, `VM.GuestAgent.Audit`, `Datastore.AllocateSpace`,
   `Datastore.Audit`, `SDN.Use`, `Sys.Audit`, `Pool.Audit`.
   On Proxmox VE 8, pick `VM.Monitor` instead of `VM.GuestAgent.Audit`.
3. **Users** → **Add**. User name `deveagentmanager`, Realm *Proxmox VE authentication server*. The UI asks for a
   password: use a long random one, the Manager never uses it.
4. **API Tokens** → **Add**. User `deveagentmanager@pve`, Token ID `manager`, and **untick Privilege Separation**, so
   the token gets the user's permissions. Copy the secret now; it is only shown once.
5. The permissions themselves are on **Permissions** (the top item, not a sub-item) → **Add → User Permission**. Add one
   entry per path, each with User `deveagentmanager@pve`, Role `DeveAgentManager` and *Propagate* ticked:

   | Path | Why |
   |---|---|
   | `/pool/deveagents` | create, change, start/stop and delete Runners |
   | `/vms/10100` | clone the headless template |
   | `/vms/10120` | clone the desktop template |
   | `/storage/ReplicatedPool_2` | create the Runners' disks |
   | `/sdn/zones/localnetwork` | connect Runners to the network |

   The path is a dropdown. The pool only shows up after step 1, and the SDN zone only if you use SDN (otherwise skip
   that line).

The permissions list should now show five lines for `deveagentmanager@pve`. The token has no permission lines of its own;
that's expected with Privilege Separation off.

### Template requirements

Runners are full clones of `TEMPLATE_HEADLESS_VMID` (and `TEMPLATE_DESKTOP_VMID`). The template needs:

- **A cloud-init drive**: on the template, **Hardware → Add → CloudInit Drive**. The Manager sets the Runner Account
  (`RUNNER_USERNAME` / `RUNNER_PASSWORD`), its own SSH key, DHCP and the hostname through it.
- **qemu-guest-agent installed** and **Options → QEMU Guest Agent** set to *Enabled*. The Manager reads the Runner's IP
  from it.
  Debian cloud images don't include it; add it to the image before importing it:
  ```bash
  apt install libguestfs-tools
  virt-customize -a debian-13-genericcloud-amd64.qcow2 --install qemu-guest-agent
  ```
  or start a clone once, run `sudo apt install qemu-guest-agent`, shut it down and turn that into the template.
- **Debian or Ubuntu** with `apt`. Setup installs git, tmux, Node.js 24 and the harnesses itself. The cloud-init user
  gets passwordless sudo by default, which setup needs.
- **DHCP** on the Runner's network.

**Settings → Providers → Test connection** checks the token, the templates (cloud-init drive, guest agent option),
the pool, the storage and the VMID range.

## Configuration

Everything is set with environment variables in the compose file.

| Variable | Default | What it does |
|---|---|---|
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | `admin` / `change-me` | Portal login. The Portal gives shell access to every Runner: set a real password. |
| `PROXMOX_URL` | | e.g. `https://proxmox1:8006`. Without it only existing machines can be added. |
| `PROXMOX_TOKEN_ID` / `PROXMOX_TOKEN_SECRET` | | The API token. |
| `PROXMOX_VERIFY_TLS` | `true` | `false` accepts Proxmox's self-signed certificate. |
| `PROXMOX_NODE` | template's node | Node to create Runners on. |
| `PROXMOX_POOL` | | Pool for new Runners. Recommended: the token only has rights there. |
| `PROXMOX_STORAGE` | template's storage | Storage for the cloned disks. |
| `PROXMOX_BRIDGE` | template's bridge | Bridge for the Runner's first NIC. |
| `PROXMOX_VMID_RANGE` | `30000-30999` | Runners only get VMIDs from this range. The Manager never deletes VMs outside it. |
| `PROXMOX_FULL_CLONE` | `true` | `false` makes linked clones (faster, needs a template on storage that supports it). |
| `TEMPLATE_HEADLESS_VMID` / `TEMPLATE_DESKTOP_VMID` | | Template VMIDs, e.g. `10100` and `10120`. |
| `RUNNER_USERNAME` / `RUNNER_PASSWORD` | `remoteman` / `remoteman` | Account created on new VMs by cloud-init. |
| `RUNNER_DEFAULT_CORES` / `_MEMORY_MB` / `_DISK_GB` | `4` / `8192` / `64` | Default size in the New Runner dialog. |
| `GITHUB_PAT` | | Token for cloning and pushing private repositories. Can also be set in the Portal. |
| `GIT_USER_NAME` / `GIT_USER_EMAIL` | | Default git identity on Runners. Can also be set in the Portal. |
| `DATA_DIR` | `/data` | Database, encryption keys and the Manager's SSH key. |

### Running behind a reverse proxy

Terminals and live updates use WebSockets, so the proxy must forward `Upgrade` / `Connection` headers
(nginx: `proxy_http_version 1.1; proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade";`).
`X-Forwarded-*` headers are honored.

### Data and backups

Back up the `/data` volume. It holds the SQLite database, the SSH key the Manager uses to log in to Runners, and the
keys that decrypt stored secrets (Setup Profile, GitHub token). Losing the keys means re-entering those secrets.

## How it works

- **Providers** only create, start, stop and delete machines (`IRunnerProvider`). Proxmox and *Existing machine* are
  built in; Kubernetes, Azure or AWS can be added as another provider project.
- **Setup** runs over SSH: an idempotent script installs packages, Node.js, tmux (with extended keys, so Shift+Enter
  works in pi) and git config, then applies the Setup Profile and installs the harnesses. Output streams to the
  setup log. Run it again any time with **Re-run setup**.
- **Agents** run as `tmux` sessions named `dam-<name>` in `~/work/<name>`. The harness is started with a fixed session
  id, so **Start** after a stop or reboot resumes the same conversation.
- **Terminals** are xterm.js in the browser, connected through the Manager to an SSH session on the Runner. The Runner
  never needs to be reachable from your browser.

## Development

```bash
dotnet run --project DeveAgentManager       # http://localhost:5099, data in DeveAgentManager/data
dotnet test --solution DeveAgentManager.slnx
```

Any Debian machine you can SSH to works as an *Existing machine* Runner for testing, for example a container:

```bash
docker run -d --name fake-runner -p 2222:22 debian:trixie bash -c "apt-get update && apt-get install -y openssh-server sudo \
  && useradd -m -s /bin/bash remoteman && echo remoteman:remoteman | chpasswd \
  && echo 'remoteman ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/remoteman && mkdir -p /run/sshd && /usr/sbin/sshd -D"
```

Then add it as an existing machine at `127.0.0.1`, port `2222` (from the Manager container: `host.docker.internal`).

Every push builds and tests on GitHub Actions and publishes a multi-arch (amd64, arm64) image to
`ghcr.io/devedse/deveagentmanager`: `latest` and `1.0.<build>` from `master`, `beta_<branch>` from other branches.
No secrets are needed; the workflow uses the built-in `GITHUB_TOKEN`. Run it manually (workflow_dispatch) to pick a
self-hosted runner.
