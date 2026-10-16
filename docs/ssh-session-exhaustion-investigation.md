# SSH session exhaustion investigation and recovery

## Implemented correction

The command pool now reuses up to ten SSH transports per Runner, lending each transport to one operation at a time. Ten preserves the previous SSH.NET command concurrency budget; it does not increase the Runner's MaxSessions setting. Connections are created on demand with authentication serialized within each pool. Sequential operations reuse an idle connection. Waiting for capacity and connection setup share the caller's command deadline.

Exclusive leases let cancellation close the affected transport during SSH.NET's synchronous channel setup, as well as while awaiting a result. Later independent operations can open a replacement. Other leased transports and browser terminals keep running. Failed operations are never replayed: a remote command may have executed even when its result was lost. Setup exceptions from streaming commands are handled inside the same protected execution path, and output pumps are owned through cleanup.

Endpoint/credential invalidation retires the old pool: idle transports close immediately and existing operations drain before their connections close. Old failures act only on their own leases, so they cannot remove a newer replacement. Manager disposal closes active transports during shutdown. SFTP uses its own exclusive single-transport pool and asynchronous transfers; command connection failures do not discard a healthy file transfer.

Transport failures log Runner ID, connection ID/age, the known execution phase, exception type and cancellation state. The public library API does not distinguish channel open from exec-request submission, so that combined phase is reported honestly. Shell text, output and exception payloads are excluded from these diagnostics. No keepalive, server capacity or workload replay policy was changed.

The isolated OpenSSH regression uses loopback, temporary keys/data and the normal MaxSessions=10. It verifies sequential reuse, a twenty-command burst on the bounded pool, simultaneous commands on distinct transports, deadline recovery, a running command surviving pool retirement, no duplicated side effect, cancellation during a paused channel open, no late exec request, streaming output and SFTP. It resumes and removes only its disposable server processes. CI installs openssh-server for this test. Unit tests cover capacity, canceled waiters, late old failures and retirement during connection setup. Live deployment behavior must still be recorded separately from these tests.

Local validation: all 659 tests pass, the Release solution builds, and activity/scheduling browser checks pass in Chromium/light and Firefox/dark at desktop and phone sizes. The read-only native probe was also checked against running sessions: a completed idle session returned its recorded completion, while long active sessions without a lifecycle event in the bounded tail fell back to screen evidence. No production SSH service or agent was restarted for verification.

## Historical investigation

The remainder records the evidence collected before implementation and the proposed direction at that time. The initial production delay remains unproven; the confirmed recovery defect can be corrected independently.

Investigated 2026-10-16. Status: evidence collected and correction proposed; SSH transport is unchanged.

## Finding

The reported channel refusals were caused by exhaustion of the SSH server's session slots on the Manager's
existing command connection. That connection did not have to disconnect for commands to fail. The server
recorded 54 `no more sessions` errors for that connection during the supplied failure window.

An isolated reproduction with the repository's pinned SSH.NET 2026.0.0 and the normal ten-session limit shows
how channel-open timeouts can leave server slots occupied after the client discards those channel objects.
Later commands then fail on a connection whose `IsConnected` remains true. A new connection works.

This is a demonstrated library failure mode and a strong fit to the production sequence. It does not prove
that all production slots were abandoned opens, or explain the initial stall. No historical channel trace or
per-process resource sample establishes those details. The deployed binary identity also needs confirmation
before claiming the fixture reproduces every detail of that deployment.

## Evidence and uncertainty

| Observation | Meaning | Limit |
|---|---|---|
| 54 server-side `no more sessions` records on the affected authenticated connection | Server session allocation was exhausted at those times. | The records do not identify each occupied slot or its command. |
| That connection was still established when inspected later | A TCP disconnect is unnecessary to explain this incident. | A later observation does not exclude other historical transport failures. |
| Effective current `MaxSessions` is ten for the Manager account and peer, including `Match` rules | The actual configuration supports ten concurrent session channels per connection. | Historical configuration is not independently reconstructed. |
| Earlier channel-open and command timeouts precede the refusal burst | There was a delay before repeated refusal. | A timeout does not identify its underlying resource or network cause. |
| Two system services reported memory-pressure cache flushes at the refusal burst | Memory pressure affected the guest at that time. | No sample proves it caused the SSH delay; no OOM kill was recorded. |
| The guest currently has about 8 GB RAM and no swap | Memory headroom deserves measurement during recurrence. | Current free memory and cumulative pressure totals cannot reconstruct that incident. |
| A ten-session fixture reproduces persistent exhaustion after delayed opens | The pinned client can leave an established connection unusable after this sequence. | The injected pause is a test stimulus, not evidence that production was deliberately paused. |

The first journal inspection queried the SSH service unit only. The decisive messages were attached to an
authenticated session scope, so that query missed them. A broader query by `sshd` and `sshd-session` identifier
found the refusals. This corrects the earlier report that no refusal evidence had been found.

## UTC timeline from the supplied incident

| Time on 2026-10-15 | Evidence |
|---|---|
| 18:02:20 | Manager terminal-state command exceeded its 30-second deadline. |
| 18:02–18:04 | Manager records channel-opening timeouts; session synchronization and job collection are affected too. |
| 18:03:04.704 | First server `no more sessions` record found in the checked window. |
| 18:04:07.530 | Journaling service reports memory pressure and cache flushing. |
| 18:04:07.592–07.725 | Another 53 server refusals; the resolver also reports memory pressure during this interval. |
| Around 18:04:07 | Manager reports `Failed to open a channel after 10 attempts`. |

The retained journals show no matching SSH service restart or OOM kill. Absence of those events is not proof
that the guest had adequate resources. No historical sampling data establishes the duration or origin of the
stall. The separate recorded socket reset on another date must not be assigned this cause without its own evidence.

## Controlled reproductions

Both experiments used a disposable loopback OpenSSH server, temporary keys and the pinned SSH.NET package.
All fixture processes, keys and data were removed afterward. Production SSH settings and processes were untouched.

### Ordinary saturation recovers

With a one-session server, one live command occupies the slot. A competing open receives ten refusals and the
same exception reported by the Manager. `IsConnected` remains true. After the occupying command finishes,
another command succeeds on the original connection. This is why every refusal must not trigger blanket reconnects.

### Delayed opens can leave the connection unusable

1. Configure ten server sessions and use the client's default ten-session allowance. Connect and run a baseline read.
2. Pause only the fixture's authenticated server process. Use a short client operation timeout to bound the test.
3. Attempt ten channel opens sequentially. All ten time out and their command objects are disposed.
4. Resume the fixture process. The delayed channel opens can now allocate server sessions, while the client has
   discarded their channel objects and cannot close them through those disposed commands.
5. Probe the original connection twice. Both probes fail after ten refusals, with `IsConnected` still true.
   The server records twenty session-limit refusals.
6. Open an independent connection and run a new read. It succeeds without replaying a failed operation.

The repository currently reuses any pooled command client whose `IsConnected` is true. Execution invalidates
the pool for `SshConnectionException`, but not for these channel-open timeout/refusal exceptions. That allows
subsequent polling to keep using an exhausted connection.

SSH.NET performs channel opening before submitting the exec request. Its channel-open refusal handler discards
the server reason before the aggregate exception, so the Manager message alone cannot identify a server limit.
See the pinned [channel opening implementation](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/Channels/ChannelSession.cs),
[command execution](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/SshCommand.cs) and
[channel disposal](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/Channels/Channel.cs).
OpenSSH's [session allocation](https://github.com/openssh/openssh-portable/blob/V_10_0_P1/session.c) records
`no more sessions` when it cannot allocate a session; the
[MaxSessions manual](https://man.openbsd.org/sshd_config#MaxSessions) explains the per-connection limit.

## What a user can notice

- Agent activity can become Unclear, and Finished/Working information can stop updating because terminal observation fails.
- Conversation synchronization, Runner disk readings and DAM job-completion delivery can be delayed or fail.
- Manager actions that require pooled commands, including start/switch/setup and parts of file access, can wait or fail.
- Automatic continuation can be delayed because it also needs successful observation and transport checks.
- Already running harnesses and tmux workloads generally continue on the Runner. Separate browser-terminal SSH
  connections can remain usable. These logs do not establish that the agent workload stopped or conversation data was lost.

Proxmox resource sampling uses a separate HTTP path. Its brief outages need separate diagnosis; they are not
evidence of an SSH disconnect. An SFTP transport is separate too, although some file operations also run pooled commands.

## Proposed correction for review

1. Track command connection generations and operation phases. After a channel-establishment timeout, stop handing
   that affected connection to later operations. Dispose/retire it safely and let later independent operations
   establish a fresh one. A late failure from an old generation must never remove its replacement.
2. Distinguish establishment timeouts, command deadlines after submission, server refusals and actual socket loss.
   The present public execution API does not expose every phase; use supported diagnostics or a narrowly scoped
   library change to make that distinction dependable. Do not guess from the generic timeout message alone.
3. Never replay the failed command automatically. Once execution or terminal delivery could have started, preserve
   existing uncertain-delivery and durable-claim rules. Retirement can interrupt concurrent commands, so account for
   their leases and outcomes explicitly rather than treating them as safe to retry.
4. Log generation, connection age, operation category, active/opening channel counts and failure phase/reason.
   Do not log shell bodies, credentials or terminal histories. Pair recurrence with guest/cgroup memory and pressure samples.
5. Repeat the ten-session delayed-open fixture, temporary-saturation fixture, concurrent cancellation and old-generation
   failure cases. Verify recovery, no late workload submission after a timed-out establishment and no duplicate delivery.

This evidence does not justify an arbitrary four-command cap, increasing `MaxSessions`, longer timeouts, changed
keepalives or restarting the Runner's SSH service. Capacity tuning can follow measured legitimate concurrency.
Resource changes can follow measured guest/cgroup pressure. Neither is implemented by this investigation.

If the pool is currently wedged, replacing its command connection is the relevant recovery. There is no targeted
operator action for that today. Restarting the Manager also discards its pools but interrupts browser connections;
it is a broader operational workaround, not a diagnosis or a replay mechanism. No production restart was performed.

## Capturing evidence on recurrence

Query the process identifiers as well as the service unit, using the incident's UTC interval:

```sh
sudo journalctl --utc -t sshd -t sshd-session \
  --since "YYYY-MM-DD HH:MM:SS UTC" --until "YYYY-MM-DD HH:MM:SS UTC" \
  -o short-iso-precise
```

Correlate the authenticated server process, connection peer and effective settings locally. Keep operator
identities, addresses, key fingerprints and raw authentication logs out of tracked documentation. Current INFO
logging already captured the decisive refusal, so global DEBUG logging and an SSH restart are unnecessary.
Any future increase in server logging applies to new connections; it cannot be assumed to affect an already
authenticated session. Preserve the deployed Manager version and UTC clock alignment alongside the evidence.
