# Agent activity and attention

Process state and activity are separate. An agent can have a running process while it is ready for input, blocked by an approval, or waiting for its usage allowance to reset. The status button opens an explanation showing the process state, last observation, matched rule, and a short excerpt of the evidence.

The Manager reads the visible tmux screen every five seconds, alongside pane metadata. It does not use output volume as proof that an agent is working: typing a draft also produces terminal output. Named rules inspect the current composer, live activity indicators, control hints, and current errors. Rules have explicit priority and screen regions. Usage errors and interactive controls take priority over activity and ready prompts. Queued questions remain an additional indicator while an agent works. Unknown screens and observations older than 30 seconds are shown as **Unclear**.

A vanished spinner alone does not mark a turn finished. A previously observed working turn must return to a ready prompt with new completion evidence, stable across two observations at least five seconds apart. An ambiguous redraw holds Working for up to ten seconds before becoming Unclear. The pane's process identity includes PID, process start time, and boot ID; restarting a process cannot turn an old response into a new completion. Starting the Manager at an already-ready prompt also does not announce a new completion.

The attention menu appears on normal pages and the terminal toolbar. It lists agents needing an answer, queued questions, unscheduled usage limits, unexpected exits, unseen completions, and persistent unclear states. Limits already scheduled by the Manager or being handled by the harness's own continuation are suppressed. Intentional stops do not create exit alerts.

Completion acknowledgements live in the Manager database. Viewing an agent's terminal while the tab is visible and focused acknowledges the exact completion sequence displayed there. Background tabs cannot mark completions seen, and a delayed acknowledgement cannot hide a newer completion. All devices see the same acknowledgement. Viewing a blocked agent does not dismiss its question or usage limit; those disappear when the actual condition changes.

Only a short evidence excerpt and the latest semantic observation are retained, plus a hash of completion evidence. Full captured screens are not persisted for activity detection. Activity state and acknowledgement sequence survive Manager restarts. These are screen-based observations, so an unrecognized harness UI may require a new rule; the explanation names the rule to help diagnose that case.

Claude usage limits support the `└` error marker and a completed elapsed-time footer after the error. A real response, submitted input, or live working indicator after the error invalidates it. An explicit IANA timezone in the reset message takes precedence over the process timezone. For example, `resets Oct 4, 3am (Europe/Amsterdam)` resolves to October 4 at 01:00 UTC in 2026; opting in schedules a single `continue` at 01:01 UTC. The existing empty-input, process-identity, and last-minute screen checks still apply.

## Verification

Run `dotnet test DeveAgentManager.slnx`. The captured-screen regressions cover ready and working states, compaction, background commands, queued questions, approval dialogs, historical indicators, and the Claude weekly limit. Tracker and SQLite tests cover stable completion, process restarts, stale observations, and delayed acknowledgement.

The browser regression uses an isolated development Manager and mocked agent APIs. Start a development Manager on `http://127.0.0.1:5099` with a temporary `DATA_DIR` and `ADMIN_PASSWORD=preview-test-only`, then run `node DeveAgentManager.Tests/Browser/agent-activity.mjs` with Playwright installed. `PLAYWRIGHT_MODULE` can point to its `index.mjs`; `DAM_TEST_URL` and `DAM_TEST_PASSWORD` override the test connection. It checks the attention menu, explanation dialog, hidden and unfocused tabs, visible completion acknowledgement, and the blinking Claude button on a phone viewport. Screenshots are written to `/tmp`.
