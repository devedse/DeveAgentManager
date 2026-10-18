# Automatic Manager memory evidence

Container deployments automatically collect bounded diagnostic evidence under
`/data/diagnostics/memory/`. This observes the existing session processing; it does
not change synchronization frequency or formats, force a collection, or take a
heap dump. Non-container development runs are disabled by default. Set
`MEMORY_DIAGNOSTICS_ENABLED=false` to opt out, or `true` to enable a local run.

## Retrieving the evidence

After deploying, allow at least two minutes of normal agent activity, then retrieve:

```text
/data/diagnostics/memory/latest.zip
```

This is the path **inside the Manager container**, relative to its existing data
volume. A different `DATA_DIR` moves the `diagnostics/memory` directory with it.
No extra software, shell profiling commands, privileged container flags, public
endpoint or configuration change is needed. The ZIP is replaced atomically and
refreshed after traces and approximately every minute. Reading it does not stop
the Manager. A startup-only ZIP can exist before the first trace completes.

If the process is killed during collection, its persisted `run-*` directory
contains the samples and any partial trace written before the kill. On restart,
the new ZIP automatically includes the previous run's evidence under
`previous-run/`, including incomplete traces. Retrieve the same `latest.zip`;
there is no additional recovery command to run. A partial trace remains explicitly
labeled as partial rather than being treated as a successful collection.

## What is recorded

All timestamps are UTC.

- An immutable `manifest.json` with build, runtime, PID, CPU count, GC mode/budget
  and collection settings, retained even when sample logs rotate.
- A memory sample every five seconds: process RSS and private bytes, estimated
  managed bytes, last-GC heap size and fragmentation, GC committed bytes, cumulative
  allocations, collection counts, CPU time, available system memory, cgroup memory
  usage/limit and concurrent observed operations. Last-GC readings are not instantaneous live-object counts.
- Begin/end markers around history downloads, native parsing, serialization, saved
  history reads/writes, reply-preview generation and startup message-time backfill.
  Markers include sizes, durations, success, parent/operation numbers, harness and
  an opaque session tag. Allocation deltas are **process-wide**, so overlapping
  operations cannot be assigned exclusive allocation totals from these deltas.
- Runtime allocation/GC traces with sampled allocation stacks, loader/JIT metadata,
  method rundown and the same operation markers. The Microsoft diagnostics client
  attaches to the Manager itself; there is no diagnostic sidecar to install.

Prompts, conversation text, tokens, environment variables, account names, native
history paths and raw session IDs are not included in the JSON samples. Trace
providers are limited to runtime events and these operation markers, rather than
HTTP request payloads or heap contents. Runtime traces can contain assembly paths
and symbol names and belong with internal diagnostic artifacts.

## Capture triggers and limits

The sampler starts before migration/backfill. The first trace starts when RSS
reaches 512 MiB or ten seconds have elapsed. It runs for about 30 seconds; ending
and draining the trace can take several additional seconds. A second trace is
eligible after a minute if RSS has grown another 256 MiB since the previous
capture finished, or the first capture failed. At most two attempts occur per
process. A restart begins a new diagnostic run.

A trace is not started with less than 256 MiB of effective headroom, and its
window ends early if that falls below 128 MiB. Effective headroom is the smaller
of available system memory and remaining cgroup memory when a limit is present. This is a guard against adding load
under pressure, not a guarantee that a rapidly allocating process cannot OOM.
The EventPipe circular buffer is 16 MiB. Each trace file is capped at 32 MiB;
size-limited files retain the `.partial.nettrace` suffix and are explicitly labeled
in the JSON log, including when rundown crosses the limit. They remain in the ZIP.

There are two rotating JSON logs of approximately 5 MiB each, bounded snapshot
copies for packaging, at most two traces per run, three retained run directories,
and one latest ZIP plus its temporary replacement. The ZIP includes the current
and previous run. This bounds disk use to
roughly a few hundred MiB. Diagnostics failures are reported without preventing
session work; the ZIP's `trace-error` rows explain failed capture attempts.

Tracing and packaging themselves allocate some memory and consume CPU. Trace
start/end timestamps identify these intervals so their overhead is not mistaken
for ordinary application behavior. Sampling is observational: no periodic forced
GC, heap-dump trigger, application memory cap or automatic restart is introduced.

## Analysis and verification

Correlate CPU/allocation/GC changes with the operation markers and source sizes.
Inspect sampled allocation stacks in the `.nettrace` to find actual allocating
methods. A high RSS by itself does not establish a managed leak; compare managed
heap, committed memory, collections and activity over time. Allocation traces
identify allocations, not a complete retained-object graph. If retention remains
unexplained, decide separately whether a heap capture with adequate headroom is
needed rather than inferring roots from allocation volume.

`MemoryDiagnosticsTests` verifies disabled behavior, container defaults, bounded
logs, opaque tags, parent operations, capped partial traces and bundle contents.
Its integration test captures a real EventPipe trace, converts it with TraceEvent,
and checks sampled allocations, call stacks and operation markers. Host tests and
published-app validation are distinct from verifying capture on the live Manager.

Upstream references:

- [Microsoft diagnostics client library](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostics-client-library)
- [Allocation and GC tracing](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)
- [Heap-capture overhead](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump)

## Incremental history synchronization

See [lean conversation synchronization](lean-session-sync.md) for the optimized
storage path and its recovery rules. Download markers now report the requested
suffix size, and native parsing markers report the bytes actually parsed (a suffix
or a full rebuild). Native synchronization no longer serializes the whole history
on every update. List reply previews read a small persisted checkpoint; full saved
history reads should correspond to explicit operations or legacy migration.
