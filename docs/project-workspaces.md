# Projects, workspace agents and conversations

The hierarchy is Runner → Project → Workspace agent → Conversations. A project is one repository or folder on one
Runner. Its original agent is Main; additional managed Git worktrees have independent agents. Each agent retains its
own public ID, tmux terminal, harness/login, activity, tasks, continuation policy and active conversation. Opening a
workspace changes this viewer's destination; it never starts, stops or redirects another agent.

Existing agents migrate to separate projects without restarting their processes or moving their files. A matching
repository URL does not merge projects. A project with one workspace uses a compact row. With one running workspace,
its sidebar shortcut opens that agent; multiple running workspaces appear below a project group. Ready means a
running agent waiting for input, not a stopped agent.

The Agents page groups workspaces and defaults Show stopped workspaces off. Fully stopped projects remain visible,
with an action to reveal their workspaces. Errors, unexpected exits, unread results and uncertain Runner states are
not hidden as quietly stopped. The workspace dropdown has a stopped section; opening a stopped workspace exposes
Start without launching it. Conversations have a separate dropdown: reading opens saved history, and Resume or
New conversation explicitly replaces only this workspace's conversation.

## Creation and ownership

New workspace creates a fresh conversation and a new Git branch/worktree from an exact committed revision. HEAD
means the selected source workspace's committed HEAD. Dirty, staged, untracked and ignored source files stay there.
Creation does not fetch, pull, commit, push or merge. It does not run extra project setup commands. Starting the agent
after creation is an explicit option, off by default. Branch instructions identify the assigned workspace branch.
Start reconciles an externally changed branch before resuming.

Managed directories are siblings of Main, under `<main>.dam-worktrees/<project-id>/<workspace-id>`. Names and branch
names are not runtime or folder identities. Git's same-branch and existing-path safeguards remain enabled. The
Manager records creation intent before Git mutation and uses a separate ownership marker. After an interrupted
request, inspect the recorded workspace; Start verifies its marker, repository and registration rather than rerunning
creation. A failed/incomplete operation retains its evidence. No initial prompt is replayed.

Creating workspaces requires project ownership and Runner Use access. Sharing or leaving the project applies to all
its current and future workspace agents, using the existing Read/Write grants. A standalone conversation share stays
limited to that history. Harness login permission remains independent. Project files shared as artifacts are visible
across authorized workspaces, with their original conversation provenance and without copying the stored bytes.

Plain folders continue to work as Main. Managed worktree creation requires an ordinary Git checkout. Submodules,
sparse/shallow checkouts, Git LFS and externally linked Git metadata are rejected until their compatibility is proven.
Automatic merging, copying dirty workspace state, adopting external worktrees and automatic project setup are not
included. Stop retains a managed workspace; deleting it independently is blocked until a dedicated removal flow can
review dirty/ignored files, branches and retained processes. Main cannot be deleted while child workspaces exist.

Worktrees still share Runner CPU/RAM, ports, desktop and external services. Creation does not automatically start an
agent. Heavy builds and servers need explicit resource planning; worktrees are not VM or process isolation.
Pi workspaces use private settings and history directories. Pi still uses its existing Runner-level login or the
agent owner's environment keys; this change does not introduce centrally shared pi subscription logins.

## Moving a project

Move project always transfers the complete repository, including Main/common Git metadata and every managed
worktree. Opening the action from a child workspace has the same project-wide scope. The destination must support
all project harnesses and preserve absolute paths. External/unmanaged linked worktrees, missing workspaces, conflicting
destination files and another project's overlapping directories block a move.

The durable move freezes all members, records their running/stopped states and reviews their background processes.
It stops and verifies all source terminals, fully verifies native histories, preserves artifacts, and transfers the
workspace graph, private runtimes, tasks, native replay data and referenced attachments with checksums. The restored
Git links are verified. Only agents previously running resume. A single durable operation is collected once, although
every member exposes its progress and recovery state.

All destination terminals must be confirmed stopped before source fallback. Ambiguous launches are not replayed.
Source cleanup starts only after every required destination runtime is confirmed; a later cleanup failure cannot
roll back the live destination. Stopped background commands are not restarted by a move or its fallback. Host tests
and disposable SSH transfer fixtures do not establish that a live multi-VM deployment has been verified.

## Reverting the change

Keep a backup of the Manager data directory before upgrading. This feature is delivered in one PR; reverting it
does not remove Git worktrees or branches from Runners.

Before downgrading, finish creation/move recovery and cleanup, stop workspace agents and preserve a current backup
of conversations, artifacts and Runner files. The safest return is restoring the compatible Manager data backup and
previous image. Retain any newer Runner worktrees and exported conversations separately so rollback does not lose
work created after the backup.

Alternatively, while the Manager is stopped, use this version's EF tools to migrate the database back to
`20261021150000_AgentTasks`, then run the previous binary. The migration removes grouping metadata and retains agents,
conversation IDs and terminal identities; child workspaces become separate legacy agents. It does not remove their
files or branches. The previous binary cannot move linked worktrees and must not attempt a project-wide recovery
record written by this version. Downgrade/re-upgrade preservation is covered by a migration regression.

## Verification

`AgentMoveTests`, `AgentMoveFilesTests`, `WorkspaceSessionTests`, `WorkspaceGroupTests` and `AgentArtifactTests` cover
independent execution, project sharing, move membership/recovery, exact file transfer, migration and list semantics.
`Browser/project-workspaces.mts` checks grouping, filtering, explicit navigation, dropdown loading/error states and
phone themes. Existing conversation management, loading, ordering and terminal toolbar regressions remain applicable.
