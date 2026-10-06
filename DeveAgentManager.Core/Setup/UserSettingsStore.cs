using System.Text.Json;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Setup;

/// <summary>
/// Each user's own settings (My settings): their <see cref="UserProfile"/> and GitHub token, stored encrypted per user.
/// GITHUB_PAT, GIT_USER_NAME and GIT_USER_EMAIL in the compose file are the settings of the user named ADMIN_USERNAME:
/// they win over what that user enters, and are shown read-only.
/// </summary>
public sealed class UserSettingsStore(
    IDbContextFactory<ManagerDbContext> dbFactory,
    SecretProtector protector,
    ManagerOptions options,
    ManagerEvents events)
{
    private const string ProfileKey = "profile";
    private const string GitHubTokenKey = "github-token";

    /// <summary>What the compose file sets for this user (empty for everyone but the ADMIN_USERNAME user).</summary>
    public async Task<ComposeUserSettings> ComposeSettingsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var username = await db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(cancellationToken);
        return string.Equals(username, options.AdminUsername, StringComparison.OrdinalIgnoreCase)
            ? new ComposeUserSettings(options.GitUserName, options.GitUserEmail, options.GitHubPat)
            : ComposeUserSettings.None;
    }

    public async Task<UserProfile> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var json = protector.Unprotect(await GetRawAsync(userId, ProfileKey, cancellationToken));
        var profile = json is null ? new UserProfile() : JsonSerializer.Deserialize<UserProfile>(json) ?? new UserProfile();
        var compose = await ComposeSettingsAsync(userId, cancellationToken);
        profile.GitUserName = compose.GitUserName ?? profile.GitUserName;
        profile.GitUserEmail = compose.GitUserEmail ?? profile.GitUserEmail;
        return profile;
    }

    public async Task SaveProfileAsync(Guid userId, UserProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        await SetRawAsync(userId, ProfileKey, protector.Protect(JsonSerializer.Serialize(profile.Cleaned())), cancellationToken);
    }

    public async Task<string?> GetGitHubTokenAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await ComposeSettingsAsync(userId, cancellationToken)).GitHubToken ?? protector.Unprotect(await GetRawAsync(userId, GitHubTokenKey, cancellationToken));

    public async Task SaveGitHubTokenAsync(Guid userId, string? token, CancellationToken cancellationToken = default)
    {
        if ((await ComposeSettingsAsync(userId, cancellationToken)).GitHubToken is not null)
        {
            throw new InvalidOperationException("Your token is set with GITHUB_PAT in the compose file.");
        }

        var clean = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        if (clean is not null && (clean.Any(char.IsWhiteSpace) || clean.Contains('@') || clean.Contains(':')))
        {
            throw new ArgumentException("That doesn't look like a GitHub token (ghp_…, github_pat_…).");
        }

        await SetRawAsync(userId, GitHubTokenKey, protector.Protect(clean), cancellationToken);
    }

    /// <summary>
    /// Before users existed, the git identity, environment variables and GitHub token were Manager settings. They become
    /// the settings of the first user (who owns everything from that time), once.
    /// </summary>
    public async Task MoveSettingsFromBeforeUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(u => u.Id == User.BootstrapId, cancellationToken))
        {
            return;
        }

        var shared = await db.Settings.FindAsync([SettingsStore.SetupProfileKey], cancellationToken);
        if (protector.Unprotect(shared?.Value) is { } json && JsonNode.Parse(json) is JsonObject legacy
            && (legacy.ContainsKey("GitUserName") || legacy.ContainsKey("GitUserEmail") || legacy.ContainsKey("EnvironmentVariables")))
        {
            if (await db.UserSettings.FindAsync([User.BootstrapId, ProfileKey], cancellationToken) is null)
            {
                var profile = new UserProfile
                {
                    GitUserName = (string?)legacy["GitUserName"],
                    GitUserEmail = (string?)legacy["GitUserEmail"],
                    EnvironmentVariables = legacy["EnvironmentVariables"]?.Deserialize<List<SetupEnvironmentVariable>>() ?? [],
                };
                db.UserSettings.Add(new UserSetting
                {
                    UserId = User.BootstrapId,
                    Key = ProfileKey,
                    Value = protector.Protect(JsonSerializer.Serialize(profile.Cleaned())),
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            legacy.Remove("GitUserName");
            legacy.Remove("GitUserEmail");
            legacy.Remove("EnvironmentVariables");
            shared!.Value = protector.Protect(legacy.ToJsonString());
            shared.UpdatedAt = DateTime.UtcNow;
        }

        if (await db.Settings.FindAsync([LegacyGitHubTokenKey], cancellationToken) is { } token)
        {
            if (token.Value is not null && await db.UserSettings.FindAsync([User.BootstrapId, GitHubTokenKey], cancellationToken) is null)
            {
                db.UserSettings.Add(new UserSetting { UserId = User.BootstrapId, Key = GitHubTokenKey, Value = token.Value, UpdatedAt = DateTime.UtcNow });
            }

            db.Settings.Remove(token);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Where the GitHub token was a Manager setting, before users existed (same encryption).</summary>
    private const string LegacyGitHubTokenKey = "github-pat";

    private async Task<string?> GetRawAsync(Guid userId, string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await db.UserSettings.FindAsync([userId, key], cancellationToken))?.Value;
    }

    private async Task SetRawAsync(Guid userId, string key, string? value, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.UserSettings.FindAsync([userId, key], cancellationToken);
        if (entry is null)
        {
            entry = new UserSetting { UserId = userId, Key = key };
            db.UserSettings.Add(entry);
        }

        entry.Value = value;
        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged, UserId: userId));
    }
}

/// <summary>What the compose file sets for a user: GIT_USER_NAME, GIT_USER_EMAIL and GITHUB_PAT for the ADMIN_USERNAME user.</summary>
public sealed record ComposeUserSettings(string? GitUserName, string? GitUserEmail, string? GitHubToken)
{
    public static readonly ComposeUserSettings None = new(null, null, null);
}
