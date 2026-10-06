using System.Security.Cryptography;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>
/// How people log in to a Runner themselves: the Runner Account's password and "keys only".
/// <list type="bullet">
/// <item>Every VM the Manager creates gets a password of its own, so knowing one Runner's password doesn't open another.
/// A VM from before that (with the password all Runners shared) gets its own at its next setup.</item>
/// <item>People who manage a Runner can see and change its password; every view and change is in the audit log.</item>
/// <item>Keys only: SSH refuses passwords (the Manager logs in with its key; people with access with theirs).</item>
/// </list>
/// An existing machine keeps the password it was added with, and its SSH settings are only changed with keys only.
/// </summary>
public sealed class RunnerPasswords(
    IDbContextFactory<ManagerDbContext> dbFactory,
    IEnumerable<IRunnerProvider> providers,
    RunnerShell shell,
    SecretProtector protector,
    AuditLog audit,
    ManagerEvents events,
    ILogger<RunnerPasswords> logger)
{
    public const int MinLength = 8;
    private const int GeneratedLength = 20;
    private const string SshdConfig = "/etc/ssh/sshd_config.d/10-deveagentmanager.conf";

    // No characters that look alike (0/O, 1/l/I), so it can be typed from the screen into a VM console.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private readonly Dictionary<string, IRunnerProvider> _providers = providers.ToDictionary(p => p.Id);

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, GeneratedLength);

    public async Task<string> RevealAsync(CurrentUser actor, Guid runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken) ?? throw new KeyNotFoundException("Runner not found.");
        var password = protector.Unprotect(runner.ProtectedPassword)
            ?? throw new InvalidOperationException("This Runner has no password the Manager knows. Set one.");
        await audit.RecordAsync(actor, AuditActions.RunnerPasswordViewed, $"Runner {runner.Name}", cancellationToken: cancellationToken);
        return password;
    }

    /// <summary>Sets the Runner Account's password on the Runner (which must be ready), then keeps it.</summary>
    public async Task SetAsync(CurrentUser actor, Guid runnerId, string password, CancellationToken cancellationToken = default)
    {
        if (password.Length < MinLength || password.Length > 256 || password.Any(char.IsControl))
        {
            throw new ArgumentException($"Use a password of at least {MinLength} characters, without line breaks.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken) ?? throw new KeyNotFoundException("Runner not found.");
        RequireReady(runner);
        await ApplyPasswordAsync(runner, password, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        events.RunnerChanged(runner.Id);
        await audit.RecordAsync(actor, AuditActions.RunnerPasswordChanged, $"Runner {runner.Name}", cancellationToken: cancellationToken);
    }

    public async Task SetKeysOnlyAsync(CurrentUser actor, Guid runnerId, bool keysOnly, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runner = await db.Runners.FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken) ?? throw new KeyNotFoundException("Runner not found.");
        if (runner.KeysOnly == keysOnly)
        {
            return;
        }

        RequireReady(runner);
        await ApplyKeysOnlyAsync(runner, keysOnly, cancellationToken);
        runner.KeysOnly = keysOnly;
        runner.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.RunnerChanged(runner.Id);
        await audit.RecordAsync(actor, AuditActions.RunnerKeysOnlyChanged, $"Runner {runner.Name}",
            keysOnly ? "SSH refuses passwords" : "SSH accepts the password again", cancellationToken);
    }

    /// <summary>
    /// During setup: gives a VM that still has the password all Runners once shared one of its own, and applies keys
    /// only. Changes <paramref name="runner"/>; the caller saves it.
    /// </summary>
    internal async Task ApplyAtSetupAsync(Runner runner, IRunnerLog log, CancellationToken cancellationToken)
    {
        var createdByManager = _providers.GetValueOrDefault(runner.ProviderId) is { Capabilities.RequiresConnectionDetails: false };
        if (runner.PasswordChangedAt is null && createdByManager)
        {
            log.Step("Giving the Runner Account a password of its own (it had the one all Runners shared)");
            await ApplyPasswordAsync(runner, Generate(), cancellationToken);
            await audit.RecordAsync(null, AuditActions.RunnerPasswordChanged, $"Runner {runner.Name}", "Replaced the password all Runners shared", cancellationToken);
        }

        if (runner.KeysOnly || createdByManager)
        {
            // Also turned off again here, in case the setting changed while the Runner was unreachable.
            log.Step(runner.KeysOnly ? "SSH: keys only" : "SSH: password logins allowed");
            await ApplyKeysOnlyAsync(runner, runner.KeysOnly, cancellationToken);
        }
    }

    private async Task ApplyPasswordAsync(Runner runner, string password, CancellationToken cancellationToken)
    {
        var line = ShellQuote.Quote($"{runner.Username}:{password}");
        (await shell.RunAsync(runner, $"printf '%s\\n' {line} | sudo -n chpasswd", TimeSpan.FromSeconds(30), cancellationToken))
            .EnsureSuccess("Setting the password");
        runner.ProtectedPassword = protector.Protect(password);
        runner.PasswordChangedAt = DateTime.UtcNow;
        runner.UpdatedAt = DateTime.UtcNow;
        // New connections use the new password where the key doesn't do (the Manager logs in with its key first).
        shell.Invalidate(runner.Id);
        logger.LogInformation("Password of {Runner} changed", runner.Name);
    }

    private async Task ApplyKeysOnlyAsync(Runner runner, bool keysOnly, CancellationToken cancellationToken)
    {
        // A drop-in sorted before cloud-init's (50-cloud-init.conf): sshd takes the first value it reads.
        var script = keysOnly
            ? $$"""
                set -e
                grep -qE '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config\.d/' /etc/ssh/sshd_config || { echo "This machine's SSH server doesn't read /etc/ssh/sshd_config.d." >&2; exit 3; }
                printf 'PasswordAuthentication no\nKbdInteractiveAuthentication no\n' | sudo -n tee {{SshdConfig}} >/dev/null
                """
            : $"set -e\nsudo -n rm -f {SshdConfig}";
        (await shell.RunAsync(runner, $"""
            {script}
            sudo -n sshd -t
            sudo -n systemctl reload ssh 2>/dev/null || sudo -n systemctl reload sshd
            """, TimeSpan.FromSeconds(30), cancellationToken)).EnsureSuccess(keysOnly ? "Turning off SSH password logins" : "Allowing SSH password logins");
    }

    private static void RequireReady(Runner runner)
    {
        if (runner.State != RunnerState.Ready)
        {
            throw new InvalidOperationException($"Runner '{runner.Name}' is not ready ({runner.State}).");
        }
    }
}
