# DeveAgentManager

A self-hosted web portal that creates **Runners** (Proxmox VMs, or Linux machines you already have), sets them up for
coding agents over SSH, and lets you run agents on them from your browser. Agents run in tmux, so they keep working
when you close the tab.

- **Manager**: this app, one Docker container with the web portal.
- **Runner**: a machine that hosts agents. Proxmox Runners are full clones of a template VM.
- **Agent**: a harness ([pi](https://pi.dev), [Claude Code](https://code.claude.com),
  [Codex](https://github.com/openai/codex) or [Grok Build](https://docs.x.ai/build/overview)) running in its own tmux session and working folder on a Runner.
- **Project**: a repository or folder grouping Main and its workspaces on one Runner. A workspace is an independent clone
  next to Main with its own agent and conversation; several run at once and each starts and stops on its own. Move
  project transfers Main and every workspace. See [workspace behaviour and rollback](docs/project-workspaces.md).
- **Users**: everyone signs in with a username and password of their own and has personal or shared harness logins, a GitHub
  token, git identity, environment variables and SSH keys (**My settings**). Roles: *settings manager*, *user
  manager*, *runner creator*, *runner manager*, *conversation manager*, *log viewer* and *harness login manager*; *Admin* is all of them. A Runner has an owner and people with *Use*
  or *Manage* access, who share its Linux account (so they trust each other with what is on it). Agents can be shared
  read-only or read-write, conversations read-only. Conversation managers (including admins) can open, prompt and manage every agent
  and read, export, rename, share or delete every conversation. Existing admins receive
  this role on upgrade and must sign in again. See [docs/users-plan.md](docs/users-plan.md).
- **Account** (**My settings → Harness logins**): a Claude, ChatGPT or Grok login, or an API key, connected once in the
  Manager and used on the Runners where authorized agents run. Logins have **Use** and **Manage** grants rather than a permanent owner. Use **Manage access** on a login row to share it; each person chooses their own default. **Manage → Harness logins** administers all logins with the *Harness login manager* role. Claude logs in with `claude setup-token` (a token valid for a year). Codex logs in
  with `codex login --device-auth` (a code you enter on any device); the Manager renews that login itself and Runners
  only get its access token, so agents on several Runners can share it ([details](docs/codex-central-auth-plan.md)).
  A harness can have several logins. Sharing one does not share agents or conversations. Removing access stops affected
  harnesses; offline stops and credential cleanup are retried and shown as pending. Affected agents need a permitted
  login selected explicitly. Before deleting a user, give their logins another manager or explicitly remove them.
  See [the login sharing design](docs/harness-login-sharing-plan.md) for migration and Runner trust details.
  Grok uses device authentication or `XAI_API_KEY`; the Manager renews its login centrally and isolates each agent's history.
  See [Grok setup and session backups](docs/grok-harness.md).
- **Continue with…**: the switch button in an agent's terminal continues the conversation with another account or in
  another harness, in the same folder, e.g. when a subscription ran out. Another harness gets the conversation as
  text: messages, answers and what the tools did carry over, and the model's hidden thinking stays behind.
- **Session**: an agent's conversation. The Manager keeps a copy in a harness-neutral format, so it survives the agent
  and its Runner and can be continued on another Runner.
- **Tasks**: an agent shows its progress next to its terminal with `dam-tools tasks`: each task's % done, what it is or
  what was done, time taken and time left. See [agent task lists](docs/agent-tasks.md).
- **Move to another runner…**: an agent's actions menu moves its workspace, native histories and attachments while
  keeping its identity and sharing. Failed moves retain the source and offer recovery. See [agent moves](docs/agent-moves.md).
- **Setup Profile** (**Manager settings**): what every Runner gets during setup: login and settings files, and a script.
  Each person's git identity, environment variables (API keys) and GitHub token are their own (**My settings**).
  Per harness (**Manager settings → pi**): packages (pi extensions, skills, themes, e.g. `npm:pi-web-access`), the default
  model and thinking level, and extra settings merged into `~/.pi/agent/settings.json`. **Save and apply to Runners**
  updates the ready Runners right away. Packages removed from the list are removed from the Runners.
  **Manager settings → Claude Code / Codex / Grok**: the default model and effort for new agents. Empty (the default) lets the
  harness choose, which follows new models by itself. Claude takes aliases like `opus`, and Codex lists the models its
  catalog offers on your Runner.
- **VS Code** (opt-in per Runner: tick it in **New Runner**, or **Install VS Code** on the Runner page): VS Code in the
  browser ([code-server](https://github.com/coder/code-server)) to browse files, edit and see git changes. The Runner
  page opens it in the Runner's home folder, an agent's terminal in the agent's folder. It opens inside the Portal,
  with buttons for a new tab and full screen. Public editors use separate Runner hostnames on port `8080`; local IP access can use port `8081`, see below.
- A Runner's **Activity** tab lists what ran on it (setup, starts, installs, updates), how long it took and how it ended.
- On a Runner's page, **Re-run setup** runs the whole setup again, and **Update pi** only installs the latest pi,
  updates its packages and applies Manager settings → pi. Every step is idempotent, so both are safe to repeat. Running agents
  keep running and use the new versions after a restart.
- Each Runner card on the Runners page and the top of a Runner's page have **Start** or **Stop**, **Restart** (stop,
  then start; running agents stop as with Stop) and **Update all** (the same as Re-run setup). Each is held for 3
  seconds to confirm instead of asking in a dialog; hovering one says what it does.

See [PLAN.md](PLAN.md) for where this is going (Jobs API, projects).

## Quick start

1. Create a Proxmox API token and check the template (below).
2. Copy [`docker-compose.yml`](docker-compose.yml) to the Docker host and fill in the values marked `CHANGE ME`.
3. Log in to the GitHub Container Registry once (the image is private, like the repo). Use a
   [personal access token](https://github.com/settings/tokens) with `read:packages`:
   `echo <token> | docker login ghcr.io -u <github-username> --password-stdin`
4. Run `docker compose up -d`, open `http://<docker-host>:8080` and sign in as `ADMIN_USERNAME` using the one-time setup code in `docker compose logs` (or the optional first-start `ADMIN_PASSWORD`).
   Port `8081` serves VS Code (optional). Add the other people under **Users**.
5. In **Manager settings → Providers**, click **Test connection**. Every check should be green.
6. **New Runner** → Headless → Create. Watch the setup log (about 3 to 5 minutes).
7. **New agent** → clone a repository → pi opens in the browser.

pi needs a model provider. Either add an API key (e.g. `ANTHROPIC_API_KEY`) under **My settings → Environment**, or
run `/login` inside an agent's terminal once and then use **Import a login from a Runner** under **Manager settings →
Setup Profile**, so every new Runner gets it. Use **Re-run setup** on existing Runners to apply Setup Profile changes.

## Proxmox setup

### API token

The Manager uses an API token that can only touch VMs in its own pool, plus clone the templates. Run on a Proxmox node:

```bash
# A pool for all Runners
pveum pool add deveagents --comment "DeveAgentManager runners"

# A role with only what the Manager needs (Proxmox VE 9)
pveum role add DeveAgentManager --privs "VM.Allocate VM.Clone VM.Audit VM.PowerMgmt \
VM.Config.CPU VM.Config.Memory VM.Config.Disk VM.Config.HWType VM.Config.Network VM.Config.Options VM.Config.Cloudinit \
VM.GuestAgent.Audit Datastore.AllocateSpace Datastore.Audit SDN.Use Sys.Audit Pool.Audit"
#   Proxmox VE 8: use VM.Monitor instead of VM.GuestAgent.Audit (needed to read the Runner's IP).

# A user and a token (copy the secret, it is shown once)
pveum user add deveagentmanager@pve --comment "DeveAgentManager"
pveum user token add deveagentmanager@pve manager --privsep 0

# Where the role applies
pveum acl modify /pool/deveagents          --users deveagentmanager@pve --roles DeveAgentManager
pveum acl modify /vms/10100                --users deveagentmanager@pve --roles DeveAgentManager  # headless template
pveum acl modify /vms/10120                --users deveagentmanager@pve --roles DeveAgentManager  # desktop template
pveum acl modify /storage/example-storage --users deveagentmanager@pve --roles DeveAgentManager  # disk storage
pveum acl modify /sdn/zones/localnetwork   --users deveagentmanager@pve --roles DeveAgentManager  # network
```

Put the token in the compose file: `PROXMOX_TOKEN_ID=deveagentmanager@pve!manager` and `PROXMOX_TOKEN_SECRET=<secret>`.
Adjust the storage and SDN zone names to yours. Without `SDN.Use` on the zone, cloning fails with a 403 on
`/sdn/zones/...`.

Desktop Runners set the virtual display to standard VGA with 64 MiB of video memory. Proxmox requires
`VM.Config.HWType` for this change. For an existing `DeveAgentManager` role, append the privilege on a Proxmox node:

```bash
pveum role modify DeveAgentManager --append --privs VM.Config.HWType
```

Use your role's name if it differs. With Privilege Separation enabled, the user and token must both have this
privilege on the Runner pool or VM. **Test connection** checks it, and desktop creation checks before cloning.
If a new Runner already failed at **Configuring VM**, remove that failed Runner including its cloned VM and create
it again after updating the role. **Start** and **Re-run setup** do not repeat the VM configuration or cloud-init step.

#### The same in the web UI

Everything is under **Datacenter → Permissions**. Click the ▸ arrow next to *Permissions* to expand it: Users,
API Tokens, Groups, **Pools**, Roles and Realms are sub-items. Do the steps in this order, because the pool and the
role have to exist before you can pick them.

1. **Pools** → **Create**. Name `deveagents`, comment `DeveAgentManager runners`.
2. **Roles** → **Create**. Name `DeveAgentManager`. In *Privileges*, type to filter and tick each of these:
   `VM.Allocate`, `VM.Clone`, `VM.Audit`, `VM.PowerMgmt`, `VM.Config.CPU`, `VM.Config.Memory`, `VM.Config.Disk`,
   `VM.Config.HWType`, `VM.Config.Network`, `VM.Config.Options`, `VM.Config.Cloudinit`, `VM.GuestAgent.Audit`, `Datastore.AllocateSpace`,
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
   | `/storage/example-storage` | create the Runners' disks |
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
  (`RUNNER_USERNAME`, with a random password of its own per Runner), its own SSH key, DHCP and the hostname through it.
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

**Manager settings → Providers → Test connection** checks the token, the templates (cloud-init drive, guest agent option),
the pool, the storage and the VMID range.

## Configuration

Startup and connection settings use environment variables in the compose file.

| Variable | Default | What it does |
|---|---|---|
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | `admin` / random setup code | First-start credentials. Without a password, a random code is printed once in startup logs and must be changed after sign-in. Existing chosen passwords are never reset at normal startup. Legacy `change-me` credentials are replaced with a logged random setup code. |
| `RESET_ADMIN_PASSWORD` | `false` | Operator recovery: restores the named admin, resets its password, removes its authenticator. Without `ADMIN_PASSWORD`, logs a fresh recovery code. Ends old credentials; requires password change. Remove it immediately after recovery. |
| `PROXMOX_URL` | | e.g. `https://proxmox.example.com:8006`. Without it only existing machines can be added. |
| `PROXMOX_TOKEN_ID` / `PROXMOX_TOKEN_SECRET` | | The API token. |
| `PROXMOX_VERIFY_TLS` | `true` | `false` accepts Proxmox's self-signed certificate. |
| `PROXMOX_NODE` | template's node | Node to create Runners on. |
| `PROXMOX_POOL` | | Pool for new Runners. Recommended: the token only has rights there. |
| `PROXMOX_STORAGE` | template's storage | Storage for the cloned disks. |
| `PROXMOX_BRIDGE` | template's bridge | Bridge for the Runner's first NIC. |
| `PROXMOX_VMID_RANGE` | `30000-30999` | Runners only get VMIDs from this range. The Manager never deletes VMs outside it. |
| `PROXMOX_FULL_CLONE` | `true` | `false` makes linked clones (faster, needs a template on storage that supports it). |
| `TEMPLATE_HEADLESS_VMID` / `TEMPLATE_DESKTOP_VMID` | | Template VMIDs, e.g. `10100` and `10120`. |
| `RUNNER_USERNAME` | `remoteman` | Account created on new VMs by cloud-init. Each Runner gets a random password of its own, which whoever manages the Runner sees on its page. (`RUNNER_PASSWORD` is no longer used.) |
| `RUNNER_DEFAULT_CORES` / `_MEMORY_MB` / `_DISK_GB` | `4` / `8192` / `64` | Default size in the New Runner dialog. |
| `DATA_DIR` | `/data` | Database, encryption keys and the Manager's SSH key. |
| `CPU_TRACE_STARTUP` | `false` | Record one five-minute startup CPU capture. Manual captures remain available under Manager settings → Diagnostics. |
| `CPU_PROFILER_ENABLED` | Docker: `true` on AMD64, `false` on ARM64 | Load the native CPU profiler for on-demand captures; `false` uses portable thread samples. See [CPU captures](docs/manager-cpu-traces.md). |
| `EDITOR_PORT` | `8081` | Optional editor-only port for direct local IP access. Public editors use hostname routing on the Portal port. |
| `EDITOR_URL` | Local Portal host on `EDITOR_PORT` | Public mode requires `{runner}` within the first hostname label: `https://{runner}.code.{host}`, or flat names such as `https://dam-vscode-{runner}.example.com` (one level deep, so a free `*.example.com` certificate such as Cloudflare's covers them). `{host}` is derived only from an allowlisted public Portal origin. |
| `PUBLIC_ORIGINS` | empty | Semicolon-separated HTTPS Portal origins, e.g. `https://dam.example.com;https://dam.example.org`. Unknown public origins are rejected; editor hosts never serve the Portal. |
| `TRUSTED_PROXIES` | empty | Semicolon-separated exact proxy IP addresses (actual socket peers). Only these can set forwarded client IP and HTTPS scheme. Host is preserved directly; forwarded Host is never used. |
| `ALLOW_LOCAL_ACCESS` | `true` | Allow direct private/loopback IP HTTP connections, with separate credentials. Proxy or forwarded traffic cannot activate this exception. |
| `PROXY_IDENTITY_HEADERS` | empty | Extra semicolon-separated identity headers stripped before forwarding to a Runner. Authorization, Proxy-Authorization, forwarding and common authentication headers are always stripped. |

Conversation history verification is configured under **Manager settings → Diagnostics → Conversation sync**.
Choose Sampled, Shadow or Full and save; the choice is stored in the database and applies to subsequent syncs
without restarting the Manager. See [conversation sync](docs/lean-session-sync.md).

### Running behind Nginx Proxy Manager

Configure the Portal proxy host for your public domains, forwarding HTTP to DAM port **8080**. Enable WebSockets,
Force SSL and HSTS; disable asset caching. Set the exact public origins and NPM's actual connection IP in compose.
The final proxy has no Basic authentication: DAM handles sign-in and Runner authorization.

Configure **one wildcard editor proxy host per domain**, e.g. `*.code.dam.example.com`, forwarding to the same port
**8080**, with WebSockets enabled. Add the corresponding wildcard DNS record and wildcard TLS certificate obtained by
DNS challenge. `EDITOR_URL=https://{runner}.code.{host}` chooses the matching public domain automatically. Adding a
Runner never needs an NPM entry, DNS record or certificate. Each Runner keeps a separate browser origin.

Behind Cloudflare (proxied DNS or a tunnel), use flat editor names instead: the free Universal SSL certificate only
covers one level (`*.example.com`). With `EDITOR_URL=https://dam-vscode-{runner}.example.com`, an existing
`*.example.com` DNS record/tunnel route already reaches NPM; add
`server_name ~^dam-vscode-[a-z0-9-]+\.example\.com$;` to the Portal proxy host's **Advanced** tab so NPM routes them
to DAM. A broader NPM wildcard host such as `*.example.com` would take priority over that regex.

Public deployments must keep backend ports off the internet. Trust only NPM's specific socket addresses, including
its IPv6 address if used. Requests preserve Host; `X-Forwarded-Host` is ignored. NPM must overwrite forwarded headers
and send the real client IP and HTTPS scheme. Verify these on your installed proxy; the app's **Account security**
page shows its configured boundaries. Optional local port 8081 serves editors only, with reduced browser isolation
accepted for direct LAN IP workflows; it can be removed from public compose port mappings.

VS Code requires Use access to the Runner, including when opened from a shared agent. Its single-use ticket expires
in 30 seconds and travels in a URL fragment, so proxy access logs never receive it. Portal and editor cookies refer
to the same revocable session. Logout, disabling an account, removing access or expiry closes live connections;
agents continue running. HTTP requests recheck immediately; long-lived connections check expiry within 15 seconds.

### Account security

Use **Account menu → Account security** to turn on an authenticator app (optional, per user; nothing requires it),
save its ten single-use recovery codes, and see or sign out your signed-in devices. Setting it up or turning it off asks
for your password on the same form. Authenticator codes accept a 30-second clock skew and cannot be reused. Turning it on
or off signs out your other devices.

Upgrading from self-contained cookies requires a fresh sign-in; existing chosen passwords are preserved.

Remembered sessions have seven days of inactivity and a thirty-day absolute limit. Admin actions (changes only user or
settings managers can make, and viewing or changing a Runner's password) ask for your password again in a dialog when
you last entered it more than ten minutes ago; the action then continues.
Account forms use antiforgery tokens and reject absent, null or foreign Origins. Public cookies use `__Host-`, Secure,
HttpOnly and Path=/; local HTTP credentials are separate and cannot authenticate public endpoints.

Sign-in, password prompts, password changes and authenticator setup/removal share limits of
20 attempts per account, 30 per IP and 200 globally in a fixed
five-minute window, with at most four concurrent password checks. There are at most 4096 limiter entries, and no
fixed account lockout or sleeping requests. Excess requests receive 429; existing sessions continue working.
Distributed attacks can still interfere with new sign-ins; these controls do not promise immunity to denial of service.

Operator recovery uses `RESET_ADMIN_PASSWORD=true` as described above. Restrict access to startup logs and `/data`.

Dependency CI fails on high/critical NuGet advisories, including transitive dependencies, and audit-feed failures.
Vendored addons are compared with locked npm packages and audited. The container job scans OS/application packages.
Browser packages embedded inside NuGet bundles remain dependent on their upstream inventory and advisories; scans are
supporting evidence, not proof of authorization. See [the implementation plan](docs/internet-facing-plan.md) for
security tests and validation limitations.

### Data and backups

Back up the `/data` volume. It holds the SQLite database, the SSH key the Manager uses to log in to Runners, and the
keys that decrypt stored secrets (harness logins, GitHub tokens, Runner passwords, the Setup Profile). Losing the keys
means re-entering those secrets. The database uses write-ahead logging: recent changes can be in
`deveagentmanager.db-wal` next to it, so copy the whole folder (best with the container stopped), not only the `.db`
file. Keep `/data` on a local disk; SQLite's write-ahead log doesn't work on network shares.

## How it works

- **Providers** only create, start, stop and delete machines (`IRunnerProvider`). Proxmox and *Existing machine* are
  built in; Kubernetes, Azure or AWS can be added as another provider project.
- **Setup** runs over SSH: an idempotent script installs packages, Node.js, tmux (with extended keys, so Shift+Enter
  works in pi) and git config, then applies the Setup Profile and installs the harnesses. Output streams to the
  setup log. Run it again any time with **Re-run setup**.
- **Agents** run as `tmux` sessions named `dam-<name>` in `~/work/<username>/<name>`, with their owner's git identity,
  GitHub token and environment (from `~/.deveagentmanager/users/<id>/`, through `GIT_CONFIG_GLOBAL`). The harness is started with a fixed session
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
- **Security**: everything but the login page needs the login cookie (SameSite=Lax). Every API endpoint declares who
  may call it (a role, or access to the Runner, agent, conversation or harness login it names); other people's things
  answer 404, as if they didn't exist. Live updates only go to the people a change concerns, and a terminal, the live
  connection or VS Code closes when access to it ends. Requests that change something
  must carry the Portal's `X-DAM-Request` header (other sites can't send it without CORS, which the Manager doesn't
  allow) and, like the hub and terminal WebSockets, come from the Manager's own origin. The API never returns a secret:
  account logins, Runner passwords and GitHub tokens stay on the Manager. A login terminal only names the harness;
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
  nothing. The **screens** button in the terminal's toolbar (or the ⋮ menu when it has no room) shows what is
  attached and can disconnect a screen, which then says so and offers Reconnect.
- **Pasting** into a terminal uses your computer's clipboard. Ctrl+V (or Ctrl+Shift+V, Shift+Insert) pastes text, and
  also images such as screenshots and files copied in your file manager, such as a log. Files can be dropped on the
  terminal too, and images picked with the **📎 Image** key (on phones: gallery, camera, screenshots). They are stored
  on the Runner in `~/.deveagentmanager/uploads`, and their path is pasted into the terminal, like pi's own Ctrl+V does
  locally. The harness sends a PNG, JPEG, WebP or GIF image to the model; the agent reads other files. Up to 25 MB.
- **Copying** out of a terminal goes to your device's clipboard: select text with the mouse (tmux copies it when you
  let go), or use the harness's own copy, such as Claude Code's `/copy`. Programs copy with OSC 52, which the browser
  accepts only within 5 seconds of a key, tap or click in that terminal, so a program can't fill your clipboard by
  itself. Every copy shows a message. Requests to read your clipboard are never answered. Someone watching read-only
  doesn't get what others copy. When the browser refuses, the message has a **Copy** button. Up to 1 MB. Shift+drag
  selects in the browser instead (on a read-only terminal a plain drag does): a **Copy** button appears where the
  selection ends, and Ctrl+C (Cmd+C) copies it too.

The terminal’s **Select text** button opens a snapshot with saved history and native phone selection. **Copy all**,
**Copy screen**, and **Copy selection** are also available to read-only viewers; **Load earlier** reads more history.
On the conversation page, each text message has a copy button; **Copy last reply** is in the ⋮ menu. These copy
the original Markdown, including code blocks, without thinking text or tool output.

## Manager logs

**Manage → Manager logs** shows recent application logs, newest first, with severity and text filters,
automatic refresh, and a download of the displayed results. Access requires the **Log viewer** role; existing
admins receive it during upgrade. Grant it under **Users**.

The Manager keeps up to 5,000 entries in memory and two log files of about 5 MiB each in the data folder’s
`logs` directory, so recent logs survive restarts. Existing logging levels apply. Common credential fields are
masked; logs can still contain deployment details and user activity, so review downloads before sharing.
Runner setup logs and the audit log remain on their existing pages.

## Development

Requires the .NET 10 SDK, Node 22+ and npm.

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

Every push runs .NET tests, native AMD64 and ARM64 production-image builds, and security/browser checks in parallel.
After the checks pass, publication combines the two images under the release tags without rebuilding or using QEMU.
The GitHub release points to the tested commit. Newer pushes cancel older builds on the same branch.

Images at `ghcr.io/devedse/deveagentmanager` get `latest` and `1.0.<build>` from `master`; other branches get `beta`
and `beta_<branch>`, with application version `1.0.<build>-beta`. Non-Dependabot builds also update `beta_nightly`.
Security checks run within the build workflow on pushes, and independently on pull requests and the weekly schedule.
Portal browser tests and the real five-minute startup capture run in separate jobs so neither waits for the other.
The detailed native profiler probe is a separate manual workflow for runtime or profiler changes. ARM64 uses the
portable recorder by default; its experimental native engine remains opt-in.
No secrets are needed; the workflow uses the built-in `GITHUB_TOKEN`. Run it manually (`workflow_dispatch`) to pick
self-hosted .NET and AMD64 runners. ARM64 builds always run on native GitHub hardware.

### Browser sources and TypeScript

Application logic stays in C# where possible: update polling and scheduled-time conversion use .NET. Browser API and
DOM adapters are TypeScript in `DeveAgentManager/Browser`; Playwright tests use `.mts` and Node fixtures use `.cts`.
Vendored xterm and PDF.js assets remain unchanged upstream JavaScript.

Building now requires Node 22+ and npm alongside the .NET SDK. `dotnet build` installs the pinned root npm dependencies
when missing and compiles TypeScript before collecting browser assets. Run `npm ci --ignore-scripts` after dependency
changes; `npm run check` checks types, and `npm run build` generates the files. Compiler errors fail the build.
Generated browser scripts go to `DeveAgentManager/wwwroot/js`; tests and tools go under `.typescript`, for example
`node .typescript/DeveAgentManager.Tests/Browser/hold-to-confirm.mjs`. Neither output directory is committed. Docker
includes the compiler in its build stage only, so deployed containers still run without Node.
