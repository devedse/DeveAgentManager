using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Grok;
using DeveAgentManager.Harnesses.Pi;
using System.Diagnostics;
using System.Text;
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
    private readonly string _home = Directory.CreateTempSubdirectory("dam-launch-home-").FullName;
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
        WaitFor(() => Starts(marker) == 1);
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

    /// <summary>
    /// tmux refuses commands over 16 KiB ("command too long") and SSH.NET packets over 68,536 bytes ("Packet is too big"),
    /// and the launch command is quoted again for every shell it passes through. Scripts belong in launch files.
    /// </summary>
    [Theory]
    [InlineData("pi")]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("grok")]
    public void Start_FitsTmuxAndSsh_ForEveryHarness(string harnessId)
    {
        if (!HasTmux())
        {
            return;
        }

        IHarness harness = harnessId switch { "claude" => new ClaudeHarness(), "codex" => new CodexHarness(), "grok" => new GrokHarness(), _ => new PiHarness() };
        var command = harness.BuildLaunchCommand(new(Guid.NewGuid().ToString(), "size-test", _work, Model: "gpt-6-sol",
            Instructions: AgentService.LaunchInstructions, Effort: "high", AgentId: Guid.NewGuid()));
        // Composed as AgentService does, with more login and runtime environment than a real start has.
        var environment = string.Concat(Enumerable.Range(0, 8).Select(i =>
            $". \"$HOME/.deveagentmanager/accounts/{Guid.NewGuid():N}/env.sh\"; export DAM_TEST_{i}=\"{new string('x', 160)}\"; "));
        var launch = "bash -c " + ShellQuote.Quote($". \"{RunnerShell.EnvScript}\"; {environment}exec {command}");
        // The pane exits before it reaches the harness: only tmux's acceptance of the command is tested.
        var script = Script("exit 0; " + launch, restart: false, AgentService.BuildLaunchFilesScript(harness.LaunchFiles));
        Assert.InRange(Encoding.UTF8.GetByteCount(RunnerShell.Wrap(script)), 0, 60_000);

        Run(script);
        foreach (var (path, content) in harness.LaunchFiles)
        {
            Assert.Equal(content, File.ReadAllText(path.Replace("$HOME", _home)));
        }
    }

    private string Script(string launch, bool restart, string files = "") => AgentService.BuildLaunchScript(
        ShellQuote.Quote("=" + SessionName), ShellQuote.Quote(SessionName), ShellQuote.Quote("=" + SessionName + ":"),
        ShellQuote.Quote(_work), ShellQuote.Quote(launch), restart, files);

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
        new IsolatedTmux(_tmuxDir).Configure(start);
        start.Environment["HOME"] = _home;
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
            new IsolatedTmux(_tmuxDir).StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort.
        }

        Directory.Delete(_tmuxDir, true);
        Directory.Delete(_work, true);
        Directory.Delete(_home, true);
    }
}
