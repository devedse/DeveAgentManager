using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Ssh;
using System.Text;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Telling the terminals attached to an agent apart.</summary>
public class ViewerTests
{
    [Fact]
    public void ParseViewers_ReadsTmuxClients_OldestFirst()
    {
        var viewers = AgentService.ParseViewers("/dev/pts/6|85|33|1790412999|1790413100\n/dev/pts/3|39|31|1790412900|1790412950\ngarbage\n|1|2|3|4\n");
        Assert.Equal(["/dev/pts/3", "/dev/pts/6"], viewers.Select(v => v.Tty));
        Assert.Equal((39, 31), (viewers[0].Columns, viewers[0].Rows));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790412900).UtcDateTime, viewers[0].ConnectedAt);
    }

    [Fact]
    public void ParseViewers_ReadsWhetherAScreenOnlyWatches()
    {
        var viewers = AgentService.ParseViewers("/dev/pts/3|85|33|1|2|attached,focused,UTF-8\n/dev/pts/4|39|31|3|4|attached,focused,ignore-size,UTF-8\n");
        Assert.Equal([false, true], viewers.Select(v => v.Watching));
    }

    [Theory]
    [InlineData("\u001b[?1;2c", true)]          // device attributes
    [InlineData("\u001b[>0;276;0c", true)]      // secondary device attributes
    [InlineData("\u001b[12;40R", true)]         // cursor position
    [InlineData("\u001b[I", true)]              // focus in
    [InlineData("\u001b]11;rgb:0a0a/0d0d/1313\u001b\\", true)] // OSC colour reply
    [InlineData("\u001b[?1;2c\u001b[I", true)]
    [InlineData("a", false)]
    [InlineData("\r", false)]
    [InlineData("\u001b[A", false)]             // arrow up
    [InlineData("\u001b[<0;10;5M", false)]      // mouse click
    [InlineData("\u001b[13;2u", false)]         // Shift+Enter
    public void TerminalReplies_AreNotTyping(string data, bool reply) => Assert.Equal(reply, TerminalInput.IsTerminalReply(data));

    [Fact]
    public void StartupFilter_PicksUpTheTty_AndHidesIt()
    {
        var filter = new StartupOutputFilter();
        // The shell echoes the command (with the escaped markers) before echo is off, then prints the real ones.
        var echoed = $"stty -echo; printf '{StartupOutputFilter.EscapedTtyMarker}%s\\n' \"$(tty)\"; printf '{StartupOutputFilter.EscapedMarker}\\n'\r\n";
        Assert.Empty(filter.Process(Encoding.UTF8.GetBytes(echoed)));
        Assert.Null(filter.Tty);

        var output = filter.Process(Encoding.UTF8.GetBytes($"{StartupOutputFilter.TtyMarker}/dev/pts/7\r\n{StartupOutputFilter.Marker}\r\nhello"));
        Assert.Equal("hello", Encoding.UTF8.GetString(output));
        Assert.Equal("/dev/pts/7", filter.Tty);
    }
}
