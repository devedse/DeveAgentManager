# Lean conversation synchronization

Periodic synchronization preserves the entire native JSONL history on disk and
parses appended records into a compact metadata index. Reading, exporting,
copying or moving a conversation reconstructs the complete DeveSession through
the same format reader. Native replay data, tool records, thinking signatures,
images and abandoned branches stay in the raw copy; they are not discarded to
make the index smaller. Explicit cross-harness conversion uses structured tool exchanges (see [session-formats.md](session-formats.md));
it remains outside periodic synchronization.

## Storage and compatibility

Newly synchronized conversations use `/data/sessions/native/<session-id>/`:

- A native `.jsonl` file containing the exact received bytes.
- A disposable `.index.json` file containing parser state, tree topology and
  bounded text snippets, without native payloads, images or tool data.
- A small `state.json` checkpoint naming those files, the committed byte count,
  SHA-256 (empty while sampled appends await verification), last full-verification time,
  source fingerprint, reply preview and last assistant timestamp.

The Portal's list previews read the small checkpoint, not the full conversation
or index. Import, copy and harness-switch backups still use the DeveSession JSON
format, now serialized directly to a stream. Legacy `<session-id>.json` files
remain readable and migrate only after a successful native sync. Existing
attachments and access checks are unchanged. Back up the entire data directory,
including its native subdirectory; older builds cannot directly read the new
native storage layout, while DeveSession exports remain portable.

## Transfer and recovery rules

The existing monitor interval stays unchanged. One batched locate command
captures paths, sizes, inodes and nanosecond modification/change timestamps.
An unchanged checkpoint skips download and parsing. Changed files use bounded
SFTP reads into a temporary disk file, without a growing MemoryStream or complete
UTF-16 history string.

A growing file with the same path, device and inode normally uses bounded SFTP reads:
DAM compares the first 4 KiB and the last 64 KiB of its committed prefix with the
Runner, then transfers the new bytes. Overlapping sample ranges are read only
once. The located size and nanosecond fingerprint must match before and after
transfer; a moving source falls back to the full verifier. This path performs no
whole-history hashing on either machine. Protocol buffering can read ahead by a
bounded amount; the requested sample budget is at most 68 KiB plus the new bytes.

This is a deliberate integrity trade-off: an edit outside the sampled ranges,
combined with growth of the same file, can temporarily leave the Manager's copy
with an older middle section. A successful sampled append clears the checkpoint
SHA-256; it is **pending verification**, not a fully verified backup. Normal
history views and previews may show this provisional copy. DAM never merges
histories: after detecting a prefix edit, the verified Runner snapshot replaces
the local generation, and the previous generation is removed after commitment.

Full verification runs on the first sync of each history after a Manager restart,
at least every 30 minutes when sync is available, and when idle/stopped with pending
appends. Idle histories that change are fully verified immediately. Unchanged,
already audited idle histories do not trigger repeated full reads. Explicit
stop/delete/Runner lifecycle syncs and outgoing session/harness changes also
request full verification. These are bounded snapshots; live harnesses need not
stop writing, and bytes appended beyond the requested boundary are picked up on
the next pass. The 30-minute interval is checked by normal synchronization, not a
separate timer; offline Runners are audited when they reconnect.

Same-size timestamp changes, shrinkage, changed identity/path/harness, failed
sample checks and missing local files use full verification/rebuild. Full
verification hashes the entire local committed prefix and compares it with the
Runner's prefix, then verifies the requested Runner snapshot against the assembled
local file. A mismatch retries or rebuilds, preserving the last good checkpoint.
The checkpoint SHA-256 is repopulated only after full verification succeeds.
Local hashes read the file in 1 MiB chunks (`NativeHistoryStore.HashFileAsync`).
`SHA256.HashDataAsync` on a synchronously opened file reads 4 KiB per thread pool
round trip, which made restart audits of a few multi-hundred-megabyte histories
keep about two Manager cores busy for a minute.

Export, shared copies, and session/harness continuation require verification of
any pending checkpoint under the session operation lock. If the source Runner is
unavailable, the operation fails instead of exporting or replaying a provisional
history. Already verified archives remain readable/exportable offline. A failed
best-effort final sync can leave a provisional archive requiring source recovery;
removing a Runner is not evidence that its final verification succeeded.

**Manager settings → Diagnostics → Conversation sync → History verification**
controls rollout and rollback. Settings managers choose a mode and select
**Save conversation sync**. The choice is stored in the Manager database and read
at the start of each sync pass, so it takes effect without restarting the Manager
or agents. Existing in-flight passes finish using the mode they started with.
`HISTORY_SYNC_MODE` has been replaced by this saved setting.

The API is `GET`/`PUT /api/settings/history-sync`, with a JSON body such as
`{"mode":"Full"}`. Both endpoints require the Settings manager role; writes retain
the normal Portal request and origin checks.

The modes are:

| Value | Behavior |
| --- | --- |
| `sampled` (default) | Bounded append checks and the full-audit policy above. |
| `shadow` | Run eligible sample checks, then use the full verifier before every changed checkpoint; warn if sampling accepts an edited prefix. |
| `full` | Full verification for every changed checkpoint, without sample checks. |

Use `shadow` on a test Runner when collecting compatibility evidence; synthetic
regressions are not evidence of a completed live shadow rollout. `full` is the
conservative rollback. Both modes audit existing pending checkpoints before
skipping unchanged files. Old checkpoints without an audit time are audited on
their next sync; no database migration or native-format conversion is required.
Before downgrading the application to an older build, save **Full** verification
and allow synchronization to verify all pending histories. Older builds do not understand
provisional checkpoints: they can skip unchanged files, although a subsequent
changed sync rebuilds through their checksum-mismatch path. Do not downgrade while
an offline provisional archive still needs source recovery.

The checkpoint is replaced atomically only after the raw copy and new index have
been closed successfully. Readers see only committed bytes; an interrupted append
may leave an uncommitted suffix, which is excluded from reads and truncated before
the next append. Failed full replacements leave the old generation intact. Orphan
generations are removed during the next sync. Missing/corrupt indexes rebuild from
the raw copy on a changed sync. An unterminated final line is reparsed when bytes
arrive, so partial JSON or UTF-8 cannot duplicate or lose a record.

## Regression checks

`NativeHistoryStoreTests` compares every appended prefix against the full format
reader for Claude, Codex and pi, including restarts and replay/conversion output.
`SampledHistoryTests` checks bounded reads and the absence of whole-history hash
operations, audit deadlines/restarts/idle, head/tail edits, identity changes,
delayed detection of middle edits, rollback/shadow modes and interrupted appends.
The store regressions also check metadata/active-branch parity, native title precedence, same-size edits,
truncation, path changes, partial final lines, interrupted transfers, uncommitted
suffixes, index recovery, parsing failures, bounded image-free indexes and readable
summaries during transfers.

`NativeHistoryTransportTests` uses an isolated real SSH/SFTP server. It verifies
suffix transfer, sampled reads without remote hash commands, fingerprint/sample rejection,
whole-prefix verification, growth during a snapshot and checksum rejection. It also
checks pending-history export/copy audits and unavailable-source rejection, plus
legacy migration, previews, exports, copying histories and attachments, switching
harnesses with backups and deleting the original without affecting its copy.
`HistorySyncSettingsApiTests` covers role/origin checks, invalid modes, and saved
choices across Manager restarts. The transport regression changes the saved mode
without recreating SessionService and checks that the next sync uses it. The
`history-sync-settings.mjs` browser check covers saving/reloading the selection,
slow and failed loads with retry, failed saves, help and phone layouts. Existing
API/access and format tests continue to run.

## Measurements and deployment verification

An isolated local four-CPU probe of a 127.8 MiB Claude history used approximately
121 MiB peak RSS without an artificial heap limit. Adding about 51 KiB transferred
52,285 bytes and allocated approximately 6.1 MiB. A 58.7 MiB Codex history plus a
similar append used approximately 182 MiB RSS with a 512 MiB probe heap budget;
the append allocated approximately 3.0 MiB. These are local storage/parser probes,
not whole-Manager production memory guarantees or network timing benchmarks.

A separate comparison of 540 status observations against the previous detector
preserved statuses/rules and reduced measured allocations from approximately
30.6 MiB to 7.1 MiB. Fixed status regexes and rule ordering are now cached once,
instead of repeatedly rebuilding expressions through the small global regex cache.
Polling frequency, matching timeouts and status rules are preserved.

The temporary automatic memory collector has been removed after deployment
verification. There are no background memory samples, EventPipe captures or ZIP
refreshes. Existing files under `/data/diagnostics/memory/` remain available and
may be deleted when no longer needed; normal application logs remain enabled.

The deployed 1.0.187 capture confirmed suffix transfers and successful syncs.
Full-history reads still allocate proportionally to the conversation size, and
a short capture does not prove that all retained memory has been reclaimed.
Future investigations can use external .NET diagnostic tools when needed,
rather than leaving an automatic production collector running.
