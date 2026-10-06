# Runner timezones and usage-limit continuations

Settings → Timezones sets the default timezone for new harness processes. A Runner's page can override it or inherit the default. The Manager exports `TZ` after loading the runner/account environment. Restart an existing agent to apply a change; neither the VM's clock nor other programs on it are changed.

The terminal header shows an amber, gently blinking hourglass when it detects an active Codex or Claude usage-limit error. Click it to review the reset and schedule a single `continue` for reset + one minute. A scheduled button becomes a steady clock and lets you cancel. Reduced-motion preferences disable blinking.

## Reading reset times

The monitor reads the visible tmux pane with wrapped lines joined; it does not search arbitrary scrollback. It looks for a harness error immediately before the current prompt, excluding old errors followed by new prompts, tool output, and ordinary low-quota warnings. A short allowlist of English error formats is intentional: a new or truncated format must not silently turn into a guessed schedule.

An explicit timezone in the error takes priority. Otherwise, the monitor reads only `TZ` from the pane process environment, falling back to the Runner's `/etc/localtime` zone or `/etc/timezone`. It never uses the phone's timezone or an unapplied setting to interpret an existing process's message.

Supported reset text includes 12/24-hour clocks, month/day with optional year, and today/tomorrow. An undated clock is inferred only for the next six hours (or the previous five minutes); this covers the usual session window and midnight rollover without treating a stale error as a reset tomorrow. Dates without a year handle the year boundary. Unknown zones, ambiguous abbreviations, missing dates for distant resets, and DST gaps/overlaps require an explicitly entered UTC reset. The dialog always offers the UTC field for correction.

The resolved UTC instant is stored once per observed limit/process/session, rather than recalculated as the same text ages. A changed limit invalidates an existing schedule. The schedule request identifies the observation the user reviewed, so a stale dialog cannot arm a different limit.

## Sending and recovery

Schedules are stored in SQLite and checked by the Manager every five seconds, including while browsers are closed. Sending starts no earlier than reset + one minute (or the chosen time). A due schedule stays armed until it can send or you cancel it, and says what it waits for: the same harness and conversation/account, an idle agent, an empty prompt. Before sending, a second check on the Runner verifies the pane identity, captured screen hash and empty prompt; it never clears the input buffer. This also catches drafts entered through another tmux client when they are visible. Concurrent activity from external tmux clients cannot be made fully atomic with a screen capture.

A one-time continue is done without sending when the agent got going again without it: it is working when the continue is due, or replied after the reset. Claude continues by itself at the reset ("continuing automatically at 6:40pm"), and someone may have prompted the agent; a late `continue` would otherwise prompt a finished turn or run into the next limit.

The database marks a send as in progress before the SSH side effect. A restart or network failure during delivery leaves an uncertain result that requires checking the terminal; it never automatically retries that send. Continuing into another limit does not automatically grant a new continuation.

Claude's native automatic-wait status is recognized, including when it appears below the prompt (on one line, or as "⚠ Usage limit reached · limit resets 6:40pm" with "Continuing automatically at 6:40pm" under it). Claude's own "● Usage limit reached · continuing automatically" notice after the error belongs to the limit; "● Usage limit reset" ends it. The Manager shows that Claude is already resuming itself and refuses to schedule a second prompt.

## Message references

Reviewed on 2026-09-30:

- [Claude's official automatic-continue documentation](https://code.claude.com/docs/en/interactive-mode#wait-for-a-usage-limit-to-reset): native wait, cancellation and reset behavior.
- [Claude issue #9236](https://github.com/anthropics/claude-code/issues/9236): older usage-limit wording and explicit America/Santiago timezone.
- [Claude issue #32550](https://github.com/anthropics/claude-code/issues/32550): newer reset wording with an explicit UTC suffix.
- [Claude issue #79773](https://github.com/anthropics/claude-code/issues/79773): weekly reset with month/day and Asia/Tokyo timezone.
- [Claude issue #15685](https://github.com/anthropics/claude-code/issues/15685): an explicit year in the reset date.
- Codex: the supplied screenshot and the read-only capture of the running QueueCache terminal (its process timezone was `Etc/UTC`).

Regression tests cover these formats, timezone precedence, midnight/year rollover, DST uncertainty, stale conversation text, native auto-continue, persistence, cancellation, one-shot delivery, and a real isolated tmux session that verifies a draft is preserved and exactly `continue` plus Enter is sent.
