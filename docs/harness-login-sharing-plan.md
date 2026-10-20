# Harness login sharing and administration

The agent switcher lists enabled logins with Use access, including shared logins.
Logins visible only through global administration stay on the Harness logins page.
A usable login remains visible when its harness needs installing on the runner.

Status: **implemented; deployed verification pending**. Updated 2026-10-15.

## Goal and previous behavior

Let people share a saved harness subscription login or API key, manage access from its row in My settings,
and administer logins through a dedicated page and role. Sharing a credential does not share conversations.

Before this change an `Account` was a harness login, owned by one Portal `User`. `Account.OwnerId` gates listing, management
and selection. `Account.IsDefault` applies to its owner. Deleting that user cascades to their logins and
cleans their Runner copies. A person with write access to a shared agent can already prompt it with the
login it runs with. They cannot select that login for another agent merely because that agent was shared.

Relevant code: `AccountService`, `AccessControl.OwnsAccountAsync`, `AccountEndpoints`, `AccountsPanel`,
`AccountLoginDialog`, `TerminalSocketEndpoint`, `LoginWatcher`, `AgentService`, `UserService`, and Runner cleanup.

## Recommendation: permissions without a permanent owner

| Model | Advantages | Costs |
|---|---|---|
| Owner plus sharing | Smaller change; one person is ultimately responsible | Owner transfer, special owner privileges and deletion rules remain; defaults still need redesign |
| Per-login permissions, no owner | Personal and team logins use the same model; multiple managers; independent lifetime | Must explicitly handle the last manager, user removal and administrative recovery |

Recommend the second model. A login is an independent resource. Connecting it grants the creator **Manage**;
it is private until someone grants access. Record who created it for audit, without making that person its
permanent owner or tying its deletion to their user record. Keep `Account` as the internal name for now;
call it **Harness login** in the interface to distinguish it from a Portal user.

Use the same access model for subscriptions and API keys. Existing private logins remain private on migration.

## Permissions and administration

| Action | Use grant | Manage grant | Harness login manager role |
|---|---|---|---|
| See login summary and connection status | Yes | Yes | All logins |
| Select for own agents / set personal default | Yes | Yes | After explicitly granting themselves Use |
| Rename, reconnect, disable or delete login | No | Yes | All logins |
| Add/remove people; grant Use or Manage | No | Yes | All logins |
| See who has access and administrative impact | No | Yes | All logins |
| Reveal/export the stored credential | No | No | No |
| Read another person's conversations | No additional access | No additional access | No additional access |

**Manage includes Use** for a per-login grant. Any manager can add another manager or remove one, subject to
the last-manager rule. Recipients can leave a share; the same rule applies if they are the last manager.

Add a global **Harness login manager** role, included in Admin. It authorizes management of every login,
including recovery of missing grants. It does not grant Runner, agent or conversation access. Use in personal
agent selectors still requires an explicit grant: an administrator can grant it to themselves, with an audit
entry. This is an explicit action and useful default filtering, not a security barrier against administrators.
Explain that the role can reconnect, delete and redistribute access to every login when it is assigned.

Normally each enabled login must have at least one explicit, enabled user with Manage. Adding/removing grants
and checking this invariant must be transactional. An orphan discovered after restore/import is listed on the
admin page for recovery; it is not silently assigned to an arbitrary user or offered for new launches.

## Portal experience

**My settings → Harness logins** shows owned-by-permission and shared logins together, grouped by harness.
Each row shows name, Subscription/API key, your Use/Manage access, your default indicator, connection status,
and an appropriate agent count. It must distinguish totals from agents the viewer can open.

- A visible **Manage access** button for managers opens a user list with Use/Manage selectors, Add user,
  remove and leave actions. Choose existing Portal users; no invitation/email system in this change.
- Keep **Make my default** available to all users with Use. It changes only that person's preference.
- The existing menu offers Rename, Reconnect, Disable and Remove when permitted. Users with Use only can
  leave the share but cannot change its credential or membership. A reconnect preserves grants and defaults.
- New-agent and switch-login selectors include eligible shared logins and indicate the user's default.
  Duplicate display names must have a disambiguator; never identify a login solely by its name.

**Manage → Harness logins** is the new role-gated page. Show harness/type, name, managers, people with access,
connection/renewal status, enabled state and affected-agent count; filter/search and reuse the same management
dialogs. It can connect a new login, reconnect, edit access, disable and remove. An administrator-created login
initially grants its creator Manage; they can appoint another manager before leaving it.

Login managers can see enough usage metadata for impact review (counts grouped by user and Runner). Agent
names, terminal links, paths and conversation content remain subject to existing agent access. Never put
secret values in DTOs, logs, diagnostics, change events or exports. Login terminals can show provider output
during reconnect; authorize and expire them like other credential-management operations.

Follow the existing theme, compact rows and phone layout. Explanations go behind HelpTip. Every remotely
loaded section and access menu uses the existing loading/error/retry pattern. Live access changes refresh
open views; an already open dialog is not authorization to perform an action.

## Defaults and agent authorization

Move defaults to a per-user/per-harness preference. A shared login can be one person's default without
affecting anyone else. An inaccessible, disabled or deleted default becomes unset with a clear explanation;
do not silently choose another person's credential or fall back to the Runner's ambient login.

On start, record the effective login used by the running agent, even when its configuration says "my default".
Changing a default affects future launches, not running processes. Usage counts and revocation must include
both explicit selections and effective default bindings; today's count only includes explicit selections.

For new bindings, require permission to modify the agent, Use on the selected login for the actor, and Use for
the agent's owner. The owner is the stable identity used for unattended restarts. A collaborator cannot attach
a login that the owner cannot use; they must share it first. Recheck the owner and binding on every launch,
restart, switch, import/continue and automatic continuation that would start or prompt a restricted agent.
Changing an agent's owner also rechecks eligibility.

Agent sharing remains a separate delegation: someone allowed to prompt an owner's agent uses that agent's
configured login through the agent. They do not acquire general Use on the login. Removing their direct login
grant does not remove a separate agent share; the impact dialog must explain this. Disabling the whole login
blocks all agent use of it, including delegated use.

Legacy exception: current switching permits a collaborator to attach their own login to someone else's agent.
Do not migrate that into a general grant for the owner. Preserve the existing binding as an explicit,
agent-specific authorization, visible to login managers and revocable, until it is replaced. It must not follow
an agent clone, owner change or login switch. New bindings use the simpler rule above.

## Removing access, disabling and deleting

Recommend removal that prevents new use immediately and stops affected harnesses on reachable Runners.
Before confirmation, show which of the target user's agents lose their effective login and that active turns
may be interrupted. Other users' agents with valid access continue. Do not automatically restart affected
agents with another credential. A stopped agent explains that a permitted login must be chosen.

Commit access changes and persistent enforcement work together. Recheck access immediately before credential
distribution and launch, using binding/access revisions so a concurrent grant removal wins over a stale
launch or reconnect. Reconnect must recheck Manage both while the terminal is open and before saving its result.
Revoked login terminals close and their temporary credential files are removed.

Enforcement must survive Manager restart and retry offline Runner cleanup when it reconnects. Report pending
stops/cleanup explicitly. The existing periodic sweep is a backstop, not proof that offline use has stopped.
Stopping a harness does not prove detached or remote workloads have ended; do not delete workload evidence.
Deleting files does not revoke provider-issued tokens or erase copies in backups or already running processes.

Deleting a user removes their grants, defaults and personal files, **not every login they originally created**.
The deletion preview identifies logins for which they are the last enabled manager. Require one explicit
resolution for each: assign another manager, or delete the login with its impact confirmation. Do not make
the recipient of the user's agents a login manager automatically. A User manager without login-management
permission must have an authorized login manager resolve these dependencies first. Disabling the last manager
needs equivalent handling. Private one-person test logins can be selected for deletion in this workflow.

Deleting a login clears grants and personal defaults, stops dependent harnesses, and removes credential copies
from every applicable Runner. Keep a durable missing-login reason on dependent agents rather than allowing
the existing database SetNull behavior to select a different default silently.

## Credentials, runtime state and trust

Keep one encrypted credential and one renewal operation per login. Sharing it must not duplicate refresh-token
ownership or introduce a renewal process per recipient. Reuse the existing Manager renewal/version controls.

Audit runtime state before enabling multi-user use. Codex currently has a per-login CODEX_HOME and links to
shared sessions; Claude also has shared Runner configuration/state. A shared login must not automatically
expose another user's conversation list or import their sessions into the Portal. Separate recipient runtime
state where required and keep session import scoped to authorized agents; credential refresh can still have
one source. Test reconnect, resume and harness-switch behavior across two recipients. This is a release gate,
not a claim that today's shared Unix account provides filesystem isolation.

The existing Runner model uses one Unix account. Shell access already lets trusted Runner users inspect files
and processes there; a Use grant is not a vault that prevents credential extraction. Full isolation through
separate Unix identities/containers is a different project. The access dialog should briefly explain shared
subscription allowance/API billing and Runner trust through help text, without promising provider revocation.

## Data, API and migration

- Keep login IDs, harness, kind, encrypted credential, renewal state and version. Add Enabled and an access
  revision. Replace authoritative OwnerId with optional creator audit attribution, with no deletion cascade.
- Add AccountGrant(AccountId, UserId, Access, GrantedBy, GrantedAt), unique per login/user, with Use/Manage levels.
- Add UserHarnessDefault(UserId, HarnessId, AccountId), unique per user/harness. Validate harness match and Use.
- Persist effective running-login bindings, legacy agent-only grants, and pending revocation/cleanup work.
- Extend AccountDto with effective permissions, enabled state and scoped usage metadata. Compute IsDefault
  for the caller, retaining client compatibility where practical. Add permissions-aware list/detail/access APIs
  and a separate role-gated admin list; enforce permissions in services as well as endpoint filters.
- Replace OwnsAccount checks everywhere, including login WebSockets and capture completion. Add audit actions
  for grant changes, self-grants, reconnect, disable, delete and orphan recovery; publish to authorized users.
- Migrate each current owner to Manage, copy each current default to that user's preference, and preserve IDs
  and credentials. Introduce new bindings and cleanup logic before removing the old ownership cascade.
- Add the role to role labels, grant validation, navigation and the Admin bitmask. Upgrade existing full admins
  as in the Log viewer migration; do not grant it to every Settings manager or User manager.
- Update user deletion, renewal sweeps, backup/restore, live connection revocation and existing privacy wording
  together. Back up before migration; a rollback restores the compatible database, not just the old binary.

## Delivery stages and checks

1. Permission/default schema, migration and centralized authorization, including the legacy binding audit.
2. Launch/restart/switch enforcement, effective usage accounting, revocation persistence and cleanup.
3. Recipient runtime/session separation and reconnect/renewal checks for shared use.
4. My settings access controls, selectors and admin page/role, with phone and accessibility checks.
5. User deletion/disable impact flow, documentation and complete end-to-end verification before release.

Required tests: existing credentials/defaults survive migration; two recipients have independent defaults;
Use cannot manage grants or reconnect; global administration does not expose secret DTOs or conversations;
shared-agent delegation and legacy bindings are bounded; last-manager races are rejected; disabling/deleting
users preserves unrelated shared logins; revocation wins over launch/reconnect races; offline cleanup survives
restart; a revoked binding never falls back to ambient credentials; simultaneous use still has one renewer;
usage includes defaults; event recipients lose visibility promptly; runtime/session state stays correctly scoped.

Browser checks cover slow/failing loads, permission-appropriate buttons, pending cleanup, duplicate names,
confirmation impact, mobile widths and light/dark themes. Live checks use disposable users and test logins,
on two Runners where available, and are reported separately from host tests. Do not reconnect, revoke or
transfer real users' subscriptions just to test this feature.

## Accepted decisions

1. No permanent owner; per-login Use/Manage grants, with the creator initially granted Manage.
2. A global Harness login manager role for administration and recovery; use requires an explicit grant.
3. Personal defaults, no silent credential fallback, and existing agent sharing kept separate.
4. Removal blocks new use and stops affected harnesses, with impact confirmation and honest offline status.
5. User removal explicitly resolves last-manager logins instead of cascading away shared credentials.

Decision record, 2026-10-15: the proposal was approved for implementation. The implementation uses durable
blocked-agent flags and a persistent Runner cleanup queue, with a ten-second enforcement retry. Private runtime
profiles are scoped by user, agent and harness, so switching logins preserves the agent's own conversation.
Existing full admins migrate to the new role. Migration rollback requires restoring a compatible database backup.

Verification: automated migration, permission/default, concurrent last-manager, offline enforcement and real-shell
runtime tests; browser checks use disposable local test users and placeholder credentials. Actual provider renewal
behavior remains covered by the existing fake-provider renewal tests. Live deployment and provider-account checks
must be reported separately; no real subscription is reconnected or revoked by these tests.
