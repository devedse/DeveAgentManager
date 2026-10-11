using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace DeveAgentManager.Tests;
public sealed class AgentJobRunnerTests(ApiSecurityTests.Manager manager) : IClassFixture<ApiSecurityTests.Manager>
{
    [Fact] public async Task DeliveryLoadsTheRunnerBeforeReadingTheTerminal()
    {
        using var client = manager.CreateClient();
        var services = manager.Services;
        var factory = services.GetRequiredService<IDbContextFactory<ManagerDbContext>>();
        var runner = new Runner { Id = Guid.NewGuid(), Name = "test-job-runner", State = RunnerState.Stopped, OwnerId = User.BootstrapId };
        var agent = new Agent { Id = Guid.NewGuid(), Name = "test-job-agent", RunnerId = runner.Id, OwnerId = User.BootstrapId, State = AgentState.Running, HarnessId = "codex", HarnessSessionId = "test-conversation", TmuxSession = "test-job-agent" };
        var session = new StoredSession { Id = Guid.NewGuid(), AgentId = agent.Id, OwnerId = User.BootstrapId, HarnessId = agent.HarnessId, HarnessSessionId = agent.HarnessSessionId };
        await using (var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            db.Runners.Add(runner); db.Agents.Add(agent); db.Sessions.Add(session);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var probe = new Probe();
        var delivery = new AgentJobCompletions(factory, services.GetRequiredService<RunnerShell>(), probe, services.GetRequiredService<SessionService>(), NullLogger<AgentJobCompletions>.Instance);
        var job = new AgentJobPublication(Guid.NewGuid(), agent.Id, session.Id, "test", "local", "/test", "INSPECTION_REQUIRED", null, DateTimeOffset.UtcNow, null, null, null);
        await delivery.DeliverAsync(runner, job, TestContext.Current.CancellationToken);
        Assert.Equal(runner.Id, probe.RunnerId);
    }
    private sealed class Probe : ILimitTerminal
    {
        public Guid? RunnerId { get; private set; }
        public Task<LimitScreen> ReadAsync(Agent agent, CancellationToken ct)
        {
            Assert.NotNull(agent.Runner);
            RunnerId = agent.Runner.Id;
            return Task.FromResult(new LimitScreen("draft", "test", null, false, "test"));
        }
        public Task<ContinueDelivery> SendContinueAsync(Agent agent, LimitScreen expected, CancellationToken ct) => throw new NotSupportedException();
    }
}
