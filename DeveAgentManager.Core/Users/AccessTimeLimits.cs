using System.Globalization;
using DeveAgentManager.Contracts;

namespace DeveAgentManager.Core.Users;

/// <summary>
/// Time limits on access to logins, Runners, projects and conversations: when access given for a while ends. The
/// Manager ends it by itself once the time is up, as Remove access would (<see cref="AccessExpiry"/>).
/// </summary>
public static class AccessTimeLimits
{
    /// <summary>The longest time limit: 366 days.</summary>
    public const double MaxMinutes = 366 * 24 * 60;

    /// <summary>
    /// When access ends, for a request that gives or changes it: <paramref name="current"/> (the access's time limit now,
    /// null for a new one) when the request leaves the time limit out, null for no time limit.
    /// </summary>
    public static DateTime? Resolve(AccessExpiry? requested, DateTime? current, DateTime now)
    {
        if (requested is null)
        {
            return current;
        }

        if (requested.Until is not null && requested.Minutes is not null)
        {
            throw new ArgumentException("Choose a duration or an end time, not both.");
        }

        if (requested.Minutes is { } minutes)
        {
            if (!double.IsFinite(minutes) || minutes < 1 || minutes > MaxMinutes)
            {
                throw new ArgumentException("Choose a duration from one minute up to 366 days.");
            }

            return now.AddMinutes(minutes);
        }

        if (requested.Until is { } until)
        {
            until = until.Kind switch
            {
                DateTimeKind.Utc => until,
                DateTimeKind.Local => until.ToUniversalTime(),
                _ => DateTime.SpecifyKind(until, DateTimeKind.Utc),
            };
            if (until <= now.AddSeconds(30))
            {
                throw new ArgumentException("Choose an end time in the future.");
            }

            if (until > now.AddMinutes(MaxMinutes))
            {
                throw new ArgumentException("Choose an end time within 366 days.");
            }

            return until;
        }

        return null;
    }

    /// <summary>For the audit log: " until 2026-11-19 04:30 UTC", or nothing without a time limit.</summary>
    public static string Describe(DateTime? expiresAt) =>
        expiresAt is { } at ? " until " + at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "";
}
