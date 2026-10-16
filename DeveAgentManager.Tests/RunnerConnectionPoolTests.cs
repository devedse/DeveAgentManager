using DeveAgentManager.Core.Ssh;
using Xunit;

namespace DeveAgentManager.Tests;

public class RunnerConnectionPoolTests
{
    private sealed class Transport : IDisposable
    {
        public bool Closed { get; private set; }
        public void Dispose() => Closed = true;
    }

    [Fact]
    public async Task LeasesAreExclusiveBoundedAndReused()
    {
        var created = 0;
        using var pool = new RunnerConnectionPool<Transport>(2, _ => { created++; return Task.FromResult(new Transport()); }, t => !t.Closed);
        using var first = await pool.RentAsync(TestContext.Current.CancellationToken);
        using var second = await pool.RentAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first.Client, second.Client);
        using var canceled = new CancellationTokenSource();
        var waiting = pool.RentAsync(canceled.Token);
        Assert.False(waiting.IsCompleted);
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var returned = first.Client;
        first.Dispose();
        using var reused = await pool.RentAsync(TestContext.Current.CancellationToken);
        Assert.Same(returned, reused.Client);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task FailedLeaseDoesNotCloseAnotherOperationOrItsReplacement()
    {
        using var pool = new RunnerConnectionPool<Transport>(2, _ => Task.FromResult(new Transport()), t => !t.Closed);
        using var failed = await pool.RentAsync(TestContext.Current.CancellationToken);
        using var running = await pool.RentAsync(TestContext.Current.CancellationToken);
        failed.Discard();
        failed.Dispose();
        using var replacement = await pool.RentAsync(TestContext.Current.CancellationToken);
        failed.Discard(); // A late old-generation failure cannot invalidate the replacement.
        Assert.False(running.Client.Closed);
        Assert.False(replacement.Client.Closed);
        Assert.NotEqual(failed.Id, replacement.Id);
    }

    [Fact]
    public async Task SlowConnectionSetupDoesNotBlockReuseOfAnotherHealthyConnection()
    {
        var pending = new TaskCompletionSource<Transport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var pool = new RunnerConnectionPool<Transport>(2,
            _ => ++count == 1 ? Task.FromResult(new Transport()) : pending.Task, t => !t.Closed);
        using var first = await pool.RentAsync(TestContext.Current.CancellationToken);
        var connecting = pool.RentAsync(TestContext.Current.CancellationToken);
        first.Dispose();
        try
        {
            using var reused = await pool.RentAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Same(first.Client, reused.Client);
            Assert.False(connecting.IsCompleted);
        }
        finally { pending.TrySetResult(new Transport()); }
        using var second = await connecting;
    }

    [Fact]
    public async Task RetirementDrainsRunningOperationsAndRejectsWaiters()
    {
        using var pool = new RunnerConnectionPool<Transport>(1, _ => Task.FromResult(new Transport()), t => !t.Closed);
        using var running = await pool.RentAsync(TestContext.Current.CancellationToken);
        var waiting = pool.RentAsync(TestContext.Current.CancellationToken);
        pool.Retire();
        Assert.False(running.Client.Closed);
        running.Dispose();
        Assert.True(running.Client.Closed);
        await Assert.ThrowsAsync<RunnerConnectionPool<Transport>.RetiredException>(() => waiting);
    }

    [Fact]
    public async Task CancellationOrRetirementDuringConnectDisposesTheUnusedConnection()
    {
        foreach (var retire in new[] { false, true })
        {
            var connecting = new TaskCompletionSource<Transport>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var pool = new RunnerConnectionPool<Transport>(1, _ => connecting.Task, t => !t.Closed);
            using var ct = new CancellationTokenSource();
            var renting = pool.RentAsync(ct.Token);
            if (retire) pool.Retire(); else await ct.CancelAsync();
            var unused = new Transport();
            connecting.SetResult(unused);
            if (retire) await Assert.ThrowsAsync<RunnerConnectionPool<Transport>.RetiredException>(() => renting);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renting);
            Assert.True(unused.Closed);
        }
    }
}
