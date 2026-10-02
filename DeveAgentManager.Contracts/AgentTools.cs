namespace DeveAgentManager.Contracts;

public sealed record AgentDiagnosticDto(DateTime At, string Category, string Message, string? Rule = null);
public sealed record AgentQuickCommandDto(string Command, string Label);
public sealed record AgentQuickCommandRequest(string Command, string HarnessId);
public sealed record AgentReplyPreviewDto(Guid AgentId, string Text);
