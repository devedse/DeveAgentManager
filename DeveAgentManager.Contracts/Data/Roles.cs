namespace DeveAgentManager.Core.Data;

/// <summary>How roles are called and explained, the same in the Portal and in the Manager's messages.</summary>
public static class Roles
{
    /// <summary>Every role, in the order the Portal lists them (Admin is all of them).</summary>
    public static readonly IReadOnlyList<UserRoles> All = [UserRoles.SettingsManager, UserRoles.UserManager, UserRoles.RunnerCreator, UserRoles.RunnerManager, UserRoles.ConversationManager, UserRoles.LogViewer, UserRoles.HarnessLoginManager, UserRoles.DamagochiManager];

    public static string Name(UserRoles role) => role switch
    {
        UserRoles.SettingsManager => "Settings manager",
        UserRoles.UserManager => "User manager",
        UserRoles.RunnerCreator => "Runner creator",
        UserRoles.RunnerManager => "Runner manager",
        UserRoles.ConversationManager => "Conversation manager",
        UserRoles.LogViewer => "Log viewer",
        UserRoles.HarnessLoginManager => "Harness login manager",
        UserRoles.DamagochiManager => "Damagochi manager",
        UserRoles.Admin => "Admin",
        _ => role.ToString(),
    };

    public static string Explain(UserRoles role) => role switch
    {
        UserRoles.SettingsManager => "Manager settings: providers, templates, backups, the default timezone and the shared Setup Profile.",
        UserRoles.UserManager => "Create and disable users, reset passwords, grant roles they have themselves, read the audit log.",
        UserRoles.RunnerCreator => "Create Runners; the creator becomes the owner.",
        UserRoles.RunnerManager => "Every Runner: see it, give people access, change its owner, view and set its password, delete it.",
        UserRoles.ConversationManager => "Open, prompt, restart, share and delete every agent; read, export, rename, share and delete every conversation.",
        UserRoles.LogViewer => "Read and download Manager application logs, which may include deployment details and user activity.",
        UserRoles.HarnessLoginManager => "Manage every harness login: reconnect, disable, delete and give people access. Does not grant Runner or conversation access.",
        UserRoles.DamagochiManager => "Manage Damagochi settings and everyone’s pet, level and happiness. Does not grant conversation or user-account access.",
        _ => "",
    };

    /// <summary>"Admin", "None", or the roles by name, e.g. "User manager, Runner creator".</summary>
    public static string Describe(UserRoles roles) => roles == UserRoles.None ? "None"
        : roles == UserRoles.Admin ? "Admin"
        : string.Join(", ", All.Where(r => (roles & r) == r).Select(Name));
}
