# Manual Manager CPU captures

Settings managers can open **Manager settings → Diagnostics → Capture 60-second CPU trace**.
Recording belongs to the Manager, so navigation, browser disconnection and sign-out do not cancel it.
Reopen the page to follow an existing recording, or use **Refresh captures**. **Cancel capture** stops
it without publishing an incomplete download. No diagnostics collection runs until explicitly requested.

The completed ZIP is available through **Download capture** and is stored at
`/data/diagnostics/cpu/<capture-id>/capture.zip` when using the default Docker data volume. It contains:

- `manager.nettrace`: EventPipe managed thread samples plus GC, loader, JIT and threading events, with method rundown.
- `process.jsonl`: one-second UTC measurements of process CPU time, memory, allocated bytes, GC counts and pauses,
  and thread-pool work. CPU seconds are cumulative: divide their change by elapsed seconds for the number of busy
  cores, then divide by `processorCount` in the manifest for the fraction of the process's available CPU capacity.
- `capture.json`: capture times, process ID, processor count, runtime, architecture, Manager version and provider settings.

Open the trace in PerfView or another compatible .NET trace viewer. Managed samples include waiting threads;
sample counts must not be presented as exact on-CPU percentages. Compare stacks with the process measurements.
This portable capture does not include native kernel stacks or scheduler attribution. Recording and its measurements
add some overhead to the process being investigated. ZIP compression happens after recording has stopped.
See Microsoft's [diagnostics client library](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostics-client-library)
and [trace profiling descriptions](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).

The implementation uses `Microsoft.Diagnostics.NETCore.Client` directly; the runtime container needs no SDK,
root access, extra executables, sidecars or host changes. .NET diagnostics must be enabled and its temporary
directory writable. Capture failures appear on the page and in the Manager log.

Only one recording can run at a time. It lasts 60 seconds, with bounded connection/stop/drain timeouts,
a 16 MiB runtime buffer and a 128 MiB trace-file limit. An oversized or failed trace is not offered as a successful
download. Temporary trace files are removed after packaging or failure. Only the latest three captures are retained;
starting another removes the oldest when necessary. Individual captures can also be deleted.

Completed downloads and capture metadata survive restarts. A restart marks an unfinished capture **Interrupted**
and removes its temporary files; it does not resume or repeat recording. The authenticated API requires the Settings
manager role for status, download and mutations, with the existing CSRF and recent-password checks for mutations.
Downloads use private/no-store and nosniff headers and support ranges. Starting a recording creates an audit entry.

Traces can contain method names and filesystem paths. Share them only with a trusted recipient. The collector does
not export conversations, environment variables, HTTP request payloads, exception messages or a heap dump.

`ManagerCpuTraceTests` parses a real trace and checks process measurements, exclusive recording, cancellation,
failure, retention and restart recovery. `ManagerCpuTraceApiTests` checks authorization, CSRF and download behavior.
The isolated `manager-cpu-trace.mjs` browser test records a real 60-second trace, navigates away and back, downloads
the ZIP and checks its contents. Live deployment verification is separate from these local checks.
