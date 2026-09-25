using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Runners;

/// <summary>Opens browser terminals: a plain shell on a Runner, or attached to an agent's tmux session.</summary>
public sealed class TerminalService(IDbContextFactory<ManagerDbContext> dbFactory, RunnerShell shell, AgentService agents)
{
    public async Task<TerminalConnection> OpenShellAsync(Guid runnerId, int columns, int rows, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            ?? throw new ArgumentException("Runner not found.");
        if (runner.State != RunnerState.Ready)
        {
            throw new InvalidOperationException($"Runner '{runner.Name}' is not ready ({runner.State}).");
        }

        return await OpenAsync(runner, null, columns, rows, cancellationToken);
    }

    public async Task<TerminalConnection> OpenAgentAsync(Guid agentId, int columns, int rows, CancellationToken cancellationToken)
    {
        var (runner, command) = await agents.GetAttachCommandAsync(agentId, cancellationToken);
        return await OpenAsync(runner, command, columns, rows, cancellationToken);
    }

    private async Task<TerminalConnection> OpenAsync(Runner runner, string? command, int columns, int rows, CancellationToken cancellationToken)
    {
        var client = await shell.ConnectNewAsync(runner, cancellationToken);
        try
        {
            return await Task.Run(() => TerminalConnection.Open(client, command, columns, rows), cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
