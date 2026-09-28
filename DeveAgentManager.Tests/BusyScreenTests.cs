using System.Diagnostics;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Working or idle: the state script's check runs (with bash and grep) on screens captured from pi, Claude Code and
/// Codex while they worked and after they answered (Claude also at a phone's width, where its footer is cut off).
/// </summary>
public sealed class BusyScreenTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("dam-busy-").FullName;

    [Theory]
    [InlineData("pi-working", true)]
    [InlineData("pi-idle", false)]
    [InlineData("claude-working", true)]
    [InlineData("claude-working-narrow", true)]
    [InlineData("claude-idle", false)]
    [InlineData("claude-idle-narrow", false)]
    [InlineData("codex-working", true)]
    [InlineData("codex-idle", false)]
    public void BusyPattern_IsOnTheScreen_OnlyWhileTheHarnessWorks(string screen, bool working)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pattern = screen.Split('-')[0] switch
        {
            "pi" => new PiHarness().BusyPattern,
            "claude" => new ClaudeHarness().BusyPattern,
            _ => new CodexHarness().BusyPattern,
        };

        // tmux stands in: capture-pane prints the captured screen, list-panes one running pane.
        var screenFile = Path.Combine(AppContext.BaseDirectory, "Screens", screen + ".txt");
        var tmux = Path.Combine(_bin, "tmux");
        File.WriteAllText(tmux, $"#!/bin/sh\ncase \"$1\" in capture-pane) cat '{screenFile}' ;; list-panes) echo 'dam-x|0||100' ;; esac\n");
        File.SetUnixFileMode(tmux, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var start = new ProcessStartInfo("bash", ["-c", AgentService.BuildStateScript([("dam-x", pattern)])]) { RedirectStandardOutput = true };
        start.Environment["PATH"] = _bin + ":" + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var (_, panes, busy) = AgentService.ParsePanes(output);
        Assert.True(panes.ContainsKey("dam-x"));
        Assert.Equal(working, busy.Contains("dam-x"));
    }

    public void Dispose() => Directory.Delete(_bin, true);
}
