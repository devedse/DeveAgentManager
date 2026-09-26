namespace DeveAgentManager.Client.Shared;

/// <summary>A subscription login in a terminal: which harness, and the account it replaces (or the name of a new one).</summary>
public sealed record LoginTerminal(string HarnessId, Guid? AccountId, string? Name);
