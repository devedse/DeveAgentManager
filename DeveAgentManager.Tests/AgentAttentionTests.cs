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
    private static Agent NewAgent() => new() { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "agent", State = AgentState.Running, HarnessId = "codex", UpdatedAt = Now,
        Runner = new() { Id = Guid.NewGuid(), OwnerId = User.BootstrapId, Name = "runner", State = RunnerState.Ready } };
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
            Assert.Equal(AgentAttentionKind.Finished, Assert.Single(await service.ListAsync(TestUsers.Admin, Ct)).Kind);
            await using (var db = factory.CreateDbContext())
            {
                var newer = await db.Agents.SingleAsync(Ct);
                Complete(newer, Now.AddSeconds(-10), "second");
                await db.SaveChangesAsync(Ct);
            }
            await service.MarkSeenAsync(agent.Id, viewed, Ct);
            var latest = Assert.Single(await service.ListAsync(TestUsers.Admin, Ct));
            Assert.True(latest.CompletionSequence > viewed);
            await Assert.ThrowsAsync<ArgumentException>(() => service.MarkSeenAsync(agent.Id, latest.CompletionSequence + 1, Ct));
            await service.MarkSeenAsync(agent.Id, latest.CompletionSequence, Ct);
            service = new(factory, new ManagerEvents(), TimeProvider.System);
            Assert.Empty(await service.ListAsync(TestUsers.Admin, Ct));
            await service.MarkSeenAsync(agent.Id, viewed, Ct);
            Assert.Empty(await service.ListAsync(TestUsers.Admin, Ct));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DismissedLimitStaysHiddenOnPollButNewResetIsVisible()
    {
        var agent = NewAgent();
        var detection = new AgentScreenDetection(AgentActivityStatus.UsageLimited, "limit", "error", "resets 3pm");
        AgentActivityTracker.Apply(agent, detection, "process", Now);
        agent.DismissedAttentionToken = AgentAttentionService.Attention(agent, false, Now)!.Token;
        AgentActivityTracker.Apply(agent, detection, "process", Now.AddSeconds(5));
        Assert.Null(AgentAttentionService.Attention(agent, false, Now.AddSeconds(5)));
        Assert.Equal(AgentActivityStatus.UsageLimited, AgentActivityTracker.GetDto(agent, Now.AddSeconds(5)).Status);
        AgentActivityTracker.Apply(agent, detection with { Evidence = "resets 4pm" }, "process", Now.AddSeconds(10));
        Assert.NotNull(AgentAttentionService.Attention(agent, false, Now.AddSeconds(10)));
    }

    [Fact]
    public async Task DismissalPersistsAndDelayedDismissCannotHideANewAlert()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dam-dismiss-{Guid.NewGuid():N}.db");
        var factory = new Factory(path);
        try
        {
            var agent = NewAgent(); agent.RunnerId = agent.Runner!.Id;
            AgentActivityTracker.Apply(agent, new(AgentActivityStatus.UsageLimited, "limit", "error", "first reset"), "process", DateTime.UtcNow);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync(Ct); db.Agents.Add(agent); await db.SaveChangesAsync(Ct);
            }
            var service = new AgentAttentionService(factory, new ManagerEvents(), TimeProvider.System);
            var old = Assert.Single(await service.ListAsync(TestUsers.Admin, Ct));
            await service.DismissAsync(agent.Id, old.Token, Ct);
            service = new(factory, new ManagerEvents(), TimeProvider.System);
            Assert.Empty(await service.ListAsync(TestUsers.Admin, Ct));
            await using (var db = factory.CreateDbContext())
            {
                var current = await db.Agents.SingleAsync(Ct);
                AgentActivityTracker.Apply(current, new(AgentActivityStatus.NeedsInput, "approval", "controls", "Approve command?"), "process", DateTime.UtcNow);
                await db.SaveChangesAsync(Ct);
            }
            await service.DismissAsync(agent.Id, old.Token, Ct);
            var latest = Assert.Single(await service.ListAsync(TestUsers.Admin, Ct));
            Assert.Equal(AgentAttentionKind.NeedsInput, latest.Kind);
            await service.DismissAsync(agent.Id, latest.Token, Ct);
            Assert.Empty(await service.ListAsync(TestUsers.Admin, Ct));
            await using (var db = factory.CreateDbContext())
            {
                var current = await db.Agents.SingleAsync(Ct);
                Complete(current, DateTime.UtcNow.AddSeconds(-15), "finished");
                AgentActivityTracker.Apply(current, new(AgentActivityStatus.UsageLimited, "limit", "error", "new reset"), "process", DateTime.UtcNow);
                db.AgentLimits.Add(new AgentLimit { AgentId = current.Id, Status = "Scheduled" });
                await db.SaveChangesAsync(Ct);
            }
            var finished = Assert.Single(await service.ListAsync(TestUsers.Admin, Ct));
            Assert.Equal(AgentAttentionKind.Finished, finished.Kind);
            await service.DismissAsync(agent.Id, finished.Token, Ct);
            Assert.Empty(await service.ListAsync(TestUsers.Admin, Ct));
        }
        finally { File.Delete(path); }
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
