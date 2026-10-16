using System.Diagnostics;
using DeveAgentManager.Core.Agents;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class TerminalLinkTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserAttachmentPreservesNamedHyperlinksThroughRealTmux(bool readOnly)
    {
        if (!OperatingSystem.IsLinux()) return;
        var original = await CaptureAsync("exec tmux -u attach-session -t =dam-linktest");
        Assert.Contains("Named report", original);
        Assert.DoesNotContain("https://example.org/named#details", original);
        var browser = await CaptureAsync(AgentService.BuildAttachScript("dam-linktest", readOnly));
        Assert.Contains("\u001b]8;", browser);
        Assert.Contains("https://example.org/named#details", browser);
        Assert.Contains("Named report", browser);
        Assert.Contains("https://example.com/report#results", browser);
    }

    private static async Task<string> CaptureAsync(string attach)
    {
        var start = new ProcessStartInfo("python3", [Path.Combine(AppContext.BaseDirectory, "Browser", "tmux-link-stream.py"),
            "--attach-script", attach]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
