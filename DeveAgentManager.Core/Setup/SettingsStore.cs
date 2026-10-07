using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Setup;

/// <summary>
/// The Manager's own settings, the same for everyone (Manager settings): the default timezone and the shared Setup
/// Profile. Each user's settings are in <see cref="UserSettingsStore"/>.
/// </summary>
public sealed class SettingsStore(
    IDbContextFactory<ManagerDbContext> dbFactory,
    SecretProtector protector,
    ManagerEvents events)
{
    internal const string SetupProfileKey = "setup-profile";

    public async Task<string> GetDefaultTimeZoneAsync(CancellationToken ct = default) =>
        await GetRawAsync("runner-timezone", ct) ?? TimeZoneInfo.Local.Id;

    public Task SaveDefaultTimeZoneAsync(string timeZoneId, CancellationToken ct = default) =>
        SetRawAsync("runner-timezone", Runners.RunnerTimeZones.Validate(timeZoneId), ct);

    public async Task<SetupProfile> GetSetupProfileAsync(CancellationToken cancellationToken = default)
    {
        var json = protector.Unprotect(await GetRawAsync(SetupProfileKey, cancellationToken));
        return json is null ? new SetupProfile() : JsonSerializer.Deserialize<SetupProfile>(json) ?? new SetupProfile();
    }

    public async Task SaveSetupProfileAsync(SetupProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        // What is left without a path is an empty row (Validate refuses content without a path).
        profile.Files = profile.Files.Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
        foreach (var config in profile.Harnesses.Values)
        {
            config.Packages = config.CleanPackages().ToList();
        }

        await SetRawAsync(SetupProfileKey, protector.Protect(JsonSerializer.Serialize(profile)), cancellationToken);
    }

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
