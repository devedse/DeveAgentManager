# Runner timezones and usage-limit continuations

Manager settings → Timezones sets the default timezone for new harness processes. A Runner's page can override it or inherit the default. The Manager exports `TZ` after loading the runner/account environment. Restart an existing agent to apply a change; neither the VM's clock nor other programs on it are changed.

The terminal header shows an amber, gently blinking icon when a Codex or Claude usage-limit error needs attention. Click it to review the reset and schedule a single `continue` for reset + one minute. A healthy scheduled wait uses a blue clock; a blocked schedule turns amber. Automatic mode is green only when no unresolved limit needs attention. Unknown reset times use an amber warning icon, and uncertain delivery is red. Reduced-motion preferences disable blinking.

Every harness gets one-time schedules (a chosen time or duration), pi included: `continue` goes into pi's empty prompt box the same guarded way. Detected limits and automatic mode need a recognized usage-limit message, which only Codex and Claude show; pi shows its provider's raw error, which differs per provider.

A limit belongs to the harness, conversation and login it was seen with. After continuing in another harness, with another login or in a new conversation, it no longer applies: the button stops showing it at once, and the next check clears it. A one-time schedule you made stays and waits ("the agent's harness or session changed") until you cancel it.

## Reading reset times

The monitor reads the visible tmux pane with wrapped lines joined; it does not search arbitrary scrollback. It looks for a harness error immediately before the current prompt, excluding old errors followed by new prompts, tool output, and ordinary low-quota warnings. A short allowlist of English error formats is intentional: a new or truncated format must not silently turn into a guessed schedule.

An explicit timezone in the error takes priority. Otherwise, the monitor reads only `TZ` from the pane process environment, falling back to the Runner's `/etc/localtime` zone or `/etc/timezone`. It never uses the phone's timezone or an unapplied setting to interpret an existing process's message.

Supported reset text includes 12/24-hour clocks, month/day with optional year, and today/tomorrow. An undated future clock must fall within the next six hours; near midnight this can establish tomorrow. A past clock stays on the observation day so an overdue continuation can catch up. It must never roll forward just because the same screen is observed again. Dates without a year handle the year boundary. Unknown zones, ambiguous abbreviations, missing dates for distant resets, and DST gaps/overlaps require an explicitly entered UTC reset.

The resolved UTC instant is stored once per observed limit/process/session, rather than recalculated as the same text ages. A newer rejection replaces an automatic schedule, including when a retry changes a weekly reset to a session reset. A one-time schedule chosen by the user remains independent. The schedule request identifies the observation the user reviewed, so a stale dialog cannot arm a different limit.

The advertised reset passing does not establish recovery: the harness may still be stopped, may reject the next request, or may report an old reset. Keep the limit visible, including when a continuation was submitted but the rejection remains. Only a healthy live observation can clear it. An unknown or unavailable observation cannot establish recovery. Existing records marked `Expired` are repaired using their original observation date, without a database migration.

## Sending and recovery

Schedules are stored in SQLite and checked by the Manager every five seconds, including while browsers are closed. Sending starts no earlier than reset + one minute (or the chosen time). A due schedule stays armed until it can send or you cancel it, and says what it waits for: the same harness and conversation/account, an idle agent, an empty prompt. Before sending, a second check on the Runner verifies the pane identity, captured screen hash and empty prompt; it never clears the input buffer. If the screen changed since it was read, nothing is typed and it tries again on the next check; only when the prompt changes between typing `continue` and Enter is Enter left unsent, with a note to check the prompt. This also catches drafts entered through another tmux client when they are visible. Concurrent activity from external tmux clients cannot be made fully atomic with a screen capture.

A one-time continue is done without sending when the agent got going again without it: it is working when the continue is due, or replied after the reset, and the current screen no longer shows a rejection. A current rejection takes priority over cached work/reply timestamps. Claude continues by itself at the reset ("continuing automatically at 6:40pm"), and someone may have prompted the agent; a late `continue` would otherwise prompt a finished turn or run into the next limit.

The database marks a send as in progress before the SSH side effect. A restart or network failure during delivery leaves an uncertain result that requires checking the terminal; it never automatically retries that send. Continuing into another limit does not automatically grant a new continuation.

Claude's native automatic-wait status is recognized, including when it appears below the prompt (on one line, or as "⚠ Usage limit reached · limit resets 6:40pm" with "Continuing automatically at 6:40pm" under it). Claude's own "● Usage limit reached · continuing automatically" notice after the error belongs to the limit; "● Usage limit reset" ends it. The Manager shows that Claude is already resuming itself and refuses to schedule a second prompt.

## Message references

Reviewed on 2026-09-30:

- [Claude's official automatic-continue documentation](https://code.claude.com/docs/en/interactive-mode#wait-for-a-usage-limit-to-reset): native wait, cancellation and reset behavior.
- [Claude issue #9236](https://github.com/anthropics/claude-code/issues/9236): older usage-limit wording and explicit America/Santiago timezone.
- [Claude issue #32550](https://github.com/anthropics/claude-code/issues/32550): newer reset wording with an explicit UTC suffix.
- [Claude issue #79773](https://github.com/anthropics/claude-code/issues/79773): weekly reset with month/day and Asia/Tokyo timezone.
- [Claude issue #15685](https://github.com/anthropics/claude-code/issues/15685): an explicit year in the reset date.
- Codex: sanitized fixtures in `DeveAgentManager.Tests/Screens`, including a dated error followed by a new clock-only error after `Continue`.

Regression tests cover these formats, timezone precedence, midnight/year rollover, DST uncertainty, stale conversation text, native auto-continue, limits left from before a switch of harness, login or conversation, pi schedules, persistence, cancellation, one-shot delivery, and a real isolated tmux session that verifies a draft is preserved and exactly `continue` plus Enter is sent.

## Unavailable detection

A parser timeout or input budget failure is different from finding no limit. Saved limits, selected reset times, armed schedules and uncertain-delivery evidence remain intact. The existing limit API returns a short `detectionProblem` alongside the original state. If the first observation fails before a durable limit exists, it returns a warning-only manual status without inventing a limit or reset. Retry in the dialog reloads the status; automatic background observation also retries. The automatic-policy request is independent, so a failed limit read does not display an enabled policy as off.

Automatic continuation waits while detection is unavailable and does not consume a send count. A one-time schedule remains armed. When only its historical reset cannot be interpreted, it still follows the normal live readiness, empty-input and session checks. Failed interpretation of the current screen cannot authorize a send. Parser diagnostics, including a bounded sanitized excerpt of the failing subject and the expression, are available to the Log viewer role in Manager Logs.

## Regression contract and verification plan

The 2026-10-16 regression exposed two incorrect assumptions in the existing behavior: a passed reset hid an unresolved limit and changed its activity to Ready, and an automatic schedule could retain an earlier reset after a new rejection appeared. Tests had explicitly required the first behavior. The repair keeps recovery, schedule ownership and delivery evidence separate.

| Scenario | Required result |
| --- | --- |
| Dated weekly rejection, `Continue`, then a different clock-only rejection | Detect the latest error in the process timezone and replace only the automatic schedule. |
| Manager restarts before or after reset | Preserve the first observation date, catch up on the due schedule, and submit at most once for that reset. |
| Error remains after reset or submission | Keep Usage limit status and the message visible; do not infer Ready, Finished or permission to resend. |
| Cached assistant timestamp is newer than reset, but current rejection remains | The current rejection wins; a cached timestamp cannot cancel continuation. |
| New rejection has no usable reset | Remove the obsolete automatic schedule, show an amber warning and explain that a time must be chosen manually. |
| Codex appends a yellow warning panel, including a wrapped “Heads up” quota advisory | End the rejection at the warning marker. Repair already-stored combined messages using their original observation date; never use a warning's clock as the reset. |
| Rejection wording changes but the reset is unchanged | Preserve the unsent automatic reservation; a confirmed send still prevents another send for that reset. |
| Explicit one-time schedule encounters changed text | Preserve the user's schedule and its empty-prompt/readiness checks. |
| Claude completion footer ends with `· 1 shell still running` or `· 2 shells still running` | Keep the session rejection and reset visible; recognize native continuation and never send a duplicate prompt. Later assistant/tool output still invalidates the old rejection. |
| Draft, changed screen or native Claude continuation | Preserve the draft and guarded delivery; let Claude's own continuation take priority. |
| Transport loss, unavailable parser or unfamiliar screen | Preserve saved limits and delivery evidence; never infer recovery or replay an ambiguous send. |
| Healthy live activity resumes | Clear the obsolete rejection and retain the automatic policy for the next limit. |

When changing this flow, run the .NET suite, including `UsageLimitParserTests`, `UsageLimitStateTests`, `AgentContinuationTests`, `NativeActivityTests` and the real tmux `LimitTerminalTests`. Run the `auto-continue-dialog.mjs` and `agent-activity.mjs` browser checks through `node tools/security-browser/continuations.mjs` after publishing to `.validation/publish`. The browser wrapper creates an isolated Manager and disposable test-only login, and removes its data afterward. CI runs these checks on pushes; they cover unknown resets, future schedules, blocked delivery, unresolved submissions, uncertain delivery, independent loading failures and healthy automatic mode, including a narrow dark-theme screen.

For a new report, compare the latest visible rejection, the running process timezone, the stored first observation/reset and the current delivery state. The agent diagnostic history records detected reset/timezone and automatic-schedule replacement; Manager logs record detection and scheduling transitions. Parser failures additionally record the sanitized, bounded subject and expression. Do not replace these with guesses based on phone time or a historical error. Record live deployment verification separately from local test success; tests do not prove that the running Manager has been upgraded.

Include all visible panels when transcribing a reported screen into a sanitized fixture. The follow-up to 1.0.179 showed why: a yellow “Heads up” warning after a valid clock was missing from the first fixture and was appended to the stored reset text. Live and stored-message parsing must share the same panel boundaries. Warning wording and line wrapping may change; a later assistant/tool result must still invalidate the earlier rejection.
