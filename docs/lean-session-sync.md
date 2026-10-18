# Lean conversation synchronization

Periodic synchronization preserves the entire native JSONL history on disk and
parses appended records into a compact metadata index. Reading, exporting,
copying or moving a conversation reconstructs the complete DeveSession through
the same format reader. Native replay data, tool records, thinking signatures,
images and abandoned branches stay in the raw copy; they are not discarded to
make the index smaller. Cross-harness conversion keeps its existing semantics.

## Storage and compatibility

Newly synchronized conversations use `/data/sessions/native/<session-id>/`:

- A native `.jsonl` file containing the exact received bytes.
- A disposable `.index.json` file containing parser state, tree topology and
  bounded text snippets, without native payloads, images or tool data.
- A small `state.json` checkpoint naming those files, the committed byte count,
  SHA-256, source fingerprint, reply preview and last assistant timestamp.

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

Before fetching a suffix, the Runner hashes the entire committed prefix and DAM
compares it with the previous checkpoint. This detects edits anywhere in the
prefix, including same-size rewrites. A mismatch, truncation or different source
path/harness rebuilds from a full snapshot. After transfer, the Runner hashes the
requested snapshot prefix and DAM compares that with the assembled local copy.
A mismatch retries on the normal next pass, preserving the last good checkpoint.
Appends beyond the requested snapshot are allowed and picked up on the next pass;
a busy agent does not have to stop writing. The running harness is not paused.

Prefix and snapshot verification read the requested bytes on the Runner, and
computing the checkpoint hash reads the local native copy. These are sequential disk/hash costs;
they do not transfer or parse the old history again. This is deliberately stronger
than checking only the last few kilobytes. Opening/exporting a complete history
still costs work proportional to its size.

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
It checks metadata/active-branch parity, native title precedence, same-size edits,
truncation, path changes, partial final lines, interrupted transfers, uncommitted
suffixes, index recovery, parsing failures, bounded image-free indexes and readable
summaries during transfers.

`NativeHistoryTransportTests` uses an isolated real SSH/SFTP server. It verifies
suffix transfer, whole-prefix verification, growth during a snapshot and checksum rejection, plus
legacy migration, previews, exports, copying histories and attachments, switching
harnesses with backups and deleting the original without affecting its copy.
Existing API/access and format tests continue to run.

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

Automatic memory evidence remains enabled as before. Compare the deployed
`latest.zip` with earlier evidence: download markers now describe suffix bytes,
and native-parse markers describe the bytes actually reparsed. Full loads should
correspond to explicit history operations or one-time legacy migration, rather
than list refreshes. Compare CPU outside trace collection windows, allocations,
RSS and GC activity. Local tests and measurements do not establish the live
Manager's deployed footprint; record that result separately after rollout.
