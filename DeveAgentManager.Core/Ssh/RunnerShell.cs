using System.Collections.Concurrent;
using System.Text;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeveAgentManager.Core.Ssh;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output.Trim() : $"{Output.Trim()}\n{Error.Trim()}".Trim();

    public CommandResult EnsureSuccess(string what)
    {
        if (!Succeeded)
        {
            throw new RunnerCommandException($"{what} failed (exit code {ExitCode}): {Combined}");
        }

        return this;
    }
}

public sealed class RunnerCommandException(string message) : Exception(message);

/// <summary>
/// Runs commands and transfers files on Runners over SSH. Keeps one connection per Runner open and reuses it.
/// Every command runs in bash with the Manager's env.sh sourced, so tools installed by setup are on the PATH.
/// </summary>
public sealed class RunnerShell(
    SshConnector connector,
    SecretProtector protector,
    IDbContextFactory<ManagerDbContext> dbFactory,
    ManagerEvents events,
    ILogger<RunnerShell> logger) : IDisposable
{
    public const string EnvScript = "$HOME/.deveagentmanager/env.sh";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, Pooled> _pool = new();

    public SshTarget CreateTarget(Runner runner)
    {
        if (string.IsNullOrEmpty(runner.Host))
        {
            throw new InvalidOperationException($"Runner '{runner.Name}' has no address yet.");
        }

        return new SshTarget(runner.Id, runner.Host, runner.SshPort, runner.Username, protector.Unprotect(runner.ProtectedPassword), runner.HostKeyFingerprint);
    }

    /// <summary>Stores the host key the first time we connect to a Runner (trust on first use).</summary>
    public Func<string, Task> HostKeyLearner(Runner runner) => async fingerprint =>
    {
        runner.HostKeyFingerprint = fingerprint;
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Runners.Where(r => r.Id == runner.Id && r.HostKeyFingerprint == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.HostKeyFingerprint, fingerprint));
        events.RunnerChanged(runner.Id);
    };

    /// <summary>Opens a new, unpooled connection (used for terminals, so they never compete with background commands).</summary>
    public Task<SshClient> ConnectNewAsync(Runner runner, CancellationToken cancellationToken) =>
        connector.ConnectAsync(CreateTarget(runner), HostKeyLearner(runner), cancellationToken);

    public async Task<CommandResult> RunAsync(Runner runner, string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

        var client = await GetClientAsync(runner, timeoutSource.Token);
        using var command = client.CreateCommand(Wrap(script));
        try
        {
            await command.ExecuteAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Command on '{runner.Name}' timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0}s.");
        }
        catch (SshConnectionException)
        {
            Invalidate(runner.Id);
            throw;
        }

        return new CommandResult(command.ExitStatus ?? -1, command.Result, command.Error);
    }

    /// <summary>Runs a command and reports every output line (stdout and stderr) as it arrives. Returns the exit code.</summary>
    public async Task<int> RunStreamingAsync(Runner runner, string script, Action<string> onLine, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var client = await GetClientAsync(runner, timeoutSource.Token);
        using var command = client.CreateCommand(Wrap("exec 2>&1\n" + script));
        var execute = command.ExecuteAsync(timeoutSource.Token);
        var pump = Task.Run(() => PumpLines(command.OutputStream, onLine), CancellationToken.None);
        try
        {
            await execute;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Command on '{runner.Name}' timed out after {timeout.TotalMinutes:0} minutes.");
        }
        catch (SshConnectionException)
        {
            Invalidate(runner.Id);
            throw;
        }
        finally
        {
            // The output stream ends when the channel closes; don't wait forever if it doesn't.
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        return command.ExitStatus ?? -1;
    }

    public Task UploadAsync(Runner runner, string remotePath, string content, string mode = "600", CancellationToken cancellationToken = default) =>
        UploadAsync(runner, remotePath, Encoding.UTF8.GetBytes(content), mode, cancellationToken);

    /// <summary>Writes a file on the Runner (creating its folder) and returns its absolute path there.</summary>
    public async Task<string> UploadAsync(Runner runner, string remotePath, byte[] bytes, string mode = "600", CancellationToken cancellationToken = default)
    {
        var directory = remotePath.Contains('/') ? remotePath[..remotePath.LastIndexOf('/')] : "";
        if (directory.Length > 0)
        {
            (await RunAsync(runner, $"mkdir -p {ShellQuote.Path(directory)}", cancellationToken: cancellationToken)).EnsureSuccess($"Creating {directory}");
        }

        var sftp = await GetSftpAsync(runner, cancellationToken);
        var sftpPath = ToSftpPath(sftp, remotePath);
        try
        {
            using var stream = new MemoryStream(bytes);
            await Task.Run(() => sftp.UploadFile(stream, sftpPath, true), cancellationToken);
        }
        catch (SshConnectionException)
        {
            Invalidate(runner.Id);
            throw;
        }

        (await RunAsync(runner, $"chmod {mode} {ShellQuote.Path(remotePath)}", cancellationToken: cancellationToken)).EnsureSuccess($"chmod {remotePath}");
        return sftpPath;
    }

    public async Task<string?> DownloadAsync(Runner runner, string remotePath, CancellationToken cancellationToken = default)
    {
        var sftp = await GetSftpAsync(runner, cancellationToken);
        var sftpPath = ToSftpPath(sftp, remotePath);
        try
        {
            if (!sftp.Exists(sftpPath))
            {
                return null;
            }

            using var stream = new MemoryStream();
            await Task.Run(() => sftp.DownloadFile(sftpPath, stream), cancellationToken);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (SshConnectionException)
        {
            Invalidate(runner.Id);
            throw;
        }
    }

    public void Invalidate(Guid runnerId)
    {
        if (_pool.TryRemove(runnerId, out var pooled))
        {
            pooled.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var runnerId in _pool.Keys)
        {
            Invalidate(runnerId);
        }
    }

    /// <remarks>Line endings are normalized: scripts in C# raw strings get CRLF when the repo is checked out on Windows.</remarks>
    internal static string Wrap(string script) =>
        "bash -c " + ShellQuote.Quote($"[ -f \"{EnvScript}\" ] && . \"{EnvScript}\"\n{script.ReplaceLineEndings("\n")}");

    private static string ToSftpPath(SftpClient sftp, string remotePath)
    {
        if (remotePath == "~")
        {
            return sftp.WorkingDirectory;
        }

        if (remotePath.StartsWith("~/", StringComparison.Ordinal))
        {
            return sftp.WorkingDirectory.TrimEnd('/') + "/" + remotePath[2..];
        }

        return remotePath.StartsWith('/') ? remotePath : sftp.WorkingDirectory.TrimEnd('/') + "/" + remotePath;
    }

    private static void PumpLines(Stream output, Action<string> onLine)
    {
        using var reader = new StreamReader(output, Encoding.UTF8);
        var line = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    Emit(line, onLine);
                }
                else if (c == '\r')
                {
                    // Progress output rewrites the same line; only keep what comes after the last carriage return.
                    if (i + 1 < read && buffer[i + 1] == '\n')
                    {
                        continue;
                    }

                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0)
        {
            Emit(line, onLine);
        }
    }

    private static void Emit(StringBuilder line, Action<string> onLine)
    {
        var text = line.ToString();
        line.Clear();
        onLine(text);
    }

    private async Task<SshClient> GetClientAsync(Runner runner, CancellationToken cancellationToken)
    {
        var pooled = GetPooled(runner);
        await pooled.Lock.WaitAsync(cancellationToken);
        try
        {
            if (pooled.Client is { IsConnected: true })
            {
                return pooled.Client;
            }

            pooled.Client?.Dispose();
            pooled.Client = null;
            logger.LogDebug("Connecting to runner {Runner} at {Host}", runner.Name, runner.Host);
            pooled.Client = await connector.ConnectAsync(CreateTarget(runner), HostKeyLearner(runner), cancellationToken);
            return pooled.Client;
        }
        finally
        {
            pooled.Lock.Release();
        }
    }

    private async Task<SftpClient> GetSftpAsync(Runner runner, CancellationToken cancellationToken)
    {
        // Make sure the host key is known before SFTP connects, so both use the same trusted key.
        await GetClientAsync(runner, cancellationToken);

        var pooled = GetPooled(runner);
        await pooled.Lock.WaitAsync(cancellationToken);
        try
        {
            if (pooled.Sftp is { IsConnected: true })
            {
                return pooled.Sftp;
            }

            pooled.Sftp?.Dispose();
            pooled.Sftp = await connector.ConnectSftpAsync(CreateTarget(runner), cancellationToken);
            return pooled.Sftp;
        }
        finally
        {
            pooled.Lock.Release();
        }
    }

    private Pooled GetPooled(Runner runner)
    {
        var endpoint = $"{runner.Username}@{runner.Host}:{runner.SshPort}";
        while (true)
        {
            var pooled = _pool.GetOrAdd(runner.Id, _ => new Pooled(endpoint));
            if (pooled.Endpoint == endpoint)
            {
                return pooled;
            }

            // The Runner got a new address (DHCP): drop the old connection.
            if (_pool.TryRemove(new KeyValuePair<Guid, Pooled>(runner.Id, pooled)))
            {
                pooled.Dispose();
            }
        }
    }

    private sealed class Pooled(string endpoint) : IDisposable
    {
        public string Endpoint { get; } = endpoint;

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public SshClient? Client { get; set; }

        public SftpClient? Sftp { get; set; }

        public void Dispose()
        {
            try
            {
                Client?.Dispose();
                Sftp?.Dispose();
            }
            catch
            {
                // Closing a dead connection can throw; nothing to do about it.
            }
        }
    }
}
