using System.Diagnostics;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class TmuxIsolationTests
{
    [Fact(Timeout = 30000)]
    public async Task FixtureCommandsAndCleanupIgnoreAnInheritedForeignSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/tmux")) return;
        var root = Directory.CreateTempSubdirectory("dam-tmux-isolation-").FullName;
        var sentinel = new IsolatedTmux(Path.Combine(root, "sentinel"));
        var fixture = new IsolatedTmux(Path.Combine(root, "fixture"));
        try
        {
            await RunAsync(sentinel, "tmux new-session -d -s sentinel 'sleep 120'");
            await RunAsync(fixture, "tmux new-session -d -s fixture 'sleep 120'");
            Assert.Equal("fixture", (await RunAsync(fixture, "tmux list-sessions -F '#{session_name}'", contaminate: true)).Trim());
            await fixture.StopAsync();
            Assert.Equal("sentinel", (await RunAsync(sentinel, "tmux list-sessions -F '#{session_name}'")).Trim());
            // Repeating cleanup with no fixture socket must remain a no-op.
            await fixture.StopAsync();
            Assert.Equal("sentinel", (await RunAsync(sentinel, "tmux list-sessions -F '#{session_name}'")).Trim());
        }
        finally
        {
            await fixture.StopAsync(); await sentinel.StopAsync();
            Directory.Delete(root, true);
        }

        async Task<string> RunAsync(IsolatedTmux isolated, string script, bool contaminate = false)
        {
            var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
            isolated.Configure(start);
            // Deliberately contaminate AFTER Configure to prove the explicit -S wrapper is sufficient by itself.
            if (contaminate) start.Environment["TMUX"] = sentinel.SocketPath + ",1,0";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
    }
}
