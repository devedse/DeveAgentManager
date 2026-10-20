using System.Collections.Concurrent;
using System.Text;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

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
/// Runs commands and transfers files on Runners over SSH. Reuses a bounded pool of exclusively leased connections per Runner.
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

    // Preserve the former ten-command concurrency budget, without sharing a transport between commands.
    internal const int CommandCapacity = 10;
    private readonly ConcurrentDictionary<Guid, Pooled> _pool = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

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

    public Task<CommandResult> RunAsync(Runner runner, string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(runner, script, timeout ?? DefaultTimeout, null, cancellationToken);

    /// <summary>Runs a command and reports every output line (stdout and stderr) as it arrives. Returns the exit code.</summary>
    public async Task<int> RunStreamingAsync(Runner runner, string script, Action<string> onLine, TimeSpan timeout, CancellationToken cancellationToken) =>
        (await ExecuteAsync(runner, "exec 2>&1\n" + script, timeout, onLine, cancellationToken)).ExitCode;

    private async Task<CommandResult> ExecuteAsync(Runner runner, string script, TimeSpan timeout,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        deadline.CancelAfter(timeout);
        RunnerConnectionPool<SshClient>.Lease? lease = null;
        var phase = "waiting for a connection";
        try
        {
            lease = await RentCommandAsync(runner, deadline.Token);
            phase = "opening channel / submitting command";
            // SSH.NET ExecuteAsync performs channel setup synchronously, before it observes cancellation.
            // Closing this exclusively leased transport also interrupts setup, preventing a delayed open
            // from submitting work after its deadline. Never reuse or replay this operation after failure.
            using var abort = deadline.Token.Register(lease.Discard);
            return await Task.Run(async () =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var command = lease.Client.CreateCommand(Wrap(script));
                Task? pump = null;
                try
                {
                    var execute = command.ExecuteAsync(deadline.Token);
                    phase = "awaiting command result";
                    if (onLine is not null) pump = Task.Run(() => PumpLines(command.OutputStream, onLine), CancellationToken.None);
                    await execute;
                    deadline.Token.ThrowIfCancellationRequested();
                    if (pump is not null) await pump;
                    return new CommandResult(command.ExitStatus ?? -1, onLine is null ? command.Result : "", command.Error);
                }
                catch
                {
                    lease.Discard();
                    command.Dispose();
                    // Cleanup must not replace the transport failure with a pump's disposed-stream error.
                    if (pump is not null) { try { await pump; } catch { } }
                    throw;
                }
                finally
                {
                    // Own the pump through teardown, including exceptions during synchronous setup.
                    command.Dispose();
                }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            lease?.Discard();
            // Do not attach SSH.NET's exception: some exceptions include the full shell command.
            logger.Log(cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested ? LogLevel.Debug : LogLevel.Warning,
                "SSH command failed on runner {RunnerId}; connection {ConnectionId}, age {AgeSeconds:F1}s, phase {Phase}, failure {Failure}, canceled {Canceled}. The command was not replayed",
                runner.Id, lease?.Id, lease?.Age.TotalSeconds, phase, ex.GetType().Name, deadline.IsCancellationRequested);
            if (deadline.IsCancellationRequested)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"Command on '{runner.Name}' timed out after {timeout.TotalSeconds:0}s. Its remote outcome may be unknown.");
            }
            throw;
        }
        finally { lease?.Dispose(); }
    }

    public Task UploadAsync(Runner runner, string remotePath, string content, string mode = "600", CancellationToken cancellationToken = default) =>
        UploadAsync(runner, remotePath, Encoding.UTF8.GetBytes(content), mode, cancellationToken);

    /// <summary>Installs a complete verified file by rename. A failed/cancelled upload cannot replace the last good file.</summary>
    internal async Task UploadAtomicAsync(Runner runner, string remotePath, string content, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var temporary = remotePath + ".dam-tmp-" + Guid.NewGuid().ToString("N");
        await UploadAsync(runner, temporary, bytes, "600", ct);
        var path = ShellQuote.Path(temporary);
        // This submission is never retried: transport loss after rename is an uncertain installation, not permission
        // to launch/replay work. Uncommitted temporary files do not match the harness's session-file glob.
        (await RunAsync(runner, $"set -e\ntemporary={path}\ntrap 'rm -f -- \"$temporary\"' EXIT\n"
            + $"actual=$(sha256sum -- {path})\n[ \"${{actual%% *}}\" = {ShellQuote.Quote(hash)} ]\n"
            + $"mv -fT -- {path} {ShellQuote.Path(remotePath)}", cancellationToken: ct)).EnsureSuccess("Installing verified conversation history");
    }

    /// <summary>Writes a file on the Runner (creating its folder) and returns its absolute path there.</summary>
    public async Task<string> UploadAsync(Runner runner, string remotePath, byte[] bytes, string mode = "600", CancellationToken cancellationToken = default)
    {
        var directory = remotePath.Contains('/') ? remotePath[..remotePath.LastIndexOf('/')] : "";
        if (directory.Length > 0)
        {
            (await RunAsync(runner, $"mkdir -p {ShellQuote.Path(directory)}", cancellationToken: cancellationToken)).EnsureSuccess($"Creating {directory}");
        }

        var sftpPath = await WithSftpAsync(runner, async (sftp, ct) =>
        {
            var path = ToSftpPath(sftp, remotePath);
            using var stream = new MemoryStream(bytes);
            await sftp.UploadFileAsync(stream, path, ct);
            return path;
        }, cancellationToken);

        (await RunAsync(runner, $"chmod {mode} {ShellQuote.Path(remotePath)}", cancellationToken: cancellationToken)).EnsureSuccess($"chmod {remotePath}");
        return sftpPath;
    }

    public async Task<string?> DownloadAsync(Runner runner, string remotePath, CancellationToken cancellationToken = default) =>
        await DownloadBytesAsync(runner, remotePath, long.MaxValue, cancellationToken) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Streams an artifact to a prepared private request folder, without buffering it in managed memory.</summary>
    internal async Task UploadFromAsync(Runner runner, string remotePath, Stream source, CancellationToken ct, Action<long, long>? progress = null)
    {
        var total = source.CanSeek ? source.Length - source.Position : 0;
        progress?.Invoke(0, total);
        await WithSftpAsync(runner, async (sftp, token) =>
        {
            if (progress is null) await sftp.UploadFileAsync(source, ToSftpPath(sftp, remotePath), token);
            else await sftp.UploadFileAsync(source, ToSftpPath(sftp, remotePath),
                new TransferProgress<UploadFileProgressReport>(value => progress(checked((long)value.TotalBytesUploaded), total)), token);
            return true;
        }, ct);
        (await RunAsync(runner, $"chmod 600 {ShellQuote.Path(remotePath)}", cancellationToken: ct)).EnsureSuccess("Protecting artifact download");
    }

    internal Task DownloadToAsync(Runner runner, string remotePath, Stream destination, CancellationToken ct, Action<long, long>? progress = null) =>
        WithSftpAsync(runner, async (sftp, token) =>
        {
            using var input = sftp.OpenRead(ToSftpPath(sftp, remotePath));
            if (progress is null) await input.CopyToAsync(destination, 1024 * 1024, token);
            else
            {
                var total = input.Length;
                long completed = 0;
                progress(0, total);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    completed += read;
                    progress(completed, total);
                }
            }
            return true;
        }, ct);

    // Inline delivery keeps progress ordered and owned by the transfer, unlike Progress<T>'s queued callbacks.
    private sealed class TransferProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly object _gate = new();
        public void Report(T value) { lock (_gate) report(value); }
    }

    /// <summary>A file's bytes, or null when it doesn't exist or is larger than <paramref name="maxBytes"/>.</summary>
    public async Task<byte[]?> DownloadBytesAsync(Runner runner, string remotePath, long maxBytes, CancellationToken cancellationToken = default)
    {
        return await WithSftpAsync<byte[]?>(runner, async (sftp, ct) =>
        {
            var path = ToSftpPath(sftp, remotePath);
            if (!await sftp.ExistsAsync(path, ct) || await sftp.GetAttributesAsync(path, ct) is not { IsRegularFile: true } attributes || attributes.Size > maxBytes)
                return null;
            using var stream = new MemoryStream();
            await sftp.DownloadFileAsync(path, stream, ct);
            return stream.Length > maxBytes ? null : stream.ToArray();
        }, cancellationToken);
    }

    /// <summary>Cheap append check: bounded known ranges and the suffix, with the located fingerprint checked on both sides.</summary>
    internal async Task<bool> DownloadHistoryAppendAsync(Runner runner, string remotePath, long offset, long size,
        string stamp, IReadOnlyList<NativeHistorySample> samples, Stream destination, CancellationToken ct)
    {
        async Task<bool> Unchanged()
        {
            var result = await RunAsync(runner,
                $"stat -L -c '%s %d:%i:%y:%z' -- {ShellQuote.Path(remotePath)}", cancellationToken: ct);
            return result.Succeeded && result.Output.TrimEnd('\r', '\n') == $"{size} {stamp}";
        }
        if (offset < 0 || size <= offset || !await Unchanged()) return false;
        var matched = await WithSftpAsync(runner, async (sftp, token) =>
        {
            using var input = sftp.OpenRead(ToSftpPath(sftp, remotePath));
            foreach (var sample in samples)
            {
                input.Position = sample.Offset;
                var actual = new byte[sample.Bytes.Length];
                await input.ReadExactlyAsync(actual, token);
                if (!actual.AsSpan().SequenceEqual(sample.Bytes)) return false;
            }
            input.Position = offset;
            var buffer = new byte[65536];
            var remaining = size - offset;
            while (remaining > 0)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (count == 0) return false;
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
                remaining -= count;
            }
            return true;
        }, ct);
        // A moving source falls back to whole-prefix verification, which permits growth past the snapshot boundary.
        return matched && await Unchanged();
    }

    /// <summary>Transfers a stable, bounded snapshot suffix. Hash verification catches edits anywhere in the old prefix.</summary>
    internal async Task<string?> DownloadHistoryRangeAsync(Runner runner, string remotePath, long offset, long size,
        string? prefixHash, Stream destination, CancellationToken ct)
    {
        var path = ShellQuote.Path(remotePath);
        if (prefixHash is not null)
        {
            using var timing = Diagnostics.CaptureOperations.Start("history.remote-prefix-hash", runner: runner.Id, bytes: offset);
            var check = await RunAsync(runner, $"set -o pipefail; head -c {offset} -- {path} | sha256sum", cancellationToken: ct);
            check.EnsureSuccess("Verifying native history prefix");
            if (!check.Output.StartsWith(prefixHash, StringComparison.OrdinalIgnoreCase)) return null;
        }
        await WithSftpAsync(runner, async (sftp, token) =>
        {
            using var input = sftp.OpenRead(ToSftpPath(sftp, remotePath));
            input.Position = offset;
            var remaining = size - offset;
            var buffer = new byte[65536];
            while (remaining > 0)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (count == 0) throw new IOException("Native history was truncated during transfer.");
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
                remaining -= count;
            }
            return true;
        }, ct);
        // Validate exactly the requested prefix, not the whole growing file. Appends after size are harmless.
        using var snapshotTiming = Diagnostics.CaptureOperations.Start("history.remote-snapshot-hash", runner: runner.Id, bytes: size);
        var snapshot = await RunAsync(runner, $"set -o pipefail; head -c {size} -- {path} | sha256sum", cancellationToken: ct);
        snapshot.EnsureSuccess("Verifying native history snapshot");
        var hash = snapshot.Output.Trim().Split(' ', 2)[0];
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new IOException("Invalid native history snapshot checksum.");
        return hash;
    }

    /// <summary>Streams a file into the destination. False when absent or larger than maxBytes.</summary>
    public Task<bool> DownloadToAsync(Runner runner, string remotePath, Stream destination, long maxBytes, CancellationToken cancellationToken = default) =>
        WithSftpAsync(runner, async (sftp, ct) =>
        {
            var path = ToSftpPath(sftp, remotePath);
            if (!await sftp.ExistsAsync(path, ct) || await sftp.GetAttributesAsync(path, ct) is not { IsRegularFile: true } attributes || attributes.Size > maxBytes)
                return false;
            await sftp.DownloadFileAsync(path, destination, ct);
            return true;
        }, cancellationToken);

    private async Task<T> WithSftpAsync<T>(Runner runner, Func<SftpClient, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        using var lease = await RentSftpAsync(runner, lifetime.Token);
        using var abort = lifetime.Token.Register(lease.Discard);
        try { return await operation(lease.Client, lifetime.Token); }
        catch
        {
            lease.Discard();
            lifetime.Token.ThrowIfCancellationRequested();
            throw;
        }
    }

    public void Invalidate(Guid runnerId)
    {
        if (_pool.TryRemove(runnerId, out var pooled))
        {
            pooled.Retire();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); // Also reaches leases draining out of retired generations.
        foreach (var pair in _pool)
            if (_pool.TryRemove(pair)) pair.Value.Dispose();
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

    private async Task<RunnerConnectionPool<SshClient>.Lease> RentCommandAsync(Runner runner, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await GetPooled(runner).Commands.RentAsync(ct); }
            catch (RunnerConnectionPool<SshClient>.RetiredException) { /* Replaced before any command was assigned. */ }
        }
    }

    private async Task<RunnerConnectionPool<SftpClient>.Lease> RentSftpAsync(Runner runner, CancellationToken ct)
    {
        // Learn/verify the host key first, without holding a command lease during a file transfer.
        using (await RentCommandAsync(runner, ct)) { }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await GetPooled(runner).Files.RentAsync(ct); }
            catch (RunnerConnectionPool<SftpClient>.RetiredException) { }
        }
    }

    private Pooled GetPooled(Runner runner)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var endpoint = $"{runner.Username}@{runner.Host}:{runner.SshPort}";
        while (true)
        {
            var pooled = _pool.GetOrAdd(runner.Id, _ => new Pooled(endpoint,
                new(CommandCapacity, ct => connector.ConnectAsync(CreateTarget(runner), HostKeyLearner(runner), ct), c => c.IsConnected),
                new(1, ct => connector.ConnectSftpAsync(CreateTarget(runner), ct), c => c.IsConnected)));
            if (pooled.Endpoint == endpoint) return pooled;
            if (_pool.TryRemove(new KeyValuePair<Guid, Pooled>(runner.Id, pooled))) pooled.Retire();
        }
    }

    private sealed record Pooled(string Endpoint, RunnerConnectionPool<SshClient> Commands, RunnerConnectionPool<SftpClient> Files) : IDisposable
    {
        public void Retire() { Commands.Retire(); Files.Retire(); }
        public void Dispose() { Commands.Dispose(); Files.Dispose(); }
    }
}
