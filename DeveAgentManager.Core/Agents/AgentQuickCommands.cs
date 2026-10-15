using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Agents;

public sealed class AgentQuickCommands(IDbContextFactory<ManagerDbContext> factory, ILimitTerminal terminal,
    RunnerShell shell, AgentDiagnostics diagnostics)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    public static IReadOnlyList<AgentQuickCommandDto> For(string harness) => harness switch
    {
        "codex" => [new("/model", "Choose model"), new("/status", "Usage and status"), new("/diff", "View changes"), new("/permissions", "Permissions")],
        "claude" => [new("/model", "Choose model"), new("/usage", "Usage limits"), new("/status", "Session status"), new("/help", "Help")],
        "pi" => [new("/model", "Choose model"), new("/session", "Usage and session"), new("/settings", "Settings"), new("/thinking", "Reasoning level")],
        _ => []
    };
    public async Task SendAsync(Guid id, AgentQuickCommandRequest request, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(id, _ => new(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("Another command is being sent. Wait for it to finish.");
        try
        {
            using var loginLease = await Accounts.AccountCoordination.LockAsync(ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var agent = await db.Agents.Include(a => a.Runner).SingleOrDefaultAsync(a => a.Id == id, ct)
                ?? throw new KeyNotFoundException("Agent not found.");
            if (agent.HarnessId != request.HarnessId || !For(agent.HarnessId).Any(c => c.Command == request.Command))
                throw new ArgumentException("The command is not available for this harness. Reopen the menu.");
            if (agent.LoginBlocked || agent.LoginStopPending || agent.State != AgentState.Running || agent.Runner?.State != RunnerState.Ready)
                throw new InvalidOperationException("Start the agent on a ready Runner first.");
            var screen = await terminal.ReadAsync(agent, ct);
            if (!screen.InputEmpty) throw new InvalidOperationException("The terminal has a draft or an open menu. Clear it before sending a quick command.");
            diagnostics.Record(id, "Command", $"Sending {request.Command}");
            try
            {
                var result = (await shell.RunAsync(agent.Runner!, LimitTerminal.BuildSubmitScript(agent.TmuxSession, screen, request.Command, requireUnchangedScreen: false), TimeSpan.FromSeconds(10), ct)).EnsureSuccess("Sending quick command");
                if (result.Output.Trim() != "SENT") throw new InvalidOperationException("The prompt changed. Enter was not sent; check the terminal before trying again.");
                diagnostics.Record(id, "Command", $"Submitted {request.Command}");
            }
            catch
            {
                diagnostics.Record(id, "Command", $"Could not confirm {request.Command}. Check the terminal; no automatic retry.");
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
