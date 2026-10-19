namespace DeveAgentManager.Core.Data;

/// <summary>The actions the audit log records, and how the Portal says them.</summary>
public static class AuditActions
{
    public const string ManagerCpuTraceStarted = "manager.cpu-trace.started";
    public const string AccountSecurityChanged = "account-security-changed";
    public const string IdentityConfirmed = "identity-confirmed";
    public const string SignedIn = "signed-in";
    public const string SignInFailed = "sign-in-failed";
    public const string SignInLocked = "sign-in-locked";
    public const string PasswordChanged = "password-changed";
    public const string UserCreated = "user-created";
    public const string UserUpdated = "user-updated";
    public const string UserPasswordReset = "user-password-reset";
    public const string UserDeleted = "user-deleted";
    public const string AdminPasswordReset = "admin-password-reset";
    public const string RunnerCreated = "runner-created";
    public const string RunnerDeleted = "runner-deleted";
    public const string RunnerOwnerChanged = "runner-owner-changed";
    public const string RunnerAccessChanged = "runner-access-changed";
    public const string RunnerAccessRemoved = "runner-access-removed";
    public const string RunnerPasswordViewed = "runner-password-viewed";
    public const string RunnerPasswordChanged = "runner-password-changed";
    public const string RunnerKeysOnlyChanged = "runner-keys-only-changed";
    public const string AgentShared = "agent-shared";
    public const string AgentUnshared = "agent-unshared";
    public const string SessionShared = "session-shared";
    public const string SessionUnshared = "session-unshared";

    public static string Describe(string action) => action switch
    {
        AccountSecurityChanged => "Changed account security",
        IdentityConfirmed => "Confirmed their identity",
        SignedIn => "Signed in",
        SignInFailed => "Sign-in refused",
        SignInLocked => "Signing in locked",
        PasswordChanged => "Changed their password",
        UserCreated => "Added a user",
        UserUpdated => "Changed a user",
        UserPasswordReset => "Set a user's password",
        UserDeleted => "Deleted a user",
        AdminPasswordReset => "Reset the admin's password",
        RunnerCreated => "Created a Runner",
        RunnerDeleted => "Deleted a Runner",
        RunnerOwnerChanged => "Gave a Runner to someone else",
        RunnerAccessChanged => "Gave access to a Runner",
        RunnerAccessRemoved => "Took access to a Runner away",
        RunnerPasswordViewed => "Viewed a Runner's password",
        RunnerPasswordChanged => "Changed a Runner's password",
        RunnerKeysOnlyChanged => "Changed a Runner's SSH logins",
        AgentShared => "Shared an agent",
        AgentUnshared => "Ended an agent's share",
        SessionShared => "Shared a conversation",
        SessionUnshared => "Ended a conversation's share",
        ManagerCpuTraceStarted => "Started a Manager CPU trace",
        _ => action,
    };
}
