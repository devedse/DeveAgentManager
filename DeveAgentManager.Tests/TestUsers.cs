using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Tests;

/// <summary>Users for tests. Test databases have the first user (<see cref="User.BootstrapId"/>), as every Manager does.</summary>
internal static class TestUsers
{
    public static readonly CurrentUser Admin = new(User.BootstrapId, "admin", "Admin", UserRoles.Admin);

    public static CurrentUser Plain(Guid id, string username = "ketty") => new(id, username, username, UserRoles.None);
}
