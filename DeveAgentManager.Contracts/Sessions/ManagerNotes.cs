namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// Text the Manager adds to a conversation for the agent (a harness switch, a move to another folder, archived history),
/// between start and end markers. It is not something the user asked, so titles and previews leave it out.
/// </summary>
public static class ManagerNotes
{
    public const string MigrationStart = "[DAM session migration v1]";
    public const string MigrationEnd = "[/DAM session migration]";
    public const string FolderStart = "[DAM workspace change v1]";
    public const string FolderEnd = "[/DAM workspace change]";
    public const string ArchiveStart = "[DAM historical record";
    public const string ArchiveEnd = "[/DAM historical record]";

    /// <summary>The <see cref="DeveEntry.MigrationKind"/> of a note about a move to another folder.</summary>
    public const string FolderKind = "workspace";

    // Before the markers, the Manager put a one-line note in front of the first prompt.
    private const string LegacyMigrationStart = "(This conversation was moved from ";
    private const string LegacyMigrationEnd = "Tool calls from before the move are shown as text; what they changed is in the files.)";

    private static readonly (string Start, string End)[] Migration = [(MigrationStart, MigrationEnd), (LegacyMigrationStart, LegacyMigrationEnd)];
    private static readonly (string Start, string End)[] All = [.. Migration, (FolderStart, FolderEnd), (ArchiveStart, ArchiveEnd)];

    /// <summary>The text without the harness-switch notes it starts with.</summary>
    public static string WithoutMigrationNote(string text) => Strip(text, Migration) ?? text;

    /// <summary>
    /// The text without the notes it starts with, or null when it starts with a note that is cut off (a shortened preview),
    /// so what follows the note is unknown.
    /// </summary>
    public static string? WithoutNotes(string text) => Strip(text, All);

    /// <summary>What the agent is told when its conversation continues in another folder.</summary>
    public static string FolderNote(string before, string now) =>
        $"{FolderStart}\nThis conversation now continues in {now}. Until now it ran in {before}, a separate folder: changes there "
        + "that weren't committed and pushed are not here, and another agent may be working in it. Paths under "
        + $"{before} in earlier messages refer to that folder. From now on, read and change files in {now} only, and check "
        + $"its state (for example git status and the current branch) before you continue.\n{FolderEnd}";

    private static string? Strip(string text, (string Start, string End)[] notes)
    {
        var stripped = false;
        while (true)
        {
            var trimmed = text.TrimStart();
            var note = notes.FirstOrDefault(n => trimmed.StartsWith(n.Start, StringComparison.Ordinal));
            if (note.Start is null) return stripped ? trimmed : text;
            var end = trimmed.IndexOf(note.End, note.Start.Length, StringComparison.Ordinal);
            if (end < 0) return null;
            text = trimmed[(end + note.End.Length)..];
            stripped = true;
        }
    }
}
