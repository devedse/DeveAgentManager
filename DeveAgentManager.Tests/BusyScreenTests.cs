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
    [InlineData("codex-background-wait-narrow", true)]
    [InlineData("codex-compacting", true)]
    [InlineData("codex-idle", false)]
    public void BusyPattern_IsOnTheScreen_OnlyWhileTheHarnessWorks(string screen, bool working)
        => AssertBusy(screen.Split('-')[0], File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", screen + ".txt")), working);

    [Theory]
    [InlineData("codex", "› Explain esc to interrupt")]
    [InlineData("claude", "❯ Explain esc to interrupt")]
    [InlineData("codex", "• The status says Working (6s • esc to interrupt).")]
    [InlineData("claude", "● The status says Clauding… (5s · ↓ 77 tokens).")]
    [InlineData("codex", "  │ Codex │ Working (6s • esc to interrupt) │")]
    [InlineData("claude", "  │ Claude │ Clauding… (5s · ↓ 77 tokens) │")]
    [InlineData("codex", "› ◦ Working (6s • esc to interrupt)")]
    [InlineData("codex", "› Waiting for background terminal (23m • esc to interrupt)")]
    [InlineData("codex", "› • Waiting for background terminal (23m • esc to interrupt)")]
    [InlineData("codex", "• The status says Waiting for background terminal (23m • esc to interrupt).")]
    [InlineData("codex", "• Waited for background terminal (23m)")]
    [InlineData("codex", "› • Compacting context (1m 42s • esc to interrupt)")]
    [InlineData("codex", "• The status says Compacting context (1m 42s • esc to interrupt).")]
    [InlineData("codex", "• Compacted context (1m 42s)")]
    [InlineData("codex", "  1 background terminal running · /ps to view · /stop to close")]
    [InlineData("claude", "❯ ✢ Clauding… (5s · ↓ 77 tokens)")]
    [InlineData("pi", " Explain ⠼ Working (medium effort)...")]
    public void BusyPattern_IgnoresBusyPhrasesInInputAndConversation(string harness, string text)
    {
        var screen = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", harness + "-idle.txt"));
        AssertBusy(harness, screen + "\n" + text, false);
    }

    [Theory]
    [InlineData("◦ Compacting context (1s • esc to interrupt)")]
    [InlineData("• Compacting context (1m…")]
    public void BusyPattern_RecognizesCompactingAtDifferentWidths(string status)
        => AssertBusy("codex", status + "\n› Ask Codex to do anything\n  ? for shortcuts", true);

    private void AssertBusy(string harness, string screen, bool working)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pattern = harness switch
        {
            "pi" => new PiHarness().BusyPattern,
            "claude" => new ClaudeHarness().BusyPattern,
            _ => new CodexHarness().BusyPattern,
        };

        // tmux stands in: capture-pane prints the captured screen, list-panes one running pane.
        var screenFile = Path.Combine(_bin, "screen.txt");
        File.WriteAllText(screenFile, screen);
        var tmux = Path.Combine(_bin, "tmux");
        File.WriteAllText(tmux, $"#!/bin/sh\ncase \"$1\" in capture-pane) cat '{screenFile}' ;; list-panes) echo 'dam-x|0||100' ;; esac\n");
        File.SetUnixFileMode(tmux, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var start = new ProcessStartInfo("bash", ["-c", AgentService.BuildStateScript([("dam-x", pattern)])]) { RedirectStandardOutput = true };
        start.Environment["PATH"] = _bin + ":" + Environment.GetEnvironmentVariable("PATH");
        // SSH sessions can use the C locale; Unicode spinner alternatives must work there too.
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        var (_, panes, busy) = AgentService.ParsePanes(output);
        Assert.True(panes.ContainsKey("dam-x"));
        Assert.Equal(working, busy.Contains("dam-x"));
    }

    public void Dispose() => Directory.Delete(_bin, true);
}
