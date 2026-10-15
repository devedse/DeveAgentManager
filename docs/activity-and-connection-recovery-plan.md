# Activity and connection recovery plan

Status: **plan only; implementation pending**. Updated 2026-10-16.

## Scope and delivery

Implement the four investigated log issues directly on `master`: usage-limit parsing failures, SSH connection
recovery, Proxmox sampling failures, and misleading session-sync messages. Include the previously discussed
Finished/Ready correction in the same delivery. No feature branch or pull request is required.

This document authorizes no runtime changes by itself. Once implementation is requested, complete the changes
and verification locally, then commit and push the complete, tested change to `master`. Avoid publishing partial
changes to the parser or connection lifecycle, because a push starts the release pipeline.

## Evidence and limits of the diagnosis

| Finding | Established from logs and code | Still unconfirmed |
|---|---|---|
| Usage-limit parsing | A regex timeout escapes through `UsageLimitState.DetectedReset` into `GET /agents/{id}/limit`. A separate timeout occurred during background limit observation. | The precise input or machine condition that exhausted the timeout. Ordinary and long synthetic inputs did not reproduce it locally. |
| SSH | Terminal observation, job-completion collection and session sync share pooled SSH connections. Operation timeouts and exhausted channel-open attempts are not covered by the existing connection-invalidation catch. | What initially caused the Runner to stop answering or refuse channels. The available Runner journal did not establish the cause. |
| Proxmox | Both Runners briefly lost fast sampling and resumed it. A network-unreachable incident and an HTTP timeout incident are recorded. Any `PveResultException` currently triggers VM-location rediscovery. | Why the shared Proxmox path was unavailable. The logs do not establish a VM migration or a Manager defect as the original trigger. |
| Session-sync wording | Periodic monitoring calls the method that logs `Final session sync ... failed`. | These entries alone establish neither an agent exit nor lost conversation data. |
| Finished/Ready | The badge requires an unread completion. A visible, focused terminal marks it read and therefore changes the badge to Ready. | No further cause is needed to explain this behavior. |

Treat the timeout and channel-refusal messages as evidence of failure, not proof of catastrophic regex
backtracking, a server session limit, or a particular network fault. Capture enough non-sensitive diagnostics
to distinguish those causes if they recur.

## Behavior required across all changes

- A parsing failure is different from successfully finding no usage limit.
- A failed observation cannot clear a saved limit, erase a schedule, manufacture a completion, or authorize a send.
- An explicit schedule remains recorded when a dependency is unavailable. Delivery still needs the existing
  current-session, empty-prompt, permission and readiness checks.
- Reconnecting transport never automatically replays a command whose execution or delivery could have started.
  Preserve existing durable claims and uncertain-delivery states for jobs and continuations.
- An error from an older SSH connection cannot invalidate a replacement connection created afterward.
- Reading a reply acknowledges its notification; it does not change whether the current turn has completed.
- Logs retain actionable failure and recovery evidence without recording credentials, full terminal screens,
  command bodies or raw parser input.

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
6. Log a bounded diagnostic containing the parser stage/rule identifier, input length, harness and failure kind.
   Collapse repeated identical failures and record recovery. Never log `RegexMatchTimeoutException.Input` or the
   raw terminal text. Keep diagnostics useful even if redaction itself cannot process an oversized value.

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
- Browser-check loading, detection failure, retry and retained policy/schedule state.

## 2. Recover SSH connections without replaying commands

Primary files: `DeveAgentManager.Core/Ssh/RunnerShell.cs`, `SshConnector.cs`, and the polling callers in
`RunnerMonitor`, `AgentJobCompletions`, `SessionService` and file collection.

### Connection ownership and failure handling

1. Introduce a small testable transport boundary and a connection lease that identifies the specific pooled
   connection generation being used. Keep host-key verification and credential selection in `SshConnector`.
2. Distinguish caller cancellation, a command exceeding its execution budget, connection/channel establishment
   failures, and ordinary remote nonzero exits. Do not treat a remote command error as broken transport.
3. Retire a connection after an observed channel-establishment failure, SSH transport timeout/reset, or failed
   health assessment. Stop new leases using that generation; create a replacement for later operations.
   Do not invalidate by Runner ID alone when handling an error from an older lease.
4. Dispose retired generations safely. Allow healthy in-flight leases to finish when possible; if a hard deadline
   requires closing a stuck connection, affected submitted operations become uncertain. Never replay them as part
   of reconnection. Do not tear down a healthy SFTP connection merely because an independent exec connection failed.
5. Add a bounded queue per Runner exec connection, initially allowing four concurrent commands. Include queue wait
   in the request deadline, release permits on every exit path, and keep unrelated Runners independently usable.
   Record queue wait and active-channel counts so future concurrency changes have evidence.
6. Bound channel-open attempts and enforce the complete deadline, including opening a channel. SSH.NET currently
   opens the channel synchronously before registering command cancellation; applying `WaitAsync` to its returned
   task alone does not cover that phase. Verify the chosen adapter/connection-abort mechanism with a real blocked
   channel-open test. Avoid unbounded detached tasks around synchronous work.
7. Add a short capped backoff for repeated establishment failures, shared by users of that Runner's pool, with one
   reconnect attempt at a time. Reset it after success. Ordinary read-only polling may try again on its next pass.
   `RunAsync` itself must not silently retry arbitrary commands.
8. Emit one clear outage warning and a recovery entry, with rate-limited detail for prolonged failures. Include
   operation category and phase, not shell command contents. Retain stale/unknown observation behavior while offline.

The implementation should be tested against the pinned SSH.NET version. Its current behavior is documented in
[channel opening](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/Channels/ChannelSession.cs)
and [command execution](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/SshCommand.cs).

### Regression coverage

- Timeout/refusal retires a connection; a subsequent read uses a new connection and succeeds.
- A delayed failure from generation A cannot retire healthy generation B.
- Concurrent failures produce one replacement attempt; permits/leases are released after cancellation and errors.
- Ordinary command failure and caller cancellation do not cause unnecessary pool churn.
- Simulate a server accepting a command and dropping the response: the command is issued exactly once and its
  outcome stays uncertain. Cover continuation/job submission through their real claim-handling paths.
- Test deadline expiry while queued, opening a channel and running a command. The bounded adapter must terminate
  outstanding local work and must not allow a late command submission after the caller has timed out.
- Verify a failing Runner does not prevent commands to a second Runner.
- Use an isolated local SSH server or controlled transport fixture to refuse channels, stall a channel-open
  response and drop a connection. Never fault-inject into an active user's Runner.

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
| 2 | SSH leases, failure classification, deadlines and backoff | Deterministic pool tests and isolated transport-failure tests prove recovery and no command replay. |
| 3 | Proxmox fallback classification and accurate sync context | Sampling/migration tests and sync-log assertions pass. |
| 4 | Current-turn completion independent of acknowledgement | Tracker, badge/filter/attention and browser transition tests pass. |
| 5 | Integrated build, documentation and delivery | Release build, full existing suite and affected browser checks pass; reviewed diff and privacy scan are clean. |

Use the installed .NET SDK to build `DeveAgentManager.slnx` in Release, then run the full solution tests with
the repository's configured Microsoft.Testing.Platform runner. The previous baseline was 599 passing tests;
record the new count and any pre-existing warnings without treating that baseline as verification of these changes.

Run affected browser flows in Chromium and Firefox, including phone width and light/dark themes where UI changes
apply. Exercise the real local Manager API with disposable test-only data for error/recovery behavior. Mocks are
appropriate for deterministic screen states but must not replace API and transport integration checks.

Review the final diff for command replay, swallowed failures, changed schedule semantics and cross-connection races.
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
4. Perform disruptive recovery checks only against an isolated test Runner/provider. Verify one failed connection
   does not duplicate a submitted command, and a recovered connection resumes subsequent reads.
5. Check the ordinary sync-failure wording and diagnostic detail. Remove disposable agents, test users and temporary
   credentials created for validation.

Do not claim that the original infrastructure trigger was fixed merely because recovery is stronger or a synthetic
test passes. If live access is unavailable, report deployed verification as pending. Keep the prior build available
for rollback; if a schema change becomes necessary, document database compatibility and backup/restore requirements.

The work is complete when all five behavior groups are implemented, the tests above pass, the change is pushed to
master and CI passes. Live verification has its own recorded outcome. No unresolved parser failure may silently
clear state, no reconnect may replay an ambiguous command, and reading a finished reply must leave it Finished.
