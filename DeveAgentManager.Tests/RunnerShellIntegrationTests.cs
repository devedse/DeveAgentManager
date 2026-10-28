using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Ssh;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public class RunnerShellIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task PoolRecoversWithoutReplayingOrInterruptingOtherCommandsAndPreventsLateSubmission()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) return;
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "The isolated transport regression requires openssh-server.");
        await using var fixture = await Fixture.StartAsync();
        var shell = fixture.Shell;
        var runner = fixture.Runner;
        Assert.Equal(fixture.Root + "/home", (await shell.RunAsync(runner, "printf '%s' \"$HOME\"", cancellationToken: Ct)).Output);
        Assert.Equal("reused", (await shell.RunAsync(runner, "printf reused", cancellationToken: Ct)).Output);
        Assert.Equal(1, fixture.Authentications);
        var burst = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => shell.RunAsync(runner, $"sleep 0.05; printf '{i}'", cancellationToken: Ct)));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString()), burst.Select(r => r.Output));
        Assert.InRange(fixture.Authentications, 2, RunnerShell.CommandCapacity);

        var started = fixture.Root + "/started";
        var effect = fixture.Root + "/effect";
        var running = shell.RunAsync(runner, $"printf started > {ShellQuote.Quote(started)}; sleep 2; printf preserved", TimeSpan.FromSeconds(10), Ct);
        await WaitForFileAsync(started);
        await Assert.ThrowsAsync<TimeoutException>(() => shell.RunAsync(runner,
            $"printf once >> {ShellQuote.Quote(effect)}; sleep 5", TimeSpan.FromMilliseconds(150), Ct));
        Assert.Equal("healthy", (await shell.RunAsync(runner, "printf healthy", cancellationToken: Ct)).Output);
        Assert.True(fixture.Authentications >= 3);
        // Invalidating an endpoint drains leases rather than dropping another caller's result.
        shell.Invalidate(runner.Id);
        Assert.Equal("replacement", (await shell.RunAsync(runner, "printf replacement", cancellationToken: Ct)).Output);
        Assert.Equal("preserved", (await running).Output);
        Assert.Equal("once", await File.ReadAllTextAsync(effect, Ct));

        // Reproduce an open that outlasts the caller's deadline on a reused connection.
        // Pause only this disposable server's children, never the host SSH service.
        var paused = Descendants(fixture.Server.Id).ToArray();
        Assert.NotEmpty(paused);
        await SignalAsync("-STOP", paused);
        var late = fixture.Root + "/late";
        var sessionsBefore = fixture.CommandSessions;
        var watch = Stopwatch.StartNew();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => shell.RunAsync(runner,
                $"printf forbidden > {ShellQuote.Quote(late)}", TimeSpan.FromMilliseconds(150), Ct));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "Cancellation did not interrupt synchronous channel setup.");
            Assert.Equal("fresh", (await shell.RunAsync(runner, "printf fresh", TimeSpan.FromSeconds(3), Ct)).Output);
        }
        finally { await SignalAsync("-CONT", paused); }
        await Task.Delay(250, Ct);
        Assert.False(File.Exists(late));
        Assert.Equal(sessionsBefore + 1, fixture.CommandSessions); // Only the independent fresh read ran.

        var lines = new List<string>();
        Assert.Equal(7, await shell.RunStreamingAsync(runner, "printf 'first\\n'; sleep 0.1; printf 'second\\n'; exit 7", lines.Add, TimeSpan.FromSeconds(3), Ct));
        Assert.Equal(new[] { "first", "second" }, lines);
        var remote = fixture.Root + "/home/file";
        await shell.UploadAsync(runner, remote, "fixture content", cancellationToken: Ct);
        Assert.Equal("fixture content", await shell.DownloadAsync(runner, remote, Ct));
        await using var downloaded = new MemoryStream();
        Assert.True(await shell.DownloadToAsync(runner, remote, downloaded, 100, Ct));
        Assert.Equal("fixture content", System.Text.Encoding.UTF8.GetString(downloaded.ToArray()));
        Assert.Null(await shell.DownloadBytesAsync(runner, remote, 1, Ct));
        Assert.Null(await shell.DownloadAsync(runner, remote + "-missing", Ct));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shell.RunAsync(runner, "exit 99", cancellationToken: canceled.Token));
        Assert.DoesNotContain("no more sessions", await File.ReadAllTextAsync(fixture.Log, Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ALargePastedFileIsStreamedFromItsBufferToTheRunner()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "The isolated transport regression requires openssh-server.");
        await using var fixture = await Fixture.StartAsync();
        var data = Directory.CreateTempSubdirectory("dam-upload-data-").FullName;
        try
        {
            var terminals = new DeveAgentManager.Core.Runners.TerminalService(null!, fixture.Shell, null!, null!,
                new DeveAgentManager.Core.Configuration.ManagerOptions { DataDirectory = data });
            // As the terminal socket fills it: 64 MB in 64 KB frames, then sent on from its start.
            var chunk = new byte[64 * 1024];
            await using var buffer = terminals.NewUploadBuffer();
            for (var i = 0; i < 1024; i++)
            {
                Array.Fill(chunk, (byte)(i % 251));
                await buffer.WriteAsync(chunk, Ct);
            }

            buffer.Position = 0;
            var expected = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(buffer, Ct));
            buffer.Position = 0;
            var remote = fixture.Root + "/home/uploads/nested/build.tar";
            var path = await fixture.Shell.UploadAsync(fixture.Runner, remote, buffer, "600", Ct);
            Assert.Equal(remote, path);
            Assert.Equal(64L * 1024 * 1024, new FileInfo(remote).Length);
            Assert.Equal(expected, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(remote))));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(remote));
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var i = 0; !File.Exists(path) && i < 100; i++) await Task.Delay(20, Ct);
        Assert.True(File.Exists(path));
    }

    private static IEnumerable<int> Descendants(int pid)
    {
        var path = $"/proc/{pid}/task/{pid}/children";
        string children;
        try { children = File.ReadAllText(path); }
        catch (IOException) { yield break; }
        foreach (var child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse))
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task KillTmuxServerAsync(string root)
    {
        if (!File.Exists("/usr/bin/tmux")) return;
        await new IsolatedTmux(root).StopAsync();
    }

    private static async Task SignalAsync(string signal, int[] pids)
    {
        if (pids.Length > 0) await RunAsync("/bin/kill", [signal, "--", .. pids.Select(p => p.ToString())], ensureSuccess: false,
            cancellationToken: CancellationToken.None); // Cleanup must also resume fixtures after test cancellation.
    }

    private static async Task<string> RunAsync(string command, string[] args, bool ensureSuccess = true, CancellationToken? cancellationToken = null)
    {
        var ct = cancellationToken ?? Ct;
        using var process = Process.Start(new ProcessStartInfo(command, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (ensureSuccess) Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    internal sealed class Fixture(string root, Process server, ManagerSshKey key, RunnerShell shell, Runner runner) : IAsyncDisposable
    {
        internal string Root => root;
        internal string Log => root + "/server.log";
        internal Process Server => server;
        internal RunnerShell Shell => shell;
        internal Runner Runner => runner;
        internal int Authentications => File.ReadAllLines(Log).Count(l => l.Contains("Accepted publickey"));
        internal int CommandSessions => File.ReadAllLines(Log).Count(l => l.Contains("Starting session: command"));

        internal static async Task<Fixture> StartAsync()
        {
            var root = Directory.CreateTempSubdirectory("dam-shell-test-").FullName;
            ManagerSshKey? key = null;
            Process? server = null;
            RunnerShell? shell = null;
            try
            {
                Directory.CreateDirectory(root + "/home");
                var tmux = new IsolatedTmux(root);
                key = ManagerSshKey.LoadOrCreate(root + "/key");
                await File.WriteAllTextAsync(root + "/authorized_keys", key.PublicKey + "\n", Ct);
                await RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", root + "/host_key"]);
                var fingerprint = (await RunAsync("ssh-keygen", ["-lf", root + "/host_key.pub", "-E", "sha256"])).Split(' ')[1];
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                await File.WriteAllTextAsync(root + "/sshd_config", $"""
                    ListenAddress 127.0.0.1
                    Port {port}
                    HostKey {root}/host_key
                    PidFile {root}/sshd.pid
                    AuthorizedKeysFile {root}/authorized_keys
                    StrictModes no
                    UsePAM no
                    PubkeyAuthentication yes
                    PasswordAuthentication no
                    KbdInteractiveAuthentication no
                    MaxSessions 10
                    SetEnv HOME={root}/home PATH={tmux.Bin}:{Environment.GetEnvironmentVariable("PATH")} TMUX= TMUX_PANE=
                    Subsystem sftp internal-sftp -d {root}/home
                    LogLevel VERBOSE

                    """, Ct);
                server = Process.Start(new ProcessStartInfo("/usr/sbin/sshd", ["-D", "-E", root + "/server.log", "-f", root + "/sshd_config"]) { RedirectStandardError = true })!;
                for (var i = 0; i < 50; i++)
                {
                    if (server.HasExited) throw new Exception("Isolated sshd failed: " + await server.StandardError.ReadToEndAsync(Ct));
                    try { using var probe = new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, port, Ct); break; }
                    catch (SocketException) { await Task.Delay(20, Ct); }
                }
                shell = new(new SshConnector(key), new SecretProtector(new EphemeralDataProtectionProvider()), null!, new ManagerEvents(), NullLogger<RunnerShell>.Instance);
                return new(root, server, key, shell, new Runner { Id = Guid.NewGuid(), Name = "fixture", Host = "127.0.0.1", SshPort = port, Username = Environment.UserName, HostKeyFingerprint = fingerprint });
            }
            catch
            {
                shell?.Dispose();
                if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(CancellationToken.None); }
                server?.Dispose();
                key?.KeyFile.Dispose();
                Directory.Delete(root, true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Resume paused fixture children even if an assertion/cancellation failed.
            await SignalAsync("-CONT", Descendants(server.Id).ToArray());
            shell.Dispose();
            await KillTmuxServerAsync(root);
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync(CancellationToken.None);
            server.Dispose();
            key.KeyFile.Dispose();
            Directory.Delete(root, true);
        }
    }
}
