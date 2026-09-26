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

/// <summary>A terminal attached to an agent's tmux session.</summary>
/// <param name="Tty">The client's tty on the Runner, e.g. /dev/pts/3.</param>
/// <param name="Watching">Joined while others were attached and hasn't typed yet: doesn't change the terminal's size.</param>
public sealed record AgentViewer(string Tty, int Columns, int Rows, DateTime ConnectedAt, DateTime LastActiveAt, bool Watching = false);
