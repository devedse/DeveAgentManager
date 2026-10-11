using DeveAgentManager.Client.Shared;
using DeveAgentManager.Core.Sessions;
using Xunit;
namespace DeveAgentManager.Tests;
public sealed class ConversationCopyTests
{
    [Fact] public void CopyPreservesMarkdownAndUsesActiveReplyOnly()
    {
        var session = new DeveSession { LeafId = "thinking", Entries = [
            new() { Id = "reply", Kind = DeveEntryKind.Message, Role = DeveRole.Assistant, Content = [new() { Type = "text", Text = "**Reply**\n\n```cs\ncode\n```" }, new() { Type = "thinking", Text = "private thinking" }, new() { Type = "toolCall", Text = "tool output" }] },
            new() { Id = "other", ParentId = "reply", Kind = DeveEntryKind.Message, Role = DeveRole.Assistant, Content = [new() { Type = "text", Text = "abandoned" }] },
            new() { Id = "thinking", ParentId = "reply", Kind = DeveEntryKind.Message, Role = DeveRole.Assistant, Content = [new() { Type = "thinking", Text = "thinking" }] }
        ] };
        Assert.Equal("**Reply**\n\n```cs\ncode\n```", ConversationCopy.LastReply(session));
    }
}
