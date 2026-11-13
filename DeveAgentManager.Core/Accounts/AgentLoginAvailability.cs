using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Accounts;

public sealed partial class AccountService
{
    public const string LoginSelectionRequired = "This agent no longer has access to its configured login. Choose another harness or login to continue.";
    public const string LoginStopPendingMessage = "Login access ended. Waiting for the Runner to confirm the previous harness has stopped.";
    public const string LoginMissing = "No login is configured. Choose a harness or login to continue.";

    internal static string? LoginBlockProblem(Agent agent) => agent.LoginStopPending ? LoginStopPendingMessage
        : agent.LoginBlocked ? LoginSelectionRequired : null;

    private static string? LoginProblem(string harnessId, Account? account, bool permitted) =>
        account is null || !account.Enabled || !permitted || !Compatible(harnessId, account.HarnessId, account.Kind)
            ? LoginSelectionRequired
            : harnessId == "pi" && account.HarnessId == "codex" && !account.ManagedRenewal
                ? "Reconnect this Codex login in My settings → Harness logins before using it with pi." : null;

    /// <summary>Batch metadata only: lists must not load credentials or mistake a viewer's grants for the owner's.</summary>
    internal async Task LoadAvailabilityAsync(ManagerDbContext db, IReadOnlyList<Agent> agents, UserSettingsStore? userSettings, CancellationToken ct)
    {
        if (agents.Count == 0) return;
        var owners = agents.Select(a => a.OwnerId).Distinct().ToArray();
        var enabledOwners = await db.Users.Where(u => owners.Contains(u.Id) && !u.Disabled).Select(u => u.Id).ToListAsync(ct);
        var defaults = await db.UserHarnessDefaults.AsNoTracking().Where(p => owners.Contains(p.UserId)).ToListAsync(ct);
        var pi = new Dictionary<Guid, List<Guid>?>();
        foreach (var owner in agents.Where(a => a.HarnessId == "pi").Select(a => a.OwnerId).Distinct())
            pi[owner] = userSettings is null ? null : (await userSettings.GetModelDefaultsAsync(owner, ct)).GetValueOrDefault("pi")?.PiAccountIds;
        var ids = agents.Where(a => a.AccountId != null).Select(a => a.AccountId!.Value)
            .Concat(defaults.Select(p => p.AccountId)).Concat(pi.Values.OfType<List<Guid>>().SelectMany(x => x)).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new Account { Id = a.Id, HarnessId = a.HarnessId, Kind = a.Kind, Enabled = a.Enabled, ManagedRenewal = a.ManagedRenewal })
            .ToDictionaryAsync(a => a.Id, ct);
        var grants = await db.AccountGrants.Where(g => ids.Contains(g.AccountId) && owners.Contains(g.UserId) && g.Access >= AccountAccess.Use)
            .Select(g => new { g.AccountId, g.UserId }).ToListAsync(ct);
        var agentIds = agents.Select(a => a.Id).ToArray();
        var legacy = await db.AgentLoginGrants.AsNoTracking().Where(g => agentIds.Contains(g.AgentId)).ToListAsync(ct);
        foreach (var agent in agents)
        {
            var problem = LoginBlockProblem(agent);
            if (problem is null && !enabledOwners.Contains(agent.OwnerId)) problem = "The agent's owner is disabled or removed.";
            if (problem is null && agent.HarnessId == "pi" && pi.GetValueOrDefault(agent.OwnerId) is { } selected)
            {
                problem = selected.Select(id => LoginProblem("pi", accounts.GetValueOrDefault(id),
                    grants.Any(g => g.AccountId == id && g.UserId == agent.OwnerId))).FirstOrDefault(p => p is not null);
            }
            else if (problem is null && (_harnesses.GetValueOrDefault(agent.HarnessId)?.AccountSupport is not null || agent.HarnessId == "pi" && agent.AccountId is not null))
            {
                var id = agent.AccountId ?? defaults.FirstOrDefault(p => p.UserId == agent.OwnerId && p.HarnessId == agent.HarnessId)?.AccountId;
                problem = id is null ? LoginMissing : LoginProblem(agent.HarnessId, accounts.GetValueOrDefault(id.Value),
                    grants.Any(g => g.AccountId == id && g.UserId == agent.OwnerId)
                    || legacy.Any(g => g.AgentId == agent.Id && g.OwnerId == agent.OwnerId && g.AccountId == id));
            }
            agent.LoginStatus = new(problem is null, problem, agent.LoginStopPending);
        }
    }
}
