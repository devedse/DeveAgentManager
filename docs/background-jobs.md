# Owned background verification jobs

Use the harness’s built-in background tasks whenever it supports them. Use `dam-tools run-job`
only when the harness has no built-in background-job support. Long runtimes, restart survival
and completion validation are not exceptions to this rule.

On a Linux Runner, `dam-tools run-job` starts an owned foreground command outside
the agent turn. The controller waits for process exit, runs a separate read-only
completion validator, then publishes a durable outbox entry. It never reruns the
workload to deliver a result. The Manager scans that outbox every five seconds
without consuming agent turns and submits one analysis prompt when the original
conversation is selected, idle and has no draft.

Supply structured JSON argument arrays, never a shell-quoted command string:

```text
dam-tools run-job --prepare-json <argv-json> --command-json <argv-json> --completion-json <argv-json> --output-parent <unique-absolute-parent> --machine <non-secret-label> --caption <description>
dam-tools job-status <job-id>
dam-tools job-completion <job-id>
dam-tools job-cancel <job-id>
dam-tools job-reviewed <job-id>
```

The preparation command allocates and verifies an empty unique output parent on
the workload machine. The workload command uses that parent. For QueueCache,
the completion command runs the same CLI on that machine:
`qcache developer verify-completion <unique-absolute-parent> --output-parent`.
That reader requires one campaign and validates its manifest digest, identities,
counts, completion marker and restoration. The helper then checks the event's
parent, campaign ID, schema and stable terminal ID against its job binding.
Existing remote transport may run these commands; use its existing authentication
owner and preserve the exact remote path. Transport commands belong in the private
job record, never documentation or public notification text.

Ownership is persisted before spawn. The local controller and workload PID/start
identity are recorded; private stdout/stderr and validator output stay in that
job's folder. A nonzero workload exit can still have valid failure evidence.
Missing/invalid evidence, preparation failure or controller death produces an
inspection outcome. Remote transport exit alone never proves remote workload exit.
After transport recovery, `job-completion` retries only the supported completion
reader. A live owned worker refuses competing validation through its file lock. If the
result has already been inspected in the active turn, `job-reviewed` records
`REVIEWED` and suppresses a later notification; it does not claim automatic delivery.
Cancellation signals only the recorded local process group after identity checks;
it does not claim a remote workload stopped. Never use it as destructive recovery.

Delivery shares the terminal input lock with quick commands and continuations. A
durable key binds runner/event, agent and conversation, so registering the same
event as another job does not deliver it twice. `SENDING` is flushed before
typing, and `SENT` after Enter. Once typing begins, the notification is submitted
with Enter after a short paste-settling delay, without trying to reconstruct its
text from the visible composer. Long or scrolled input can hide part of the text.
A retry after acknowledgement loss returns `SENT`
without typing. An interrupted ambiguous send becomes `UNCERTAIN`, requires
inspection and is never automatically resubmitted. This gives duplicate-resistant,
at-most-once automatic submission, **not guaranteed exactly-once analysis** across
terminal/process crashes. Switching conversations/harnesses makes the publication
stale rather than delivering it to another conversation. A draft or busy agent
keeps a valid result pending.

This integration requires the updated Manager service and updated Runner helper.
Host tests cover ownership, strict binding, failure/cancellation events, missing
evidence, controller death, reconnect without workload replay, durable pending
outboxes, acknowledgement loss and uncertain delivery. They do not prove a live
Manager upgrade or an actual Windows campaign resumed an agent.
