# DeveAgentManager — Plan

A self-hosted web portal (one `docker compose up`) that creates **Proxmox VMs to run coding agents on**, lets you open them in the browser, and keeps all your agent **sessions** in one place, so you can pick up any session in **pi**, **Claude Code** or **Codex**. Besides interactive work, it can also run **autonomous agents (Jobs)** that you start through an API.

This document covers what it does, how it's set up and how it should look. Technical implementation comes later.

---

## 1. Terms

Use these names everywhere: code, UI, docs.

| Term | What it is |
|---|---|
| **Manager** | The DeveAgentManager app running in Docker. It hosts the web portal, talks to Proxmox, stores sessions and secrets, and proxies all terminal traffic. |
| **Portal** | The web UI served by the Manager. |
| **Runner** | A Proxmox VM created and owned by the Manager. Agents run here. One Runner can host many agents at once. |
| **Runner Template** | A Proxmox template that Runners are cloned from. There are two kinds: **Headless** (e.g. `10100 cloudimage-debian13`) and **Desktop** (e.g. `10120 cloudimage-debian13-desktop`). |
| **Runner Account** | The Linux user the Manager uses on every Runner (default username `remoteman`, random password per Runner). |
| **Project** | Something you want agents to work on: a GitHub repo or a plain folder. |
| **Workspace** | A checkout of a Project on a specific Runner. |
| **Harness** | An agent CLI: `pi`, `claude` (Claude Code), `codex`. |
| **Session** | One agent conversation, stored by the Manager in a single harness-neutral format (**DeveSession format**). A Session belongs to one Project. |
| **Live Session** | A Session that currently has a Harness running for it on a Runner. |
| **Terminal** | The browser view of a Live Session, or a plain shell on a Runner. The browser only talks to the Manager, and the Manager talks SSH to the Runner. |
| **Job** | An autonomous agent run: a prompt handed to a Harness that works on its own, with nobody at the keyboard, until it's done. It is started through the **Jobs API** (or the Portal), and every Job produces a normal Session. |
| **API Key** | A named, revocable key (e.g. "n8n") that allows a script or tool to call the Jobs API. |

In short: the **Manager** creates **Runners** from **Runner Templates**. A **Project** gets a **Workspace** on a Runner. A **Session** is run by a **Harness** in that Workspace, and you watch it through a **Terminal** in the **Portal**. A **Job** is a Session that runs without you.

---

## 2. Goals and non-goals

**Goals (v1)**
- One compose file. Point it at Proxmox, open the Portal, and you're up.
- Create, start, stop and delete Runners from the Portal (Linux only).
- A browser terminal to any Runner or Live Session, proxied through the Manager. Runners never need to be reachable from your browser.
- Projects from GitHub (using a PAT) or from a folder.
- Start a new Session or resume an existing one in any of the three Harnesses.
- Live Sessions keep running when you close the browser, and you can re-open them later.
- Sessions are stored centrally and can be imported and exported between Harnesses.
- Autonomous Jobs started through an HTTP API, with results (summary, diff, branch/PR) and the ability to take over a Job interactively.

**Non-goals (v1)**
- Windows Runners.
- Multiple users or teams. v1 has one owner, with a login.
- A built-in scheduler or triggers (cron, GitHub events). Anything that can make an HTTP call, such as n8n, starts Jobs through the API.
- Auto-scaling: the Manager doesn't create Runners by itself to handle load.
- Replacing the Harnesses' own UIs. We run the real CLIs.

---

## 3. Setup

### 3.1 Proxmox: create a credential for the Manager

The Manager uses a **Proxmox API token** with a dedicated role. It only gets rights inside a resource pool it owns, plus clone rights on the templates. Run these on a Proxmox node shell:

```bash
# 1. A pool that holds every Runner (the Manager can only touch VMs in here)
pveum pool add deveagents --comment "DeveAgentManager runners"

# 2. A role with just what the Manager needs
pveum role add DeveAgentManager -privs "\
VM.Allocate VM.Clone VM.Audit VM.PowerMgmt VM.Console \
VM.Config.CPU VM.Config.Memory VM.Config.Disk VM.Config.Network \
VM.Config.Options VM.Config.Cloudinit VM.GuestAgent.Audit \
Datastore.AllocateSpace Datastore.Audit SDN.Use Sys.Audit Pool.Audit"
#   On Proxmox 8.x replace VM.GuestAgent.Audit with VM.Monitor
#   (needed to read the Runner's IP from the guest agent).

# 3. A user and an API token
pveum user add deveagentmanager@pve --comment "DeveAgentManager"
pveum user token add deveagentmanager@pve manager --privsep 0
#   -> copy the token secret now; it is shown only once.

# 4. Grant the role where it's needed
pveum acl modify /pool/deveagents          -user deveagentmanager@pve -role DeveAgentManager
pveum acl modify /vms/10100                -user deveagentmanager@pve -role DeveAgentManager   # headless template
pveum acl modify /vms/10120                -user deveagentmanager@pve -role DeveAgentManager   # desktop template
pveum acl modify /storage/example-storage -user deveagentmanager@pve -role DeveAgentManager   # disk target
pveum acl modify /sdn/zones/localnetwork   -user deveagentmanager@pve -role DeveAgentManager   # network
```

The same steps can also be done in the Proxmox UI under Datacenter → Permissions (Pools, Roles, Users, API Tokens, then Add → User/API Token Permission).

**What the Runner Templates need** (a one-time check on 10100 and 10120):
- A cloud-init drive. The Manager sets the user, password, SSH key and hostname through it.
- `qemu-guest-agent` installed and the "QEMU Guest Agent" option enabled. The Manager reads the Runner's IP from it.
- DHCP on the network (or a static IP range configured in the Manager, see open questions).

The Manager checks all of this on its **Settings → Proxmox → Test connection** page and shows exactly which check failed.

### 3.2 The compose file

A single file. Everything below is config, with no code changes needed:

```yaml
services:
  deveagentmanager:
    image: ghcr.io/devedse/deveagentmanager:latest
    ports: ["8080:8080"]
    volumes:
      - ./data:/data                 # DB, sessions, secrets, SSH keys
    environment:
      # Portal login
      ADMIN_USERNAME: admin
      ADMIN_PASSWORD: ${ADMIN_PASSWORD}  # first-start password from a private environment file

      # Proxmox
      PROXMOX_URL: https://proxmox.example.com:8006
      PROXMOX_TOKEN_ID: deveagentmanager@pve!manager
      PROXMOX_TOKEN_SECRET: ${PROXMOX_TOKEN_SECRET}
      PROXMOX_VERIFY_TLS: "false"
      PROXMOX_NODE: example-node
      PROXMOX_POOL: deveagents
      PROXMOX_STORAGE: example-storage
      PROXMOX_BRIDGE: vmbr0
      PROXMOX_VMID_RANGE: 30000-30999   # Runners only get IDs from this range

      # Runner Templates
      TEMPLATE_HEADLESS_VMID: 10100
      TEMPLATE_DESKTOP_VMID: 10120

      # Runner Account (injected via cloud-init, with a random password per Runner)
      RUNNER_USERNAME: remoteman
    restart: unless-stopped
```

Notes:
- The Manager creates its own SSH keypair on first start and injects the public key into every Runner. The password stays as a fallback and for desktop login. After setup, SSH login uses the key.
- Put the Portal behind the existing reverse proxy (`vm-reverseproxy`) for HTTPS. Terminals use websockets, so the proxy must allow them.
- Anything set in compose shows as read-only in Settings. Anything not set can be edited in the Portal.

---

## 4. How it looks

The layout is a left sidebar with the main content on the right. It has a dark theme, similar in style to the Proxmox tree in your screenshot.

```
┌──────────────┬──────────────────────────────────────────────────────┐
│ DeveAgentMgr │  Projects › DeveMazeGeneratorCore                    │
│              │                                                      │
│ ▸ Projects   │  [ + New session ▾ ]   harness: (pi) (claude) (codex)│
│   Deve Maze… │                                                      │
│   ...        │  LIVE                                                │
│ ▸ Runners    │  ● fix-maze-perf    claude  runner-01   12m  [Open]  │
│   runner-01● │  ● add-tests        pi      runner-01    3m  [Open]  │
│   runner-02○ │                                                      │
│ ▸ Sessions   │  SESSIONS                                            │
│ ▸ Jobs  (2)  │    refactor-core    codex   2d ago  [Resume in ▾]    │
│ ▸ Settings   │    readme-update    pi      5d ago  [Resume in ▾]    │
│              │  ⚙ fix-issue-42     claude  job ✓   1h ago  [PR #43] │
└──────────────┴──────────────────────────────────────────────────────┘
```

### Screens

1. **Dashboard**: Runner status (running/stopped, CPU/RAM), current Live Sessions, running and queued Jobs, and recent Sessions.
2. **Runners**
   - A list with name, VMID, kind (Headless/Desktop), state, IP, number of Live Sessions, and actions (Start / Stop / Shell / Desktop / Delete).
   - **New Runner** dialog: name, kind (Headless/Desktop), CPU, RAM, disk size, which Harnesses to install (all three by default), and what the Runner accepts: **Sessions**, **Jobs** or **both** (default both).
   - Runner detail: the setup log with live bootstrap output, installed Harness versions, Workspaces on it, and Live Sessions on it.
3. **Projects**
   - A list of Projects.
   - **Add Project**: *From GitHub* (pick from your repos via the PAT, or paste a URL like `github.com/example/example-project`) or *Folder* (an empty folder, or an existing path on a Runner).
   - Project detail (shown above): Live Sessions at the top, then past Sessions (those started by a Job get a ⚙ badge), plus a **Jobs** tab and a Workspaces tab showing which Runners have a checkout.
4. **Session detail**: the conversation rendered read-only (messages, tool calls, files touched), which Harnesses it has run in, its branch, and buttons for **Resume in pi / claude / codex**, **Fork**, **Export**, **Rename**, **Delete**.
5. **Terminal**: full-screen browser terminal. A top bar shows the Project, Session, Harness and Runner, plus **Detach** (leave it running), **Stop**, and **Open shell here** (a plain shell in the same Workspace). Tabs let you keep several Terminals open.
6. **Desktop** (Desktop Runners only): a browser view of the Runner's desktop, proxied by the Manager.
7. **Jobs**: see 6.5.
8. **Settings**: Proxmox (with Test connection), Runner Templates, Runner Account, GitHub PAT, Harness credentials, Portal login, **API Keys**, **Job defaults** (Harness, limits, "when done" action).

---

## 5. Main flows

### 5.1 Create a Runner
1. Runners → **New Runner** → pick Headless or Desktop, set the size, then Create.
2. The Manager clones the template into the pool with a free VMID from the range, sets cloud-init (Runner Account, SSH key, hostname) and starts the VM.
3. It waits for the guest agent to report an IP, then connects over SSH.
4. **Bootstrap** (shown live in the Runner's setup log): update packages, install git, tmux and Node, install the selected Harnesses, configure git identity and GitHub credentials, and push Harness credentials.
5. The Runner shows as **Ready**.

Bootstrap can be re-run at any time (**Runner → Re-provision**), for example to update Harnesses.

### 5.2 Add a Project
1. Projects → **Add Project** → From GitHub → pick `devedse/DeveMazeGeneratorCore`.
2. The Project is saved. No Runner is involved yet. A Workspace is created on a Runner the first time a Session needs one.

### 5.3 Start a new Session
1. Open the Project and click **New session**, then pick a **Harness** (and a Runner if there's more than one Ready; otherwise the default is used).
2. If the Runner has no Workspace for this Project yet, the Manager clones the repo.
3. Each Session gets its **own working copy** (a git worktree on branch `agent/<session-name>`), so several agents on the same Runner and Project don't step on each other.
4. The Harness starts inside a persistent terminal session on the Runner, and the Portal opens the **Terminal**.

### 5.4 Resume a Session (in any Harness)
1. Project → click a Session → **Resume in ▾** → choose `pi`, `claude` or `codex`.
2. The Manager converts the stored Session into that Harness's own format, places it on the Runner in the Session's working copy, and starts the Harness with that session loaded.
3. The Terminal opens and the agent continues with the full history.
4. **Resuming on a different Runner** (the old one is stopped, deleted or full): the conversation always moves along. The code only moves if the Session's branch was pushed. The Manager checks out that branch on the new Runner. If there are unpushed changes on a Runner it can't reach, it warns you before resuming ("code changes since <time> are not available").

### 5.5 Re-open a Live Session
- Every Live Session has an **Open** button (in the Project, on the Dashboard and on the Runner page). It re-attaches to the same running process, with scrollback.
- Closing the browser tab or clicking **Detach** leaves the agent running.
- The same Live Session can be open in two browser tabs. Both show the same screen.
- Restarting or updating the Manager container does not kill agents. They keep running on the Runners, and the Manager picks them up again when it starts.

### 5.6 Session sync (how the central store stays up to date)
- While a Session is Live, the Manager regularly pulls the Harness's native session file from the Runner and updates the stored Session. It does this again when the Harness exits.
- Because of this, the Session detail page shows the conversation even while it's running, and the conversation survives a Runner being deleted. The code in its working copy only survives if the branch was pushed (see 5.4).
- **One Live Session per Session.** A Session can only run in one Harness at a time. "Resume in codex" on a Session that is Live in claude gives you three choices: *Stop claude and switch*, *Fork into a new Session*, or *Cancel*.

### 5.7 Import / export
- **Import**: upload a native session file (pi, Claude Code or Codex), or run **Scan Runner** to find sessions that were started by hand on a Runner. Either way, pick which Project it belongs to.
- **Export**: download a Session as a DeveSession file or in any Harness's native format.

### 5.8 Stop and clean up
- **Stop** a Live Session: the Harness is closed, a final sync runs, and the Session stays in the list.
- **Delete Runner**: the Manager warns you about any Live Sessions and unpushed branches, offers to push them, then destroys the VM. Sessions are kept because they live in the Manager.
- **Stop Runner** (powered off) is allowed. Its Sessions are still browsable and resumable, either on another Runner (same rules as 5.4) or when it is started again.

---

## 6. Autonomous Jobs (API)

Besides working with an agent yourself, you can hand the Manager a task and let an agent finish it on its own. Scripts and tools start these through the **Jobs API**, and the Portal has the same form for starting one by hand.

A Job reuses everything else: the same Projects, Runners and Harnesses, its own working copy and branch (as in 5.3), and the result is a **normal Session** that you can read, export, resume or continue in any Harness.

### 6.1 Starting a Job

Required:
- **Project**: an existing Project, or a GitHub URL (unknown repos are added as a Project automatically).
- **Prompt**: the task.

Optional:
- **Harness**: pi / claude / codex (default from Settings).
- **Model**.
- **Continue from Session**: run the prompt on top of an existing Session instead of starting fresh. This is also how you send a follow-up to a finished Job.
- **Base branch** (default: the repo's default branch) and **branch name** (default `agent/job-<id>`).
- **Runner**: a specific Runner, or "any Runner that accepts Jobs" (default).
- **Limits**: max duration (always set, with a default from Settings), plus max turns or cost where the Harness supports it.
- **When done**: *nothing* / *push branch* (default) / *push branch and open a draft PR*.
- **Webhook URL**: the Manager calls it whenever the Job's status changes.
- **Metadata**: free key/values that come back unchanged in every response and webhook (e.g. an issue number).

Example:

```http
POST /api/jobs
Authorization: Bearer dam_xxxxxxxxxxxx

{
  "project": "github.com/example/example-project",
  "harness": "claude",
  "prompt": "Fix issue #42 and add a test for it.",
  "whenDone": "pull-request",
  "webhook": "https://automation.example.com/webhook/agent-done",
  "metadata": { "issue": 42 }
}

→ 202 { "jobId": "j_7f3a", "status": "queued", "url": "https://agents.example.com/jobs/j_7f3a" }
```

The other API actions are: **list** Jobs (filter by Project, status or metadata), **get** a Job's status and result, **follow** its live output as a stream, and **cancel** it. In v1 the API covers Jobs (plus listing Projects) only. Runners and interactive Sessions stay Portal-only until there's a need to expose them.

### 6.2 Lifecycle

`Queued → Preparing → Running → Finishing → Succeeded | Failed | Cancelled | Timed out`

- **Preparing** picks a Runner and creates the working copy. **Finishing** runs the final session sync, then the push or PR.
- **Queue**: each Runner has a *max concurrent Jobs* setting (default 2). When every Runner is full, Jobs wait their turn. Interactive Sessions are never blocked by Jobs and don't count toward the limit.
- **Succeeded** means the agent finished without an error. It does *not* mean the work is correct, so review the diff or PR.
- Jobs keep running when the Manager restarts, the same as Live Sessions (5.5).

### 6.3 What you get back

Through the API, the webhook and the Job page:
- Status, timings, Runner, Harness and model.
- The agent's **final message** (its own summary of what it did).
- **Changes**: changed files with diff stats, commits, branch, and the PR link if one was opened.
- The **Session**: the full conversation, exportable in any format (5.7).
- Tokens and cost, when the Harness reports them.
- The failure reason for failed Jobs (error, timeout, limit hit, cancelled).

### 6.4 Take over

If a Job gets stuck or goes the wrong way, click **Take over**. The autonomous run stops and the Job's Session opens interactively in a Terminal, in whichever Harness you pick (the same as 5.4). From then on it's a normal Session.

### 6.5 Jobs in the Portal

- **Jobs** page in the sidebar: a list with status, Project, Harness, Runner, start time and duration, filterable. The badge shows the number of running Jobs.
- **Job detail**: the prompt, **live output** (read-only), a result panel (summary, diff, PR link) and the buttons **Cancel**, **Take over**, **Re-run** (same input, new branch) and **Open Session**.
- **New Job** button on the Jobs page and the Project page: the same fields as the API, for starting a Job by hand without writing a script.

### 6.6 Safety

Jobs run with the Harness's **approval prompts turned off**, because nobody is there to click "allow". So:
- Jobs work on their own branch, and the Manager only pushes that branch. The agent could still run `git push` itself, though, so **protect the default branch in GitHub** and preferably use a **fine-grained PAT** limited to the repos you want agents on.
- **Limits are always on.** Every Job has a max duration, and the defaults live in Settings.
- **Isolation**: a Job can see everything on its Runner, including other Workspaces. If that matters, keep one or more Runners set to *Jobs only* and the rest to *Sessions only*.
- **API Keys** are created in Settings → API Keys, shown once, named ("n8n", "github-action") and revocable. Every API call needs one.

### 6.7 Who calls the API

The Manager doesn't need to know. Anything that can make an HTTP call can start Jobs. Some examples:
- **n8n** (already running on `vm-n8n`): schedules ("every night, update dependencies"), "GitHub issue labeled `agent` → start Job → comment the PR link", chat bots.
- A **GitHub Action** or any CI pipeline.
- `curl` from your own machine.

---

## 7. Sessions across Harnesses: what to expect

Moving a Session between Harnesses **keeps the conversation** but may not keep everything:

| Carries over | May be lost or simplified |
|---|---|
| User and assistant messages | Hidden reasoning/"thinking" (it's vendor-specific and often encrypted) |
| Tool calls and their results (mapped to the closest equivalent, otherwise kept as text) | Harness-specific extras (plan mode state, custom tool types, token/cost stats) |
| Files touched, working directory, branch | Model choice (the new Harness uses its own default unless you pick one) |

The Session detail page shows a **history strip** so it's clear what happened, for example `pi → claude → codex`, with a note at every switch.

**Branches inside a Session**: pi keeps a conversation as a tree (`/tree`, `/fork`), while Claude Code and Codex effectively continue in a straight line. The DeveSession keeps the full tree. When you resume in claude or codex, only the currently active branch is sent. The other branches stay in the Manager and are still visible in the Session view.

The stored DeveSession is always the source of truth. Harness-native files are just temporary copies that get converted to and from it.

---

## 8. Secrets and credentials

The Manager stores all of these encrypted in `/data`:
- **Proxmox token**: from compose.
- **GitHub PAT**: used to list repos, clone, and push agent branches. It is pushed to Runners as a git credential.
- **Harness credentials**: an API key per Harness, **or** a "login on Runner" option that opens a Terminal where you do the Harness's own login once (for Claude/ChatGPT subscriptions). After that, the Manager copies the login to new Runners.
- **Runner Account password and SSH key**.
- **API Keys**: only a hash is stored. The key itself is shown once, when it's created.

The Portal requires a login (`ADMIN_USERNAME` / `ADMIN_PASSWORD`). Nothing is reachable without it, and that includes terminals. The API only accepts API Keys.

---

## 9. Milestones

1. **M1: Runners.** Compose, Portal login, Proxmox settings and test, create/start/stop/delete a Runner, bootstrap log, browser shell to a Runner.
2. **M2: Projects and Live Sessions.** GitHub/folder Projects, Workspaces, a working copy per Session, start a Harness in a persistent Terminal, detach and re-open.
3. **M3: Central Sessions.** Sync native sessions into the DeveSession format, a read-only Session view, resume in the *same* Harness.
4. **M4: Cross-Harness.** Convert between pi, Claude Code and Codex, "Resume in ▾", fork, import/export, Scan Runner.
5. **M5: Jobs.** API Keys, the Jobs API (start/list/get/follow/cancel), queue and limits, when-done push/PR, webhooks, Jobs page, Take over.
6. **M6: Nice to have.** Desktop view, Runner resource graphs, pushing a branch and opening a PR from the Session page, notifications when an agent is waiting for input.

---

## 10. Open questions

1. **IP assignment**: is DHCP on `localnetwork` fine, or should the Manager hand out static IPs from a range through cloud-init?
2. **Runner selection**: always ask which Runner, or use a "default Runner" and only ask when there are several?
3. **Branch per Session**: OK as the default? Should the Manager auto-push `agent/*` branches on stop, or only when you click?
4. **Folder Projects**: should a folder live on one Runner only, or should the Manager also keep a copy so it can move between Runners?
5. **Harness auth**: are API keys or subscription logins the main case? This decides how much of the "login once, copy to Runners" flow is needed for v1.
6. **Runner lifecycle**: should idle Runners auto-stop after X hours?
7. **Desktop Runners**: what are they for? Should the agent itself use the desktop (browser testing), or is it just for you to look at?
8. **Jobs before cross-Harness?** Jobs only need M1–M3. If Jobs matter more to you than switching Harnesses, M5 can come before M4.
9. **Throw-away Runner per Job**: clone a fresh VM for each Job and delete it afterwards, for full isolation? It's slower (clone plus bootstrap, unless the templates come with the Harnesses pre-installed), so it would be optional.
10. **Jobs asking questions**: should a Job always run to the end (you take over if needed), or should it be able to pause with a question and notify you, so you can answer in the Portal or through the API?
