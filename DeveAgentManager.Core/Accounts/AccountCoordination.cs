namespace DeveAgentManager.Core.Accounts;

/// <summary>Serializes login authorization changes and launches for the supported single Manager process.</summary>
public static class AccountCoordination
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        return new Lease();
    }
    private sealed class Lease : IDisposable
    {
        public void Dispose() => Gate.Release();
    }
}
