namespace DeveAgentManager.Client.Shared;

/// <summary>What copying says, wherever text is copied (the terminal, its text view, the conversation).</summary>
public static class CopyText
{
    public static string Copied(int characters) => $"Copied {Characters(characters)}";

    public static string Characters(int count) => count == 1 ? "1 character" : $"{count:N0} characters";

    /// <summary>Characters as people count them (an emoji is one), like terminal.js does.</summary>
    public static int Count(string text) => text.EnumerateRunes().Count();
}
