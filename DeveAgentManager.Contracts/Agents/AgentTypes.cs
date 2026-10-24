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

public sealed record CreateWorktreeRequest(string Name, string Branch, string? BaseRevision = null,
    string? HarnessId = null, Guid? AccountId = null, bool Start = false);

/// <summary>A terminal attached to an agent's tmux session.</summary>
/// <param name="Tty">The client's tty on the Runner, e.g. /dev/pts/3.</param>
/// <param name="Watching">Joined while others were attached and hasn't typed yet: doesn't change the terminal's size.</param>
public sealed record AgentViewer(string Tty, int Columns, int Rows, DateTime ConnectedAt, DateTime LastActiveAt, bool Watching = false);

/// <summary>An agent's terminal as plain text, to select and copy from.</summary>
/// <param name="History">tmux's history above the screen (the last lines asked for), wrapped lines joined.</param>
/// <param name="Screen">What the terminal shows now.</param>
/// <param name="More">tmux has more history than was read.</param>
public sealed record AgentTerminalText(string History, string Screen, bool More);
