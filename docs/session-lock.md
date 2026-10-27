# The session lock

The Manager's session lock (`SessionService.LockAsync`) serializes agent lifecycle steps (start, stop, switching
harness, changing a workspace's conversation, creating agents and workspaces, moves, deletion) and recording synced
histories. It is held only for short steps. Anything that can take long runs without it: SSH transfers, hashing,
conversions, clones, uploads and exports. A slow or stuck step therefore delays at most the agent it concerns.

Before this, status checks and history syncs took the lock too. One long holder (a large switch, a clone, an export,
or an audit of large histories after a Manager restart) stopped status checks on every Runner, so after 30 seconds
every running agent showed **Unclear**.

## Status checks

`AgentService.RefreshRunnerAsync` reads the terminals without the lock. `RunnerMonitor` checks each Runner on its own
schedule: a slow or unreachable Runner does not delay the others' next check. Each Runner's conversations sync as a task
of their own too, so a long sync (an audit of large histories after a restart) never delays its agents' status.

Results are written under a short gate (`_observationWrites`, `AgentOperations.cs`). Every operation on an agent marks
it busy (`LockAgentAsync`, `BeginOperationAsync`) and takes that gate briefly when it begins. A reading is dropped for an
agent that had an operation running or beginning while its terminal was read, or whose state, update time, harness,
native session, tmux session or Runner changed meanwhile. The next check sees it as it is then. So a Stop that runs
while the terminal is read can't be reported as an unexpected exit. An operation is marked once it holds the lock, so an
agent's status stays current while the operation waits for it. Typing a usage-limit **continue** takes the lock, as a
job's report does, so it never reaches a harness that is being stopped or replaced.

## Long operations

Switching harness, changing a workspace's conversation (**Resume here**) and creating or removing a workspace run in
steps:

1. Under the lock: check, stop the harness, synchronize and verify the latest history, and open a snapshot of it.
2. Without it: back up, convert or copy the conversation and install it on the Runner (and clone the workspace).
3. Under the lock again: record the result, after checking that the agent and its conversation are unchanged, and start
   the harness.

Meanwhile the agent shows **Starting** with what it is doing. Its own actions (start, stop, switch, a conversation
change, moves, workspace removal, deletion, terminal attach) are refused until it ends, and status checks leave it
alone. Once the harness has been stopped, the operation is no longer tied to the request: a closed tab or a proxy timeout
doesn't stop it halfway. Attaching a saved conversation to a new agent uses the same three steps; until the new agent
runs, syncs don't give it a conversation of its own (`SessionService.PrepareConversation`). Workspace creation also
releases the project and move gates once the new agent (with its creation plan) is saved: it reserves the name and
folder. Meanwhile the Runner's maintenance (update, restart, stop, delete) is refused, and so is removing another
workspace of the project, whose folder the new one may copy. Removing a workspace stops it and syncs under the locks, deletes or checks its folder without them, and removes
the agent under them again.

## History sync

`SessionService.SyncRunnerAsync` reads which conversations to sync without the lock; only creating sessions for agents
without one takes it. Locating and transferring run without it (each history's own lock in `NativeHistoryStore` keeps its
files consistent). Each result is recorded under the lock briefly, unless the conversation changed meanwhile: another
agent, harness, native session or Runner, or a newer sync recorded first. A conversation deleted meanwhile has its
transferred copy removed. One unlocked sync per Runner runs at a time; lifecycle steps that hold the lock sync only the
agent's own conversation directly (`SyncRunnerLockedAsync`) and never wait for it. Each sync takes a sequence number
before it locates the files, and the native store refuses to save a transfer located before the history it holds
(`StaleLocationException`): an unlocked sync that located a file just before a lifecycle step saved more of it can't
replace the newer records with its shorter copy.

## Large histories

Conversions read Claude Code, Codex and pi histories one record at a time (`NativeRecords`, `SessionMigration`): three
passes to another harness, two (metadata, then records) to the same harness. Copies for another owner are saved through
the native store from a snapshot. Exports stream their own format, another harness's format and DeveSession JSON. Grok
histories and legacy JSON sessions are still loaded whole, one at a time.

## Tests

`SessionLockTests` uses a real Runner (isolated sshd and tmux): status checks while the lock is held and a reading that
overlapped a Stop; a sync that transfers while the lock is held and doesn't record a conversation that moved away; syncs
leaving a new agent's conversation to its creation;
**Resume here** writing without the lock while refusing other actions; exports while the lock is held, matching the
whole conversation exactly. `AgentSwitchTests` covers switching. `NativeHistoryStoreTests` checks that a transfer located
before the saved history never replaces it. `SessionMigrationTests` and the streamed write
comparisons check that streamed and whole conversions are identical.
