# Agent activity and attention

The Agent activity page (`/activity`) is available from the sidebar, dashboard and notification menu. It shows every agent's status, explanation, latest reply and observation age without opening a terminal or acknowledging completion. It, the dashboard and agent list refresh every five seconds in addition to live events. All terminal observations run on the Manager, independently of browser viewers.

An observation that has never been saved is distinct from an invalid saved observation, a failed connection or a stale observation. If initial observation remains missing after 30 seconds, its explanation directs the operator to the Runner connection and Manager monitor logs. A command timeout reported as cancellation is recorded as an observation failure unless the monitor itself is stopping.

Missing tmux sessions can return empty metadata with a successful command exit. The snapshot reader validates the pane and PID before reading `/proc`, and treats missing or disappearing processes as an unavailable snapshot for that session. They cannot abort observations for other running agents in the same batch.

Process state and activity are separate. An agent can have a running process while it is ready for input, blocked by an approval, or waiting for its usage allowance to reset. The status button opens an explanation showing the process state, last observation, matched rule, and a short excerpt of the evidence.

The Manager reads the visible tmux screen every five seconds, alongside pane metadata. It does not use output volume as proof that an agent is working: typing a draft also produces terminal output. Named rules inspect the current composer, live activity indicators, control hints, and current errors. Rules have explicit priority and screen regions. Usage errors and interactive controls take priority over activity and ready prompts. Queued questions remain an additional indicator while an agent works. Unknown screens and observations older than 30 seconds are shown as **Unclear**.

A vanished spinner alone does not mark a turn finished. A previously observed working turn must return to a ready prompt with new completion evidence, stable across two observations at least five seconds apart. An ambiguous redraw holds Working for up to ten seconds before becoming Unclear. The pane's process identity includes PID, process start time, and boot ID; restarting a process cannot turn an old response into a new completion. Starting the Manager at an already-ready prompt also does not announce a new completion.

Readiness is independent of completion notifications. A recognized composer can become Ready after five seconds without a new completion fingerprint; it does not produce a Finished notification. Codex's composer is recognized with its model footer or shortcuts hint, including warning counters and typed drafts. Its `Worked for … · HH:mm` footer supplies completion evidence even when the shortcuts hint is hidden. A live working indicator still takes priority over an editable composer.

The attention menu appears on normal pages and the terminal toolbar. It lists agents needing an answer, queued questions, unscheduled usage limits, unexpected exits, unseen completions, and persistent unclear states. Limits already scheduled by the Manager or being handled by the harness's own continuation are suppressed. Intentional stops do not create exit alerts.

Pi's current `Error: 429 quota exceeded` message also shows **Usage limit** and an attention notification. Pi does not supply a reset time for this error, so no reset or automatic continuation is inferred. Later output clears the detection; an error in an older response or in the prompt draft does not count.

Completion acknowledgements live in the Manager database. Viewing an agent's terminal while the tab is visible and focused acknowledges the exact completion sequence displayed there. Background tabs cannot mark completions seen, and a delayed acknowledgement cannot hide a newer completion. All devices see the same acknowledgement. Viewing a blocked agent does not dismiss its question or usage limit. Each bell notification has an explicit dismiss button. Dismissals persist across browsers and Manager restarts, without changing the actual status or continuation schedule. A new semantic observation, process, completion, or changed blocking message produces a fresh notification; routine polls do not. Delayed dismissal requests cannot hide a newer alert.

Only a short evidence excerpt and the latest semantic observation are retained, plus a hash of completion evidence. Full captured screens are not persisted for activity detection. Activity state and acknowledgement sequence survive Manager restarts. These are screen-based observations, so an unrecognized harness UI may require a new rule; the explanation names the rule to help diagnose that case.

Claude usage limits support the `└` error marker and a completed elapsed-time footer after the error. A real response, submitted input, or live working indicator after the error invalidates it. An explicit IANA timezone in the reset message takes precedence over the process timezone. For example, `resets Oct 4, 3am (Europe/Amsterdam)` resolves to October 4 at 01:00 UTC in 2026; opting in schedules a single `continue` at 01:01 UTC. The existing empty-input, process-identity, and last-minute screen checks still apply.

Claude's optional session-feedback survey can appear below a live spinner. Its complete heading and four rating choices are excluded from response-boundary detection, including wrapped lines. Actual replies, submitted prompts, completion footers and approval controls retain their normal meaning.

## Verification

Run `dotnet test DeveAgentManager.slnx`. The captured-screen regressions cover ready and working states, compaction, background commands, queued questions, approval dialogs, historical indicators, and the Claude weekly limit. Tracker and SQLite tests cover stable completion, process restarts, stale observations, and delayed acknowledgement.

The browser regression uses an isolated development Manager and mocked agent APIs. Start a development Manager on `http://127.0.0.1:5099` with a temporary `DATA_DIR` and `ADMIN_PASSWORD=preview-test-only`, then run `node DeveAgentManager.Tests/Browser/agent-activity.mjs` with Playwright installed. `PLAYWRIGHT_MODULE` can point to its `index.mjs`; `DAM_TEST_URL` and `DAM_TEST_PASSWORD` override the test connection. It checks the attention menu, explanation dialog, hidden and unfocused tabs, visible completion acknowledgement, and the blinking Claude button on a phone viewport. Screenshots are written to `/tmp`.

## Latest replies, resources and scheduling

Agent lists show the timestamp of the latest nonempty assistant text message from the normalized saved conversation. User input, tool output, thinking, process restarts and status polling do not advance this time. Conversation sync normally updates it every 15 seconds. Existing saved conversations are indexed locally at Manager startup, including stopped or offline agents; an agent with no recorded response shows “No reply yet.”

Disk bars and the live graph show the root filesystem (`/`) usage measured via SSH `df`, refreshed every 15 seconds. CPU and memory keep their per-second sampling. Disk values are carried into those samples until the next disk probe, with the same one-hour retention and older-reading downsampling. A failed disk probe shows unavailable, not zero. Disk values measure filesystem capacity and usage, which can differ from provisioned VM disk size.

The scheduler supports ordinal dates such as `Oct 1st, 2026 12:30 AM`. Date and time pickers show the device's local timezone and convert to UTC for storage and delivery. The picker selects the reset; delivery is one minute later. The alternative duration selects delivery directly: “Continue in 5 minutes” sends five minutes after scheduling, without another minute added. Nonexistent or repeated local times during daylight-saving changes require another time or the duration option.

Terminal observation runs before provider and disk reads, also during operations on an already-ready runner. Provider failures and conversation-sync failures do not invalidate a successful terminal observation. Observation failures are logged and appear as an explicit failed observation rather than remaining “not observed yet.”

Scheduling is always available through the terminal toolbar. Without a detected usage limit it offers a manual duration, defaulting to one minute. Typing, screen changes, resumed work and missed times do not cancel a scheduled attempt. At delivery time it waits for the same session to be available with a recognized idle or usage-limited screen and empty prompt. The dialog provides explicit cancellation and shows why a due schedule is waiting. Sending inserts `continue`, pauses 350 ms for paste handling, verifies the exact draft and process, and then presses Enter. Ambiguous delivery is never retried automatically.

See [the input and activity review](input-and-activity-review.md) for the reproduced bugs, design assessment and remaining limitations.

Reset parsing excludes Codex background-terminal footers and Claude upgrade hints. The schedule dialog keeps the original detected reset distinct from the selected reset; “Use detected reset (+1 minute)” restores the pickers to that reset and previews delivery one minute later. Both the schedule tooltip and dialog display the device timezone. Resource bars share one value-column width, keeping CPU, RAM and disk tracks aligned.

A sent continuation stops showing as an active limit once the live screen confirms normal Ready or Working activity without a limit message. This returns the toolbar button to the neutral manual scheduler. Missing connections or unrecognized screens do not by themselves prove recovery; uncertain delivery is still kept for review.

A known usage-limit reset is anchored to the original detection time and message timezone. Once it passes, the old limit screen is treated as Ready (idle), without inferring completion. Its dismissible notification remains to explain that the prompt was paused; expiry does not revive an already dismissed notification. Unscheduled or completed limit alerts disappear from the continuation button at expiry. Explicit schedules remain armed for their chosen time (normally reset plus one minute), and uncertain delivery still requires review.

## Shared observations and diagnostics

The Runner monitor captures one observation per pane: joined screen text, process identity, timezone, prompt state, screen hash, and UTC observation time. Activity detection and continuation monitoring consume this same observation. Continuations no longer run a separate polling loop. Explicit one-time scheduling uses the chosen time without requiring a fresh usage-limit detection; every submission rechecks the process, screen and empty prompt on the Runner. A per-session lock serializes quick commands and scheduled continues. One missing pane cannot discard other observations.

Click an agent status, then **Recent diagnostic history**, to see activity transitions, connection failures and continuation/command outcomes. **Copy diagnostics** includes the current detection and history. History survives Manager restarts, is capped at 100 entries per agent, and shows only the last seven days. Repeated identical events are collapsed. The history does not store full screens, prompts or credentials. Files are in `logs/agents/` under the Manager data directory.

The activity page sorts agents needing attention first, provides status filters and a **Next needing attention** link, and shows a bounded preview of the latest assistant text from the active saved conversation branch. Opening this overview does not acknowledge completions. Previews are cached until the saved conversation changes; they update after session synchronization rather than streaming every token.

## Harness quick commands

The terminal key row has a **/** menu; desktop terminals also have it in the toolbar. Selecting a command types it and submits Enter after the text settles. An existing draft or an unrecognized input area prevents submission. Quick commands also work while the agent is busy: changing output does not block them, and the harness decides how to handle each command. Process identity and prompt contents are still checked before submission; scheduled continuations retain their stricter screen check. Commands are allowlisted for the actual harness on the server, and a harness switch invalidates an old menu request. Interrupted or ambiguous submissions are not retried automatically.

- Codex: `/model`, `/status`, `/diff`, `/permissions`.
- Claude Code: `/model`, `/usage`, `/status`, `/help`.
- Pi: `/model`, `/session`, `/settings`, `/thinking`.

Command references: [Codex commands](https://developers.openai.com/codex/cli/slash-commands/), [Claude Code commands](https://code.claude.com/docs/en/commands), and the installed Pi 0.87.1 `docs/slash-commands.md`. Pi's unprefixed editor is checked across its entire prompt box, including multiline drafts. The existing phone keyboard adapter remains enabled; these changes do not add a prompt composer.

The terminal notification menu stays inside the visible viewport, including landscape and keyboard resizing. Long lists scroll within the popup instead of expanding the page. The browser layout check covers wide and narrow touch viewports, rotation, long names, and unchanged terminal geometry while the menu is open.

## Persistent automatic continuation

The continuation dialog offers One-time schedule and Automatic after usage limits. Automatic mode is off by default; enabling it defaults to unlimited sends. Turning on the count limit reveals a field starting at 1. The number is the remaining confirmed sends, not polling attempts. A pulsing green repeat icon and a tooltip with the remaining count distinguish enabled automatic mode. Reduced-motion preferences disable the pulse.

Policies and pending attempts persist in SQLite, scoped to the agent conversation, harness and account. A detected reset schedules a send one minute later using its resolved timezone. A persisted conversation/reset key prevents repeated sends for the same error while it remains on screen, including across Manager or terminal restarts. A new reset can arm the next attempt. Turning automatic mode off cancels its pending attempt; one-time schedules cannot run alongside it. Delivery uncertainty or a changed conversation stops automatic mode. Unknown reset times wait for manual input, and Claude's native continuation is not duplicated.

One-time scheduling accepts an explicit date/time or duration whether a usage error is visible or not. The browser always sends the selected reset timestamp; the Manager adds one minute (durations are measured directly from the Manager clock). Losing or changing the detected error while the dialog is open cannot reject that explicit instruction. Existing drafts and busy agents still wait at send time. The Codex remote-compaction usage-error form is recognized by the same parser used for status and scheduling.

Codex quota footers (such as `⚠ 5h limit: 48% left · /status`) are excluded from error/reset text. Stored detections are normalized at their original observation time when parser fixes resolve them. A time-only reset already passed on the observation day is eligible for catch-up; only a near-midnight morning reset within six hours is interpreted as tomorrow. Recurring continuation still sends at most once for each conversation/reset key.
