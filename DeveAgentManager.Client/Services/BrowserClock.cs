using System.Globalization;
using Microsoft.JSInterop;

namespace DeveAgentManager.Client.Services;

/// <summary>Browser timezone discovery stays at the interop boundary; date validation and conversion use .NET.</summary>
public sealed class BrowserClock(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;
    private async Task<TimeZoneInfo> ZoneAsync()
    {
        var module = await (_module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/schedule-time.js").AsTask());
        // Read each time so changing the device's timezone does not require reloading the Portal.
        return TimeZoneInfo.FindSystemTimeZoneById(await module.InvokeAsync<string>("timeZone"));
    }

    public sealed record LocalParts(string Date, string Time, string Zone, string Offset);
    public sealed record Conversion(string? Utc, string? Error);

    public async Task<LocalParts> LocalPartsAsync(DateTime utc) => Parts(utc, await ZoneAsync());
    public async Task<Conversion> ToUtcAsync(string date, string time) => Convert(date, time, await ZoneAsync());
    public async Task<string> DescribeAsync(DateTime utc)
    {
        var parts = await LocalPartsAsync(utc);
        return $"{parts.Date} {parts.Time} ({parts.Zone})";
    }

    public static LocalParts Parts(DateTime utc, TimeZoneInfo zone)
    {
        var value = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return new(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value.ToString("HH:mm", CultureInfo.InvariantCulture), zone.Id,
            $"{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}");
    }

    public static Conversion Convert(string date, string time, TimeZoneInfo zone)
    {
        if (!DateTime.TryParseExact($"{date} {time}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new(null, "Choose a valid date and time.");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            return new(null, "This local time does not exist, possibly because the clocks change. Choose another time or use a duration.");
        if (zone.IsAmbiguousTime(local))
            return new(null, "This local time occurs twice because the clocks change. Use a duration to select an exact reset.");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
            try { await (await _module).DisposeAsync(); }
            catch (JSDisconnectedException) { }
    }
}
