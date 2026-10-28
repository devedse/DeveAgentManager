using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Agents;

public sealed record AgentVoiceTarget(Guid AgentId, string HarnessId, string SessionId, Runner Runner, string ConnectCommand);

public sealed partial class AgentService
{
    /// <summary>Snapshot under the lifecycle lock. Opening SSH and voice negotiation run after releasing it.</summary>
    public async Task<AgentVoiceTarget> GetVoiceTargetAsync(Guid agentId, CancellationToken ct)
    {
        using var lease = await LockAgentAsync(agentId, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Runner).SingleOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new KeyNotFoundException("Agent not found.");
        RequireNotMoving(agent);
        var runner = RequireReady(agent.Runner!);
        if (agent.State != AgentState.Running || agent.LoginBlocked || agent.LoginStopPending)
            throw new InvalidOperationException("Start the agent with a permitted login before connecting voice.");
        var command = GetHarness(agent.HarnessId).VoiceConnectCommand(agent.Id, agent.HarnessSessionId)
            ?? throw new InvalidOperationException("This harness does not support browser voice.");
        return new(agent.Id, agent.HarnessId, agent.HarnessSessionId, runner, command);
    }

    public async Task<bool> VoiceTargetIsCurrentAsync(AgentVoiceTarget target, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Agents.AsNoTracking().AnyAsync(a => a.Id == target.AgentId && a.RunnerId == target.Runner.Id
            && a.HarnessId == target.HarnessId && a.HarnessSessionId == target.SessionId && a.State == AgentState.Running
            && !a.LoginBlocked && !a.LoginStopPending && a.MoveJson == null && a.Runner!.State == RunnerState.Ready, ct);
    }
}
