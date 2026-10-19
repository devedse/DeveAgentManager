namespace DeveAgentManager.Client.Shared;

public sealed record AccessPerson(Guid Id, string Username, string DisplayName, string Level,
    bool Owner = false, bool Disabled = false, DateTime? GrantedAt = null, string? Key = null);
