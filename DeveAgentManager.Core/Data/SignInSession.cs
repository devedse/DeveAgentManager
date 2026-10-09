namespace DeveAgentManager.Core.Data;

public sealed class SignInSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string SecurityStamp { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime AbsoluteExpiresAt { get; set; }
    public DateTime AuthenticatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool Local { get; set; }
    public string ClientIp { get; set; } = "";
    public string Browser { get; set; } = "";
}

public sealed class UserSecondFactor
{
    public Guid UserId { get; set; }
    public string Secret { get; set; } = "";
    public bool Enabled { get; set; }
    public long LastCounter { get; set; } = -1;
    public string RecoveryHashes { get; set; } = "";
}

public sealed class EditorTicket
{
    public string Id { get; set; } = "";
    public Guid SessionId { get; set; }
    public Guid RunnerId { get; set; }
    public string Origin { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}
