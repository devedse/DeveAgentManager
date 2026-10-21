namespace DeveAgentManager.Core.Sessions;

/// <summary>A conversation too large to load for showing it (HTTP 413); its message is meant for the user.</summary>
public sealed class ConversationTooLargeException(string message) : Exception(message);
