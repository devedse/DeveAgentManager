using System.Collections.Concurrent;
using System.Net;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace DeveAgentManager.Core.Editor;

/// <summary>Where a Runner's code-server answers on the Manager, and the session cookie that logs in to it.</summary>
public sealed record EditorUpstream(int LocalPort, string SessionCookie);

/// <summary>Reaches the code-server of a Runner. Replaced in tests.</summary>
public interface IEditorUpstreams
{
    /// <summary>Starts code-server on the Runner when needed and returns where it answers.</summary>
    Task<EditorUpstream> GetAsync(Runner runner, CancellationToken cancellationToken);

    /// <summary>The login stopped working (code-server restarted with another password): log in again next time.</summary>
    void Forget(Guid runnerId);

    /// <summary>Marks a request in progress, so the tunnel isn't closed as idle under it (a WebSocket can last hours).</summary>
    IDisposable Use(Guid runnerId);
}

/// <summary>
/// One tunnel per Runner whose VS Code is in use: an SSH connection of its own with a port forward from 127.0.0.1 on
/// the Manager to code-server on the Runner's loopback, and a code-server session (logged in with its password).
/// Tunnels close after half an hour without requests.
/// </summary>
public sealed class EditorTunnels(RunnerShell shell, ILogger<EditorTunnels> logger) : IEditorUpstreams, IHostedService, IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private Timer? _sweeper;

    public async Task<EditorUpstream> GetAsync(Runner runner, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(runner.Id, _ => new Entry());
        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            entry.LastUsed = DateTime.UtcNow;
            var endpoint = $"{runner.Username}@{runner.Host}:{runner.SshPort}";
            if (entry.Tunnel is { } open && open.Endpoint == endpoint && open.IsAlive && entry.Cookie is { } cookie)
            {
                return new EditorUpstream(open.LocalPort, cookie);
            }

            entry.Close();
            var password = (await shell.RunAsync(runner, VsCodeTool.EnsureRunningScript, TimeSpan.FromMinutes(1), cancellationToken))
                .EnsureSuccess("Starting VS Code").Output.Trim();
            var client = await shell.ConnectNewAsync(runner, cancellationToken);
            var forward = new ForwardedPortLocal(IPAddress.Loopback.ToString(), 0, IPAddress.Loopback.ToString(), VsCodeTool.Port);
            try
            {
                client.AddForwardedPort(forward);
                forward.Start();
            }
            catch
            {
                forward.Dispose();
                client.Dispose();
                throw;
            }

            entry.Tunnel = new Tunnel(endpoint, client, forward);
            entry.Cookie = await LogInAsync(entry.Tunnel.LocalPort, password, cancellationToken);
            logger.LogInformation("VS Code tunnel to {Runner} on 127.0.0.1:{Port}", runner.Name, entry.Tunnel.LocalPort);
            return new EditorUpstream(entry.Tunnel.LocalPort, entry.Cookie);
        }
        catch
        {
            entry.Close();
            throw;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public void Forget(Guid runnerId)
    {
        if (_entries.TryGetValue(runnerId, out var entry))
        {
            entry.Cookie = null;
        }
    }

    public IDisposable Use(Guid runnerId)
    {
        var entry = _entries.GetOrAdd(runnerId, _ => new Entry());
        Interlocked.Increment(ref entry.InUse);
        return new Release(() =>
        {
            entry.LastUsed = DateTime.UtcNow;
            Interlocked.Decrement(ref entry.InUse);
        });
    }

    /// <summary>Logs in to code-server with its password (its login form) and returns the session cookie it sets.</summary>
    private static async Task<string> LogInAsync(int port, string password, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = password }), cancellationToken);
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';', 2)[0].Trim()).FirstOrDefault(v => v.StartsWith("code-server-session=", StringComparison.Ordinal))
            : null;
        return cookie ?? throw new InvalidOperationException(
            $"VS Code on the Runner refused the login ({(int)response.StatusCode}). If another program uses port {VsCodeTool.Port} there, stop it.");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sweeper = new Timer(_ => CloseIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sweeper?.Dispose();
        return Task.CompletedTask;
    }

    private void CloseIdle()
    {
        foreach (var (runnerId, entry) in _entries)
        {
            if (Volatile.Read(ref entry.InUse) > 0 || DateTime.UtcNow - entry.LastUsed < IdleTimeout || !entry.Lock.Wait(0))
            {
                continue;
            }

            try
            {
                if (entry.Tunnel is not null)
                {
                    logger.LogInformation("Closing the idle VS Code tunnel of runner {RunnerId}", runnerId);
                }

                entry.Close();
            }
            finally
            {
                entry.Lock.Release();
            }
        }
    }

    public void Dispose()
    {
        _sweeper?.Dispose();
        foreach (var entry in _entries.Values)
        {
            entry.Close();
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public int InUse;

        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        public Tunnel? Tunnel { get; set; }

        public string? Cookie { get; set; }

        public void Close()
        {
            Tunnel?.Dispose();
            Tunnel = null;
            Cookie = null;
        }
    }

    private sealed class Tunnel(string endpoint, SshClient client, ForwardedPortLocal forward) : IDisposable
    {
        public string Endpoint { get; } = endpoint;

        public int LocalPort { get; } = (int)forward.BoundPort;

        public bool IsAlive => client.IsConnected && forward.IsStarted;

        public void Dispose()
        {
            try
            {
                forward.Stop();
                forward.Dispose();
                client.Dispose();
            }
            catch
            {
                // Closing a dead connection can throw; nothing to do about it.
            }
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
