using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;
public sealed class AgentDiagnosticsTests
{
    [Fact]
    public void HistoryIsBoundedDeduplicatedAndSurvivesRestart()
    {
        var folder = Directory.CreateTempSubdirectory("dam-diagnostics-").FullName;
        try
        {
            var options = new ManagerOptions { DataDirectory = folder };
            var clock = new Clock();
            var store = new AgentDiagnostics(options, clock, NullLogger<AgentDiagnostics>.Instance);
            var id = Guid.NewGuid();
            for (var i = 0; i < 105; i++) store.Record(id, "Activity", "State " + i, "test-rule");
            store.Record(id, "Activity", "State 104", "test-rule");
            var restarted = new AgentDiagnostics(options, clock, NullLogger<AgentDiagnostics>.Instance);
            Assert.Equal(100, restarted.Read(id).Count);
            Assert.Equal("State 5", restarted.Read(id)[0].Message);
            Assert.Empty(restarted.Read(Guid.NewGuid()));
            clock.Now = clock.Now.AddDays(8);
            Assert.Empty(restarted.Read(id));
            restarted.Record(id, "Command", new string('x', 1000));
            Assert.Equal(500, Assert.Single(restarted.Read(id)).Message.Length);
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("pi")]
    public void CommandsAreSmallExplicitNonDestructiveMenus(string harness)
    {
        var commands = AgentQuickCommands.For(harness);
        Assert.Equal(4, commands.Count);
        Assert.Contains(commands, c => c.Command == "/model");
        Assert.DoesNotContain(commands, c => c.Command is "/clear" or "/new" or "/quit");
        Assert.Empty(AgentQuickCommands.For("unknown"));
    }
}
