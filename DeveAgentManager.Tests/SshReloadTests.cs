using System.Diagnostics;
using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Reloading SSH after changing whether passwords are allowed, with systemctl and sudo stubbed out.</summary>
public sealed class SshReloadTests
{
    [Theory]
    // A server that opens its port itself (the Runner VMs): a reload keeps it running.
    [InlineData("ssh", "", "reload ssh")]
    [InlineData("sshd", "", "reload sshd")]
    // Debian 13 and Ubuntu: systemd holds the port, and a reloaded sshd exits because it can't open it again.
    [InlineData("ssh ssh.socket", "no", "try-restart ssh")]
    [InlineData("ssh.socket", "no", "try-restart ssh")]
    [InlineData("sshd sshd.socket", "no", "try-restart sshd")]
    // An sshd for every connection reads the new configuration anyway.
    [InlineData("ssh.socket", "yes", "")]
    [InlineData("sshd.socket", "yes", "")]
    // A unit that isn't running doesn't count, whatever its Accept says.
    [InlineData("ssh", "no", "reload ssh")]
    public async Task ReloadsOrRestartsTheRunningServer(string active, string accept, string expected)
    {
        var (exitCode, output, _) = await RunAsync(active, accept);
        Assert.Equal(0, exitCode);
        Assert.Equal(expected, output.Trim());
    }

    [Fact]
    public async Task FailsWhenNoSshServerRuns()
    {
        var (exitCode, output, error) = await RunAsync("", "no");
        Assert.Equal(4, exitCode);
        Assert.Equal("", output);
        Assert.Contains("no running SSH server", error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string active, string accept)
    {
        // systemctl show -p Accept prints "no" for any socket, even one that doesn't exist.
        var script = $$"""
            active=' {{active}} '
            systemctl() {
                case "$1" in
                    is-active) case "$active" in *" $3 "*) return 0 ;; *) return 3 ;; esac ;;
                    show) echo '{{(accept == "" ? "no" : accept)}}' ;;
                    *) echo "$*" ;;
                esac
            }
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
