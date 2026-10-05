# DeveAgentManager

A self-hosted web portal that creates **Runners** (Proxmox VMs, or Linux machines you already have), sets them up for
coding agents over SSH, and lets you run agents on them from your browser. Agents run in tmux, so they keep working
when you close the tab.

- **Manager**: this app, one Docker container with the web portal.
- **Runner**: a machine that hosts agents. Proxmox Runners are full clones of a template VM.
- **Agent**: a harness ([pi](https://pi.dev), [Claude Code](https://code.claude.com) or
  [Codex](https://github.com/openai/codex)) running in its own tmux session and working folder on a Runner.
- **Account** (**Settings → Accounts**): a Claude or ChatGPT subscription, or an API key, connected once in the
  Manager and used on every Runner. Claude logs in with `claude setup-token` (a token valid for a year). Codex logs in
  with `codex login --device-auth` (a code you enter on any device). A harness can have several accounts.
- **Continue with…**: the switch button in an agent's terminal continues the conversation with another account or in
  another harness, in the same folder, e.g. when a subscription ran out. Another harness gets the conversation as
  text: messages, answers and what the tools did carry over, and the model's hidden thinking stays behind.
- **Session**: an agent's conversation. The Manager keeps a copy in a harness-neutral format, so it survives the agent
  and its Runner and can be continued on another Runner.
- **Setup Profile**: what every Runner gets during setup: git identity, API keys, login and settings files, and a script.
  Per harness (**Settings → pi**): packages (pi extensions, skills, themes, e.g. `npm:pi-web-access`), the default
  model and thinking level, and extra settings merged into `~/.pi/agent/settings.json`. **Save and apply to Runners**
  updates the ready Runners right away. Packages removed from the list are removed from the Runners.
  **Settings → Claude Code / Codex**: the default model and effort for new agents. Empty (the default) lets the
  harness choose, which follows new models by itself. Claude takes aliases like `opus`, and Codex lists the models its
  catalog offers on your Runner.
- **VS Code** (opt-in per Runner: tick it in **New Runner**, or **Install VS Code** on the Runner page): VS Code in the
  browser ([code-server](https://github.com/coder/code-server)) to browse files, edit and see git changes. The Runner
  page opens it in the Runner's home folder, an agent's terminal in the agent's folder. It opens inside the Portal,
  with buttons for a new tab and full screen. It is served on the Manager's second port (`8081`), see below.
- A Runner's **Activity** tab lists what ran on it (setup, starts, installs, updates), how long it took and how it ended.
- On a Runner's page, **Re-run setup** runs the whole setup again, and **Update pi** only installs the latest pi,
  updates its packages and applies Settings → pi. Every step is idempotent, so both are safe to repeat. Running agents
  keep running and use the new versions after a restart.

See [PLAN.md](PLAN.md) for where this is going (Jobs API, projects).

## Quick start

1. Create a Proxmox API token and check the template (below).
2. Copy [`docker-compose.yml`](docker-compose.yml) to the Docker host and fill in the values marked `CHANGE ME`.
3. Log in to the GitHub Container Registry once (the image is private, like the repo). Use a
   [personal access token](https://github.com/settings/tokens) with `read:packages`:
   `echo <token> | docker login ghcr.io -u devedse --password-stdin`
4. Run `docker compose up -d` and open `http://<docker-host>:8080`. Port `8081` serves VS Code (optional).
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

Everything is under **Datacenter → Permissions**. Click the ▸ arrow next to *Permissions* to expand it: Users,
API Tokens, Groups, **Pools**, Roles and Realms are sub-items. Do the steps in this order, because the pool and the
role have to exist before you can pick them.

1. **Pools** → **Create**. Name `deveagents`, comment `DeveAgentManager runners`.
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

Desktop VM autologin, connecting agents to the real display, and updating existing runners are described in
[Desktop runners](docs/runner-desktop.md).

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
| `EDITOR_PORT` | `8081` | The port that serves VS Code, and nothing else. Publish it next to 8080. |
| `EDITOR_URL` | Portal's host on `EDITOR_PORT` | Where browsers reach that port, when it differs (behind a proxy), e.g. `https://code.example.com`. With `{runner}` in it (e.g. `https://{runner}.code.example.com`, a wildcard DNS name), each Runner's VS Code gets a host of its own, which isolates Runners from each other too. |

### Running behind a reverse proxy

Terminals and live updates use WebSockets, so the proxy must forward `Upgrade` / `Connection` headers
(nginx: `proxy_http_version 1.1; proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade";`).
`X-Forwarded-*` headers are honored.

VS Code needs its own origin: proxy `EDITOR_PORT` (8081) separately, on another port or hostname, with WebSockets too,
and set `EDITOR_URL` to it. Inside the Portal, VS Code only works when both are the same site (another port, or e.g.
`code.example.com` next to `agents.example.com`); on an unrelated domain, use its **Open in a new tab** button.

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
  id, so **Start** after a stop or reboot resumes the same conversation. Start leaves a harness that is still running
  alone. **Restart** asks first, sends the harness SIGTERM, and kills it only after 10 seconds.
  Claude Code and Codex run without permission prompts (`--dangerously-skip-permissions`,
  `--dangerously-bypass-approvals-and-sandbox`), because Runners are throwaway VMs. Their first-run questions (theme,
  trusting the folder) are answered by the Manager. A Runner set up without them gets them with **Install …** under
  Operations.
- **Sessions** are synced from the Runners every 15 seconds (only files that changed) and stored as a
  [DeveSession](docs/session-formats.md) in `/data/sessions`. **Continue on a Runner** writes the conversation back in
  the harness's own format, in the new agent's folder, before the harness starts. Sessions can be exported (DeveSession
  or pi `.jsonl`) and imported. See [docs/session-formats.md](docs/session-formats.md) for the pi, Claude Code and Codex
  formats and how they map.
- **The Portal** is a Blazor WebAssembly app: it runs in the browser and talks to the Manager only through its HTTP
  API (`/api`), a SignalR hub for live updates and setup logs (`/hubs/live`), and a WebSocket per terminal
  (`/ws/terminal`). The Manager keeps everything that matters: the database, the secrets, SSH to the Runners. Only the
  login and error pages are server-rendered. A phone that sleeps just reconnects (pages reload what they missed, and an
  agent's terminal reconnects by itself); there is no server-side session to lose.
- **Security**: everything but the login page needs the login cookie (SameSite=Lax). Requests that change something
  must carry the Portal's `X-DAM-Request` header (other sites can't send it without CORS, which the Manager doesn't
  allow) and, like the hub and terminal WebSockets, come from the Manager's own origin. The API never returns a secret:
  account logins, Runner passwords and the GitHub token stay on the Manager. A login terminal only names the harness;
  the Manager runs the harness's login itself and saves what it produces.
- **VS Code** runs as code-server on the Runner, on its loopback only (`127.0.0.1:13337`) with a password of its own
  (in `~/.deveagentmanager/code-server/config.yaml`, mode 600), so other users of the machine can't use it. The
  Manager starts it on first use and reaches it through an SSH tunnel, like terminals. It is served on its own port,
  because VS Code runs code from the Runner (extensions, or anything an agent there puts in code-server's place): on the
  Portal's origin, that code could use the Portal as the admin. The Portal hands the browser a ticket for one Runner,
  valid for two minutes, which the editor port turns into a cookie for that Runner's path. The browser's other cookies
  (the Portal's login) are removed before a request goes to the Runner, and cookies from the Runner never reach the
  browser. The Portal only hands out tickets to its own pages, can't be framed by the editor port, and frames VS Code
  without letting it navigate the Portal's tab. The code-server password is only given to a program on its port that
  belongs to the Runner Account. What one origin can't do: keep Runners apart from each other. A Runner whose VS Code
  you open could reach the VS Code of other Runners opened in the same browser (for 12 hours); `EDITOR_URL` with
  `{runner}` gives each Runner its own origin.
- **Terminals** are xterm.js in the browser, connected through the Manager to an SSH session on the Runner. The Runner
  never needs to be reachable from your browser. On phones there is a row of extra keys, and swiping scrolls
  (through tmux's mouse mode). Phone keyboards garble text typed straight into xterm.js (xterm.js issue #3600). So on
  touch devices the keyboard types into a hidden field that keeps the current line, and only the differences are sent:
  Backspaces, arrows and new text. Autocorrect, swipe typing and Backspace then behave as in other apps. The
  **⌨ fix** key switches this off.
- **Several screens per agent**: a phone and a PC can have the same agent open. tmux sizes the terminal to the screen
  used last, and pi redraws its whole conversation when the size changes. So a screen that joins while others are
  attached only watches (tmux's `ignore-size`) until something is typed on it. Opening or refreshing a tab changes
  nothing. The **screens** button in the terminal's toolbar shows what is attached and can disconnect a screen, which
  then says so and offers Reconnect.
- **Pasting** into a terminal uses your computer's clipboard. Ctrl+V (or Ctrl+Shift+V, Shift+Insert) pastes text, and
  also images such as screenshots. Images can be dropped on the terminal too, or picked with the **📎 Image** key
  (on phones: gallery, camera, screenshots). Images are stored on the Runner in `~/.deveagentmanager/uploads`, and their
  path is pasted into the terminal, like pi's own Ctrl+V does locally. The harness sends the image to the model.
  PNG, JPEG, WebP and GIF images, up to 25 MB.

## Development

```bash
dotnet run --project DeveAgentManager       # http://localhost:5099, data in DeveAgentManager/data
dotnet test --solution DeveAgentManager.slnx
```

Projects: `DeveAgentManager` (the Manager: API, hub, terminal sockets, hosting), `DeveAgentManager.Client` (the Portal,
WebAssembly), `DeveAgentManager.Contracts` (what both share: states, API data, the session and Setup Profile models),
`DeveAgentManager.Core` (Runners, agents, sessions, accounts, SSH), the harnesses and the providers.

Any Debian machine you can SSH to works as an *Existing machine* Runner for testing, for example a container:

```bash
docker run -d --name fake-runner -p 2222:22 debian:trixie bash -c "apt-get update && apt-get install -y openssh-server sudo \
  && useradd -m -s /bin/bash remoteman && echo remoteman:remoteman | chpasswd \
  && echo 'remoteman ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/remoteman && mkdir -p /run/sshd && /usr/sbin/sshd -D"
```

Then add it as an existing machine at `127.0.0.1`, port `2222` (from the Manager container: `host.docker.internal`).

Every push builds and tests on GitHub Actions and publishes a multi-arch (amd64, arm64) image to
`ghcr.io/devedse/deveagentmanager`: `latest` and `1.0.<build>` from `master`; other branches get `beta` (the newest
branch build) and `beta_<branch>`, versioned `1.0.0-beta.<run>`, and never touch `latest`.
No secrets are needed; the workflow uses the built-in `GITHUB_TOKEN`. Run it manually (workflow_dispatch) to pick a
self-hosted runner.
