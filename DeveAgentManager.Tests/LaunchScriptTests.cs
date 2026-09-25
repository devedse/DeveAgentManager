using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Ssh;
using System.Diagnostics;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Runs the agent launch script against a real tmux (on its own server, so it doesn't touch the user's sessions).
/// A start must never kill a harness that is still running: that is what crashed pi mid-tool-call.
/// </summary>
public sealed class LaunchScriptTests : IDisposable
{
    private readonly string _tmuxDir = Directory.CreateTempSubdirectory("dam-tmux-").FullName;
    private readonly string _work = Directory.CreateTempSubdirectory("dam-work-").FullName;
    private const string SessionName = "dam-launchtest";

    [Fact]
    public void Start_LeavesARunningHarnessAlone_RestartReplacesIt()
    {
        if (!HasTmux())
        {
            return;
        }

        // The "harness" exits on SIGTERM, like pi does.
        var marker = Path.Combine(_work, "starts");
        var launch = $"sh -c 'echo x >> {marker}; trap \"exit 0\" TERM; while :; do sleep 0.2; done'";

        Run(Script(launch, restart: false));
        var firstPid = PanePid();
        Assert.Equal(1, Starts(marker));

        var output = Run(Script(launch, restart: false));
        Assert.Contains(AgentService.AlreadyRunningMarker, output);
        Assert.Equal(firstPid, PanePid());
        Assert.Equal(1, Starts(marker));

        Run(Script(launch, restart: true));
        Assert.NotEqual(firstPid, PanePid());
        WaitFor(() => Starts(marker) == 2);

        // After the harness exits, a start runs it again.
        Tmux($"respawn-pane -k -t ={SessionName}: true");
        WaitFor(() => Tmux($"display-message -p -t ={SessionName}: '#{{pane_dead}}'").Trim() == "1");
        Run(Script(launch, restart: false));
        WaitFor(() => Starts(marker) == 3);
    }

    private string Script(string launch, bool restart) => AgentService.BuildLaunchScript(
        ShellQuote.Quote("=" + SessionName), ShellQuote.Quote(SessionName), ShellQuote.Quote("=" + SessionName + ":"),
        ShellQuote.Quote(_work), ShellQuote.Quote(launch), restart);

    private static int Starts(string marker) => File.Exists(marker) ? File.ReadAllLines(marker).Length : 0;

    private string PanePid() => Tmux($"display-message -p -t ={SessionName}: '#{{pane_pid}}'").Trim();

    private static void WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "Timed out waiting.");
            Thread.Sleep(100);
        }
    }

    private string Tmux(string arguments) => Run("tmux " + arguments);

    private string Run(string script)
    {
        var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["TMUX_TMPDIR"] = _tmuxDir;
        start.Environment.Remove("TMUX");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {output}{error}");
        return output;
    }

    private static bool HasTmux()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("tmux", "-V") { RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            Run($"tmux kill-server 2>/dev/null || true");
        }
        catch
        {
            // Best effort.
        }

        Directory.Delete(_tmuxDir, true);
        Directory.Delete(_work, true);
    }
}
