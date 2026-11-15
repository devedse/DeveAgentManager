using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace DeveAgentManager.Core.Editor;

/// <summary>Where a Runner's browser desktop (KasmVNC) answers on the Manager, and the login the Manager adds to requests.</summary>
public sealed record DesktopUpstream(int LocalPort, string Authorization);

/// <summary>Reaches the browser desktop of a Runner. Replaced in tests.</summary>
public interface IDesktopUpstreams
{
    /// <summary>Starts the desktop on the Runner when it isn't running and returns where it answers.</summary>
    Task<DesktopUpstream> GetAsync(Runner runner, CancellationToken cancellationToken);

    /// <summary>The desktop stopped answering: open the tunnel again on the next request (at most every few seconds).</summary>
    void Forget(Guid runnerId);

    /// <summary>Marks a request in progress, so the tunnel isn't closed as idle under it (the viewer's WebSocket lasts hours).</summary>
    IDisposable Use(Guid runnerId);
}

/// <summary>
/// One tunnel per Runner whose browser desktop is open: an SSH connection of its own with a port forward from 127.0.0.1
/// on the Manager to KasmVNC on the Runner's loopback (desktop-setup.sh browser). KasmVNC's login is a password only the
/// Manager knows (read from the Runner); the browser never sees it. Tunnels close after half an hour without requests,
/// and when their Runner is removed.
/// </summary>
public sealed partial class DesktopTunnels(RunnerShell shell, ManagerEvents events, ILogger<DesktopTunnels> logger) : IDesktopUpstreams, IHostedService, IDisposable
{
    /// <summary>KasmVNC's port on the Runner's loopback (browser-desktop.sh).</summary>
    public const int Port = 6901;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinTimeBetweenReconnects = TimeSpan.FromSeconds(10);

    /// <summary>Starts the desktop if it was stopped, then prints KasmVNC's password for the Manager.</summary>
    internal const string StartScript = """
        export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
        systemctl --user is-active --quiet dam-browser-desktop.service || systemctl --user start dam-browser-desktop.service
        cat ~/.deveagentmanager/browser-desktop.password
        """;

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private Timer? _sweeper;

    public async Task<DesktopUpstream> GetAsync(Runner runner, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(runner.Id, _ => new Entry());
        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            entry.LastUsed = DateTime.UtcNow;
            var endpoint = $"{runner.Username}@{runner.Host}:{runner.SshPort}";
            if (entry.Tunnel is { } open && open.Endpoint == endpoint && open.IsAlive && entry.Authorization is { } authorization)
            {
                return new DesktopUpstream(open.LocalPort, authorization);
            }

            entry.Close();
            var output = (await shell.RunAsync(runner, StartScript, TimeSpan.FromMinutes(1), cancellationToken)).EnsureSuccess("Starting the desktop").Output;
            var password = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            if (!Password().IsMatch(password))
            {
                throw new InvalidOperationException("The desktop's login is missing on the Runner. Use Update all to set the desktop up again.");
            }

            var client = await shell.ConnectNewAsync(runner, cancellationToken);
            var forward = new ForwardedPortLocal(IPAddress.Loopback.ToString(), 0, IPAddress.Loopback.ToString(), Port);
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
            entry.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{runner.Username}:{password}"));
            entry.OpenedAt = DateTime.UtcNow;
            logger.LogInformation("Desktop tunnel to {Runner} on 127.0.0.1:{Port}", runner.Name, entry.Tunnel.LocalPort);
            return new DesktopUpstream(entry.Tunnel.LocalPort, entry.Authorization);
        }
        catch when (entry.Tunnel is { IsAlive: false })
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
        if (_entries.TryGetValue(runnerId, out var entry) && DateTime.UtcNow - entry.OpenedAt >= MinTimeBetweenReconnects && entry.Lock.Wait(0))
        {
            try
            {
                entry.Close();
            }
            finally
            {
                entry.Lock.Release();
            }
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

    /// <summary>Closes a Runner's tunnel now, with the viewers that use it (the Runner is gone).</summary>
    public async Task CloseAsync(Guid runnerId)
    {
        if (_entries.TryRemove(runnerId, out var entry))
        {
            await entry.Lock.WaitAsync();
            try
            {
                entry.Close();
            }
            finally
            {
                entry.Lock.Release();
            }
        }
    }

    // What desktop-setup.sh writes: letters and digits only, so it can't break the login header.
    [GeneratedRegex("^[A-Za-z0-9]{16,128}$")]
    private static partial Regex Password();

    private void OnChanged(ManagerEvent managerEvent)
    {
        if (managerEvent is { Kind: ManagerEventKind.RunnerRemoved, RunnerId: { } runnerId })
        {
            _ = CloseAsync(runnerId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sweeper = new Timer(_ => CloseIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        events.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        events.Changed -= OnChanged;
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
                    logger.LogInformation("Closing the idle desktop tunnel of runner {RunnerId}", runnerId);
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

        public DateTime OpenedAt { get; set; }

        public Tunnel? Tunnel { get; set; }

        public string? Authorization { get; set; }

        public void Close()
        {
            Tunnel?.Dispose();
            Tunnel = null;
            Authorization = null;
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
