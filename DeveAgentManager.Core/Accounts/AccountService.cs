using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Accounts;

/// <summary>
/// Harness logins ("accounts": subscriptions and API keys), each owned by one user. They are stored encrypted in the
/// Manager and put on a Runner when an agent starts with one, in ~/.deveagentmanager/accounts/&lt;id&gt;: an environment
/// file the agent's harness starts with, and the harness's own files (Codex: its auth.json). Every user has their own
/// default login per harness.
/// </summary>
public sealed class AccountService(
    IDbContextFactory<ManagerDbContext> dbFactory,
    SecretProtector protector,
    RunnerShell shell,
    IEnumerable<IHarness> harnesses,
    ManagerEvents events,
    ILogger<AccountService> logger)
{
    private readonly Dictionary<string, IHarness> _harnesses = harnesses.ToDictionary(h => h.Id);

    /// <summary>The harnesses that have accounts (Claude Code, Codex).</summary>
    public IEnumerable<IHarness> Harnesses => _harnesses.Values.Where(h => h.AccountSupport is not null);

    public IHarness GetHarness(string harnessId) =>
        _harnesses.GetValueOrDefault(harnessId) is { AccountSupport: not null } harness ? harness : throw new ArgumentException($"'{harnessId}' has no accounts.");

    /// <summary>The account's folder on a Runner, as a path the shell expands.</summary>
    public static string Folder(Guid accountId) => $"$HOME/.deveagentmanager/accounts/{accountId:N}";

    /// <summary>Sourced before an agent's harness starts.</summary>
    public static string EnvironmentFile(Guid accountId) => $"{Folder(accountId)}/env";

    public async Task<List<Account>> ListAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts.AsNoTracking().Where(a => a.OwnerId == ownerId)
            .OrderBy(a => a.HarnessId).ThenByDescending(a => a.IsDefault).ThenBy(a => a.Name).ToListAsync(cancellationToken);
    }

    /// <summary>How many agents use each account (by choice, not through the default).</summary>
    public async Task<Dictionary<Guid, int>> CountAgentsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Agents.Where(a => a.AccountId != null).GroupBy(a => a.AccountId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
    }

    /// <summary>Adds a login for <paramref name="ownerId"/>. Their first one of a harness becomes their default for it.</summary>
    public async Task<Account> AddAsync(Guid ownerId, string harnessId, string name, AccountKind kind, string secret, CancellationToken cancellationToken = default)
    {
        GetHarness(harnessId);
        var clean = RequireSecret(secret);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            HarnessId = harnessId,
            Name = string.IsNullOrWhiteSpace(name) ? await NextNameAsync(db, ownerId, harnessId, kind, cancellationToken) : name.Trim(),
            Kind = kind,
            ProtectedSecret = protector.Protect(clean)!,
            IsDefault = !await db.Accounts.AnyAsync(a => a.OwnerId == ownerId && a.HarnessId == harnessId, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged, UserId: ownerId));
        return account;
    }

    /// <summary>A new login for an existing account (e.g. after it was logged out). Runners take it on the next start.</summary>
    public async Task ReplaceSecretAsync(Guid accountId, AccountKind kind, string secret, CancellationToken cancellationToken = default)
    {
        var clean = RequireSecret(secret);
        await UpdateAsync(accountId, account =>
        {
            account.Kind = kind;
            account.ProtectedSecret = protector.Protect(clean)!;
            account.Version++;
        }, cancellationToken);
    }

    public Task RenameAsync(Guid accountId, string name, CancellationToken cancellationToken = default) =>
        UpdateAsync(accountId, account => account.Name = string.IsNullOrWhiteSpace(name) ? account.Name : name.Trim(), cancellationToken);

    public async Task SetDefaultAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, cancellationToken);
        foreach (var other in await db.Accounts.Where(a => a.OwnerId == account.OwnerId && a.HarnessId == account.HarnessId).ToListAsync(cancellationToken))
        {
            other.IsDefault = other.Id == accountId;
        }

        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged, UserId: account.OwnerId));
    }

    /// <summary>Removes the login; agents that used it fall back to their owner's default. Its files are removed from ready Runners.</summary>
    public async Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return;
        }

        await db.Agents.Where(a => a.AccountId == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AccountId, (Guid?)null), cancellationToken);
        db.Accounts.Remove(account);
        if (account.IsDefault && await db.Accounts.Where(a => a.OwnerId == account.OwnerId && a.HarnessId == account.HarnessId && a.Id != accountId)
                .OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(cancellationToken) is { } next)
        {
            next.IsDefault = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var runner in await db.Runners.AsNoTracking().Where(r => r.State == RunnerState.Ready).ToListAsync(cancellationToken))
        {
            try
            {
                await shell.RunAsync(runner, $"rm -rf \"{Folder(accountId)}\"", TimeSpan.FromSeconds(20), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Could not remove account {Account} from {Runner}", accountId, runner.Name);
            }
        }

        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged, UserId: account.OwnerId));
    }

    /// <summary>The login an agent runs with: the one it was switched to, else its owner's default, else none (the Runner's own login).</summary>
    public async Task<Account?> ResolveAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        if (_harnesses.GetValueOrDefault(agent.HarnessId)?.AccountSupport is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (agent.AccountId is { } id ? await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken) : null)
            ?? await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.OwnerId == agent.OwnerId && a.HarnessId == agent.HarnessId && a.IsDefault, cancellationToken);
    }

    /// <summary>
    /// Puts the account on the Runner, unless the Runner already has this version of it (one quick check). Files are only
    /// rewritten for a new version, so a login the harness renewed on the Runner itself (Codex) is left alone.
    /// </summary>
    public async Task EnsureOnRunnerAsync(Runner runner, Account account, CancellationToken cancellationToken = default)
    {
        var folder = Folder(account.Id);
        var marker = $"{account.Version}-{account.Kind}";
        var current = await shell.RunAsync(runner, $"cat \"{folder}/version\" 2>/dev/null || true", TimeSpan.FromSeconds(20), cancellationToken);
        if (current.Output.Trim() == marker)
        {
            return;
        }

        var harness = GetHarness(account.HarnessId);
        var secret = protector.Unprotect(account.ProtectedSecret) ?? throw new InvalidOperationException($"The login of account '{account.Name}' can't be read. Connect it again.");
        var setup = harness.BuildAccountSetup(folder, account.Kind, secret);
        var remote = $"~/.deveagentmanager/accounts/{account.Id:N}";
        foreach (var (path, content, mode) in setup.Files)
        {
            await shell.UploadAsync(runner, $"{remote}/{path}", content, mode, cancellationToken);
        }

        await shell.UploadAsync(runner, $"{remote}/env", "# Managed by DeveAgentManager: account " + account.Name.ReplaceLineEndings(" ") + "\n" + setup.Environment, "600", cancellationToken);
        if (setup.Script is not null)
        {
            (await shell.RunAsync(runner, setup.Script, TimeSpan.FromSeconds(60), cancellationToken)).EnsureSuccess($"Setting up account '{account.Name}'");
        }

        await shell.UploadAsync(runner, $"{remote}/version", marker + "\n", "600", cancellationToken);
        logger.LogInformation("Account {Account} ({Harness}) set up on {Runner}", account.Name, account.HarnessId, runner.Name);
    }

    /// <summary>A fresh folder on the Runner for a login's files (<c>$DAM_LOGIN_DIR</c>).</summary>
    public static string NewLoginFolder() => $"$HOME/.deveagentmanager/login-{Guid.NewGuid():N}";

    /// <summary>Reads the file a login wrote (e.g. Codex's auth.json) and removes the login folder.</summary>
    public async Task<string> ReadLoginFileAsync(Runner runner, string loginFolder, string file, CancellationToken cancellationToken = default)
    {
        var path = loginFolder.Replace("$HOME/", "~/", StringComparison.Ordinal) + "/" + file;
        try
        {
            return await shell.DownloadAsync(runner, path, cancellationToken) is { Length: > 0 } content
                ? content
                : throw new InvalidOperationException("The login didn't write its file. Try again.");
        }
        finally
        {
            await shell.RunAsync(runner, $"rm -rf \"{loginFolder}\"", TimeSpan.FromSeconds(20), CancellationToken.None);
        }
    }

    private async Task UpdateAsync(Guid accountId, Action<Account> change, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, cancellationToken);
        change(account);
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged, UserId: account.OwnerId));
    }

    private async Task<string> NextNameAsync(ManagerDbContext db, Guid ownerId, string harnessId, AccountKind kind, CancellationToken cancellationToken)
    {
        var count = await db.Accounts.CountAsync(a => a.OwnerId == ownerId && a.HarnessId == harnessId, cancellationToken);
        var baseName = kind == AccountKind.Subscription ? "Subscription" : "API key";
        return count == 0 ? baseName : $"{baseName} {count + 1}";
    }

    private static string RequireSecret(string secret) =>
        string.IsNullOrWhiteSpace(secret) ? throw new ArgumentException("The login is empty.") : secret.Trim();
}
