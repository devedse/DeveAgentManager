using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>The Manager's log stays readable: every Proxmox API call used to add four lines (about 90 MB a day).</summary>
public sealed class LoggingTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    [Fact]
    public void RoutineHttpRequests_AreNotLogged_ButTheirProblemsAre()
    {
        using var client = manager.CreateClient();
        var logs = manager.Services.GetRequiredService<ILoggerFactory>();
        var proxmox = logs.CreateLogger("System.Net.Http.HttpClient.proxmox.ClientHandler");
        Assert.False(proxmox.IsEnabled(LogLevel.Information));
        Assert.True(proxmox.IsEnabled(LogLevel.Warning));
        Assert.True(logs.CreateLogger("DeveAgentManager.Core.Agents.AgentService").IsEnabled(LogLevel.Information));
    }
}
