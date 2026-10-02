# Input, activity and scheduled continuation review

Reviewed 2026-10-01 against the implementation at `adab7a5`, the existing comparison report and its local reference sources.

## Findings and changes

| Finding | Effect | Change |
|---|---|---|
| Autocorrect can strip the phone adapter's synthetic leading spaces; the adapter restored them by rewriting the textarea. | The keyboard's following unpadded Backspace edit erased earlier terminal draft text. Existing tests assumed the keyboard adopted the rewritten padding. | Keep stripped padding virtual through replacement, composition and deletion. Do not rewrite the IME's context after correction. Handle Backspace in an empty unpadded field explicitly. Dispose document listeners and pending composition callbacks with the terminal. |
| Codex progress detection required a bullet and, for dynamic labels, an interrupt hint. | Valid reduced-motion or customized progress timers became Ready. Animated prompt markers were missed too. | Recognize bounded live timers with optional bullet and interrupt hint, retain narrow-screen cases, and recognize animated prompt markers. Exclude completed/tool-result labels and transcript-viewer controls. |
| Ordinary assistant commentary was treated as completion evidence. | A pause or redraw could produce a false Finished notification. | Keep readiness separate. Codex completion notifications require its explicit completed-turn footer. The completion identity is the footer, not changing or reflowing conversation text. |
| Completion stabilization only covered a direct Working-to-Ready transition. | Working → Unclear → Ready could announce completion on one sample. | Require stable new completion evidence across two observations at least five seconds apart, regardless of the intermediate state. Record an already-visible completion marker as the baseline when work is first observed. |
| A screen matcher timeout escaped into the Runner batch. | One problematic screen could invalidate observations for unrelated agents. | Return an explicit matching-timeout result for that screen and retry on the next poll. |
| Scheduled continuation delivered text and Enter in the same input burst. | Codex's paste handling could leave the text unsent while the Manager reported Sent. | Insert text, wait 350 ms, verify the same process and exact `continue` draft, then send Enter. If the draft changes, leave Enter unsent and keep the schedule waiting for an empty prompt. |
| All continuation results except Scheduled used the same hourglass and generic scheduling tooltip. | Sent, Cancelled and Uncertain looked like a new scheduling opportunity. | Give each outcome its own icon and explanation. Sent/Uncertain dialogs show the outcome rather than time-entry controls. Poll the button so results remain fresh without a terminal-state event. |
| Relative scheduling replaced the detected reset with a fabricated reset one minute before delivery. | The UI could report an incorrect allowance-reset time. | Store the requested delivery time separately and retain the detected reset. |

## Manual scheduling

The toolbar always offers scheduling, including without a detected limit. The no-limit dialog defaults to a one-minute duration. This is an explicit manual mode recorded separately from usage-limit detection; it never fabricates a limit. Once scheduled, typing, screen changes, work resuming, changing limits and missed times do not cancel the request. At delivery time the Manager waits for an empty, recognized idle/usage-limited prompt; drafts are never submitted. A changed harness/session or unavailable Runner leaves it waiting with an explanation. The user cancels through the dialog. Delivery uses the same one-shot claim and guarded submission as limit-based schedules. Completed results clear when a live screen confirms normal ready or working activity without a usage limit. The neutral scheduling button remains available for another explicit manual attempt. Uncertain network delivery remains a reported outcome without automatic retry.

## Design assessment

Named rules, explicit reasons, process identity, stale-observation handling and a separate completion sequence are useful. A configurable manifest engine is not necessary for three supported harnesses. The useful lesson from the reference is to constrain evidence to the live region and distinguish readiness from completion; its exact timing constants assume a local runtime and do not fit five-second SSH polling.

The direct phone adapter still streams edits. A separate Compose prompt mode remains the stronger long-term option for full mobile text editing: keep a normal local draft, submit deliberately, retain it until acceptance, and do not retry ambiguous delivery automatically. It should be an explicit mode because another browser or the harness can already have a draft. This review fixes the reproduced direct-input bug; it does not add that separate feature.

Completion notifications remain conservative. A cropped or missing completion footer may omit a notification while the agent can still show Ready. Two indistinguishable completion footers cannot establish distinct completions. The scheduler does not use Finished notifications as permission to send: it checks current readiness, process identity and the prompt separately.

## Verification

- Reproduced the unpadded autocorrect/Backspace sequence before fixing it: `existing draft whena` incorrectly became `whe`; it now becomes `existing draft whe`.
- Tested replacement, composition deletion/reinsertion, caret movement, repeated Backspace and adapter disposal in the JavaScript regressions.
- Used native Chromium textarea Backspace events after an IME replacement, then sent the adapter's actual edit bytes to an isolated Codex 0.159.2 prompt. The earlier draft was preserved. No model prompt was submitted by this typing check.
- In the isolated Codex prompt, immediate text-plus-Enter left `/status` unsent; inserting it and waiting 350 ms before Enter opened the local status command. This validates submission timing without starting agent work.
- Added activity cases for reduced-motion timers, missing interrupt hints, animated prompts, transcript viewing, commentary, old completion markers, reflow and completion after an unknown observation.
- Added continuation tests for insertion/submission spacing, preserving an edited prompt during the delay and keeping the real reset during relative scheduling. Tests cover one-shot delivery, restart recovery, explicit cancellation, timezone handling, preserving drafts, keeping schedules across screen changes and submitting after a draft is cleared.

These checks reproduce the event sequence in a browser and real terminal editor. They do not replace a physical Samsung keyboard check or confirm the outcome of the user's past scheduled attempt, whose server logs were not available.
