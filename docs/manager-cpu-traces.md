# Manager CPU captures

The Manager image includes a pinned native CPU profiler and matching runtime
symbols. Captures start on demand. Set `CPU_TRACE_STARTUP=true` to record one five-minute capture on
process startup. A settings manager sees
**CPU capture** in the header and agent/runner/editor toolbars while recording. Selecting it opens
**Manager settings → Diagnostics**, with the mode, current phase, progress, cancellation and downloads.
The global indicator uses live notifications; it does not poll when idle.

## Actual CPU samples and the portable fallback

The native image uses [Grafana's Apache-licensed .NET profiler](https://github.com/grafana/pyroscope-dotnet),
pinned to **1.5.1** with SHA-256 verification for both x64 and arm64 release archives. Its Linux
[CPU-clock timer sampler](https://github.com/grafana/pyroscope-dotnet/blob/pyroscope-1.5.1/profiler/src/ProfilerEngine/Datadog.Profiler.Native.Linux/TimerCreateCpuProfiler.cpp)
samples a managed thread for each 19 milliseconds of its consumed CPU time. Sleeping threads do not accrue
CPU samples. It requires neither privileged Docker mode nor additional capabilities, a separate profiling
server, a Grafana account or a change to the host's perf policy. Profiles stay inside the container and its data
directory. The receiver binds only to loopback and requires a random per-process path generated at startup.
No profiler uploads go to a hosted service.

The sampler is disabled outside captures and during baselines. The profiler remains loaded in the CLR;
loading it and recording measurements still have overhead. CPU values are statistical estimates, not exact
timings. Threads never registered with the CLR and discarded stack walks leave gaps. This profiler retains
managed caller frames and omits native/kernel frames: native library and syscall CPU can be attributed to the
nearest retained managed caller. Symbols alone cannot reconstruct native call stacks that were not retained. Compare summed samples with process and per-thread CPU counters to assess coverage. Inclusive
method times overlap and must not be summed. Percentages in the method CSV use **sampled CPU**, not total VM CPU.

The UI explicitly shows **CPU-clock samples** or **Portable thread samples**. If the native profiler cannot
initialize, it explains the fallback. The portable recorder uses EventPipe, includes waiting threads and
cannot provide a method CPU ranking. A native capture that receives no usable CPU samples fails instead of
publishing an empty ranking as successful. Existing portable downloads remain readable unchanged.

Set `CPU_PROFILER_ENABLED=false` before launching the image to disable loading the native profiler and use
the portable recorder. The normal non-Docker development/test host uses the portable recorder unless launched
with the full profiler configuration. Disabling profiling does not change agent/session behavior.

## Capture phases

| Capture mode | Initial baseline | Sampling | Final baseline |
| --- | --- | --- | --- |
| CPU-clock startup | None | 270 seconds | 30 seconds |
| CPU-clock manual | None | 45 seconds | 15 seconds |
| Portable startup | 30 seconds | 240 seconds | 30 seconds |
| Portable manual | 15 seconds | 30 seconds | 15 seconds |

CPU-clock captures arm immediately, so a short spike is not missed while waiting for an initial baseline.
Startup measurements begin during application initialization. Initialization and profiler startup still take
time; the manifest records the actual windows. The CPU profiler's final local uploads drain for six seconds,
labeled separately from sampling and the baseline. Startup/shutdown overhead adds to the planned duration.
There is no meaningful initial baseline in CPU-clock mode; its zero-duration placeholder phase should not
be used as an overhead comparison. Workload changes mean a final baseline alone cannot isolate profiler cost.

**Capture 60-second CPU trace** starts a manual capture when no other capture is running. Recording belongs to
the Manager; navigation, browser disconnection and sign-out do not cancel it. The Diagnostics panel follows an
active capture every two seconds. **Cancel capture** disarms timers even during sampling. Canceled or failed
captures are not offered as completed downloads.

Startup recording is off by default in Docker and development/test hosts; opt in with
`CPU_TRACE_STARTUP=true`. Set it back to `false` after investigating startup. Cancellation/failure
does not schedule a retry in the same process. The portable fallback retains the process-wide
`DOTNET_EventPipeThreadSamplingRate=0x32` setting (50 ms, interpreted as a hexadecimal DWORD before Main).

## Downloads and correlation data

**Download capture** offers the completed ZIP. With the default Docker volume it also lives under
`/data/diagnostics/cpu/<capture-id>/capture.zip`. Version 3 captures include:

- `capture.json`: process/runtime/image versions, actual recorder, native profiler version, sampling interval,
  phase windows and measured process CPU/allocation/GC summaries. Native captures also include total sampled
  CPU and unresolved leaf CPU, for comparison against process CPU during the sampling phase.
- `process.jsonl`: one-second UTC CPU, memory, allocation, GC and thread-pool counters with phase and uptime.
  CPU-seconds delta divided by elapsed seconds gives busy cores; divide again by `processorCount` for the
  fraction of the process's visible CPU capacity. These are process counters, not a whole-VM measurement.
- `profiles/*.pprof`: native-mode CPU profiles, readable by standard pprof-compatible tools.
- `runtime.nettrace` and `runtime-events.json` in CPU-clock mode: optional lightweight loader/JIT events, with
  explicit availability and timestamps. These identify methods being compiled on compiler threads that may be
  absent from managed CPU stacks. This session requests neither thread samples nor rundown. Compilation event
  durations are elapsed time, not exact compiler CPU time. A failure is reported separately and does not discard
  usable CPU-clock profiles.
- `cpu-methods.csv` and `cpu-methods.json`: self/inclusive CPU-time estimates, explicit unresolved frames,
  sampled-CPU percentages and individual profile windows. The CSV uses milliseconds; JSON uses nanoseconds.
- `operations.jsonl`: elapsed times, byte counts and opaque session/runner IDs for history synchronization,
  transfer, parsing and hashing; sign-in validation/touches; database command kinds; HTTP route templates and
  status codes. History operations also identify sampled versus full verification,
  sample match results and the requested bytes for remote prefix/snapshot hashing. Trace/span identifiers correlate child commands with their owning operation. These are
  **elapsed operation times**, not per-operation CPU times. SQL, parameters, URLs, request bodies, authentication
  headers, arbitrary activity tags and baggage are not recorded. Up to 100,000 start/stop operation events are retained
  through a bounded queue; a final summary reports dropped observations. Operations begun before recording may
  lack timing/owner correlation, and an operation can still be in progress when recording finishes.
- `thread-cpu.jsonl` on Linux: per-thread user/kernel CPU counters, OS thread names and start ticks. Start ticks
  distinguish reused thread IDs. Very short-lived threads can exit between one-second observations.
- `maps-before.txt` and `maps-after.txt` on Linux: module mappings for interpreting native addresses.
- `symbols-manifest.json` in the image: runtime and profiler versions, hashes and sizes of symbol/binary files.
- `manager.nettrace` in portable mode only: managed thread samples and runtime events with method rundown.

**Download current image symbols** is a separate, immutable ZIP containing matching CoreCLR/JIT/native runtime
debug symbols, runtime binaries, and the Manager's assemblies and portable PDBs. The build fetches Microsoft
debug files using the runtime ELF build IDs and verifies that each downloaded debug file matches its binary.
The relevant debug files are also installed beside the runtime libraries. No SDK or symbol-download tool remains
in the final image. This intentionally makes the diagnostic image larger; the download is not duplicated in
every capture. Match its manifest to the one in the capture before using it with older files. It does not
include debug symbols for every system/third-party library, so some native names can remain unresolved.

## Bounds, access and lifecycle

Only one capture runs at a time, with at least 512 MiB free disk space required. Native upload bodies are capped
at 8 MiB and expanded individual profiles at 16 MiB; native profile data is capped at 128 MiB per capture. The
portable recorder has a 16 MiB runtime buffer, 128 MiB raw trace limit and bounded attach/stop/drain operations.
The additional native-mode loader/JIT stream has an 8 MiB buffer and 32 MiB file limit; it closes with a bounded
shutdown and never enables the sampled-thread-time provider.
Packaging and report generation happen after measurements. Only the latest three captures are retained, and
individual captures can be deleted. Completed ZIPs and metadata survive restarts. An unfinished capture becomes
**Interrupted**, its working files are removed, and it is never resumed. A new process can start its own startup
capture.

Status, live notifications, capture downloads and symbols require the Settings manager role. Mutations retain
CSRF/recent-password checks. Downloads are private/no-store, nosniff and support ranges. Manual starts create an
audit entry; automatic starts and phase boundaries are logged. Traces contain method names, file paths and opaque
session/runner identifiers; share them with trusted recipients. No heap dump, conversation export, environment
export, query text or payload export is collected. Operation timing and process/thread measurement collectors
have no active listener or file writer when idle; the loopback profiler receiver remains bound.

## Verification

`ManagerCpuTraceTests`, `ManagerCpuTraceApiTests` and `NativeCpuProfileTests` cover real portable traces, phase
accounting, startup exclusivity, cancellation, failure, retention, restart recovery, access control, sample units,
recursive inclusive counting, expansion bounds, timing privacy and named JIT events without thread samples. The `tools/cpu-profiler/probe` executable
validates the native engine in the built image: it runs a CPU-bound method beside a sleeping worker, checks the
method CPU estimates, cancels another capture and verifies that no POSIX profiling timers remain armed.

The manual **CPU profiler diagnostics** workflow builds the `cpu-smoke` target on native AMD64 and ARM64 runners.
Each probe runs with a read-only root filesystem, no extra capabilities and a private writable capture directory.
`tools/cpu-profiler/verify-container.sh` stops a stalled probe after two minutes and retains console output and
capture files as workflow artifacts. Run this diagnostic after runtime, base-image or profiler changes. Normal
release builds build production images directly and do not run the detailed profiler probe.

ARM64 currently defaults to portable thread samples: its experimental native unwinder failed method CPU attribution.
The manual diagnostic explicitly enables that engine to investigate it. Do not treat an ARM64 portable trace as
on-CPU attribution. AMD64 defaults to the validated native engine.

The isolated `manager-cpu-trace.mjs` browser check covers the real startup capture, global indicator, phases,
navigation, downloads, manual cancellation and phone layouts. Local/container verification and the actual
deployment are separate. After deployment, check that Diagnostics reports **CPU-clock samples**, then inspect
the new ZIP's measured/sample coverage and operation timings. Another portable EventPipe ZIP cannot supply the
missing on-CPU attribution.

## Merge review

Keep the settings panel, access controls, capture/download lifecycle and capture-only
operation timings as permanent debugging tools. Startup capture is now opt-in, so
normal restarts do not automatically incur five minutes of collection. The native
profiler stays loaded for on-demand CPU-clock captures but disarms sampling outside
captures. `CPU_PROFILER_ENABLED=false` remains available when that resident overhead
is undesirable; changing it requires a process restart.

The Docker build intentionally retains matching runtime symbols and the pinned
native profiler, which increases image/download size and build work. Keep the
manual native smoke diagnostic, including the no-extra-capabilities check,
when changing the profiler or runtime. Local host tests do not establish that a
new image loaded its native engine or that a deployed capture succeeded. The
optional JIT context stream can fail independently and reports that explicitly;
its absence must not be presented as compilation coverage.

The sample-based history synchronization is a separate behavioral change from
the diagnostic UI. Review its delayed-edit detection and rollout modes in
[lean-session-sync.md](lean-session-sync.md) when merging this branch. No automatic
merge or deployment is part of this review.
