# Activity fixes and connection investigation plan

> Follow-up implementation: the accepted behavior is now Finished for an unseen current completion, clearing to Ready when its terminal is viewed. Codex native lifecycle recovery preserves acknowledgement through upgrades. SSH pooling uses exclusively leased reusable transports, isolating cancellation and draining retirement without replay. See [current activity behavior](agent-activity.md) and [SSH recovery](ssh-session-exhaustion-investigation.md). The remaining sections preserve the earlier plan and investigation history; statements that viewing must retain Finished or that SSH is investigation-only are superseded by this follow-up.


Status: **items 1, 3, 4 and 5 implemented and verified locally; SSH remains investigation only**. Updated 2026-10-16. Live deployment verification is separate and pending.

## Scope and delivery

Plan fixes directly on `master` for usage-limit parsing failures, Proxmox sampling failures, misleading
session-sync messages and the previously discussed Finished/Ready behavior. The SSH item is investigation only:
establish the cause before proposing a transport change. No feature branch or pull request is required.

The user authorized implementation of the non-SSH items after reviewing this plan. The sections below retain
the design and verification requirements. Deliver the complete tested implementation on `master`; a push starts
the release pipeline. SSH connection retirement, a concurrency cap and reconnect backoff remain unimplemented
pending evidence and review.

## Local verification record

- Release build succeeds with the repository's .NET 10 SDK.
- All 635 host tests pass, including parser/API failure preservation, diagnostics redaction, continuation delivery,
  current-turn completion, Proxmox request classification/budgets and sampler backoff/recovery.
- Chromium in light mode and Firefox in dark mode pass the activity/continuation browser checks at desktop and
  phone sizes, including acknowledgement, later work, independent policy loading, unavailable detection and retry.
- The disposable development Manager, test user and keys were removed after verification.
- SSH transport behavior is unchanged. CI/artifact publication and live deployment verification remain separate results.
- The first CI run was blocked at restore by newly published high-severity advisories affecting the existing
  Microsoft 10.0.12 packages. References were updated to 10.0.13; a fresh audited restore, all 635 host tests and
  the Chromium/Firefox browser checks pass with that update.

## Evidence and limits of the diagnosis

| Finding | Established from logs and code | Still unconfirmed |
|---|---|---|
| Usage-limit parsing | A regex timeout escapes through `UsageLimitState.DetectedReset` into `GET /agents/{id}/limit`. A separate timeout occurred during background limit observation. | The precise input or machine condition that exhausted the timeout. Ordinary and long synthetic inputs did not reproduce it locally. |
| SSH | A broader journal query found 54 `no more sessions` refusals from the affected Manager connection. A second isolated experiment with the normal ten-session limit reproduced persistent exhaustion after channel-open timeouts; a fresh connection worked. | What caused the initial delay, and whether the production slots were all abandoned opens. Memory-pressure events coincide with the refusal burst, but historical channel and resource traces are unavailable. |
| Proxmox | Both Runners briefly lost fast sampling and resumed it. A network-unreachable incident and an HTTP timeout incident are recorded. Any `PveResultException` currently triggers VM-location rediscovery. | Why the shared Proxmox path was unavailable. The logs do not establish a VM migration or a Manager defect as the original trigger. |
| Session-sync wording | Periodic monitoring calls the method that logs `Final session sync ... failed`. | These entries alone establish neither an agent exit nor lost conversation data. |
| Finished/Ready | The badge requires an unread completion. A visible, focused terminal marks it read and therefore changes the badge to Ready. | No further cause is needed to explain this behavior. |

The server journal establishes session exhaustion for this SSH incident. A timeout alone does not establish
catastrophic regex backtracking or a network fault. Keep the confirmed refusal separate from the inferred
timeout/abandoned-channel mechanism and the still-unconfirmed initial trigger.

## Behavior required across all changes

- A parsing failure is different from successfully finding no usage limit.
- A failed observation cannot clear a saved limit, erase a schedule, manufacture a completion, or authorize a send.
- An explicit schedule remains recorded when a dependency is unavailable. Delivery still needs the existing
  current-session, empty-prompt, permission and readiness checks.
- Reconnecting transport never automatically replays a command whose execution or delivery could have started.
  Preserve existing durable claims and uncertain-delivery states for jobs and continuations.
- Do not change SSH connection lifetime, concurrency, retries, keepalive or server settings without evidence
  identifying the failure and a reviewed proposal for addressing it.
- Reading a reply acknowledges its notification; it does not change whether the current turn has completed.
- Regex failure logs include the failing expression and a bounded, escaped excerpt of its actual input, sanitized
  before any logger receives it. Preserve whitespace and diagnostic structure while redacting credentials.
  Do not log entire unrelated terminal histories or shell command bodies.

## 1. Make usage-limit parsing bounded and failure-aware

Primary files: `UsageLimitParser.cs`, `UsageLimitState.cs`, `AgentContinuations.cs`, `AgentScreenDetector.cs`
and the observation loop in `AgentService.cs`, all under `DeveAgentManager.Core/Agents`.

### Parser structure

1. Introduce an explicit parse outcome: a recognized limit, a successful no-limit result, or an unavailable
   interpretation with a reason. A recognized limit with an uncertain reset remains a recognized limit.
   Do not implement timeout handling by returning the same `null` used for no limit.
2. Replace simple prompt-marker and whitespace operations with bounded character/span operations. In particular,
   checking for a trimmed `>`-style harness marker does not need a regex; preserve the actual supported Unicode
   prompt markers and required following whitespace.
3. Separate terminal-screen recognition from parsing the reset in an already stored limit message. Stored
   messages should not need a synthetic prompt and another scan of the terminal grammar.
4. Put input-size limits before expensive normalization/matching. Bound the visible screen region and individual
   line length; reject an over-budget relevant region as unavailable rather than truncating it into an apparent
   valid timestamp. Use existing captured screens to choose limits that cover supported phone/desktop layouts.
5. Give remaining regex operations explicit budgets and reuse fixed matchers. Use non-backtracking matching
   where the patterns support it, with regression coverage for matching behavior. Keep a final timeout boundary
   even after optimizing patterns; increasing the timeout alone is not the fix.
6. Parse a stored reset once per service operation and reuse that result for expiry and DTO construction.
   Preserve the original observation date/timezone and keep the detected reset distinct from the user's override.

### Service and API behavior

1. Update every parser consumer, including `UsageLimitState.ApplyExpiry`, so failure cannot escape the per-agent
   observation boundary or abort another agent's update in the same Runner batch.
2. On parser failure, preserve stored status, reset overrides, continuation time, automatic policy and delivery
   evidence. Mark detection temporarily unavailable. Do not infer expiry or recovery from the failed parse.
3. `GET /limit` must return an existing limit with a short detection problem when reset interpretation is
   unavailable. Extend `AgentLimitDto` with an optional detection-problem field rather than replacing meaningful
   lifecycle statuses such as Scheduled, Sending or Uncertain. A genuinely absent/cleared limit retains its current
   no-result behavior. Database and unrelated programming failures remain errors.
4. Background automatic continuation must wait when the current limit/readiness interpretation is unavailable.
   A manually chosen schedule remains armed and follows its existing independent delivery checks; it must not
   become eligible merely because parsing failed. Unknown outcomes must not consume the allowed-send count.
5. Update `AgentLimitButton` and `ContinueAfterLimitDialog` to show loading, unavailable detection and retry in
   place. Keep existing data during refresh failures. Load automatic-policy state independently where needed so a
   limit-read problem does not make an enabled policy appear off. Longer explanations belong in HelpTip.
6. Capture the expression and actual subject text at the failing regex boundary, including failures in replacement
   and normalization operations. Record the parser stage/rule, options, configured timeout, harness, source kind
   (live screen or saved message), original input length and line index where available. Use the exception's
   input/pattern when available; do not substitute an unrelated whole-screen dump for the failed subject.
7. Sanitize the captured text before passing it to any logger, including console sinks. Preserve spaces, tabs,
   line breaks and relevant Unicode characters through escaped serialization; whitespace normalization would erase
   the evidence needed to reproduce some failures. Keep the full sanitized subject when short; for longer subjects,
   cap the excerpt (initially 4,096 characters), include head/tail context, a fingerprint and omitted-character count,
   and explicitly mark truncation/redaction. Do not claim an edited excerpt is an exact reproducer.
8. Show that diagnostic in the existing role-protected Logs page and its copy actions. Rate-limit repeated identical
   failures, retain a count and record recovery. Bound and harden the diagnostic sanitizer itself: if sanitization
   fails, omit the payload but retain pattern/stage/length/error metadata. The existing log store redacts with regexes
   before truncating, so its fallback alone is not enough for safely capturing a failed parser input.

No database migration is expected for this approach. If implementation reveals a need for persistent detection
metadata, explicitly document and test the migration before including it; do not repurpose `ResetAt`.

### Regression coverage

- Inject a parser timeout deterministically; do not depend on a slow CI machine to reproduce one.
- Verify `/limit` remains readable for an existing limit, reports unavailable interpretation, and preserves manual
  reset overrides. A successful no-limit read remains distinguishable from a failed interpretation.
- Verify timeout during expiry, automatic arming and observation does not clear a limit, send a prompt, consume a
  send count, alter Uncertain evidence, or prevent observation of another agent.
- Exercise recovery on the next successful observation and a service restart with an existing schedule.
- Keep current timezone, midnight, daylight-saving, wrapped-message, native-auto-continue and stale-session tests.
- Add malformed, oversized and long-whitespace inputs with a generous overall deadline to catch hangs.
- Verify that a timeout records the expression and relevant input text, preserves diagnostic whitespace, indicates
  truncation/redaction, redacts test-only secrets before every sink, and retains metadata if sanitization fails.
- Browser-check loading, detection failure, retry and retained policy/schedule state.

## 2. Investigate SSH failures before choosing a fix

Primary files: `DeveAgentManager.Core/Ssh/RunnerShell.cs`, `SshConnector.cs`, and the polling callers in
`RunnerMonitor`, `AgentJobCompletions`, `SessionService` and file collection.

### What the errors mean

- `Failed to open a channel after 10 attempts` counts refused channel-open requests. It does not mean ten TCP
  connections dropped, ten commands executed, or even that the underlying connection disconnected. The library's
  failure handler discards the server's reason code and description before throwing the aggregate exception.
- `Session operation has timed out` from channel opening means no usable response arrived within the operation
  budget. It does not identify whether the delay was in the server, client message processing or network path.
- The Manager's command deadline is a third failure category. Do not combine it with refused channel opens or
  an actual socket reset when diagnosing a timeline.

SSH command channels are multiplexed over one authenticated connection. Server rules can refuse a channel even
on a fast, low-latency host. That is an explanation of the protocol, not evidence that a particular rule caused
the reported incident. See the pinned library's
[channel opening](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/Channels/ChannelSession.cs),
[connection defaults](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/ConnectionInfo.cs) and
[OpenSSH session limits](https://man.openbsd.org/sshd_config#MaxSessions).

### Investigation completed so far

- Querying the `sshd` and `sshd-session` identifiers, including session scopes outside `ssh.service`, found
  54 `no more sessions` refusals on the affected Manager connection in the reported window. Earlier service-only
  inspection missed these records. The authenticated connection remained established when inspected afterward.
- Effective current settings for the actual account and source, including `Match` rules, allow ten sessions.
  The pinned client's default is also ten. No arbitrary four-command cap is justified by these findings.
- A disposable `MaxSessions 1` fixture showed ordinary saturation: the exact ten-attempt exception occurs while
  the connection remains established, and the same connection recovers after the occupying command finishes.
- A second fixture used `MaxSessions 10` and SSH.NET 2026.0.0. Pausing only its authenticated server process while
  ten channel opens timed out reproduced persistent exhaustion after resumption. Two subsequent probes each
  failed after ten refusals while `IsConnected` stayed true; an independent connection succeeded immediately.
  Fixture processes, disposable keys and data were removed. No production process was paused or reconfigured.
- The Runner recorded two memory-pressure cache-flush events at the refusal burst. There is no recorded SSH
  restart or OOM kill in the checked window. These events support investigating a resource stall but do not
  establish the initial cause; cumulative pressure counters cannot be attributed to this incident.
- The Manager reuses a client reporting `IsConnected` and only invalidates it for `SshConnectionException` in
  command execution. Channel-open timeout/refusal exceptions therefore leave the same pooled connection eligible.
- The full evidence, user impact and proposed correction are in
  [SSH session exhaustion investigation](ssh-session-exhaustion-investigation.md). SSH implementation is unchanged.

### Remaining evidence and proposed correction

1. Confirm the deployed Manager revision and SSH.NET build before comparing its control flow with current source.
   Correlate the first failure, repeated failures and first successful operation on one UTC timeline.
2. Retain the confirmed current effective settings and check historical configuration changes if available.
3. Correlate Manager command start/end/cancellation with server channel open/close, child-process exit and
   refusal reason. Count active channels per connection and determine whether cancelled or stalled commands left
   server-side sessions occupied. Keep terminal connections and SFTP separate from pooled exec connections.
4. Inspect client connection/disconnect/error events, connection age and the exact failing phase. Distinguish a
   server refusal from a client listener stall, pool invalidation by another caller and actual transport loss.
   The missing reason code may require a targeted library trace or server diagnostic capture.
5. Check for relevant process/file-descriptor limits, stalls and kernel/network events only where timestamps or
   refusal reasons support them. Do not infer a slow host or unreliable network from these exceptions alone.
6. Add narrowly scoped diagnostics only in a separately reviewed SSH change: connection generation/age,
   operation category and channel establishment/close/failure evidence, without command bodies or credentials.
   Pair them with guest/cgroup memory and pressure measurements around recurrence.
7. The representative fixture now supports a targeted proposal: retire the affected pooled connection after a
   channel-establishment timeout, using connection-specific identity so an old failure cannot discard a newer
   connection. Later independent operations may connect afresh; never replay the failed command. Distinguish
   establishment from post-submission timeouts and ordinary temporary saturation. Verify concurrent callers,
   cancellation and delivery uncertainty before implementation.

The initial trigger remains uncertain. The reproduced abandoned-open failure supports reviewing a narrow
connection-lifecycle correction; it does not justify changing keepalives, increasing server limits or retrying
workloads. Any later fix must preserve the prohibition on replaying ambiguously submitted commands.

## 3. Keep Proxmox sampling failures cheap and recoverable

Primary files: `DeveAgentManager.Providers.Proxmox/ProxmoxProvider.cs` and
`DeveAgentManager.Core/Runners/RunnerUsageMonitor.cs`.

1. Replace the catch-all migration fallback in `GetUsageAsync` with a classifier based on the SDK's available
   HTTP/result information and recognized VM-location errors. A generic HTTP 500 is not enough to establish migration.
2. Network failures, request timeout, cancellation, authentication/permission failures, rate limiting and generic
   server errors must not trigger a second cluster-wide lookup. Return control to the sampler's backoff.
3. Preserve actual migration recovery: rediscover once after a recognized location error, retry only if the VM's
   node changed, and cache the new node. Periodic full status refresh remains a way to learn location changes.
4. Apply the sampling budget to the whole fast-sampling attempt, including any allowed rediscovery, rather than
   permitting successive requests to each consume a fresh five seconds. Preserve cancellation through SDK wrappers.
5. If permitted rediscovery also fails, keep the original failure identifiable instead of replacing it with an
   unrelated secondary exception. Do not log API tokens, authentication headers or unfiltered response bodies.
6. Keep the existing ten-second retry delay and rate-limited warning/recovery behavior. A sampling failure must
   create a gap in fresh readings, not a false zero, a VM-stop indication, or a relaunch of agents.

### Regression coverage

- HTTP timeout, unreachable network, cancellation, 401/403, rate limit and generic 5xx make no rediscovery request.
- A representative VM-location error performs one lookup and one retry, then subsequent samples use the cached node.
- Rediscovery that finds the same node preserves the original failure; secondary failures retain both contexts safely.
- A whole-attempt deadline covers the migration path and does not leave background HTTP requests running.
- Verify the sampler backs off, records recovery once and resumes new readings without fabricating data.
- Update the existing migration test, which currently uses an undifferentiated HTTP 500, to model a real location error.

## 4. Make session-sync failure messages describe the operation

Primary files: `DeveAgentManager.Core/Sessions/SessionService.cs`, `RunnerMonitor.cs` and the lifecycle callers in
`RunnerService.cs`.

1. Use `Session sync ... failed` for ordinary periodic sync. Add explicit operation context for attempts made
   before stopping/removing a Runner, so those can accurately say they were final synchronization attempts.
2. Pass that context from callers; do not infer it from the shared helper's name.
3. Preserve the existing periodic retries and lifecycle behavior. The wording change must not delete source files,
   mark unsynchronized data as saved, or imply a successful final sync after an SSH failure.
4. Check any error presentation that uses this message so it does not interpret a routine sync failure as an exit.

Verify with a captured logger that periodic and lifecycle calls describe the right context and retain the original
exception. Reuse an existing session-sync test fixture; no broad new test framework is needed for a wording change.

## 5. Include the previously proposed Finished/Ready correction

Primary files: `AgentActivityTracker.cs`, `AgentActivity.cs`, `StateStyles.cs`, `AgentStatusDialog.razor`,
`AgentActivity.razor`, `AgentAttentionService.cs` and `AgentSeenWatcher.razor`.

1. Track confirmed completion of the current turn separately from historical completion timestamps and unread
   notification sequences. Bind it to the process/session lifecycle and expose it explicitly to the client.
2. Show Finished when that turn is confirmed complete, the current observation is Ready, and no positive live
   background-work indicator is present. Reading the reply clears its unread notification only.
3. Clear current-turn completion on confirmed new work/new turn or a process/session/harness switch. An old
   `CompletedAt` value must not label a later interrupted or unconfirmed turn Finished. Typing an unsent draft,
   resizing a terminal or changing tabs is not a new turn.
4. Keep Working, Background, Needs input, Usage limit, Unclear, Stopped and Exited precedence. A stale observation
   cannot claim current completion. Starting monitoring at an already idle screen must not manufacture a new alert.
5. Keep a persisted confirmed completion across a Manager restart when process/session identity is still valid.
   Handle older serialized observations conservatively and test that upgrading does not generate duplicate alerts.
6. Make activity filters and explanations agree with the badge. The Finished filter should select currently
   finished agents; unread completion remains a notification property. Do not show a Finished bell item for a
   currently working agent merely because an older reply remains unread.

Regression cases: completion while the user watches; viewing it later; hidden/unfocused tabs; subsequent work;
an interrupted later turn; background tasks; drafts; manager/process/session restarts; stale observations; and
delayed acknowledgements that must not acknowledge a newer completion. Update browser tests whose current
expectations encode the old Ready-after-viewing behavior.

## Implementation order and verification gates

| Stage | Deliverable | Gate before proceeding |
|---|---|---|
| 1 | Parser outcome, bounded matching, service/API handling and affected UI | Parser, continuation, API and per-agent failure-isolation regressions pass. |
| 2 | SSH investigation report; no transport change | Establish a supported cause and review a targeted fix, or explicitly record insufficient evidence and the diagnostic next step. |
| 3 | Proxmox fallback classification and accurate sync context | Sampling/migration tests and sync-log assertions pass. |
| 4 | Current-turn completion independent of acknowledgement | Tracker, badge/filter/attention and browser transition tests pass. |
| 5 | Integrated build, documentation and delivery | Release build, full existing suite and affected browser checks pass; reviewed diff and privacy scan are clean. |

Use the installed .NET SDK to build `DeveAgentManager.slnx` in Release, then run the full solution tests with
the repository's configured Microsoft.Testing.Platform runner. The previous baseline was 599 passing tests;
record the new count and any pre-existing warnings without treating that baseline as verification of these changes.

Run affected browser flows in Chromium and Firefox, including phone width and light/dark themes where UI changes
apply. Exercise the real local Manager API with disposable test-only data for error/recovery behavior. Mocks are
appropriate for deterministic screen states but must not replace API and transport integration checks.

Review the final diff for command replay, swallowed failures, changed schedule semantics and unexpected SSH changes.
Update `docs/agent-activity.md` and relevant operational documentation. Scan all tracked documentation for private
deployment details and secrets. Keep temporary logs and test credentials outside the repository.

Commit and push the tested implementation directly to `master`, then verify that the exact commit passes CI and
that its release artifacts are produced. Report the commit and build/version. A failed build is not a completed
delivery; fix the failure and verify the replacement commit.

## Deployment verification and completion criteria

Host tests, a published image and successful live deployment are separate results. After the intended build is
deployed and authenticated access is available:

1. Verify the Manager build identity. Confirm that the affected limit API is readable and its saved schedule/override
   is preserved. Check the normal toolbar and unavailable-detection presentation.
2. Use a disposable agent for a short turn: Finished must remain after opening/viewing it, and another turn must
   show Working. Verify background-work precedence separately.
3. Confirm fresh terminal observations, conversation synchronization and resource readings. Inspect recovery logs
   and observation timestamps, not just the absence of errors.
4. Collect the SSH evidence described above without changing active Runner transport behavior. Any reproduction
   involving refused channels, stalls or disconnected sockets belongs on an isolated test Runner/provider.
5. Check the ordinary sync-failure wording and diagnostic detail. Remove disposable agents, test users and temporary
   credentials created for validation.

Do not claim that the original infrastructure trigger was fixed merely because recovery is stronger or a synthetic
test passes. If live access is unavailable, report deployed verification as pending. Keep the prior build available
for rollback; if a schema change becomes necessary, document database compatibility and backup/restore requirements.

The planned fixes are complete when parsing/diagnostics, sampling, synchronization wording and current-turn
completion are implemented, their tests pass, the change is pushed to master and CI passes. The SSH investigation
has its own evidence report and review gate; it does not require a speculative transport fix for completion.
Live verification has its own recorded outcome. No unresolved parser failure may silently clear state, no transport
change may replay an ambiguous command, and reading a finished reply must leave it Finished.
