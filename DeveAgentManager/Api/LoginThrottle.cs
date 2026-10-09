using System.Security.Cryptography;
using System.Text;

namespace DeveAgentManager.Api;

/// <summary>Fixed storage, coordinated account/IP/global windows and a password-hashing concurrency cap.</summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    private sealed record Window(DateTimeOffset Start, int Count);
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new();
    private readonly SemaphoreSlim _concurrency = new(4);
    private const int Capacity = 4096;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    public IDisposable? TryEnter(string ip, string username)
    {
        if (!_concurrency.Wait(0)) return null;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in _windows.Where(w => now - w.Value.Start >= Period).Select(w => w.Key).ToArray()) _windows.Remove(key);
            var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant())));
            var keys = new[] { ("global", 200), ($"ip:{ip}", 30), ($"account:{account}", 20) };
            if (_windows.Count + keys.Count(k => !_windows.ContainsKey(k.Item1)) > Capacity
                || keys.Any(k => _windows.TryGetValue(k.Item1, out var window) && window.Count >= k.Item2))
            {
                _concurrency.Release();
                return null;
            }
            foreach (var (key, _) in keys)
                _windows[key] = _windows.TryGetValue(key, out var window) ? window with { Count = window.Count + 1 } : new Window(now, 1);
        }
        return new Lease(_concurrency);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release(); }
    }
}
