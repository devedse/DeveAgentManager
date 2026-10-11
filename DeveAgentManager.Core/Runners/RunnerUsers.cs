using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Setup;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Runners;

/// <summary>
/// What each person with access to a Runner has on it. Everyone uses the same Runner Account, so this is what keeps
/// their work apart (not their secrets: see docs/users-plan.md):
/// <list type="bullet">
/// <item><c>~/.deveagentmanager/users/&lt;id&gt;/env</c>: their environment variables, <c>GH_TOKEN</c> and
/// <c>GIT_CONFIG_GLOBAL</c>, loaded before their agents, their shells and (for the owner) VS Code start.</item>
/// <item><c>gitconfig</c>: the shared <c>~/.gitconfig</c>, then their git identity and their own credentials only. Without
/// a GitHub token they have no credentials at all, so nobody pushes with someone else's token by accident.</item>
/// <item>Their SSH keys, in a managed part of <c>~/.ssh/authorized_keys</c>.</item>
/// </list>
/// <see cref="SyncAsync"/> brings a Runner in line with who has access now; removing access removes their folder and keys.
/// </summary>
public sealed class RunnerUsers(
    IDbContextFactory<ManagerDbContext> dbFactory,
    RunnerShell shell,
    UserSettingsStore settings,
    ILogger<RunnerUsers> logger)
{
    private const string KeysBegin = "# >>> DeveAgentManager: keys of the people with access to this Runner >>>";
    private const string KeysEnd = "# <<< DeveAgentManager <<<";

    public static string Folder(Guid userId) => $"$HOME/.deveagentmanager/users/{userId:N}";

    /// <summary>Loaded before a user's agents and shells start.</summary>
    public static string EnvironmentFile(Guid userId) => $"{Folder(userId)}/env";

    /// <summary>
    /// Puts the user's files on the Runner, unless it already has these (one quick check). Called before their agents and
    /// shells start, so a change to their settings applies from the next start even on a Runner that wasn't reachable.
    /// </summary>
    public async Task EnsureAsync(Runner runner, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var username = await db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The user no longer exists.");
        var profile = await settings.GetProfileAsync(userId, cancellationToken);
        var token = await settings.GetGitHubTokenAsync(userId, cancellationToken);

        var files = new List<(string Name, string Content)>
        {
            ("env", BuildEnvironment(userId, username, profile, token)),
            ("gitconfig", BuildGitConfig(userId, username, profile, token is not null)),
        };
        if (token is not null)
        {
            files.Add(("git-credentials", $"https://x-access-token:{token}@github.com\n"));
        }

        var marker = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', files.Select(f => f.Name + '\0' + f.Content)))))[..32];
        var remote = $"~/.deveagentmanager/users/{userId:N}";
        var current = await shell.RunAsync(runner, $"cat \"{Folder(userId)}/version\" 2>/dev/null || true", TimeSpan.FromSeconds(20), cancellationToken);
        if (current.Output.Trim() == marker)
        {
            return;
        }

        foreach (var (name, content) in files)
        {
            await shell.UploadAsync(runner, $"{remote}/{name}", content, "600", cancellationToken);
        }

        if (token is null)
        {
            await shell.RunAsync(runner, $"rm -f \"{Folder(userId)}/git-credentials\"", TimeSpan.FromSeconds(20), cancellationToken);
        }

        await shell.UploadAsync(runner, $"{remote}/version", marker + "\n", "600", cancellationToken);
    }

    /// <summary>
    /// Brings the Runner in line with who has access now: the owner's and members' files and SSH keys are put on it, and
    /// those of everyone else removed.
    /// </summary>
    public async Task SyncAsync(Runner runner, CancellationToken cancellationToken = default)
    {
        var users = await UsersWithAccessAsync(runner.Id, cancellationToken);
        foreach (var (id, _) in users)
        {
            await EnsureAsync(runner, id, cancellationToken);
        }

        var keep = string.Join(' ', users.Select(u => u.Id.ToString("N")));
        (await shell.RunAsync(runner, $$"""
            for folder in "$HOME"/.deveagentmanager/users/*/; do
              [ -d "$folder" ] || continue
              case " {{keep}} " in *" $(basename "$folder") "*) ;; *) rm -rf "$folder" ;; esac
            done
            """, TimeSpan.FromSeconds(30), cancellationToken)).EnsureSuccess("Removing the files of people without access");

        var keys = new List<(string Username, IReadOnlyList<string> Keys)>();
        foreach (var (id, username) in users)
        {
            keys.Add((username, (await settings.GetProfileAsync(id, cancellationToken)).SshKeys));
        }

        var current = await shell.DownloadAsync(runner, "~/.ssh/authorized_keys", cancellationToken);
        var merged = MergeAuthorizedKeys(current, keys);
        if (merged != (current ?? ""))
        {
            await shell.RunAsync(runner, "mkdir -p ~/.ssh && chmod 700 ~/.ssh", TimeSpan.FromSeconds(20), cancellationToken);
            await shell.UploadAsync(runner, "~/.ssh/authorized_keys", merged, "600", cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="SyncAsync"/> on every ready Runner the user has access to, in the background (after they changed their
    /// settings). Runners that can't be reached get the change when the user's next agent or shell starts there, or at
    /// their next setup.
    /// </summary>
    public void SyncForUser(Guid userId) => _ = Task.Run(async () =>
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var runners = await db.Runners.AsNoTracking()
                .Where(r => r.State == RunnerState.Ready && (r.OwnerId == userId || r.Members.Any(m => m.UserId == userId))).ToListAsync();
            foreach (var runner in runners)
            {
                try
                {
                    await SyncAsync(runner);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not update the user files on {Runner}", runner.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update the user files of {User} on their Runners", userId);
        }
    });

    /// <summary>
    /// After a user was deleted: removes their folder and the copies of their harness logins from every ready Runner, not
    /// only the ones they owned or were a member of (an agent puts its owner's files on whichever Runner it runs on), then
    /// brings the Runners they had access to in line (their SSH keys). Runners that can't be reached lose these in
    /// <see cref="RemoveDeletedUsersAsync"/> and the login sweep.
    /// </summary>
    public async Task RemoveDeletedUserAsync(Guid userId, DeletedUser deleted, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var runner in await db.Runners.AsNoTracking().Where(r => r.State == RunnerState.Ready).ToListAsync(cancellationToken))
        {
            try
            {
                (await shell.RunAsync(runner, RemoveScript([userId], deleted.Accounts), TimeSpan.FromSeconds(20), cancellationToken))
                    .EnsureSuccess("Removing a deleted user's files");
                if (deleted.Runners.Contains(runner.Id))
                {
                    await SyncAsync(runner, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not remove the deleted user's files from {Runner}", runner.Name);
            }
        }
    }

    /// <summary>
    /// Removes the folders of users that no longer exist from every ready Runner: those that couldn't be reached when the
    /// user was deleted. Runs with the login sweep, which does the same for their harness logins.
    /// </summary>
    public async Task RemoveDeletedUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var runner in await db.Runners.AsNoTracking().Where(r => r.State == RunnerState.Ready).ToListAsync(cancellationToken))
        {
            try
            {
                var listing = await shell.RunAsync(runner, ListScript, TimeSpan.FromSeconds(20), cancellationToken);
                // Read after listing: a folder is only written for a user that exists, so a new user's is never removed.
                var stale = Stale(listing.Output, (await db.Users.Select(u => u.Id).ToListAsync(cancellationToken)).ToHashSet());
                if (stale.Count > 0)
                {
                    (await shell.RunAsync(runner, RemoveScript(stale, []), TimeSpan.FromSeconds(20), cancellationToken))
                        .EnsureSuccess("Removing deleted users' files");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Could not remove deleted users' files from {Runner}", runner.Name);
            }
        }
    }

    internal const string ListScript = "for d in \"$HOME/.deveagentmanager/users/\"*/; do [ -d \"$d\" ] && basename \"$d\"; done; true";

    /// <summary>The user folders in <see cref="ListScript"/>'s output that belong to nobody in <paramref name="existing"/>.</summary>
    internal static List<Guid> Stale(string listing, IReadOnlySet<Guid> existing) =>
        listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Guid.TryParseExact(name, "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && !existing.Contains(id)).ToList();

    internal static string RemoveScript(IEnumerable<Guid> users, IEnumerable<Guid> accounts) =>
        "rm -rf" + string.Concat(users.Select(u => $" \"{Folder(u)}\"")) + string.Concat(accounts.Select(a => $" \"{AccountService.Folder(a)}\""));

    private async Task<List<(Guid Id, string Username)>> UsersWithAccessAsync(Guid runnerId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.Runners.Where(r => r.Id == runnerId).Select(r => r.OwnerId)
            .Concat(db.RunnerMembers.Where(m => m.RunnerId == runnerId).Select(m => m.UserId)).ToListAsync(cancellationToken);
        return (await db.Users.Where(u => ids.Contains(u.Id)).OrderBy(u => u.Username).Select(u => new { u.Id, u.Username }).ToListAsync(cancellationToken))
            .Select(u => (u.Id, u.Username)).ToList();
    }

    internal static string BuildEnvironment(Guid userId, string username, UserProfile profile, string? token)
    {
        var builder = new StringBuilder($"# Managed by DeveAgentManager: the environment of {username}'s agents and shells\n");
        builder.Append("export DAM_USER=").Append(ShellQuote.Quote(username)).Append('\n');
        builder.Append($"export GIT_CONFIG_GLOBAL=\"{Folder(userId)}/gitconfig\"\n");
        builder.Append(token is null ? "unset GH_TOKEN\n" : $"export GH_TOKEN={ShellQuote.Quote(token)}\n");
        foreach (var variable in profile.EnvironmentVariables)
        {
            var name = variable.Name.Trim();
            if (UserProfile.IsValidVariableName(name) && !UserProfile.ReservedVariables.Contains(name))
            {
                builder.Append("export ").Append(name).Append('=').Append(ShellQuote.Quote(variable.Value)).Append('\n');
            }
        }

        return builder.ToString();
    }

    internal static string BuildGitConfig(Guid userId, string username, UserProfile profile, bool hasToken)
    {
        var builder = new StringBuilder($"# Managed by DeveAgentManager: {username}'s git settings, after the shared ones.\n");
        builder.Append("[include]\n\tpath = ~/.gitconfig\n");
        if (profile.GitUserName is not null || profile.GitUserEmail is not null)
        {
            builder.Append("[user]\n");
            if (profile.GitUserName is { } name)
            {
                builder.Append("\tname = ").Append(GitValue(name)).Append('\n');
            }

            if (profile.GitUserEmail is { } email)
            {
                builder.Append("\temail = ").Append(GitValue(email)).Append('\n');
            }
        }

        // An empty helper clears any from the shared settings: only this user's own token, or none.
        builder.Append("[credential]\n\thelper =\n");
        if (hasToken)
        {
            builder.Append($"\thelper = store --file ~/.deveagentmanager/users/{userId:N}/git-credentials\n");
        }

        return builder.ToString();
    }

    /// <summary>The file with its managed part replaced by these users' keys (or removed, when there are none).</summary>
    internal static string MergeAuthorizedKeys(string? current, IEnumerable<(string Username, IReadOnlyList<string> Keys)> users)
    {
        var lines = new List<string>();
        var inBlock = false;
        foreach (var line in (current ?? "").Split('\n'))
        {
            if (line.Trim() == KeysBegin)
            {
                inBlock = true;
            }
            else if (line.Trim() == KeysEnd)
            {
                inBlock = false;
            }
            else if (!inBlock)
            {
                lines.Add(line);
            }
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var block = users
            .Select(u => (u.Username, Keys: u.Keys.Select(k => k.Trim()).Where(k => k.Length > 0 && UserProfile.SshKeyProblem(k) is null).ToList()))
            .Where(u => u.Keys.Count > 0)
            .SelectMany(u => u.Keys.Prepend($"# {u.Username}"))
            .ToList();
        if (block.Count > 0)
        {
            lines.Add(KeysBegin);
            lines.AddRange(block);
            lines.Add(KeysEnd);
        }

        return lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
    }

    /// <summary>A git config value in quotes, so spaces, # and ; are kept.</summary>
    private static string GitValue(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
