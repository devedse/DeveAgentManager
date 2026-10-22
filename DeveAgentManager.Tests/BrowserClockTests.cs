using DeveAgentManager.Client.Services;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class BrowserClockTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    [Theory]
    [InlineData("2026-09-30T22:30:00Z", "2026-10-01", "00:30", "+02:00")]
    [InlineData("2026-10-25T01:00:00Z", "2026-10-25", "02:00", "+01:00")]
    [InlineData("2026-10-25T00:00:00Z", "2026-10-25", "02:00", "+02:00")]
    public void LocalPartsPreserveOffset(string utc, string date, string time, string offset) =>
        Assert.Equal(new BrowserClock.LocalParts(date, time, Zone.Id, offset), BrowserClock.Parts(DateTime.Parse(utc).ToUniversalTime(), Zone));

    [Theory]
    [InlineData("2026-10-01", "00:30", "2026-09-30T22:30:00.000Z")]
    [InlineData("2026-12-01", "00:30", "2026-11-30T23:30:00.000Z")]
    [InlineData("2026-10-25", "03:30", "2026-10-25T02:30:00.000Z")]
    public void ConvertsLocalTime(string date, string time, string utc) =>
        Assert.Equal(new BrowserClock.Conversion(utc, null), BrowserClock.Convert(date, time, Zone));

    [Theory]
    [InlineData("2026-03-29", "02:30", "does not exist")]
    [InlineData("2026-10-25", "02:30", "occurs twice")]
    [InlineData("2026-02-30", "10:00", "valid date")]
    [InlineData("2026-10-01", "25:00", "valid date")]
    [InlineData("x", "y", "valid date")]
    public void RejectsInvalidOrAmbiguousTime(string date, string time, string error) =>
        Assert.Contains(error, BrowserClock.Convert(date, time, Zone).Error);
}
