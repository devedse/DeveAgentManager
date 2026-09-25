using System.Text.Json;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Setup;

/// <summary>Settings edited in the Portal. Values set in compose win and are shown read-only.</summary>
public sealed class SettingsStore(
    IDbContextFactory<ManagerDbContext> dbFactory,
    SecretProtector protector,
    ManagerOptions options,
    ManagerEvents events)
{
    private const string SetupProfileKey = "setup-profile";
    private const string GitHubPatKey = "github-pat";

    public bool GitHubPatFromCompose => options.GitHubPat is not null;

    public async Task<SetupProfile> GetSetupProfileAsync(CancellationToken cancellationToken = default)
    {
        var json = protector.Unprotect(await GetRawAsync(SetupProfileKey, cancellationToken));
        var profile = json is null ? new SetupProfile() : JsonSerializer.Deserialize<SetupProfile>(json) ?? new SetupProfile();
        profile.GitUserName ??= options.GitUserName;
        profile.GitUserEmail ??= options.GitUserEmail;
        return profile;
    }

    public async Task SaveSetupProfileAsync(SetupProfile profile, CancellationToken cancellationToken = default)
    {
        profile.EnvironmentVariables = profile.EnvironmentVariables.Where(v => !string.IsNullOrWhiteSpace(v.Name)).ToList();
        profile.Files = profile.Files.Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
        await SetRawAsync(SetupProfileKey, protector.Protect(JsonSerializer.Serialize(profile)), cancellationToken);
    }

    public async Task<string?> GetGitHubPatAsync(CancellationToken cancellationToken = default) =>
        options.GitHubPat ?? protector.Unprotect(await GetRawAsync(GitHubPatKey, cancellationToken));

    public async Task SaveGitHubPatAsync(string? pat, CancellationToken cancellationToken = default) =>
        await SetRawAsync(GitHubPatKey, protector.Protect(string.IsNullOrWhiteSpace(pat) ? null : pat.Trim()), cancellationToken);

    private async Task<string?> GetRawAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await db.Settings.FindAsync([key], cancellationToken))?.Value;
    }

    private async Task SetRawAsync(string key, string? value, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([key], cancellationToken);
        if (entry is null)
        {
            entry = new SettingEntry { Key = key };
            db.Settings.Add(entry);
        }

        entry.Value = value;
        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged));
    }
}
