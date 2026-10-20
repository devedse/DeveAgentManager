# Moving an agent between runners

Use **Move to another runner…** in the agent's actions menu. The agent keeps its
ID, conversations, shares, selected login and continuation settings. A running
agent resumes on the destination; a stopped agent stays stopped.

The owner (or a conversation manager) starts the move. Both the caller and the
owner need Use access to the destination. Both runners must be ready, with the
same SSH username and home path, and the destination must have the harness
installed. The working path stays identical, preserving native absolute file
references. Existing destination workspaces or terminals are never overwritten.

The move includes uncommitted and ignored workspace files, empty directories,
executable permissions, symlinks, the private agent runtime, and each associated
native session and replay directory. Referenced pasted uploads and fetched
artifacts are transferred too. Saved screenshots, shared documents and their
collection index stay in Manager storage under the same identities. Pending
image and file publications on the runner are included in the transfer.

The move dialog lists background processes started by this agent outside its
current terminal, including their names, PIDs and working folders. Choose
**Cancel** to keep them running, or **Stop processes and move** to approve stopping
the listed processes before the transfer. Opening the dialog does not stop anything.
The Manager asks them to exit, waits ten seconds, then force-stops any survivors.
In-memory work can be lost; these commands are not restarted after a move or fallback.
Process identities include the runner boot and process start time. Reused PIDs,
new blockers, and processes belonging to another agent are never selected for stopping.
Changed blocker lists require another review. Process command-line arguments are
not shown because they can contain credentials.

Shared workspaces, parent folders used by
other agents, symlinked directory parents and linked Git worktrees cannot be
moved. Use an independent checkout. The source, destination and Manager need
space for the temporary transfer archive; the destination also needs staging
space for the restored files.

The agent list and terminal show the current stage, a progress bar and the file
being processed. Click the status to open **Latest move**, which updates while
open and keeps the latest result available after the agent returns to Ready.
It shows the source, destination, start/finish times and any failure or cleanup
problem. This result persists across Manager restarts.

Percentages measure the current stage, not an estimated overall completion time.
Preparation and cleanup use an indeterminate bar when their total is unknown.
During SFTP transfer, a streaming metadata index maps archive byte positions to
the original filenames. File contents are not reread to calculate transfer
progress. Updates are coalesced to once a second, plus stage boundaries, and
file details are visible only to people who can read the agent.
Attachment discovery also shows its current file and processed bytes. It scans
overlapping history/runtime paths once and searches for literal home-relative
upload/cache prefixes, so slash-heavy inline images cannot cause repeated path
matching across the same chunk.

## Transfer and failure handling

1. Check paths, access, installed harness and destination conflicts. Stop only
   the background processes the user reviewed and approved.
2. Stop the source harness, check for remaining agent background processes, and
   fully verify the latest native history and artifact collection.
3. Build a private archive, stream it through the Manager over SFTP, and verify
   its SHA-256 checksum and every restored file. Install without replacing
   existing paths. Recheck source files before switching the database location.
4. Resume a previously running agent on the destination and check its terminal.
   Only then commit the move and remove the exclusive source workspace and
   native runtime. Shared upload/cache files may remain on the source because
   other agents can reference them.

Move intent and checkpoints persist in the database. If the Manager restarts
before commit, it rolls back instead of replaying an uncertain process stop, the transfer or an uncertain
launch. A failed transfer keeps the source. A failed destination launch must be
confirmed stopped before the source can resume. Changed destination files are
retained for inspection without blocking a confirmed source fallback.

If process state cannot be established, neither location is launched again
automatically. The agent shows the failure and **Recover move / finish cleanup**
allows its owner to retry reconciliation. An already attempted fallback launch
is not repeated without this explicit recovery action or observation that the
source is already alive.

Once committed, a source cleanup failure never rolls back the live destination.
The same recovery action retries cleanup. Files that changed since verification
are retained; inspect and move them aside before retrying cleanup. Pending move
records reserve the relevant agent name and workspace, prevent agent deletion,
and prevent deletion of either runner until reconciliation completes.

## Verification

`AgentMoveFilesTests` exercises real SSH/SFTP transfer, exact native records,
screenshots, uploaded and shared documents, cached artifacts, permissions,
symlinks, checksum rejection, destination conflicts and rollback after install.
`AgentMoveTests` checks authorization, workspace exclusivity, maintenance
coordination, durable queuing, source fallback, retained identity/shares/policy
and recovery without duplicate launches. It also verifies graceful/forced process
exit, stale identities, new blockers, other-agent isolation and recovery without
repeating an uncertain stop. Browser coverage checks all four access surfaces on
desktop and mobile, destination selection, process loading/retry, cancellation,
changed blocker review and explicit stop approval.
Progress checks cover archive entry boundaries, transfer byte counts, bounded
update frequency, checkpoint preservation, live dialog updates, narrow layouts,
retained success/failure results and privacy for users with only Listed access.
The file fixtures include large slash-heavy native records and attachment paths
split across read boundaries; discovery byte counts detect duplicate history scans.

These tests use disposable fixtures. They do not move or restart production
agents or validate a live deployment.
