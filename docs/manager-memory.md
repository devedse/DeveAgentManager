# Manager memory

The Manager often runs on a small VM. When it runs out of memory, the kernel kills the process without a log line and
Docker restarts it. This page covers what keeps it within its memory and what is logged when it isn't.

## Loading whole conversations

A conversation held in memory takes many times its saved size. Reading a 99 MB Claude history, converting it and
writing it as one JSON string peaked at about 1.2 GB (the conversation itself about 240 MB, the JSON about 290 MB as a
string and 150 MB as bytes). A 450 MB history needed more memory than a 4 GB VM has. A browser tab kept the conversation
page open and reloaded it after every restart, so the Manager was killed repeatedly within minutes.

- **Streaming.** `GET /api/sessions/{id}/content` serializes the conversation straight to the response, so no copy of
  the whole JSON is kept. The same 99 MB history peaks at about 340 MB. The browser reads it as a stream too.
- **Limit.** A conversation whose saved history (as the Sessions page shows it) is larger than
  `CONVERSATION_VIEW_MAX_MB` (64 by default) answers 413 before anything is loaded. The conversation page shows its
  facts and says why the conversation isn't shown. The full history stays saved and the agent keeps working.
- **One at a time.** Conversation views and exports load one whole conversation at a time, so several never add up.
  Loading one of 16 MB or more is logged with its size.
- **Fewer reloads.** The conversation page refreshes its facts on every change of its agent (status, activity), but
  fetches the conversation again only when the conversation changed (its sync time, message count, harness or update
  time).
- **Exports** load the conversation once and write the result to a temporary file in `tmp/sessions` (line by line for
  Claude, Codex and pi), never as one string; it is streamed from there and deleted once sent.
- **Harness switches** never load the whole conversation from Claude Code, Codex or pi. The conversion reads the saved
  native records one at a time, in three passes: the active branch, the size of each part of the conversation, and the
  content of the parts that fit the continuation's budget. Only compact metadata and the kept parts are held. A 1 GB
  Claude history (138,000 records) that needed 2.2 GB as a whole now converts in about 11 s with a peak of about
  300 MB, with exactly the same result. The backup is a copy of the native records, not a loaded conversation. The
  converted history is written to a temporary file, read back for validation and streamed to the Runner, which installs
  it only after its checksum matches. Grok (whose records complete earlier messages) and histories whose active branch
  doesn't run in file order are still loaded whole, one at a time.
- **One history doesn't hold up others.** Each saved history has its own lock, so reading or syncing a large one never
  delays the others.

## Logged large operations

So a problem can be traced to what the Manager was doing, these are logged at Information level, with sizes:

- Loading a conversation of 16 MB or more to show it, before loading and when done.
- Every export, before and after, with the exported size and duration.
- Loading a conversation to continue it in another harness or session, and the file written to the Runner (entries,
  size, conversion and duration).
- A whole history copied again instead of only its new part, with why: the Runner's copy no longer matches the saved
  part, is smaller (replaced or shortened), is a different file, changed harness, or the saved copy is missing.
- The first copy of a history of 16 MB or more.

## Load from open browser tabs

After a restart every open tab reconnects, and each live section used to reload on every event it received, including
the usage refresh of every Runner every 15 seconds. Several tabs made about ten requests a second.

- A hidden tab's sections neither poll nor reload; each reloads once when the tab is shown again. Notifications keep
  polling, so their sounds still play.
- Runner usage refreshes are their own event (`RunnerUsageChanged`); only Runner cards, the dashboard, the Runner page
  and its graph reload for them.
- Bursts of events coalesce into one reload per second per section, and an agent's terminal page reloads for its own
  Runner's changes only.
- The screens list of an agent's terminal, which runs a command on the Runner, isn't polled in a hidden tab.
- The live connection (`LiveUpdates`) retries after 0, 1, 2 and 3 seconds, then every 5 seconds. A tab that is shown
  again retries at once: a browser that froze it, or a computer that slept, lets the Manager close the connection
  after 30 seconds without keep-alives. **Reconnecting…** appears only after 2 seconds, so quick reconnects don't flash it.
  `live-reconnect.mts` checks this.

## .NET heap limit

The Docker image sets `DOTNET_GCHeapHardLimitPercent=0x46`: the .NET heap may use 70% of the machine's (or the
container's) memory. A request that needs more fails with an `OutOfMemoryException`, which the API logs as a failed
request, instead of the kernel killing the whole Manager. Native memory (SQLite, SSH) is not part of the heap.

## Heartbeat and low-memory warnings

`MemoryWatch` writes `diagnostics/heartbeat.json` in the data directory every 5 seconds: the process's working set, the
.NET heap and its limit, the machine's available memory (`/proc/meminfo`) and the longest-running requests (method and
path, never the query; sockets and live updates are left out). A clean shutdown marks it.

- **Low memory.** Under 15% of the machine's memory available, or the .NET heap past 80% of its limit, logs a warning
  with the reading and the requests in progress, at most once a minute.
- **After a kill.** At start-up, a heartbeat from an earlier process that didn't shut down cleanly logs a warning with
  its last reading and requests, and says when memory was low (the kernel most likely killed it). The last reading is
  up to 5 seconds old, so a sudden spike can be missing from it; its requests still show what was running.

The kernel's own record is in `dmesg` on the Manager's VM (`Out of memory: Killed process … (dotnet)`).

## Verification

`ManagerMemoryTests` checks streaming and the 413 limit through the API, streamed exports without leftover files,
identical output from the streaming writers, the start-up report of a killed process with its reading and requests, the
clean-shutdown mark, request tracking without queries or sockets, and the low-memory rules. `SampledHistoryTests`
checks the logged reasons for whole copies; `NativeHistoryTransportTests` covers exports and harness switches over real
SSH. `Browser/conversation-too-large.mjs` checks the conversation page's explanation, its facts and that each
conversation is fetched once, on desktop and phone; `Browser/hidden-tabs.mjs` checks that a hidden tab makes no requests
of its own and catches up once when shown.
