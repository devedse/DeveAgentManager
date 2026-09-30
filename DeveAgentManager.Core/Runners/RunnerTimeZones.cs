using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Setup;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Runners;

public sealed class RunnerTimeZones(IDbContextFactory<ManagerDbContext> factory, SettingsStore settings, ManagerEvents events)
{
    public static IReadOnlyList<string> Ids { get; } = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Append("UTC").Append(TimeZoneInfo.Local.Id).Distinct().Order().ToArray();

    public static string Validate(string id)
    {
        id = id.Trim();
        try
        {
            if (id.Contains(' ') || id.Contains("..", StringComparison.Ordinal) || id.StartsWith('/')) throw new TimeZoneNotFoundException();
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("Choose a valid IANA timezone, such as Europe/Amsterdam or UTC."); }
        return id;
    }

    public async Task<TimeZoneConfigDto> GetAsync(Guid? runnerId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var runner = runnerId is null ? null : await db.Runners.FindAsync([runnerId.Value], ct) ?? throw new KeyNotFoundException("Runner not found.");
        return new(await settings.GetDefaultTimeZoneAsync(ct), runner?.TimeZoneId);
    }

    public async Task SaveAsync(Guid? runnerId, string? id, CancellationToken ct)
    {
        id = string.IsNullOrWhiteSpace(id) ? null : Validate(id);
        if (runnerId is null)
        {
            await settings.SaveDefaultTimeZoneAsync(id ?? "UTC", ct);
            return;
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var runner = await db.Runners.FindAsync([runnerId.Value], ct) ?? throw new KeyNotFoundException("Runner not found.");
        runner.TimeZoneId = id;
        await db.SaveChangesAsync(ct);
        events.RunnerChanged(runner.Id);
    }
}
