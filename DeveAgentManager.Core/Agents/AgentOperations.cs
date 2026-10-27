namespace DeveAgentManager.Core.Agents;

/// <summary>
/// Which agents have an operation in progress (start, stop, switch, a conversation change, …). Status checks read agents'
/// terminals without the session lock, so a slow Runner or a long operation never holds up the others: they leave busy
/// agents alone and drop a reading when an operation began on the agent while it was taken. Beginning an operation and
/// writing a reading share <see cref="_observationWrites"/>, so neither interleaves the other.
/// </summary>
public sealed partial class AgentService
{
    private readonly Dictionary<Guid, (int Running, long Begun)> _agentOperations = new();
    private readonly SemaphoreSlim _observationWrites = new(1, 1);
    // Operations that release the session lock while they work: other actions on the agent wait for them to end.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _longOperations = new();

    private (int Running, long Begun) Operations(Guid agentId)
    {
        lock (_agentOperations) return _agentOperations.GetValueOrDefault(agentId);
    }

    /// <summary>Marks an operation on <paramref name="agentId"/> as running until disposed.</summary>
    private async Task<IDisposable> BeginOperationAsync(Guid agentId, CancellationToken ct)
    {
        await _observationWrites.WaitAsync(ct);
        try
        {
            lock (_agentOperations)
            {
                var (running, begun) = _agentOperations.GetValueOrDefault(agentId);
                _agentOperations[agentId] = (running + 1, begun + 1);
            }
        }
        finally { _observationWrites.Release(); }

        return new Released(() =>
        {
            lock (_agentOperations)
            {
                var (running, begun) = _agentOperations[agentId];
                _agentOperations[agentId] = (running - 1, begun);
            }
        });
    }

    /// <summary>
    /// The session lock, for an operation on one agent: status checks leave the agent alone meanwhile. The agent is marked
    /// once the lock is held, so its status stays current while the operation waits for it.
    /// </summary>
    private async Task<IDisposable> LockAgentAsync(Guid agentId, CancellationToken ct)
    {
        var lease = await sessions.LockAsync(ct);
        try { return new Released(lease.Dispose, (await BeginOperationAsync(agentId, ct)).Dispose); }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>
    /// An operation that releases the session lock while it works (<paramref name="doing"/>, e.g. "switching harness"): until
    /// it ends, other actions on the agent are refused (<see cref="RequireNotMoving"/>) and status checks leave it alone.
    /// </summary>
    private async Task<IDisposable> BeginLongOperationAsync(Guid agentId, string doing, CancellationToken ct)
    {
        if (!_longOperations.TryAdd(agentId, doing)) throw new InvalidOperationException(Busy(agentId));
        try
        {
            var operation = await BeginOperationAsync(agentId, ct);
            return new Released(() => _longOperations.TryRemove(agentId, out _), operation.Dispose);
        }
        catch { _longOperations.TryRemove(agentId, out _); throw; }
    }

    private bool InLongOperation(Guid agentId) => _longOperations.ContainsKey(agentId);
    private string Busy(Guid agentId) => $"This agent is {_longOperations.GetValueOrDefault(agentId) ?? "busy"}. Wait for it to finish.";

    /// <summary>Runs the given releases in reverse order, once.</summary>
    private sealed class Released(params Action[] releases) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            for (var i = releases.Length - 1; i >= 0; i--) releases[i]();
        }
    }
}
