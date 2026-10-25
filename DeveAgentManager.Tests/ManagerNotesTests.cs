using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>The notes the Manager adds to conversations: titles leave them out, and the agent learns about a new folder.</summary>
public class ManagerNotesTests
{
    private const string Legacy = "(This conversation was moved from Codex to Claude Code by DeveAgentManager. Tool calls from before the move are shown as text; what they changed is in the files.)";

    [Fact]
    public void TitlesAreTheFirstPromptWithoutTheManagersNotes()
    {
        // Before the markers, the note was the first line of the first prompt.
        Assert.Equal("Fix the maze solver", Conversation(Legacy + "\nFix the maze solver").FirstPrompt());
        Assert.Equal("Fix the maze solver", Conversation($"{ManagerNotes.MigrationStart}\nMoved.\n{ManagerNotes.MigrationEnd}\nFix the maze solver").FirstPrompt());
        // A note of its own is skipped, by its kind or its markers.
        var separate = Conversation($"{ManagerNotes.MigrationStart}\nMoved.\n{ManagerNotes.MigrationEnd}", "Add tests");
        Assert.Equal("Add tests", separate.FirstPrompt());
        separate.Entries[0].MigrationKind = "notice";
        Assert.Equal("Add tests", separate.FirstPrompt());
        // A synced preview cut off inside a note hides the prompt after it: unknown, so the title stays as it is.
        var cut = Conversation("(This conversation was moved from Codex to Claude Code by DeveAgentManager. Too…", "Second prompt");
        Assert.Null(cut.FirstPrompt());
        var stored = new StoredSession { Title = "Fix the maze solver" };
        SessionService.Apply(stored, cut, "workspace");
        Assert.Equal("Fix the maze solver", stored.Title);
    }

    [Fact]
    public void PreviewsOfPromptsLeaveTheNotesOut()
    {
        var cursor = new SessionReadCursor { Session = Conversation(Legacy + "\nFix the maze solver") };
        cursor.Compact(0);
        Assert.Equal("Fix the maze solver", cursor.Session.Entries[0].Text);
        Assert.Equal("Fix the maze solver", cursor.Session.FirstPrompt());
    }

    [Fact]
    public void ASecondSwitchReplacesOnlyTheSwitchNote()
    {
        Assert.Equal("Fix it", ManagerNotes.WithoutMigrationNote(Legacy + "\nFix it"));
        // Archived history and folder notes are part of the conversation, not a note about the switch.
        var archived = $"{ManagerNotes.ArchiveStart}: incomplete-exchange; not an executable tool exchange]\nls\n{ManagerNotes.ArchiveEnd}";
        Assert.Equal(archived, ManagerNotes.WithoutMigrationNote(archived));
        var folder = ManagerNotes.FolderNote("/work/app.workspaces/fix", "/work/app");
        Assert.Equal(folder, ManagerNotes.WithoutMigrationNote(folder));
        Assert.Equal("", ManagerNotes.WithoutNotes(folder));
    }

    [Theory]
    [InlineData("claude"), InlineData("codex"), InlineData("pi"), InlineData("grok")]
    public void TheAgentIsToldWhenItsConversationContinuesInAnotherFolder(string harness)
    {
        var format = SessionMigrationTests.Format(harness);
        var native = format.Read(format.Write(SessionMigrationTests.Sample(harness), Guid.NewGuid().ToString(), "/work/app.workspaces/fix"));
        static List<DeveEntry> Messages(DeveSession session) => session.ActivePath().Where(e => e.Kind == DeveEntryKind.Message).ToList();
        var before = Messages(native).Count;
        var title = native.FirstPrompt();

        SessionService.NoteFolderChange(native, "/work/app.workspaces/fix/", "/work/app");
        var written = format.Read(format.Write(native, Guid.NewGuid().ToString(), "/work/app"));

        // The note is the last message on the active branch, after the whole history, and comes back with its kind.
        var path = Messages(written);
        Assert.Equal(before + 1, path.Count);
        var note = path[^1];
        Assert.Equal((DeveRole.User, ManagerNotes.FolderKind), (note.Role, note.MigrationKind));
        Assert.Contains("now continues in /work/app.", note.PlainText());
        Assert.Contains("Until now it ran in /work/app.workspaces/fix/", note.PlainText());
        Assert.Equal(title, written.FirstPrompt());
    }

    [Fact]
    public void NoNoteInTheSameFolderOrWithoutHistory()
    {
        var same = SessionMigrationTests.Sample("pi");
        var count = same.Entries.Count;
        SessionService.NoteFolderChange(same, "/work/app/", "/work/app");
        SessionService.NoteFolderChange(same, null, "/work/app");
        Assert.Equal(count, same.Entries.Count);
        var empty = new DeveSession();
        SessionService.NoteFolderChange(empty, "/work/other", "/work/app");
        Assert.Empty(empty.Entries);
    }

    private static DeveSession Conversation(params string[] prompts)
    {
        var session = new DeveSession();
        foreach (var prompt in prompts)
        {
            var id = Guid.NewGuid().ToString("N");
            session.Entries.Add(new DeveEntry { Id = id, ParentId = session.LeafId, Kind = DeveEntryKind.Message, Role = DeveRole.User, Content = [DeveBlock.OfText(prompt)] });
            session.LeafId = id;
        }

        return session;
    }
}
