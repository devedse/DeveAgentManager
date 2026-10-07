using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _renewing = new();

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
        var harness = GetHarness(harnessId);
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
            ManagedRenewal = kind == AccountKind.Subscription && harness.RenewsAccounts,
            IsDefault = !await db.Accounts.AnyAsync(a => a.OwnerId == ownerId && a.HarnessId == harnessId, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged, UserId: ownerId));
        return account;
    }

    /// <summary>A new login for an existing account (e.g. after it was logged out). Runners take it on the next start; a login the Manager renews, within minutes.</summary>
    public async Task ReplaceSecretAsync(Guid accountId, AccountKind kind, string secret, CancellationToken cancellationToken = default)
    {
        var clean = RequireSecret(secret);
        await UpdateAsync(accountId, account =>
        {
            account.Kind = kind;
            account.ProtectedSecret = protector.Protect(clean)!;
            account.ManagedRenewal = kind == AccountKind.Subscription && GetHarness(account.HarnessId).RenewsAccounts;
            account.RenewalProblem = null;
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
    /// Puts the account on the Runner, unless the Runner already has this version of it (one quick check). A login the
    /// Manager renews is renewed first when due, and the Runner gets only its access token.
    /// </summary>
    public async Task EnsureOnRunnerAsync(Runner runner, Account account, CancellationToken cancellationToken = default)
    {
        if (account.ManagedRenewal)
        {
            account = await RenewIfDueAsync(account, cancellationToken) ?? throw new InvalidOperationException($"The login '{account.Name}' was removed.");
        }

        var folder = Folder(account.Id);
        var marker = Marker(account);
        var current = await shell.RunAsync(runner, $"cat \"{folder}/version\" 2>/dev/null || true", TimeSpan.FromSeconds(20), cancellationToken);
        if (current.Output.Trim() == marker)
        {
            return;
        }

        var harness = GetHarness(account.HarnessId);
        var secret = protector.Unprotect(account.ProtectedSecret) ?? throw new InvalidOperationException($"The login of account '{account.Name}' can't be read. Connect it again.");
        if (account.ManagedRenewal)
        {
            secret = harness.RunnerAccountCopy(secret);
        }

        var setup = harness.BuildAccountSetup(folder, account.Kind, secret);
        var remote = $"~/.deveagentmanager/accounts/{account.Id:N}";
        foreach (var (path, content, mode) in setup.Files)
        {
            // Write next to it, then rename: a harness reloading its login never reads half a file.
            await shell.UploadAsync(runner, $"{remote}/{path}.dam-new", content, mode, cancellationToken);
            (await shell.RunAsync(runner, $"mv -f \"{folder}/{path}.dam-new\" \"{folder}/{path}\"", TimeSpan.FromSeconds(20), cancellationToken)).EnsureSuccess("Writing the login");
        }

        await shell.UploadAsync(runner, $"{remote}/env", "# Managed by DeveAgentManager: account " + account.Name.ReplaceLineEndings(" ") + "\n" + setup.Environment, "600", cancellationToken);
        if (setup.Script is not null)
        {
            (await shell.RunAsync(runner, setup.Script, TimeSpan.FromSeconds(60), cancellationToken)).EnsureSuccess($"Setting up account '{account.Name}'");
        }

        await shell.UploadAsync(runner, $"{remote}/version", marker + "\n", "600", cancellationToken);
        logger.LogInformation("Account {Account} ({Harness}) set up on {Runner}", account.Name, account.HarnessId, runner.Name);
    }

    /// <summary>What the Runner's version file says. A renewed login changes its encrypted text, so Runners take it.</summary>
    internal static string Marker(Account account) => account.ManagedRenewal
        ? $"{account.Version}-{account.Kind}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.ProtectedSecret)))[..12]}"
        : $"{account.Version}-{account.Kind}";

    /// <summary>
    /// Renews a login the Manager renews, when its harness says it is due. One renewal per account at a time; the next
    /// caller sees the saved result instead of renewing again. Saved before any Runner gets it, and dropped when the
    /// account was reconnected or removed meanwhile. Returns the account as stored now (null: removed).
    /// </summary>
    /// <exception cref="AccountRenewalException">Permanent: the provider ended the login.</exception>
    public async Task<Account?> RenewIfDueAsync(Account account, CancellationToken cancellationToken = default)
    {
        var gate = _renewing.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var current = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == account.Id, CancellationToken.None);
            if (current is not { ManagedRenewal: true })
            {
                return current;
            }

            var secret = protector.Unprotect(current.ProtectedSecret) ?? throw new InvalidOperationException($"The login of account '{current.Name}' can't be read. Connect it again.");
            string? renewed;
            try
            {
                // Never cancelled by a caller: a renewal stopped after it was sent may have used up the refresh token.
                renewed = await GetHarness(current.HarnessId).RenewAccountAsync(secret, CancellationToken.None);
            }
            catch (Exception ex) when (ex is AccountRenewalException or HttpRequestException or TaskCanceledException)
            {
                var problem = ex is AccountRenewalException ? ex.Message : "ChatGPT couldn't be reached to renew this login. The Manager tries again in a few minutes.";
                await SaveIfUnchangedAsync(db, current, s => s.SetProperty(a => a.RenewalProblem, problem));
                logger.LogWarning("Renewing account {Account} failed: {Problem}", current.Name, problem);
                if (ex is AccountRenewalException { Permanent: true })
                {
                    throw;
                }

                return current;
            }

            if (renewed is null)
            {
                return current;
            }

            var stored = protector.Protect(renewed)!;
            if (!await SaveIfUnchangedAsync(db, current, s => s.SetProperty(a => a.ProtectedSecret, stored).SetProperty(a => a.RenewalProblem, (string?)null)))
            {
                logger.LogInformation("Account {Account} changed while it was renewed; the renewal is dropped", current.Name);
            }
            else
            {
                logger.LogInformation("Account {Account} renewed", current.Name);
                events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged, UserId: current.OwnerId));
            }

            return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == account.Id, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Only when nobody reconnected or removed the account since it was read.</summary>
    private static async Task<bool> SaveIfUnchangedAsync(ManagerDbContext db, Account read,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Account>> set) =>
        await db.Accounts.Where(a => a.Id == read.Id && a.Version == read.Version && a.ProtectedSecret == read.ProtectedSecret)
            .ExecuteUpdateAsync(set, CancellationToken.None) == 1;

    /// <summary>
    /// Renews due logins, then brings every ready Runner up to date: logins the Manager renews get their current copy
    /// (this also replaces copies that still hold a refresh token), and folders of removed accounts are deleted.
    /// </summary>
    public async Task SyncRunnersAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var account in await db.Accounts.AsNoTracking().Where(a => a.ManagedRenewal).ToListAsync(cancellationToken))
        {
            try
            {
                await RenewIfDueAsync(account, cancellationToken);
            }
            catch (AccountRenewalException)
            {
                // Recorded on the account; Runners keep the copy they have.
            }
        }

        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, cancellationToken);
        foreach (var runner in await db.Runners.AsNoTracking().Where(r => r.State == RunnerState.Ready).ToListAsync(cancellationToken))
        {
            try
            {
                var listing = await shell.RunAsync(runner,
                    "for d in \"$HOME/.deveagentmanager/accounts/\"*/; do [ -d \"$d\" ] && echo \"$(basename \"$d\") $(cat \"$d/version\" 2>/dev/null)\"; done; true",
                    TimeSpan.FromSeconds(20), cancellationToken);
                foreach (var line in listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var (name, marker) = line.IndexOf(' ') is var space and > 0 ? (line[..space], line[(space + 1)..]) : (line, "");
                    if (!Guid.TryParseExact(name, "N", out var id))
                    {
                        continue;
                    }

                    if (!accounts.TryGetValue(id, out var account))
                    {
                        await shell.RunAsync(runner, $"rm -rf \"{Folder(id)}\"", TimeSpan.FromSeconds(20), cancellationToken);
                    }
                    else if (account.ManagedRenewal && marker != Marker(account))
                    {
                        await EnsureOnRunnerAsync(runner, account, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Could not update logins on {Runner}", runner.Name);
            }
        }
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
