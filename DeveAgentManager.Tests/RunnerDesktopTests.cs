using System.Diagnostics;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Setup;
using Xunit;

namespace DeveAgentManager.Tests;

public class RunnerDesktopTests
{
    [Theory]
    [InlineData("desktop.test.py")]
    [InlineData("dam-tools.test.py")]
    public void DesktopSetupAndConnectionRegressions(string name)
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Path.Combine(AppContext.BaseDirectory, "Desktop", name);
        var start = new ProcessStartInfo("python3", [script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Desktop helper tests timed out.");
        Assert.True(process.ExitCode == 0, output + error);
    }

    [Fact]
    public void OnlyDesktopRunnersLoadTheGraphicalEnvironment()
    {
        Assert.DoesNotContain("desktop-env", RunnerSetupService.BuildAgentEnv());
        Assert.Contains("desktop-env", RunnerSetupService.BuildAgentEnv(desktop: true));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"state\":\"ready\"}")]
    [InlineData("{\"state\":\"unexpected\",\"message\":\"hello\"}")]
    public void InvalidStatusNeverClaimsDesktopIsReady(string text) => Assert.Equal("unavailable", RunnerDesktop.Parse(text).State);

    [Fact]
    public void DesktopStatusIncludesBackendAndUiAvailability()
    {
        var status = RunnerDesktop.Parse("{\"state\":\"ready\",\"message\":\"Desktop ready\",\"sessionType\":\"x11\",\"display\":\":4\",\"automationAvailable\":true}");
        Assert.Equal("ready", status.State);
        Assert.Equal(":4", status.Display);
        Assert.True(status.AutomationAvailable);
    }

    [Theory]
    [InlineData("runner-setup.sh")]
    [InlineData("desktop-setup.sh")]
    [InlineData("desktop-shell.sh")]
    public void SetupScriptsHaveValidBashSyntax(string name)
    {
        if (OperatingSystem.IsWindows()) return;
        using var process = Process.Start(new ProcessStartInfo("bash", ["-n"]) { RedirectStandardInput = true, RedirectStandardError = true })!;
        process.StandardInput.Write(RunnerSetupService.ReadResource(name));
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryBashCommandRefreshesTheDesktopAndKeepsPreviousStartupRules(bool ssh)
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "dam-shell-" + Guid.NewGuid().ToString("N"));
        var managed = Path.Combine(folder, ".deveagentmanager");
        Directory.CreateDirectory(managed);
        try
        {
            File.WriteAllText(Path.Combine(managed, "desktop-shell.sh"), RunnerSetupService.ReadResource("desktop-shell.sh"));
            File.WriteAllText(Path.Combine(managed, "desktop-env.sh"), "export DISPLAY=$(cat \"$HOME/display\")\nexport XAUTHORITY=refreshed\n");
            if (ssh)
            {
                Directory.CreateDirectory(Path.Combine(folder, ".config", "autostart"));
                const string original = "case $- in *i*) ;; *) return ;; esac\n# User's remaining settings\n";
                File.WriteAllText(Path.Combine(folder, ".bashrc"), original);
                var setup = RunnerSetupService.ReadResource("desktop-setup.sh");
                const string marker = "python3 - <<'PY'\n";
                var from = setup.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                var script = setup[from..setup.IndexOf("\nPY\n", from, StringComparison.Ordinal)];
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var python = new ProcessStartInfo("python3", ["-"]) { RedirectStandardInput = true, RedirectStandardError = true };
                    python.Environment["HOME"] = folder;
                    using var installation = Process.Start(python)!;
                    installation.StandardInput.Write(script); installation.StandardInput.Close();
                    var installationError = installation.StandardError.ReadToEnd(); installation.WaitForExit();
                    Assert.True(installation.ExitCode == 0, installationError);
                }
                var rc = File.ReadAllText(Path.Combine(folder, ".bashrc"));
                Assert.EndsWith(original, rc);
                Assert.Equal(1, rc.Split("# >>> DeveAgentManager desktop shell >>>").Length - 1);
            }
            var previous = Path.Combine(folder, "previous.sh");
            File.WriteAllText(previous, "export USER_STARTUP=kept\nbash -c ':'\n");
            File.WriteAllText(Path.Combine(folder, "display"), ":7");
            var start = new ProcessStartInfo("bash", ["-c", "printf '%s %s %s\\n' \"$DISPLAY\" \"$XAUTHORITY\" \"$USER_STARTUP\"; echo :9 > \"$HOME/display\"; bash -c 'printf \"%s\\n\" \"$DISPLAY\"'"])
                { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["HOME"] = folder;
            start.Environment["BASH_ENV"] = Path.Combine(managed, "desktop-shell.sh");
            start.Environment["DAM_PREVIOUS_BASH_ENV"] = previous;
            start.Environment["DISPLAY"] = ":stale";
            if (ssh) { start.Environment["SSH_CLIENT"] = "127.0.0.1 1234 22"; start.Environment["SHLVL"] = "0"; }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            Assert.Equal(":7 refreshed kept\n:9\n", output.ReplaceLineEndings("\n"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ViewerStopsWhileHiddenAndOnNavigationAndPreservesTheLastFrameOnError()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Js");
        using var process = Process.Start(new ProcessStartInfo("node", [Path.Combine(folder, "desktop-view.test.cjs"), Path.Combine(folder, "desktop-view.js")])
            { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }
}
