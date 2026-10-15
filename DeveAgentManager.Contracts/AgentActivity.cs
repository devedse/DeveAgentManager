namespace DeveAgentManager.Contracts;

public enum AgentActivityStatus { Unknown, Working, Ready, NeedsInput, UsageLimited }

public sealed record AgentActivityDto(AgentActivityStatus Status, string Reason, string Rule, string? Evidence,
    DateTime? ObservedAt, DateTime Since, long Sequence, bool QuestionWaiting,
    DateTime? CompletedAt, long CompletionSequence, long SeenCompletionSequence, int? BackgroundTaskCount = null,
    bool CurrentTurnCompleted = false)
{
    public bool UnseenCompletion => CompletedAt is not null && CompletionSequence > SeenCompletionSequence;
    public bool IsFinished => Status == AgentActivityStatus.Ready && CurrentTurnCompleted && BackgroundTaskCount is not > 0;
}

public enum AgentAttentionKind { NeedsInput, QuestionWaiting, UsageLimit, UnexpectedExit, Finished, Unclear }
public sealed record AgentAttentionDto(Guid AgentId, string Name, string RunnerName, string HarnessId,
    AgentAttentionKind Kind, string Reason, DateTime At, long CompletionSequence, string Token = "");
public sealed record MarkAgentSeenRequest(long CompletionSequence);

public sealed record DismissAgentAttentionRequest(string Token);
