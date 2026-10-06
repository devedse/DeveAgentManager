using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Core.Users;

/// <summary>The signed-in user a request or terminal acts for, as it was when the request started.</summary>
public sealed record CurrentUser(Guid Id, string Username, string DisplayName, UserRoles Roles, bool MustChangePassword = false)
{
    public bool Has(UserRoles role) => (Roles & role) == role;

    public static CurrentUser From(User user) => new(user.Id, user.Username, user.DisplayName, user.Roles, user.MustChangePassword);
}
