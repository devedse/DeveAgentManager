namespace DeveAgentManager.Core.Agents;

public enum AgentWorkspaceMode
{
    EmptyFolder,
    CloneRepository,
    ExistingFolder,
}

public sealed record CreateAgentRequest(
    Guid RunnerId,
    string Name,
    string HarnessId,
    AgentWorkspaceMode Mode,
    string? RepositoryUrl = null,
    string? Branch = null,
    string? ExistingPath = null,
    Guid? ContinueSessionId = null,
    Guid? AccountId = null);

/// <summary>Where a new workspace's files come from.</summary>
public enum WorkspaceStart
{
    /// <summary>The repository's main branch (e.g. master), optionally pulled first.</summary>
    MainBranch,
    /// <summary>The source workspace's branch and last commit.</summary>
    LastCommit,
    /// <summary>The last commit plus the source's changed and new files; ignored files are skipped.</summary>
    CurrentCode,
    /// <summary>Everything in the source folder, including ignored files such as node_modules.</summary>
    ExactCopy,
}

/// <param name="Name">The workspace and folder name; taken names get a suffix. Null picks workspace-2, workspace-3…</param>
/// <param name="NewBranch">Creates this branch instead of checking out the source's branch.</param>
/// <param name="ResumeSessionId">A saved conversation of this project to continue, instead of a new one.</param>
/// <param name="KeptFolder">Reuses a folder kept when its workspace was removed, instead of cloning.</param>
public sealed record CreateWorkspaceRequest(string? Name = null, WorkspaceStart Start = WorkspaceStart.MainBranch, bool Pull = false,
    string? NewBranch = null, string? HarnessId = null, Guid? AccountId = null, bool StartAgent = true,
    Guid? ResumeSessionId = null, string? KeptFolder = null);

/// <summary>How much a start point copies, and where it goes.</summary>
/// <param name="CopyOnWrite">The disk makes instant copy-on-write copies, so copied files use no space until changed.</param>
/// <param name="CanPull">The main branch follows a remote branch.</param>
public sealed record WorkspaceEstimateDto(long Bytes, int Files, long FreeBytes, bool CopyOnWrite, string? Branch, bool CanPull);

/// <summary>What removing or updating a workspace would affect.</summary>
/// <param name="Unpushed">Commits that are on no remote branch.</param>
/// <param name="Upstream">The remote branch it follows, e.g. origin/master.</param>
public sealed record WorkspaceChangesDto(string Path, bool Exists, int Uncommitted, IReadOnlyList<string> Sample, int Unpushed,
    string? Upstream, string? Branch, int Conversations);

public sealed record RemoveWorkspaceRequest(bool KeepFiles);

/// <summary>What deleting an agent would affect: its conversations stay; its folder can be kept or deleted with it.</summary>
/// <param name="Checked">The folder was checked on the Runner. When it couldn't be, <paramref name="Blocked"/> says why.</param>
/// <param name="Blocked">Why the folder can't be deleted with the agent. The agent alone can still be deleted.</param>
/// <param name="Git">The folder is a Git repository: <paramref name="Uncommitted"/> and <paramref name="Unpushed"/> count its unsaved work.</param>
/// <param name="GitProblem">Why Git couldn't tell what is unsaved.</param>
public sealed record AgentDeletionDto(string Path, int Conversations, bool Checked, bool Exists, string? Blocked,
    bool Git, int Uncommitted, IReadOnlyList<string> Sample, int Unpushed, string? GitProblem);

/// <summary>Disk space of a folder, each file counted once. <paramref name="FreedBytes"/> leaves out files also linked from outside it.</summary>
public sealed record FolderSizeDto(long Bytes, long FreedBytes, long Files);

/// <summary>A folder kept when its workspace was removed. It can be deleted or used by a new workspace.</summary>
public sealed record KeptFolderDto(string Path, string Name, string? Branch, DateTime RemovedAt);

/// <summary>A terminal attached to an agent's tmux session.</summary>
/// <param name="Tty">The client's tty on the Runner, e.g. /dev/pts/3.</param>
/// <param name="Watching">Joined while others were attached and hasn't typed yet: doesn't change the terminal's size.</param>
public sealed record AgentViewer(string Tty, int Columns, int Rows, DateTime ConnectedAt, DateTime LastActiveAt, bool Watching = false);

/// <summary>An agent's terminal as plain text, to select and copy from.</summary>
/// <param name="History">tmux's history above the screen (the last lines asked for), wrapped lines joined.</param>
/// <param name="Screen">What the terminal shows now.</param>
/// <param name="More">tmux has more history than was read.</param>
public sealed record AgentTerminalText(string History, string Screen, bool More);
