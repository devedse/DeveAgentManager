namespace DeveAgentManager.Core.Sessions;

/// <summary>Small, content-free conversion diagnostics. The original native history remains the authority.</summary>
public sealed class SessionMigrationReport
{
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? TargetSessionId { get; set; }
    public int PreservedCalls { get; set; }
    public int ArchivedExchanges { get; set; }
    public int OmittedExchanges { get; set; }
    public int OmittedImages { get; set; }
    public int BudgetCharacters { get; set; }
    public List<SessionMigrationIssue> Issues { get; set; } = [];
}

/// <summary>A reason and count, deliberately excluding commands, output, paths and user text.</summary>
public sealed record SessionMigrationIssue(string Reason, int Count);

public static class SessionMigrationProblems
{
    public const string VerificationFailed = "session_migration_verification";
}
