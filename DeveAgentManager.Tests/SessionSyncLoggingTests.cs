using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeveAgentManager.Tests;

public class SessionSyncLoggingTests
{
    [Theory]
    [InlineData(null, "Session sync from runner failed; periodic synchronization will retry")]
    [InlineData("stopping the Runner", "Final session sync from runner before stopping the Runner failed")]
    [InlineData("removing the Runner", "Final session sync from runner before removing the Runner failed")]
    public async Task FailureDescribesCallerContextAndPreservesOriginalException(string? operation, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("dam-sync-log-");
        try
        {
            var options = new ManagerOptions { DataDirectory = directory.FullName };
            var logger = new CapturedLogger<SessionService>();
            var error = new IOException("test-only unavailable session store");
            var service = new SessionService(new BrokenFactory(error), null!, [], [], new SessionImageStore(options),
                new SessionFileStore(options), options, new ManagerEvents(), logger);
            await service.TrySyncRunnerAsync(new Runner { Name = "runner" }, TestContext.Current.CancellationToken, operation);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(expected, entry.Message);
            Assert.Same(error, entry.Exception);
        }
        finally { directory.Delete(true); }
    }

    private sealed class BrokenFactory(Exception error) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => throw error;
    }
}
