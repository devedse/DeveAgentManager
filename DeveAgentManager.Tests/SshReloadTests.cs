using System.Diagnostics;
using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Reloading SSH after changing whether passwords are allowed, with systemctl and sudo stubbed out.</summary>
public sealed class SshReloadTests
{
    [Theory]
    [InlineData("ssh", "reload ssh")]
    [InlineData("sshd", "reload sshd")]
    [InlineData("ssh ssh.socket", "reload ssh")]
    // Debian's container templates: every connection starts its own sshd, which reads the new configuration.
    [InlineData("ssh.socket", "")]
    [InlineData("sshd.socket", "")]
    public async Task ReloadsTheRunningServerOrNothingWhenSocketActivated(string active, string expected)
    {
        var (exitCode, output, _) = await RunAsync(active);
        Assert.Equal(0, exitCode);
        Assert.Equal(expected, output.Trim());
    }

    [Fact]
    public async Task FailsWhenNoSshServerRuns()
    {
        var (exitCode, output, error) = await RunAsync("");
        Assert.Equal(4, exitCode);
        Assert.Equal("", output);
        Assert.Contains("no running SSH server", error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string active)
    {
        var script = $$"""
            active=' {{active}} '
            systemctl() { if [ "$1" = is-active ]; then case "$active" in *" $3 "*) return 0 ;; *) return 3 ;; esac; fi; echo "$*"; }
            sudo() { shift; "$@"; }
            {{RunnerPasswords.ReloadSshScript}}
            """;
        var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, error);
    }
}
