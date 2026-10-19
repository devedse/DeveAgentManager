# Manager CPU captures

The diagnostic Docker image automatically starts one five-minute capture on each process startup. It begins
before database migration and history backfill, without waiting for the HTTP server. A settings manager sees
**CPU capture** in the page header and agent/runner/editor toolbars while recording. Selecting it opens
**Manager settings → Diagnostics**, showing the current phase, elapsed time, progress and **Cancel capture**.
The indicator uses live notifications; it does not continuously poll when the recorder is idle.

## Capture phases

| Capture | Baseline before | Stack sampling | Baseline after |
| --- | --- | --- | --- |
| Automatic startup | 30 seconds | 4 minutes | 30 seconds |
| Manual | 15 seconds | 30 seconds | 15 seconds |

**Capture 60-second CPU trace** starts a manual capture when no other capture is running. Recording belongs to
the Manager: navigation, browser disconnection and sign-out do not cancel it. The Diagnostics panel follows
active captures every two seconds; the global indicator updates at phase boundaries. Profiler attachment and
shutdown/rundown add a little time to the planned duration and are labeled separately from the baselines.

Baselines record process measurements without this collector's EventPipe profiler attached. Comparing their
CPU with the sampled phase helps identify profiler interference. Startup workloads change over time, and other
profilers can be attached independently, so differences are not exact measurements of collection overhead.

## Sampling configuration

The Docker image sets `DOTNET_EventPipeThreadSamplingRate=0x32`: CoreCLR interprets this DWORD as hexadecimal
milliseconds, so the sampling interval is 50 ms. It is a process-wide setting, read by the runtime before Main;
changing the environment from application code when a capture starts would be too late. The manifest records
the configured interval. This interval reduces the frequency of managed-thread suspensions compared with
normal high-frequency tracing; actual collection overhead still needs to be measured on the deployed machine.

The runtime initialization and sampling implementation are documented in the matching
[CoreCLR EventPipe configuration](https://github.com/dotnet/runtime/blob/v10.0.13/src/native/eventpipe/ep.c)
and [thread sampler](https://github.com/dotnet/runtime/blob/v10.0.13/src/coreclr/vm/eventing/eventpipe/ep-rt-coreclr.cpp).

Set `CPU_TRACE_STARTUP=false` before launching the container to disable automatic startup captures. Other hosting
methods opt in by setting `CPU_TRACE_STARTUP=true` and the same sampling environment variable **before starting
dotnet**. Development and test hosts leave automatic capture off unless explicitly enabled. Manual capture remains
available. Canceling or failing a startup capture does not schedule a retry in the same process.

## Downloads

**Download capture** offers the completed ZIP. With the default Docker volume it is also stored at
`/data/diagnostics/cpu/<capture-id>/capture.zip`. It contains:

- `manager.nettrace`: managed thread samples, GC, loader, JIT and threading events, with method rundown.
- `process.jsonl`: one-second UTC measurements labeled by phase, with process uptime, cumulative CPU, memory,
  allocated bytes, GC counts/pauses and thread-pool work. Divide CPU-seconds delta by elapsed-seconds delta for
  busy cores, then by `processorCount` for the fraction of the process's available CPU capacity.
- `capture.json`: version 2 manifest with process/start/capture times, runtime, architecture, Manager version,
  sampling configuration and phase windows. Each phase includes elapsed time, CPU seconds, average cores,
  CPU-capacity percentage, allocated bytes and GC pause time. Startup/shutdown overhead have their own phases.

Open the trace in PerfView or another compatible .NET trace viewer. Samples include waiting threads: their counts
are not exact on-CPU percentages. This portable capture does not include native kernel stacks or scheduler
attribution. ZIP compression occurs after all measurement phases have finished. Older version 1 downloads are
retained and remain available unchanged.

The implementation uses `Microsoft.Diagnostics.NETCore.Client`; the runtime container needs no SDK, root access,
extra executables, sidecars or host changes. .NET diagnostics must be enabled and its temporary directory writable.
Failures appear in Diagnostics and the Manager log. The collector adds no permanent agent/session instrumentation.

Only one capture runs at a time. The runtime buffer is 16 MiB, raw trace size is limited to 128 MiB, and connection,
stop and drain operations are bounded. Oversized or failed traces are not offered as successful downloads.
Temporary trace files are removed after packaging or failure. Only the latest three captures are retained;
starting a new capture removes the oldest if necessary. Individual captures can also be deleted.

Completed downloads and metadata survive restarts. An unfinished capture becomes **Interrupted** and its working
files are removed. A new process can start its own new startup capture; it never resumes the interrupted recording.
The API and live notifications require the Settings manager role. Mutations retain CSRF and recent-password checks.
Downloads use private/no-store and nosniff headers and support ranges. Manual starts create an audit entry;
automatic starts and phase boundaries are recorded in the Manager log.

Traces can contain method names and filesystem paths. Share them only with a trusted recipient. The collector does
not export conversations, environment variables, HTTP payloads, exception messages or a heap dump.

## Verification

`ManagerCpuTraceTests` checks a readable real trace, baseline/overhead phase measurements, cancellation during
each recording phase, startup exclusivity/idempotence, failure, retention and restart recovery.
`ManagerCpuTraceApiTests` checks startup availability, authorization, CSRF and download behavior. The isolated
`manager-cpu-trace.mjs` browser check exercises a real startup capture, the global indicator, phase progress,
navigation, ZIP contents, manual recording/cancellation, and phone layouts. Local verification and live deployment
verification are separate: a deployed capture must still be checked to assess overhead on its VM.
