// Short in-memory waits: cancelling them with the test run adds nothing.
#pragma warning disable xUnit1051

using DeveAgentManager.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Stopping the Manager (an update) doesn't wait for open terminals and VS Code, which never end by themselves: before,
/// it waited the full 30 seconds until Docker or Watchtower killed it.
/// </summary>
public sealed class ShutdownTests
{
    [Fact]
    public async Task Stopping_ClosesOpenConnectionsAtOnce_AndWaitsAtMostTenSeconds()
    {
        await using var manager = new ApiSecurityTests.Manager();
        using var client = manager.CreateClient();
        Assert.Equal(TimeSpan.FromSeconds(10), manager.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);

        var connections = manager.Services.GetRequiredService<OpenConnections>();
        var closed = new TaskCompletionSource();
        using var terminal = connections.Register(TestUsers.Admin, null, (_, _) => Task.FromResult(true), () => closed.TrySetResult(), Guid.NewGuid());
        manager.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
