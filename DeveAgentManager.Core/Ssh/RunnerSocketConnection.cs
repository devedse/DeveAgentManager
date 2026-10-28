using System.Net.WebSockets;
using Renci.SshNet;
using DeveAgentManager.Core.Data;

namespace DeveAgentManager.Core.Ssh;

/// <summary>A WebSocket reached through a dedicated SSH command's byte stream, without a listening TCP port.</summary>
public sealed class RunnerSocketConnection : IAsyncDisposable
{
    private readonly SshClient _ssh;
    private readonly SshCommand _command;
    private readonly Task _execution;
    private readonly HttpMessageInvoker _http;
    private readonly CancellationTokenSource _lifetime;
    public ClientWebSocket Socket { get; }

    private RunnerSocketConnection(SshClient ssh, SshCommand command, Task execution, HttpMessageInvoker http, ClientWebSocket socket, CancellationTokenSource lifetime)
        => (_ssh, _command, _execution, _http, Socket, _lifetime) = (ssh, command, execution, http, socket, lifetime);

    public static async Task<RunnerSocketConnection> OpenAsync(RunnerShell shell, Runner runner, string command, CancellationToken ct)
    {
        var ssh = await shell.ConnectNewAsync(runner, ct);
        SshCommand? remote = null;
        ClientWebSocket? socket = null;
        HttpMessageInvoker? http = null;
        Task? execution = null;
        var lifetime = new CancellationTokenSource();
        using var cancel = ct.Register(ssh.Dispose);
        try
        {
            remote = ssh.CreateCommand(RunnerShell.Wrap(command));
            var opened = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            var invocation = remote;
            // Channel setup is synchronous in SSH.NET. Keep it off the request thread and allow cancellation to close SSH.
            execution = Task.Run(async () =>
            {
                try
                {
                    var running = invocation.ExecuteAsync(lifetime.Token);
                    opened.TrySetResult(invocation.CreateInputStream());
                    await running;
                }
                catch (Exception ex) { opened.TrySetException(ex); throw; }
            }, CancellationToken.None);
            var input = await opened.Task.WaitAsync(ct);
            var stream = new CommandStream(remote.OutputStream, input);
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(stream),
            };
            http = new HttpMessageInvoker(handler);
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            await socket.ConnectAsync(new Uri("ws://localhost/"), http, ct);
            return new(ssh, remote, execution, http, socket, lifetime);
        }
        catch
        {
            lifetime.Cancel(); socket?.Dispose(); http?.Dispose(); remote?.Dispose(); ssh.Dispose();
            if (execution is not null) try { await execution; } catch { }
            lifetime.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); Socket.Dispose(); _http.Dispose(); _command.Dispose(); _ssh.Dispose();
        try { await _execution; } catch { }
        _lifetime.Dispose();
    }

    private sealed class CommandStream(Stream output, Stream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => output.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => output.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => output.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => input.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => input.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => input.WriteAsync(buffer, ct);
        public override void Flush() => input.Flush();
        public override Task FlushAsync(CancellationToken ct) => input.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
