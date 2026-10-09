# Codex account renewal: implementation handoff

Status: **planned; prototype required; not implemented**. Written 2026-10-07 for Sol or Luna.

## Objective and recommendation

Allow a user to connect their ChatGPT subscription once in DAM and use it concurrently in several Codex agents on
several Runners. Preserve the native Codex terminal, conversation resume, harness/account switching, and DAM's
account ownership rules. Only one authority may rotate a given OAuth login's refresh token. Runners receive access
tokens; they must not receive the centrally managed refresh token or independently refresh that login.

Keep the existing public-access, optional authenticator, password-prompt, UI and Conversation manager work. Separate
this authentication redesign from the internet-facing PR. Remove the automatic cross-Runner credential synchronization
introduced in `a569c95` from that PR in a dedicated, reviewable commit; retain the investigation and this plan. Reverting
that patch is scope control, **not a concurrency fix**: the preceding implementation also copied a login to multiple
Runners. Do not describe either version as safe for shared-login renewal. Do not undo the live runner-02 repair or stop
users' running agents as part of a source revert.

This document requests implementation work in phases below. Creating this plan does not itself deploy, revert, change
live logins, merge the current PR or launch another agent.

## Established evidence and limits

- The installed version investigated was Codex 0.160.1, source tag `rust-v0.160.1`, commit
  `d27764b82f7118f674371e6d6e76271d9d606edb`.
- [Official managed-auth guidance](https://learn.chatgpt.com/docs/auth/ci-cd-auth) discourages concurrent machines/jobs
  sharing copied auth files. Restoring and preserving a renewed file is useful for a serialized workflow.
- The refresh semaphore in `codex-rs/login/src/auth/manager.rs` belongs to one AuthManager. Independent processes do
  not acquire a DAM lock. A local credential reload can recover an agent, but cannot prevent simultaneous upstream
  requests from different processes.
- A reused refresh token is classified as a permanent failure for that credential snapshot. An isolated experiment
  using the actual CLI, two temporary CODEX_HOME folders and a localhost mock OAuth service produced two requests with
  the same fake refresh token: one success, one permanent failure. Retrying the loser made no further refresh request.
  Copying the winner's file into the still-running loser recovered it. No production credentials were used. The mock
  established client behavior under a modeled rotation rule, not the production service's exact grace/reuse policy.
- The sync patch only installs newer credentials on another Runner at launch. Its database conditional updates do not
  serialize upstream refreshes. Cross-machine timestamps cannot establish token validity. The destination file hash
  check followed by rename is not an atomic transaction with Codex's own writer.
- DAM currently uses `--no-daemon` when available because account directory paths exceed Unix socket path limits.
  Multiple agents on one Runner also need coverage. Simply turning the daemon on or sharing one file is not a solution.
- [External ChatGPT tokens in app-server](https://learn.chatgpt.com/docs/app-server) are a documented **experimental**
  host integration. `account/login/start` with `chatgptAuthTokens` supplies access credentials, and
  `account/chatgptAuthTokens/refresh` asks the host for replacements. This does not prove that an unmodified interactive
  TUI can host the integration directly. The adapter and TUI connection must be demonstrated.

Useful pinned source files are under
[OpenAI Codex at the investigated commit](https://github.com/openai/codex/tree/d27764b82f7118f674371e6d6e76271d9d606edb):
`codex-rs/login/src/auth/manager.rs`, `auth/storage.rs`, `codex-rs/app-server/src/external_auth.rs`,
`request_processors/account_processor.rs`, `codex-rs/tui/src/app/app_server_requests.rs`, and `codex-rs/cli/src/main.rs`.
Re-check interfaces against the exact version chosen for implementation. Do not assume `@latest` remains compatible.

## Scope and constraints

- First implementation: managed Codex ChatGPT subscriptions, one DAM instance. Leave API key accounts, Claude and pi
  authentication behavior unchanged. Generic abstractions are appropriate only where another harness actually fits.
- No additional public port, per-Runner DNS record, NPM host or Cloudflare configuration. Authentication transport must
  work with existing public proxy authentication still enabled and with direct local deployments.
- No automatic fallback to copying refresh tokens when a broker/helper/Runner is unavailable or incompatible.
- No switch to API billing, custom model provider or a replacement chat UI to make the prototype pass.
- Preserve each agent's work directory, session ID, developer instructions, model settings, tools, skills, MCP configuration,
  owner environment, timezone and DAM agent identity. A shared helper must not make one agent inherit another's environment.
- A Runner running arbitrary user commands remains a trusted execution environment. Do not claim this creates OS-level
  isolation between users sharing the same Unix account. It reduces distribution of refresh credentials, not all access
  to bearer credentials on a compromised authorized Runner.

## Phase 0 — establish a clean workstream

1. Inspect the current PR/base and working tree. Preserve all unrelated changes. If `a569c95` is still present, remove its
   automatic capture/install behavior in a separate commit; resolve documentation conflicts deliberately. If it was already
   removed, do not repeat the revert. Keep the stale-copy and concurrency findings documented as unresolved.
2. Put central-auth work on a separate branch based on the intended post-revert state. If the internet-facing PR is still
   open, make the dependency explicit; do not merge it merely to obtain a base. Create/update the implementation PR as draft.
3. Record the exact CLI/app-server version, source commit, current passing tests and a reproducible fake-credential fixture.
   Existing cache files on this machine are optional evidence, never a required dependency for another implementer.
4. Preserve useful encryption, reconnect and stale-update test scenarios from `AccountRenewalTests.cs` when replacing the
   sync implementation. Do not retain tests that merely assert the superseded copying behavior.

Deliverable: clean branch/PR relationship, documented baseline and no deployment changes.

## Phase 1 — prove the native terminal integration before building the broker

Prototype in isolated folders with fake credentials and local services. Start with the documented external-token
app-server protocol. Use a small host adapter that owns the app-server authentication exchange and handles refresh
requests; investigate connecting the native TUI to that same app-server. A protocol bridge may be necessary. Do not assume
an app-server can transparently pass its refresh request through a TUI that does not implement that callback.

The prototype must demonstrate all of the following:

- Two native Codex terminals execute on their respective Runner environments, using external access credentials supplied
  by the adapter. Tools must execute on the Runner, not on the Manager or the central login helper.
- Existing conversation IDs resume with the correct history, model controls and per-agent environment. Account switching
  does not cross identities. Long home paths do not prevent startup. Use short private runtime socket paths where needed.
- Both terminals encounter expired access credentials together; the host supplies a replacement and both continue through
  the ordinary request/retry path. Neither Runner possesses or submits the provider's refresh token.
- The test uses a local model/OAuth fixture where feasible. First prove ordinary rendering, input and resume without a
  paid model call. An app-server-only getAuthStatus test is useful evidence but insufficient to pass this gate.
- TUI/adapter shutdown, disconnect, restart and interrupted refresh requests do not leave uncontrolled child processes,
  orphan authentication owners or lost conversations. Confirm the adapter remains connected for server-initiated requests.
- The chosen native CLI protocol supports the required model/tool features without changing authentication into API-key
  billing. Check subscription identity, account headers and workspace handling.

Also prototype the **central renewal helper**: one authoritative Codex-managed login environment per account, able to
renew and persist credentials without starting a model turn. Prefer Codex's own managed refresh mechanism through a
controlled helper over implementing an undocumented OAuth client. Trace all proactive and forced refresh paths, including
startup/background activity. One OS process must not be assumed to imply one effective refresh authority; verify requests.

The runtime choice must be explicit: preferably a version-pinned helper supervised by the Manager, with isolated account
homes and an image/runtime dependency that CI can build. If that is not viable, document and prototype the alternative
before proceeding. An ordinary user's Runner must not silently become a required central authentication machine.

**Gate:** record actual commands, protocol routing, process layout, supported versions and observed results in this file
or a linked design note. Proceed only after native terminal and central renewal feasibility are established. If the gate
fails, stop production implementation and report the exact obstacle and alternatives. Independent Runner logins are a
fallback design choice, not a silent substitution. Do not adopt `CODEX_REFRESH_TOKEN_URL_OVERRIDE` or a CLI fork as a
production dependency just because it makes the fake fixture pass; that requires a separate compatibility/security decision.

## Phase 2 — implement the account authority and durable state

Use one authority per **DAM account ID and login generation**, not per display name, Runner or ChatGPT organization alone.
Account `Version` already distinguishes explicit replacement/reconnect. Introduce a separate monotonically increasing
credential revision for successful renewals; Runner clocks and `last_refresh` are not the ordering authority.

Persist, at minimum, the encrypted authoritative credential, account identity, login generation, credential revision,
access expiry and safe operational state. Suggested states: Ready, Refreshing, TemporarilyUnavailable, ReconnectRequired.
Store only sanitized error categories/messages. Account DTOs and Manager events must not expose tokens.

The required refresh algorithm:

1. Authenticate the caller and resolve its authorized account/generation server-side. Reject deleted/revoked/mismatched
   leases. The adapter retains the revision of the access token it installed; a refresh request includes that revision.
2. Enter the account's shared refresh operation. If a newer usable revision already exists, return it rather than starting
   another upstream refresh. Expiry-driven requests with sufficiently valid credentials can also reuse the current token.
3. For an unauthorized response on the current revision, make one bounded recovery attempt. Concurrent callers await the
   same result. Old unauthorized requests must not rotate a token that another request has just renewed.
4. Invoke the sole central renewal helper. Every refresh path, including proactive maintenance, must use this authority.
   Cancelling one caller must not abort or duplicate an operation still needed by other callers.
5. Persist the rotated credential durably before serving its access token as a successful new revision. Conditional writes
   must reject results from an account that was reconnected/deleted while the operation was pending. Never resurrect it.
6. Release waiters with the same outcome. Bound repeated unauthorized recovery, retries and cooldowns to prevent a storm.
   Do not repeatedly refresh for unrelated account/model permission failures.

Failure semantics must be designed, not left to generic HTTP retries:

- An upstream timeout after a request was sent can mean the provider already rotated the token. Likewise a helper/Manager
  crash may occur between upstream rotation and durable persistence. This cannot be made exactly-once with a database
  transaction alone. Inspect any helper-persisted result, mark ambiguity explicitly and require reconnect if necessary.
  Never blindly replay the old refresh token or roll credentials back from a stale database copy.
- A lost reply to a Runner after durable completion is different: its retry returns the newer saved revision without
  refreshing again. Test this distinction.
- Treat known expired/revoked/reused credentials as reconnect-required when the authority cannot recover a newer result.
  Transient unavailability must not erase credentials or sign everyone out.
- On restart, reconcile interrupted operations and helper state before issuing another refresh. A timed-out lock lease
  must not allow a second helper to refresh while the first may still be running. Confirm termination or fence access.
- Single-Manager scope must be enforced operationally. Do not claim that an in-memory semaphore handles two active
  Managers, overlapping upgrades or a surviving helper from the old process.

The central helper may need a private plaintext auth file because Codex requires it. Make the database credential encrypted
with `SecretProtector`; isolate helper files per account, with directory/file permissions 0700/0600. Document lifecycle,
crash recovery and cleanup. Keep secrets out of process arguments, logs, traces, URLs, artifacts and telemetry. Do not wipe
an uncommitted successful renewal during cleanup and then silently restore the stale database credential.

## Phase 3 — authenticated delivery and Runner lifecycle

Prefer delivery over a Manager-controlled authenticated channel established using the existing trusted SSH connection.
The Phase 1 design must show the actual channel and reconnection path, rather than assuming the Runner can reach the
Manager's public URL. If an internal HTTP endpoint is needed, protect it with a purpose-specific, revocable, scoped machine
credential and TLS or an SSH tunnel. Browser cookies, proxy Basic auth and arbitrary account IDs are not machine credentials.

- Bind each credential/lease to account ID, login generation, Runner and the intended agent/adapter. Store lease secrets
  hashed where appropriate; never let a Runner choose another user's account by editing a request body.
- An agent share or Conversation manager permission does not grant a new API for exporting another user's credentials.
  A shared agent may keep operating with its selected owner's login through the existing authorized execution path.
- Keep provider refresh tokens central. Bootstrap adapters with scoped machine credentials and access credentials only.
  Do not place a fake managed auth.json on the Runner unless the chosen supported protocol explicitly requires its shape
  and proves it cannot trigger independent OAuth refresh.
- A disconnected Runner can keep using an unexpired access token. Once it needs renewal and cannot reach the authority,
  surface temporary unavailability; preserve the process/conversation where possible and recover on reconnection.
- Account replacement, deletion, switching and Runner removal must revoke the appropriate leases and suppress pending
  responses from old generations. Stop further token issuance immediately. Already issued provider access tokens may
  remain usable until expiry or provider revocation; do not promise instantaneous upstream invalidation.
- Connect adapter errors to useful DAM state/help. Authentication failure must not be interpreted as a usage limit or
  automatically type `continue` into a login prompt. Preserve intentional accountless Runner-native logins as a separate
  mode, with no claim that they participate in central management.

## Phase 4 — migrate existing accounts without two renewal authorities

A live Runner may still hold the shared refresh token in memory even after its auth file is removed. Existing copies can
therefore defeat central serialization. Migration must transition all managed users of a login deliberately.

1. Add an explicit per-account mode/version so existing legacy accounts and central-managed accounts cannot be confused.
   Do not enable the new mode globally merely because the database migration ran.
2. Inventory agents/Runners using an account, including default-account resolution, running processes and offline Runners.
   Show a concise migration/reconnect action with detail in a (?) HelpTip. Preserve the light/dark theme.
3. Prefer a fresh central login generation. A reconnect may not revoke all older provider sessions, so do not claim that it
   does. The new refresh token must never be exported to legacy Runners. No timestamp-based selection among divergent
   legacy copies is an acceptable migration authority.
4. Coordinate a controlled restart/resume of affected managed agents into the proven external-token adapter. Preserve their
   conversation IDs, working directories and selected accounts. Do not silently interrupt a working turn to migrate.
   Report affected agents and use the existing lifecycle operation UX; operational timing may need user selection.
5. Remove obsolete managed credential copies and invalidate old DAM leases where reachable. Track cleanup for offline
   Runners and enforce the new mode before DAM launches their next agent. An offline legacy process may continue using
   its old independent provider session; account for that residual state explicitly.
6. Migration rollback restores application behavior and conversation state, not an older token family. If central mode
   cannot run, retain encrypted credentials, report recovery steps and use an explicit reconnect when switching auth modes.
   Never fix rollback by spraying the central refresh token back onto Runners.

## Code map and suggested boundaries

| Area | Existing entry points | Required work |
| --- | --- | --- |
| Account lifecycle | `DeveAgentManager.Core/Accounts/AccountService.cs`, `Core/Data/Entities.cs` | Account mode/generation/revision, reconnect/delete, authority integration; no cross-Runner refresh-token copying. |
| Login capture | `DeveAgentManager/Api/TerminalSocketEndpoint.cs` (`LoginWatcher`), `Api/AccountEndpoints.cs` | Establish the new central login; keep authenticated ownership checks and hide credentials. |
| Native harness | `DeveAgentManager.Harnesses.Codex/CodexHarness.cs`, `Core/Abstractions/IHarness.cs` | Proven adapter launch/installation, supported-version checks and preserved session/environment setup. |
| Agent lifecycle | `DeveAgentManager.Core/Agents/AgentService.cs` | Resolve account mode, issue/revoke adapter lease, preserve start/resume/switch behavior. |
| Runner transport | `Core/Ssh/RunnerShell.cs`, `SshConnector.cs`, `Core/Runners/*` | Authenticated private channel, reconnect/cleanup, no additional proxy setup. |
| Supervision/runtime | `Core/ServiceCollectionExtensions.cs`, Manager host and container build | Account authority/helper supervision, pinned dependencies, restart recovery and bounded resources. |
| User interaction | `Client/Dialogs/AccountLoginDialog.razor`, `Client/Shared/AccountsPanel.razor` | Connection/migration/reconnect states and (?) explanations, no token display. |
| Validation | `DeveAgentManager.Tests`, current auth/security/harness tests | Meaningful authority, protocol, authorization and native-terminal regression tests. |

Suggested new components are an account authority, a Codex-specific renewal helper, and a Runner-side external-auth
adapter. Choose concrete names after Phase 1. Keep credential coordination separate from `RunnerMonitor`: periodic polling
must not become the authority. Avoid introducing generic auth plumbing into Claude/pi without a tested requirement.

## Acceptance tests and evidence

Use strict fake OAuth rotation: an old refresh token can succeed once; a second submission is rejected. Count upstream
requests and inspect only fake credentials. Include a production-like delay/barrier so tests actually overlap.

| Scenario | Required outcome |
| --- | --- |
| Several agents on two Runners expire together | One authority refresh for that credential revision; all authorized adapters receive the new access revision and continue. |
| Several agents on one Runner | Same result; no independent CLI OAuth refresh. |
| Delayed 401 for an older access revision | Current revision returned; no redundant rotation. |
| Repeated failure with current credentials | Bounded recovery, useful state, no refresh storm or usage-limit misclassification. |
| Reconnect/delete/switch during refresh | Old completion cannot overwrite or issue credentials for the new generation; leases reflect current authorization. |
| One caller cancels/disconnects | Other waiters succeed; the authority operation is not duplicated. |
| Manager/helper crashes around rotation/persistence | Defined recovery or reconnect-required state; no stale replay or false success. |
| Runner reply lost after durable completion | Retry receives stored newer revision without another rotation. |
| Offline Runner returns; clock skew/equal timestamps | Uses authority revision/generation; no stale file wins by timestamp. |
| Manager restart or overlapping helper ownership | No second active refresh authority. |
| Wrong Runner/user/account/generation/lease | Rejected; no secret-bearing errors, events, DTOs or logs. |
| Account or Runner deleted | No new credential issuance; cleanup/revocation tested, access-token expiry limits documented. |
| CLI upgrade, unsupported callback or missing adapter | Fails with a clear compatibility state, never falls back to sharing refresh tokens. |
| Native terminal start/resume/harness switch | Same conversation, correct Runner execution and environment; model/tools/skills/MCP/image handling still work. |
| Proxy Basic auth enabled and direct LAN deployment | Private authentication channel works without new NPM entries or public ports. |
| API-key, Claude and pi accounts | Existing behavior and relevant tests remain passing. |

Add deterministic unit/integration coverage for authority state and access checks. Add a reproducible real installed-CLI
fixture with fake endpoints plus native terminal smoke evidence. A live disposable-login smoke test may verify the selected
provider integration once implemented; do not rotate the user's active shared login merely to reproduce failure.

## Delivery checklist

- [ ] Phase 0 scope separation documented; no unrelated feature revert.
- [ ] Phase 1 native-terminal and renewal-helper gates pass with reproducible artifacts and pinned versions.
- [ ] Account authority, private delivery and lifecycle behavior meet the invariants above.
- [ ] Migration/recovery behavior is concrete, including offline legacy agents and uncertain refresh outcomes.
- [ ] Meaningful new tests and existing required checks pass; latest PR CI reviewed.
- [ ] Update this plan, the internet-facing plan, deployment docs and PR description to match the implementation.
- [ ] State remaining limitations accurately. Neither an auth-file sync test nor two simultaneous successful prompts proves
      renewal coordination. Do not mark the task complete while native terminal integration or migration remains unproven.

The next implementer should begin with Phase 0 and the Phase 1 prototype. Do not start by building public token endpoints
or adding database entities before establishing that the native harness integration can satisfy the requirement.
