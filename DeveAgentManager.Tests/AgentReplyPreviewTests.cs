using DeveAgentManager.Core.Sessions;
using Xunit;
namespace DeveAgentManager.Tests;
public class AgentReplyPreviewTests
{
    [Fact]
    public void PreviewUsesActiveBranchAndAssistantTextOnly()
    {
        var session = new DeveSession { LeafId = "thinking", Entries = [
            new() { Id = "reply", Kind = DeveEntryKind.Message, Role = DeveRole.Assistant, Content = [new() { Type = "text", Text = "Actual\nreply" }] },
            new() { Id = "other", ParentId = "reply", Kind = DeveEntryKind.Message, Role = DeveRole.Assistant, Text = "Abandoned branch" },
            new() { Id = "thinking", ParentId = "reply", Kind = DeveEntryKind.Message, Role = DeveRole.Assistant, Content = [new() { Type = "thinking", Text = "Not a reply" }] }
        ] };
        Assert.Equal("Actual reply", SessionService.ReplyPreview(session));
        session.Entries[0].Content![0].Text = new string('x', 400);
        Assert.Equal(281, SessionService.ReplyPreview(session).Length);
    }
}
