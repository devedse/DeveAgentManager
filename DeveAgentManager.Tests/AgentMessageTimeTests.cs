using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace DeveAgentManager.Tests;
public class AgentMessageTimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTime At = new(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);
    private static DeveEntry Entry(DeveRole role, int minute, string type = "text") => new() { Kind = DeveEntryKind.Message, Role = role, Timestamp = At.AddMinutes(minute), Content = [new() { Type = type, Text = "message" }] };
    [Fact]
    public void UsesActualAssistantTextAndIgnoresNewerUserToolAndThinking()
    {
        var agent = new Agent();
        var session = new DeveSession { Entries = [Entry(DeveRole.Assistant, 1), Entry(DeveRole.Assistant, 2), Entry(DeveRole.User, 3), Entry(DeveRole.Tool, 4), Entry(DeveRole.Assistant, 5, "thinking"), Entry(DeveRole.Assistant, 6, "toolCall")] };
        Assert.True(SessionService.UpdateLastAssistantMessage(agent, session));
        Assert.Equal(At.AddMinutes(2), agent.LastAssistantMessageAt);
        Assert.False(SessionService.UpdateLastAssistantMessage(agent, new()));
        Assert.False(SessionService.UpdateLastAssistantMessage(agent, new() { Entries = [Entry(DeveRole.Assistant, 1)] }));
        Assert.Equal(At.AddMinutes(2), agent.LastAssistantMessageAt);
    }
    [Fact]
    public async Task BackfillsStoppedAgentsFromLocalStoredConversationsWithoutContactingRunner()
    {
        var folder = Directory.CreateTempSubdirectory("dam-message-").FullName;
        var options = new ManagerOptions { DataDirectory = folder };
        var factory = new Factory(options.DatabasePath);
        try
        {
            var id = Guid.NewGuid(); var sessionId = Guid.NewGuid();
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync(Ct);
                var runner = new Runner { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "offline", State = RunnerState.Stopped };
                var agent = new Agent { Id = id, OwnerId = User.BootstrapId, Name = "agent", State = AgentState.Stopped, Runner = runner, RunnerId = runner.Id };
                db.Sessions.Add(new() { Id = sessionId, OwnerId = User.BootstrapId, Agent = agent, AgentId = id });
                await db.SaveChangesAsync(Ct);
            }
            Directory.CreateDirectory(options.SessionsDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.SessionsDirectory, $"{sessionId:N}.json"), new DeveSession { Entries = [Entry(DeveRole.Assistant, 2), Entry(DeveRole.User, 3)] }.ToJson(), Ct);
            var service = new SessionService(factory, null!, [], [], new SessionImageStore(options), new SessionFileStore(options), options, new ManagerEvents(), NullLogger<SessionService>.Instance);
            await service.BackfillMessageTimesAsync(Ct);
            await using var read = factory.CreateDbContext();
            Assert.Equal(At.AddMinutes(2), (await read.Agents.SingleAsync(Ct)).LastAssistantMessageAt);
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    { public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options); }
}
