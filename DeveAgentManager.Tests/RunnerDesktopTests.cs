using System.Diagnostics;
using System.Text;
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
    public void AgentToolsChecksum_IsWhatSha256sumPrintsForTheUploadedScript()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"dam-tools-{Guid.NewGuid():N}.py");
        try
        {
            // Uploaded as UTF-8 with Unix line endings, as RunnerShell.UploadAsync writes it.
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(RunnerSetupService.ReadResource("dam-tools.py")));
            using var process = Process.Start(new ProcessStartInfo("sha256sum", [path]) { RedirectStandardOutput = true })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(output[..64], RunnerSetupService.AgentToolsSha256);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShellsKeepTheirRuntimeDirectory_WhenNoDesktopRuns()
    {
        // desktop-env.sh clears the display settings before every Bash command. XDG_RUNTIME_DIR is the account's, not the
        // desktop's: without it systemctl --user can't start a stopped browser desktop again.
        var setup = RunnerSetupService.ReadResource("desktop-setup.sh");
        Assert.Contains("unset DISPLAY XAUTHORITY", setup);
        Assert.DoesNotMatch(@"unset [^\n]*XDG_RUNTIME_DIR", setup);
        Assert.StartsWith("export XDG_RUNTIME_DIR=", DeveAgentManager.Core.Editor.DesktopTunnels.StartScript);
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
    [InlineData("docker-setup.sh")]
    [InlineData("system-update.sh")]
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

    [Fact]
    public void SystemUpdate_ReadsThePackageListsThenUpgradesWithoutAskingAnything()
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "dam-system-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var calls = Path.Combine(folder, "calls");
            foreach (var (name, body) in new[]
            {
                ("sudo", "[ \"$1\" = -n ] && shift\nexec \"$@\"\n"),
                ("apt-get", $"printf '%s %s|%s\\n' \"$DEBIAN_FRONTEND\" \"$NEEDRESTART_MODE\" \"$*\" >> '{calls}'\n"),
            })
            {
                File.WriteAllText(Path.Combine(folder, name), "#!/bin/sh\n" + body);
                File.SetUnixFileMode(Path.Combine(folder, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var start = new ProcessStartInfo("bash", ["-s"]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = folder + ":" + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            process.StandardInput.Write(RunnerSetupService.ReadResource("system-update.sh"));
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output + error);
            const string options = "-q -y -o DPkg::Lock::Timeout=600 -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold";
            Assert.Equal([$"noninteractive l|{options} update", $"noninteractive l|{options} dist-upgrade"], File.ReadAllLines(calls));
            Assert.Contains("==> Upgrading the system packages", output);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void DockerSetup_LeavesAMachinesOwnDockerAloneAndGivesTheAccountDockerWhereAgentsGetIt(bool inGroup, bool agentsRunning)
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "dam-docker-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var calls = Path.Combine(folder, "calls");
            var log = $"echo \"$(basename \"$0\") $*\" >> '{calls}'\n";
            foreach (var (name, body) in new[]
            {
                // What sudo runs as root is recorded rather than run (the restart flag lives in /var/run).
                ("sudo", $"[ \"$1\" = -n ] && shift\nif [ \"$1\" = sh ]; then echo \"root $3\" >> '{calls}'; exit 0; fi\nexec \"$@\"\n"),
                ("docker", "case \"$1\" in --version) echo 'Docker version 27.5.1, build abc' ;; info) if [ \"$2\" = --format ]; then echo 'Docker 27.5.1 is running (storage overlay2).'; fi ;; esac\n"),
                ("id", $"if [ \"$1\" = -nG ]; then echo '{(inGroup ? "runner docker" : "runner")}'; else exec /usr/bin/id \"$@\"; fi\n"),
                ("getent", "echo 'docker:x:991:'\n"),
                ("tmux", agentsRunning ? "exit 0\n" : "exit 1\n"),
                ("loginctl", "exit 0\n"),
                ("systemctl", log + "case \"$*\" in *is-active*) exit 1 ;; esac\n"),
                ("usermod", log), ("apt-get", log), ("curl", log + "exit 1\n"), ("tee", log + "cat >/dev/null\n"),
            })
            {
                File.WriteAllText(Path.Combine(folder, name), "#!/bin/sh\n" + body);
                File.SetUnixFileMode(Path.Combine(folder, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var start = new ProcessStartInfo("bash", ["-s"]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = folder + ":" + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            process.StandardInput.Write(RunnerSetupService.ReadResource("docker-setup.sh"));
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output + error);
            var made = File.Exists(calls) ? File.ReadAllLines(calls) : [];
            Assert.Contains("Docker is already installed (Docker version 27.5.1, build abc): left as it is.", output);
            Assert.Contains("Docker 27.5.1 is running", output);
            // Its packages, repository and settings stay as they are.
            Assert.DoesNotContain(made, c => c.StartsWith("apt-get", StringComparison.Ordinal) || c.StartsWith("curl", StringComparison.Ordinal) || c.StartsWith("tee", StringComparison.Ordinal));
            Assert.Equal(!inGroup, made.Any(c => c.StartsWith("usermod -aG docker ", StringComparison.Ordinal)));
            // With agents' tmux running, Restart's badge asks for a restart; otherwise only the idle systemd manager restarts.
            Assert.Equal(!inGroup && agentsRunning, made.Contains("root touch /var/run/reboot-required && echo docker >> /var/run/reboot-required.pkgs"));
            Assert.Equal(!inGroup && !agentsRunning, made.Any(c => c.StartsWith("systemctl try-restart user@", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(folder, true); }
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
