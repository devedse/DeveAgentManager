namespace DeveAgentManager.Core.Ssh;

/// <summary>
/// Reuses transports, with one exclusive operation per transport. Retirement drains existing leases;
/// cancellation closes only the affected lease. No operation is replayed by this pool.
/// </summary>
internal sealed class RunnerConnectionPool<T>(int capacity, Func<CancellationToken, Task<T>> connect,
    Func<T, bool> connected) : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots = new(capacity, capacity);
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly Stack<Connection> _idle = new();
    private readonly HashSet<Connection> _all = [];
    private bool _retired;
    private static long _nextId;

    internal sealed class RetiredException : Exception;

    internal sealed class Connection(T client)
    {
        private int _closed;
        internal T Client { get; } = client;
        internal long Id { get; } = Interlocked.Increment(ref _nextId);
        internal DateTime CreatedAt { get; } = DateTime.UtcNow;
        internal bool Closed => Volatile.Read(ref _closed) != 0;
        internal void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { Client.Dispose(); }
            catch { /* A dead transport must still release its pool slot. */ }
        }
    }

    internal sealed class Lease(RunnerConnectionPool<T> pool, Connection connection) : IDisposable
    {
        private int _returned;
        internal T Client => connection.Client;
        internal long Id => connection.Id;
        internal TimeSpan Age => DateTime.UtcNow - connection.CreatedAt;
        internal void Discard() => connection.Close();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0) pool.Return(connection);
        }
    }

    internal async Task<Lease> RentAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            // Reusing a healthy idle transport must not wait behind another transport's slow login.
            if (TakeIdle() is { } idle) return idle;
            // Avoid a burst of simultaneous SSH authentications when the pool grows.
            await _connecting.WaitAsync(ct);
            try
            {
                if (TakeIdle() is { } returned) return returned;

                var created = new Connection(await connect(ct));
                lock (_gate)
                {
                    if (_retired || ct.IsCancellationRequested)
                    {
                        created.Close();
                        ct.ThrowIfCancellationRequested();
                        throw new RetiredException();
                    }
                    _all.Add(created);
                    return new(this, created);
                }
            }
            finally { _connecting.Release(); }
        }
        catch { _slots.Release(); throw; }
    }

    private Lease? TakeIdle()
    {
        lock (_gate)
        {
            if (_retired) throw new RetiredException();
            while (_idle.TryPop(out var existing))
            {
                if (!existing.Closed && connected(existing.Client)) return new(this, existing);
                _all.Remove(existing);
                existing.Close();
            }
            return null;
        }
    }

    private void Return(Connection connection)
    {
        lock (_gate)
        {
            if (_retired || connection.Closed || !connected(connection.Client))
            {
                _all.Remove(connection);
                connection.Close();
            }
            else _idle.Push(connection);
        }
        _slots.Release();
    }

    internal void Retire()
    {
        Connection[] idle;
        lock (_gate)
        {
            _retired = true;
            idle = _idle.ToArray();
            _idle.Clear();
            foreach (var connection in idle) _all.Remove(connection);
        }
        foreach (var connection in idle) connection.Close();
    }

    public void Dispose()
    {
        Connection[] all;
        lock (_gate)
        {
            _retired = true;
            all = _all.ToArray();
            _idle.Clear();
            _all.Clear();
        }
        foreach (var connection in all) connection.Close();
        // Waiters and outstanding leases still own the semaphores until they finish.
    }
}
