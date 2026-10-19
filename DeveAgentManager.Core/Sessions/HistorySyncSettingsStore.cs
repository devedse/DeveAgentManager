using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Manager-wide history verification policy, read once per sync so saved changes apply without restarting.</summary>
public sealed class HistorySyncSettingsStore(IDbContextFactory<ManagerDbContext> factory, ManagerEvents events)
{
    private const string Key = "history-sync-mode";

    public async Task<HistorySyncSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var saved = await db.Settings.FindAsync([Key], ct);
        if (saved?.Value is null) return new();
        if (!Enum.TryParse<HistorySyncMode>(saved.Value, out var mode) || !Enum.IsDefined(mode))
            throw new IOException("The saved conversation verification setting is invalid.");
        return new(mode);
    }

    public async Task SaveAsync(HistorySyncSettings settings, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(settings.Mode)) throw new ArgumentException("Choose Sampled, Shadow or Full verification.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var saved = await db.Settings.FindAsync([Key], ct);
        if (saved is null) { saved = new SettingEntry { Key = Key }; db.Settings.Add(saved); }
        saved.Value = settings.Mode.ToString();
        saved.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        events.Publish(new ManagerEvent(ManagerEventKind.SettingsChanged));
    }
}
