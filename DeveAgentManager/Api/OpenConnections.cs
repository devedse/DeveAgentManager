using System.Collections.Concurrent;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>
/// The long-lived connections users have open: terminals, the live hub and VS Code. Their access is checked when they
/// open; this closes them when it ends while they are open: access to a Runner removed, a share ended, a role taken
/// away, or the user disabled, deleted or their password changed (a new security stamp).
/// </summary>
public sealed class OpenConnections(ManagerEvents events, UserService users, SignInSessions sessions, ILogger<OpenConnections> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Connection> _open = new();

    /// <summary>
    /// Keeps a connection open for as long as <paramref name="stillAllowed"/> says so for its user (as they are then);
    /// <paramref name="close"/> ends it. Dispose the result when the connection ends by itself.
    /// </summary>
    public IDisposable Register(CurrentUser user, string? securityStamp, Func<CurrentUser, CancellationToken, Task<bool>> stillAllowed, Action close, Guid sessionId)
    {
        var id = Guid.NewGuid();
        _open[id] = new Connection(user.Id, securityStamp, stillAllowed, close, sessionId);
        return new Registration(() => _open.TryRemove(id, out _));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        events.Changed += OnChanged;
        sessions.Revoked += OnRevoked;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var (id, connection) in _open.ToArray()) await RecheckAsync(id, connection);
        }
        finally { events.Changed -= OnChanged; sessions.Revoked -= OnRevoked; }
    }

    private void OnRevoked(Guid session)
    {
        foreach (var (id, connection) in _open.Where(c => c.Value.SessionId == session).ToArray())
            if (_open.TryRemove(id, out _)) connection.Close();
    }

    private void OnChanged(ManagerEvent change)
    {
        // Access is only ever taken away by these; a removal can't be looked up any more, so it checks everyone's.
        var affected = change.Kind switch
        {
            ManagerEventKind.AccessChanged or ManagerEventKind.UserChanged when change.UserId is { } userId => _open.Where(c => c.Value.UserId == userId),
            ManagerEventKind.UserRemoved or ManagerEventKind.RunnerRemoved or ManagerEventKind.AgentRemoved or ManagerEventKind.SessionRemoved => _open,
            _ => [],
        };
        foreach (var (id, connection) in affected.ToList())
        {
            _ = RecheckAsync(id, connection);
        }
    }

    private async Task RecheckAsync(Guid id, Connection connection)
    {
        try
        {
            var user = await users.ValidateAsync(connection.UserId, connection.SecurityStamp);
            if (user is not null && !user.MustChangePassword
                && await sessions.ValidateAsync(connection.SessionId, user.Id, connection.SecurityStamp) is not null
                && await connection.StillAllowed(user, CancellationToken.None))
            {
                return;
            }

            if (_open.TryRemove(id, out _))
            {
                connection.Close();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check a connection of {User}; closing it", connection.UserId);
            if (_open.TryRemove(id, out _))
            {
                connection.Close();
            }
        }
    }

    private sealed record Connection(Guid UserId, string? SecurityStamp, Func<CurrentUser, CancellationToken, Task<bool>> StillAllowed, Action Close, Guid SessionId);

    private sealed class Registration(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }
}
