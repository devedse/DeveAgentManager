using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeveAgentManager.Tests;

public class AgentAttentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = DateTime.UtcNow;
    private static Agent NewAgent() => new() { Id = Guid.NewGuid(), Name = "agent", State = AgentState.Running, HarnessId = "codex", UpdatedAt = Now, Runner = new() { Id = Guid.NewGuid(), Name = "runner", State = RunnerState.Ready } };
    private static void Complete(Agent agent, DateTime at, string response)
    {
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Working, "working", "busy"), "process", at);
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Ready, "ready", "prompt", CompletionEvidence: response), "process", at.AddSeconds(5));
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Ready, "ready", "prompt", CompletionEvidence: response), "process", at.AddSeconds(10));
    }

    [Fact]
    public void AttentionDistinguishesBlockedQueuedAndManagedLimits()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Working, "working", "busy", QuestionWaiting: true), "process", Now);
        Assert.Equal(AgentAttentionKind.QuestionWaiting, AgentAttentionService.Attention(agent, false, Now)!.Kind);
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.NeedsInput, "approval", "controls"), "process", Now);
        Assert.Equal(AgentAttentionKind.NeedsInput, AgentAttentionService.Attention(agent, false, Now)!.Kind);
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.UsageLimited, "limit", "error"), "process", Now);
        Assert.Equal(AgentAttentionKind.UsageLimit, AgentAttentionService.Attention(agent, false, Now)!.Kind);
        Assert.Null(AgentAttentionService.Attention(agent, true, Now));
        agent.State = AgentState.Stopped;
        Assert.Null(AgentAttentionService.Attention(agent, false, Now));
        agent.StateMessage = "Terminal disappeared";
        Assert.Equal(AgentAttentionKind.UnexpectedExit, AgentAttentionService.Attention(agent, false, Now)!.Kind);
    }

    [Fact]
    public void UnclearRequiresPersistentUnknownOrStaleObservation()
    {
        var agent = NewAgent();
        AgentActivityTracker.Apply(agent, new(AgentActivityStatus.Unknown, "unrecognized", "fallback"), "process", Now);
        Assert.Null(AgentAttentionService.Attention(agent, false, Now.AddSeconds(29)));
        Assert.Equal(AgentAttentionKind.Unclear, AgentAttentionService.Attention(agent, false, Now.AddSeconds(61))!.Kind);
    }

    [Fact]
    public async Task DelayedAcknowledgementCannotHideNewCompletionAndPersistsAcrossServices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dam-attention-{Guid.NewGuid():N}.db");
        var factory = new Factory(path);
        try
        {
            var agent = NewAgent();
            agent.RunnerId = agent.Runner!.Id;
            Complete(agent, Now.AddSeconds(-25), "first");
            var viewed = AgentActivityTracker.Read(agent).Snapshot.CompletionSequence;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync(Ct);
                db.Agents.Add(agent);
                await db.SaveChangesAsync(Ct);
            }
            var service = new AgentAttentionService(factory, new ManagerEvents(), TimeProvider.System);
            Assert.Equal(AgentAttentionKind.Finished, Assert.Single(await service.ListAsync(Ct)).Kind);
            await using (var db = factory.CreateDbContext())
            {
                var newer = await db.Agents.SingleAsync(Ct);
                Complete(newer, Now.AddSeconds(-10), "second");
                await db.SaveChangesAsync(Ct);
            }
            await service.MarkSeenAsync(agent.Id, viewed, Ct);
            var latest = Assert.Single(await service.ListAsync(Ct));
            Assert.True(latest.CompletionSequence > viewed);
            await Assert.ThrowsAsync<ArgumentException>(() => service.MarkSeenAsync(agent.Id, latest.CompletionSequence + 1, Ct));
            await service.MarkSeenAsync(agent.Id, latest.CompletionSequence, Ct);
            service = new(factory, new ManagerEvents(), TimeProvider.System);
            Assert.Empty(await service.ListAsync(Ct));
            await service.MarkSeenAsync(agent.Id, viewed, Ct);
            Assert.Empty(await service.ListAsync(Ct));
        }
        finally { File.Delete(path); }
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
