using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Users;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>When timed access ends, from what a request asks for (docs/timed-access.md).</summary>
public sealed class AccessTimeLimitsTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LeftOutKeepsTheCurrentTimeLimit_NeitherRemovesIt()
    {
        var current = Now.AddHours(3);
        Assert.Equal(current, AccessTimeLimits.Resolve(null, current, Now));
        Assert.Null(AccessTimeLimits.Resolve(null, null, Now));
        Assert.Null(AccessTimeLimits.Resolve(new AccessExpiry(), current, Now));
    }

    [Fact]
    public void ADurationCountsFromTheManagersClock_AnEndTimeIsUtc()
    {
        Assert.Equal(Now.AddHours(5), AccessTimeLimits.Resolve(new AccessExpiry(Minutes: 300), null, Now));
        var until = new DateTime(2026, 11, 19, 3, 30, 0, DateTimeKind.Utc);
        Assert.Equal(until, AccessTimeLimits.Resolve(new AccessExpiry(Until: until), null, Now));
        // Without a kind (as some JSON gives it), it is UTC too.
        var unspecified = AccessTimeLimits.Resolve(new AccessExpiry(Until: DateTime.SpecifyKind(until, DateTimeKind.Unspecified)), null, Now);
        Assert.Equal((until, DateTimeKind.Utc), (unspecified!.Value, unspecified.Value.Kind));
    }

    [Theory]
    [InlineData(0.5, null)]
    [InlineData(366 * 24 * 60 + 1.0, null)]
    [InlineData(double.NaN, null)]
    [InlineData(null, -10)]
    [InlineData(null, 0)]
    [InlineData(null, 367 * 24 * 60)]
    [InlineData(60.0, 60)]
    public void RefusesWhatCantBeATimeLimit(double? minutes, int? untilInMinutes)
    {
        var request = new AccessExpiry(untilInMinutes is { } offset ? Now.AddMinutes(offset) : null, minutes);
        Assert.Throws<ArgumentException>(() => AccessTimeLimits.Resolve(request, null, Now));
    }

    [Fact]
    public void TheAuditLogSaysUntilWhen()
    {
        Assert.Equal(" until 2026-11-19 03:30 UTC", AccessTimeLimits.Describe(new DateTime(2026, 11, 19, 3, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("", AccessTimeLimits.Describe(null));
    }
}
