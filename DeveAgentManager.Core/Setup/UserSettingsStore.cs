using System.Text.Json;
using System.Text.Json.Nodes;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Setup;

/// <summary>
/// Each user's own settings (My settings): their <see cref="UserProfile"/> and GitHub token, stored encrypted per user
/// and only set in the Portal.
/// </summary>
public sealed class UserSettingsStore(
    IDbContextFactory<ManagerDbContext> dbFactory,
    SecretProtector protector,
    ManagerEvents events)
{
    private const string ProfileKey = "profile";
    private const string GitHubTokenKey = "github-token";
    private const string ScreenAwakeKey = "screen-awake";
    private const string CodexVoiceKey = "codex-terminal-voice";
    private const string ModelsKey = "models";

    /// <summary>The user's own default model and effort per harness id; harnesses they didn't change aren't listed.</summary>
    public async Task<Dictionary<string, ModelDefault>> GetModelDefaultsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var json = protector.Unprotect(await GetRawAsync(userId, ModelsKey, cancellationToken));
        return json is null ? [] : JsonSerializer.Deserialize<Dictionary<string, ModelDefault>>(json) ?? [];
    }

    public async Task SaveModelDefaultsAsync(Guid userId, IReadOnlyDictionary<string, ModelDefault> defaults, CancellationToken cancellationToken = default)
    {
        var cleaned = defaults.Select(d => (d.Key, Value: d.Value.Cleaned())).Where(d => !d.Value.IsEmpty).ToDictionary(d => d.Key, d => d.Value);
        await SetRawAsync(userId, ModelsKey, cleaned.Count == 0 ? null : protector.Protect(JsonSerializer.Serialize(cleaned)), cancellationToken);
    }

    public async Task<bool> GetScreenAwakeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        protector.Unprotect(await GetRawAsync(userId, ScreenAwakeKey, cancellationToken)) == "true";

    public Task SaveScreenAwakeAsync(Guid userId, bool enabled, CancellationToken cancellationToken = default) =>
        SetRawAsync(userId, ScreenAwakeKey, protector.Protect(enabled ? "true" : "false"), cancellationToken);

    public async Task<bool> GetCodexTerminalVoiceAsync(Guid userId, CancellationToken ct = default) =>
        protector.Unprotect(await GetRawAsync(userId, CodexVoiceKey, ct)) == "true";

    public Task SaveCodexTerminalVoiceAsync(Guid userId, bool enabled, CancellationToken ct = default) =>
        SetRawAsync(userId, CodexVoiceKey, protector.Protect(enabled ? "true" : "false"), ct);

    public async Task<UserProfile> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var json = protector.Unprotect(await GetRawAsync(userId, ProfileKey, cancellationToken));
        return json is null ? new UserProfile() : JsonSerializer.Deserialize<UserProfile>(json) ?? new UserProfile();
    }

    public async Task SaveProfileAsync(Guid userId, UserProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        await SetRawAsync(userId, ProfileKey, protector.Protect(JsonSerializer.Serialize(profile.Cleaned())), cancellationToken);
    }

    public async Task<string?> GetGitHubTokenAsync(Guid userId, CancellationToken cancellationToken = default) =>
        protector.Unprotect(await GetRawAsync(userId, GitHubTokenKey, cancellationToken));

    public async Task SaveGitHubTokenAsync(Guid userId, string? token, CancellationToken cancellationToken = default)
    {
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
