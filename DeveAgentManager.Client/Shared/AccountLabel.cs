using DeveAgentManager.Contracts;

namespace DeveAgentManager.Client.Shared;

public static class AccountLabel
{
    public static string Name(AccountDto account) =>
        $"{account.CreatedByUsername ?? "Unknown user"} · {account.Name}";
}
