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

Exports and harness switches still load the whole conversation; they are rare and explicit.

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

`ManagerMemoryTests` checks streaming and the 413 limit through the API, the start-up report of a killed process with
its reading and requests, the clean-shutdown mark, request tracking without queries or sockets, and the low-memory
rules. `Browser/conversation-too-large.mjs` checks the conversation page's explanation, its facts and that each
conversation is fetched once, on desktop and phone.
