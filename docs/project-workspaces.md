# Projects, workspaces and conversations

The hierarchy is Runner → Project → Workspace → Conversation. A project is one repository or folder on one Runner.
Its original folder is **Main**. Every other workspace is an independent clone of the repository next to Main, with
its own agent: its own public ID, tmux terminal, harness/login, activity, tasks and continuation policy. Several
workspaces can run at the same time. A workspace runs one conversation at a time.

Opening a workspace changes only what this viewer sees; it never starts, stops or redirects an agent. Start and Stop
(▶/■ in the workspace dropdown, on the Agents page and on the project page) target one workspace. Each row in the
dropdown shows the workspace's branch, conversation, harness and login. The Agents page, the dashboard and a Runner's
page list workspaces under their project in the same way.

What applies to the whole project is in the project's heading there and on the project page, once: Move project (or
Recover move after an interrupted one), New workspace and Manage. They act on Main, whichever workspace replied last.
A workspace row's menu keeps only what is its own. An agent without workspaces has these in its own menu, as **New
workspace…** and **Move to another runner…**.

## Folders

```
/work/
  app/                      Main, never moved or deleted by workspace actions
  app.workspaces/
    fix-toolbar/            a workspace: its own clone with its own .git
    workspace-2/
```

- **Names.** A workspace's folder is named at creation, `workspace-2`, `workspace-3`… when no name is given. A taken
  name gets a number. Renaming never moves the folder: harness histories, terminals and editors use its absolute path.
- **Ownership.** Each clone has a marker in its own `.git` folder (`.git/dam-workspace`), so it moves with the folder
  and never shows in `git status`. Only a folder with the matching marker is ever deleted.
- **Location.** Next to Main keeps the clone on the same disk, so Git hardlinks its history. Main's parent folder must
  be writable. Workspaces are never inside Main, which would confuse `git status`, searches and file watchers.

Clones behave like any checkout. The agent checks out, pulls, commits and pushes normally, with no special
instructions. Each clone gets the source's remotes and its last-known remote branches, without network access.
Branches are local to a clone until they are pushed, like separate developers sharing a remote. A repository
without a remote can still have workspaces, but they cannot push into the branch Main has checked out.

## New workspace

**Start from:**

1. **Main's branch** (default): the repository's main branch (origin's default branch, else `main`/`master`, else what
   Main has checked out), committed work only. **Pull first** fetches the remote's latest version instead; it uses the
   Runner user's Git credentials.
2. **This workspace's last commit:** the branch and commit of the workspace the dialog was opened from.
3. **This workspace's current code:** the last commit plus the changed, staged and new files that `git status` lists.
   Ignored files are skipped. The copies are separate: the source keeps its own uncommitted changes.
4. **Exact copy:** everything in the folder, including ignored files such as `node_modules`, build output and `.env`.
   A copied Python virtual environment usually breaks, because it contains absolute paths.

The workspace checks out the source's branch and follows the same remote branch, unless **New branch** names a new one.
Harness and login default to the source workspace's. The agent starts right away unless that is switched off.

Each start point shows its size and file count while the dialog is open, measured on the Runner: tracked files of a
commit come from Git, current code from the files themselves, and an exact copy measures the whole folder. Each shows
"Calculating…" until its size is known; a failed measurement offers Retry; closing the dialog ends the request and the
Runner-side measurement (also bounded by a timeout). An option that needs more than the free space is disabled.

Copies use `cp --reflink=auto`: on Btrfs, XFS, bcachefs and ZFS with block cloning they are instant copy-on-write
copies that use no space until a file changes; elsewhere (ext4) they are normal copies. A tiny test copy detects which,
and the dialog says so. Working files are never hardlinked: a write through a hardlink would change both workspaces.
Checkout always writes tracked files fresh.

Submodules, Git LFS, sparse and shallow checkouts, and Main folders that are themselves linked worktrees are refused
until their compatibility is tested. Plain folders without Git keep a single Main workspace.

### Interrupted creation

The Manager records the creation plan before the Runner changes anything. The Runner clones into a hidden temporary
folder, writes the marker, and renames the folder into place last. A refusal or failure on the Runner cleans up after
itself and the workspace is forgotten. A lost response keeps the workspace in Error: Start verifies the folder and its
marker and completes the creation, without cloning again or replaying a prompt. Remove cleans up either way.

## Conversations

Conversations belong to the project, through `StoredSession.ProjectId`; `WorkspaceAgentId` is where a conversation last
ran, and `AgentId` where it is active now. A conversation is active in at most one workspace (a unique index).

- **New conversation here** saves and stops the workspace's conversation and starts a fresh one in the same folder.
  **Update to the latest version first** pulls the workspace's branch before that; it is refused while anything is
  uncommitted or unpushed, before anything stops.
- **Names.** A workspace is named by its workspace name (Main, or the name it was created with); a conversation by its
  title. Harnesses start with `--name <workspace>`, so until the first prompt a new conversation is "New conversation".
- **Saved conversations** appear in the workspace dropdown (newest first), on the project page grouped by the
  workspace they last ran in, and on the Sessions page grouped by project; both pages use the same rows and actions.
  The project page is the place for one project; the Sessions page lists every project's conversations, imported ones
  and those shared with you. Choosing one never does anything by itself:
  the person picks **Resume here** (which saves and stops the conversation running there), **Resume in a new
  workspace**, or **Read only**. An active conversation opens its workspace.
- **Resuming in another workspace.** Its files are that workspace's, so the choice shows a warning when the
  conversation last ran elsewhere: changes there that weren't committed and pushed aren't in this folder, the branch
  when it differs, and whether the workspace it ran in still has uncommitted files or unpushed commits. The agent gets
  a note in the conversation that it now works in this folder, and that paths in earlier messages refer to the other
  one (see [session-formats.md](session-formats.md)).
- **Removing a workspace** keeps its conversations with the project. Their access then follows the project's sharing.
  A standalone conversation share stays limited to that conversation.

## Removing a workspace

**Remove workspace** stops the agent, syncs its conversation and lists what would be lost: uncommitted files (with a
few names) and commits that are on no remote branch. When there is nothing, it is a plain confirmation. Otherwise the
choices are **Cancel**, **Keep files** and **Remove everything**:

- **Remove everything** deletes the folder after checking its marker. That cannot be undone.
- **Keep files** leaves the folder and lists it on the project page. From there it can be deleted (again after checking
  its marker) or used for a new workspace, which continues in it as it is.

Main is never removed this way; the Main agent can be deleted once the other workspaces are gone. Move project waits
until kept folders are deleted or reused.

## Sharing and creation rights

Creating workspaces requires project ownership and Runner Use access. Sharing or leaving the project applies to all
its current and future workspaces, using the existing Read/Write grants. Harness login permission stays independent.
Project files shared as artifacts are visible across authorized workspaces, with their original conversation
provenance and without copying the stored bytes.

Workspaces share Runner CPU/RAM, ports, desktop and external services; they are separate folders, not VMs or process
isolation. Pi workspaces use private settings and history directories and keep pi's Runner-level login.

## Moving a project

Move project always transfers Main and every workspace folder, including stopped ones, with their private runtimes,
tasks, native histories, replay data and referenced attachments, to the same absolute paths. Opening it from a
workspace's terminal (**Move project to another runner…**) has the same project-wide scope; the dialog says how many
workspaces move with Main. The destination must support all project harnesses. Missing
workspaces, a workspace whose marker doesn't match, conflicting destination files, another project's overlapping
folders and linked Git worktrees made outside the Manager block a move. Hardlinks between clones may become separate
copies on the destination.

The durable move freezes all members, records their running/stopped states and reviews their background processes.
It stops and verifies all source terminals, fully verifies native histories, preserves artifacts, transfers the files
with checksums and verifies every restored workspace's marker. Only agents that were running resume. All destination
terminals must be confirmed stopped before falling back to the source; ambiguous launches are not replayed. Source
cleanup starts only after every required destination runtime is confirmed. Stopped background commands are not
restarted. Host tests and disposable SSH fixtures do not establish that a live multi-VM deployment has been verified.

## Reverting the change

Keep a backup of the Manager data directory before upgrading. Reverting the code does not remove workspace folders
from Runners. Before downgrading, finish creation/move recovery and cleanup, stop workspace agents and back up
conversations, artifacts and Runner files. The safest return is restoring the compatible Manager data backup and the
previous image; keep newer workspace folders and exported conversations separately.

Alternatively, while the Manager is stopped, use this version's EF tools to migrate the database back to
`20261021150000_AgentTasks`, then run the previous binary. That removes project grouping and conversation projects
and keeps agents, conversation IDs and terminal identities; workspaces become separate agents. It does not delete their
folders. Downgrade and re-upgrade are covered by a migration regression.

## Verification

`AgentMoveTests` (real SSH and Git: all four start points, pull, names, branch refresh, removal with Keep files and a
foreign marker, kept folders, project moves), `AgentMoveFilesTests` (clone creation, repeated creation, marker
verification and exact file transfer with rollback), `WorkspaceSessionTests` (resuming across workspaces, removed
workspaces, migrations), `WorkspaceGroupTests` and `AgentArtifactTests` cover the backend.
`Browser/project-workspaces.mts` checks the dropdown, ▶/■, the New workspace sizes, the conversation chooser, removal and
phone themes; `Browser/workspace-sessions.mts` checks conversations and the Sessions page.
