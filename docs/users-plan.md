# Users, roles and sharing — plan

Status: **phases 0–4 implemented** (branch `feature/users`), phase 5 partly. Last updated 2026-10-06.

Before this, the Manager had a single login (`ADMIN_USERNAME` / `ADMIN_PASSWORD`) and every Runner was one Linux
account (`remoteman`) with one shared password. This plan adds users who each have their own logins, keys and settings,
runners that can be shared, and agents and conversations that can be shared.

| Part | Status |
|---|---|
| Unique runner passwords, SSH keys (section 8, phase 0) | Implemented |
| Users and login (phase 1) | Implemented |
| Per-user data and permissions (phase 2) | Implemented |
| Shared runners (phase 3) | Implemented |
| Sharing agents and conversations (phase 4) | Implemented |
| Hardening (phase 5) | Audit log page implemented; OIDC, quotas and the Proxmox firewall rule are for later |

## 1. Principles

- **One Linux account per Runner**, shared by everyone with access to that Runner. The Manager keeps users apart in
  the Portal and gives each agent its owner's identity and keys, but on the Runner itself everyone can reach
  everything: other users' agents, files, harness logins and GitHub tokens on it.
- **Sharing a Runner means trusting each other** with what's on it. The share dialog says so: "Everyone with access to
  this Runner can reach everything on it, including other users' agents, files, logins and GitHub tokens on it. Share
  it only with people you'd trust with your keys."
- **Your keys stay private from everyone you don't share a Runner with**, and nobody can get from one Runner to
  another (section 8).

## 2. Roles

Global roles. A user can have any combination.

| Role | Can |
|---|---|
| **Settings manager** | Manager settings: providers, default timezone, the Setup Profile (setup files and script) and the harnesses' shared settings |
| **User manager** | Create and disable users, reset passwords, grant roles |
| **Runner creator** | Create Runners (a Proxmox VM or an existing machine) and become their owner |
| **Runner manager** | Every Runner: see it, give people access, change its owner, view and set its password, delete it |
| **Conversation manager** | Manage every agent and conversation, including prompting, sharing and deletion |
| **Log viewer** | Read and download Manager application logs |
| **Harness login manager** | Administer every login and its grants; usage still needs an explicit grant |
| **Admin** | All of the above. Admin is only the collection; nothing is admin-only. |
| *(no role)* | Use the Runners they have access to; their own agents, conversations and settings |

- **Granting roles** needs User manager, and you can only grant roles you hold yourself. An admin can grant
  everything; someone who is only a user manager can create plain users. This keeps a user manager from creating an
  account with more rights than their own.
- A user without access to any Runner can't create agents; **New agent** says to ask a runner manager.

## 3. Runner access

| Level | Can |
|---|---|
| **Use** | Create and run their own agents, shell, VS Code, desktop, have their SSH key on the Runner |
| **Manage** | Everything in Use, plus start, stop, Re-run setup, size, timezone, **view and set the password**, the "keys only" switch, give others access |
| **Owner** | Manage, plus delete the Runner. One person; only a runner manager can change who it is. |

- The creator becomes the owner. A runner manager can create a Runner for someone and make them the owner (for
  example: create one for Alex, make her the owner, and she logs in to it with its password or her SSH key).
- Use-level people can't see the password in the Portal, but their agents run as the same account with sudo and
  could change it. That's tidiness, not a security boundary (section 1).
- **Removing someone's access** (or leaving) removes their agents on that Runner and deletes their harness logins,
  GitHub token, environment file and SSH key there. Their conversations stay theirs; their work folder stays on the
  Runner.
- **Other people's agents on a Runner** are listed (name and status) for whoever manages it (Manage, Owner, runner
  managers), who can't open them. Use-level people only see their own agents and the ones shared with them.

## 4. Sharing agents and conversations

An agent's owner can share it with other users, **read-only** or **read-write**, and change or remove that at any time.

| Level | Can |
|---|---|
| **Read-only** | Read its conversations and shared images, download its shared files, export them, watch the live terminal (the server drops any input) |
| **Read-write** | Also type in the terminal and prompt the agent |

- A read-write user prompts **with the harness login the agent is using**, which can be the owner's. That's accepted:
  people who share an agent know each other.
- **Switching the login**: anyone with read-write access can switch the agent to *their own* harness login, never to
  someone else's. (If Alex switches it to hers, the owner then prompts with Alex's.)
- Conversations can be shared read-only on their own. Anyone who can read someone else's conversation can **continue a
  copy** of it in a new agent of their own, on a Runner they can use, with their own login. Everyone an agent is shared
  with can read its workspace's conversations.
- **Git identity**: an agent commits and pushes with one person's git name, email, GitHub token and environment
  variables, whoever types the prompt. It starts as the owner's and only an explicit switch (Git identity in the
  agent's menu) changes it; starting or restarting never does. Someone with read-write access can switch it to
  themselves, whoever it is can hand it back to the owner (read access is enough), and a conversation manager can
  choose any enabled user. Switching restarts a running harness, which resumes the same conversation. The agent list
  and terminal show whose it is; every switch is in the audit log.
- When that person loses it (their share ends, or they are disabled or deleted), the agent goes back to the owner's
  identity and a harness still running with theirs is stopped, like after a revoked login. Their files stay on the
  Runner only while an agent there uses them; they never get an SSH key on it.
- Reading an agent needs no access to its Runner, and neither does read-write: it covers the agent (its terminal,
  prompts, sessions, login and git identity), not the Runner's shell, VS Code or desktop, which still need Use access.
  Prompting an agent runs commands on its Runner, so share read-write only with people you trust there. A Runner's
  owner and managers see these people under **Access through agents** on the Runner page: everyone without access to
  the Runner whom an agent on it is shared with, per agent and level, read-write first, with a help tip on the risks.
- Conversation manager (included in Admin) grants full access to all agents and conversations. Other roles do not.
- Existing admins with all four original roles are upgraded once and their security stamps rotate; sign in again.

## 5. Settings: per user, per Runner, shared

| Where | What |
|---|---|
| **My settings** (each user) | Git name and email, environment variables, GitHub token, harness logins, SSH keys, theme |
| **Per Runner** (Manage) | Name, size, harnesses and tools to install, timezone, desktop, password, keys only |
| **Manager settings** (Settings manager) | Providers, templates, default timezone, and the rest of the Setup Profile: setup files, setup script, harness packages, default model and effort, and settings |

The Setup Profile was split on migration: git identity, environment variables and the GitHub token became the first
user's My settings; the rest stays shared. My settings are only set in the Portal: `GITHUB_PAT`, `GIT_USER_NAME` and
`GIT_USER_EMAIL` are gone from the compose file.

## 6. On the Runner (one shared account)

- Each user with access gets a private folder, `~/.deveagentmanager/users/<id>/`, with:
  - an environment file: their environment variables and `GH_TOKEN` for `gh`;
  - their own git settings (used through `GIT_CONFIG_GLOBAL`): they include the shared `~/.gitconfig` and add the
    user's name, email and a credentials file with their token, so commits carry their name and pushes use their token.
- Harness credentials have one folder per login. Agents use separate runtime profiles for session lists and local mutable state; only the selected authorized session is bootstrapped from legacy files.
- **Agent launch** loads the base environment, then the owner's environment file, then the harness login's. The
  default model and effort stay shared (Manager settings).
- **Work folders**: new agents default to `~/work/<username>/…`, so two users cloning the same repository don't collide.
- **Shell** loads the environment of whoever opens it, so git in the shell uses their identity and token.
- **SSH keys** of everyone with access are in `~/.ssh/authorized_keys`, next to the Manager's key.
- **Known limits**: VS Code is one instance per Runner; it starts with the Runner owner's environment, so its built-in
  git commits and pushes as the owner. Claude Code and
  pi settings (`settings.json`, `CLAUDE.md`, keybindings, pi packages) are shared per Runner; per-user versions are
  possible later through each harness's config folder setting.

## 7. Data model

- **User**: username, display name, password hash, security stamp, roles, disabled, must change password, created,
  last login.
- **UserSetting** (user, key, value): git identity, environment variables, GitHub token (encrypted), default models,
  SSH keys, preferences.
- **OwnerId** on Account (harness login), Runner, Agent and StoredSession.
- **RunnerMember** (Runner, user, Use or Manage, granted by, when).
- **AgentShare** (agent, user, read-only or read-write, granted by, when); **SessionShare** for stored conversations.
- **Runner**: its password (encrypted, already `ProtectedPassword`) and "keys only".
- **AuditEvent** (who, what, target, when): logins, role changes, shares, password views and changes, deletions.

**Migration**: `ADMIN_USERNAME` / `ADMIN_PASSWORD` create the first user, with all roles. Everything that exists
becomes theirs, including the GitHub token and git identity. `RESET_ADMIN_PASSWORD=true` resets that user's password
from `ADMIN_PASSWORD` at start-up, as a way back in.

## 8. Runner passwords and SSH (phase 0, can be done before users)

The problem today: every VM gets the same account and password (`RUNNER_PASSWORD`, default `remoteman`), SSH accepts
passwords, and the account has sudo. So a shell or agent on one Runner can log in to every other Runner as root. (The
Manager's private key never leaves the Manager, and VS Code is only reachable through an SSH tunnel to the Runner's
loopback, so neither is a way in.)

- **A random password per Runner**, generated when the Manager creates the VM and stored encrypted. Re-run setup
  replaces `remoteman` on existing Runners. `RUNNER_PASSWORD` goes away.
- **The Runner page shows the password** (show, copy, change) to whoever manages the Runner (Manage access, its owner
  and runner managers). Every view and change is in the audit log.
- **Password SSH stays allowed**: each password only opens its own Runner. A per-Runner **keys only** switch turns it off.
- **SSH keys** (My settings → SSH keys) go into a managed part of `authorized_keys` on the Runners their owner can use,
  and are removed when access ends.
- Existing machines added with their own password keep it; the Manager doesn't change SSH settings there unless asked.
- Optional later: a Proxmox firewall rule on Runner VMs that only lets SSH in from the Manager, so Runners can't reach
  each other at all. Needs the Proxmox firewall switched on at datacenter level.

## 9. Manager changes

- **Login**: ASP.NET Core Identity password hashing and bounded account/IP/global/concurrent throttles; a security stamp, so
  disabling a user or changing a password signs them out everywhere; a page to change your own password; a password
  reset forces a change at the next login. Single sign-on (OIDC, e.g. Authentik or Keycloak) is optional later.
- **One access service** with every check (can see / use / manage a Runner, can read / write an agent, can see a
  conversation, has role). Used by all API endpoints (~80), the terminal socket (agents, shells, logins), the VS Code
  proxy, desktop frames and snapshots, shared images and files, and session export. Deleting a shared file takes
  ownership of the conversation.
- **Lists show only what you may see**, and other users' things answer 404 (not 403), so IDs don't reveal what exists.
- **Live updates**: today every change goes to every browser (`Clients.All`). Each event goes only to the users who can
  see that Runner, agent or conversation; following a setup log checks access.
- **Background work** (status monitor, session sync, automatic continuation, image collection) keeps running as the
  system over the one SSH account; only agent launch gains the per-user environment.
- **Runner setup** writes each user's private folder and SSH key, and refreshes it when their settings or access change.
- **Notifications** are per user: their own agents and agents shared with them.

## 10. Portal changes

- **Users** page (user managers): create, disable, reset password, roles, delete (what they owned goes to someone
  else, with their Runner access). **Audit log** page (user managers).
- **Runner** page: owner, people with access (Use / Manage) with add and remove, the share dialog with the trust
  note, the password row and keys only.
- **Agent**: a Share button (read-only / read-write); the login switcher only offers your own logins.
- **Sessions**: Mine and Shared with me; continue a copy.
- **Settings** splits into My settings and Manager settings.
- Buttons you lack the rights for are hidden; empty states explain what to ask for.
- A user menu in the header: change password, log out. New users, and users whose password a user manager set, choose
  their own at their next sign-in (`/password`) before anything else works.

## 11. Phases

Sizes are relative.

0. **Runner passwords and SSH keys** (small): section 8.
1. **Users and login** (medium): users, roles, the Users page, migration, owners. Nothing changes for the admin.
2. **Per-user data** (large): harness logins, GitHub token, git identity, environment and SSH keys per user, given to
   agents at launch; permission checks on every endpoint; live updates per user.
3. **Shared runners** (medium): Use and Manage access, the share dialog, per-user folders on Runners, work folders,
   cleanup when access is removed.
4. **Sharing agents and conversations** (medium): read-only and read-write, the login switching rule, Shared with me,
   Continue in my agent.
5. **Hardening** (small to medium): an audit log page, optional OIDC, optional quotas, optional Proxmox firewall.

Phases 1–2 are useful on their own: several people, each on their own Runners.

## 12. Tests

- **Endpoint sweep**: every API endpoint declares its access (an endpoint without one answers 500), and a second user
  gets 404 on every endpoint for the first user's things (403 where a role is missing, the same for any id). Generated
  from the route table, so a new endpoint can't be forgotten. Implemented in `UserAccessTests`.
- Live updates, terminals (read-only drops input), VS Code and desktop are tested for who can reach them.
- Role granting: you can't grant a role you don't hold.
- On a real Runner: two users' agents on one Runner commit and push with their own names and tokens; removing a user
  deletes their logins, token and key there and stops their agents; a Runner's password doesn't open another Runner.
- Upgrading a copy of the current database.

## 13. Open questions

1. **Shared harness logins**: implemented through Use/Manage grants and the Harness login manager role; see [the design](harness-login-sharing-plan.md).
2. **Single sign-on**: OIDC instead of passwords, or alongside them?
3. **Quotas**: a limit on Runners or cores per runner creator?

## Decision log

- 2026-10-06 — First plan: roles, owners, sharing, Linux account per user.
- 2026-10-06 — One shared Linux account per Runner instead; sharing a Runner means trusting each other.
- 2026-10-06 — Agents can be shared read-only or read-write, changeable at any time. Read-write prompts with the
  agent's current login; anyone with read-write can switch it to their own login, never someone else's.
- 2026-10-06 — Admin is only the collection of roles; added Settings manager; you can only grant roles you hold.
- 2026-10-06 — Runner access has Use and Manage levels and one owner. Manage can view and set the Runner's password.
- 2026-10-06 — Unique random password per Runner instead of the shared `remoteman`; password SSH stays allowed with a
  per-Runner keys only switch; per-user SSH keys.
- 2026-10-06 — Implementation decisions: the default model and effort stay shared; VS Code runs with the Runner
  owner's identity; removing someone's Runner access removes their agents there; whoever manages a Runner sees other
  people's agents on it listed but can't open them, Use-level people don't see them; an agent's "needs attention"
  state is shared by everyone who sees it; deleting a user gives what they owned, and their Runner access, to a user
  of your choice; a role check comes before the check on the thing itself.
- 2026-10-07 — `GITHUB_PAT`, `GIT_USER_NAME` and `GIT_USER_EMAIL` are removed from the compose file; the admin
  enters their token and git identity under My settings like everyone else.

- 2026-10-07 — Internet-facing implementation adds revocable sign-in sessions, TOTP/recovery, recent authentication,
  strict account-form protection and wildcard per-Runner editor origins on the Portal port. Fixed account lockouts
  are replaced by bounded throttles; direct local IP access uses separate credentials. Deployment defaults and
  validation are maintained in [internet-facing-plan.md](internet-facing-plan.md).

- 2026-10-07 — Added Conversation manager to Admin: global agent/session management, including imported sessions
  and live updates. Harness credentials remain private.
- 2026-10-12 — Deleting a user removes their folder and the copies of their harness logins from every ready Runner
  at once, not only the Runners they owned or were a member of (their agents can run on any Runner, e.g. as an admin).
  Runners that were offline lose them in the 10-minute login sweep, which now also removes folders of deleted users.
  Their tokens aren't revoked at the provider.

- 2026-10-15 — Harness logins are independent resources with Use/Manage grants. Existing owners migrate to Manage;
  defaults move to per-user preferences. Login managers administer all logins without implicit usage or conversation
  access. Removing usage durably blocks affected agents and queues offline enforcement. User deletion preserves shared
  logins and requires resolving last-manager dependencies. This supersedes the earlier ownership/deletion behavior.
