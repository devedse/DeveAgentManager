using DeveAgentManager.Core.Agents;
using Xunit;
namespace DeveAgentManager.Tests;
public sealed class TerminalTextTests
{
    [Fact] public void HistoryAndScreenAreSeparatedAndMoreIsReported()
    {
        var text = AgentService.ParseTerminalText("3000\0old line  \n\0screen\n\n", 2000);
        Assert.Equal("old line", text.History);
        Assert.Equal("screen", text.Screen);
        Assert.True(text.More);
    }
    [Fact] public void EmptyHistoryDoesNotDuplicateScreen()
    {
        var text = AgentService.ParseTerminalText("0\0\0screen\n", 2000);
        Assert.Empty(text.History);
        Assert.False(text.More);
    }
}
