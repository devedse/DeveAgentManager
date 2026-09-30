using System.Globalization;
using System.Text.RegularExpressions;

namespace DeveAgentManager.Core.Agents;

public sealed record ParsedUsageLimit(string Message, DateTime? ResetAt, string? TimeZoneId, bool NativeAutoContinue, string? Detail);

/// <summary>Only recognizes a harness error immediately before its current prompt. Never interprets a low-usage warning as a stop.</summary>
public static class UsageLimitParser
{
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromMilliseconds(100));

    public static ParsedUsageLimit? Parse(string harness, string screen, string? processTimeZone, DateTime nowUtc)
    {
        if (harness is not ("codex" or "claude")) return null;
        screen = screen.Replace('’', '\'').Replace('\u00a0', ' ');
        var lines = screen.Split('\n');
        var prompt = Array.FindLastIndex(lines, line => Match(line, @"^\s*[›❯](?:\s|$)").Success);
        if (prompt < 0) return null;
        var prefix = harness == "codex"
            ? @"^\s*■\s*You've hit your usage limit\b"
            : @"^\s*(?:[⎿■]\s*)?(?:You've hit your (?:(?:session|weekly|monthly|Opus|Sonnet) )?limit\b|Claude (?:AI )?usage limit reached\b|Usage limit reached\b)";
        var start = -1;
        for (var i = Math.Max(0, prompt - 16); i < prompt; i++)
            if (Match(lines[i], prefix).Success) start = i;
        var nativeFooter = harness == "claude" ? lines.Skip(Math.Max(prompt, lines.Length - 12))
            .LastOrDefault(line => Match(line, @"^\s*(?:[⎿■]\s*)?Usage limit (?:reached|reset)\s*·\s*continuing (?:automatically|shortly)").Success) : null;
        if (start < 0 && nativeFooter is null) return null;
        // A later submitted prompt or assistant/tool output means this is an old error in the conversation.
        if (start >= 0 && lines.Skip(start + 1).Take(prompt - start - 1).Any(line => Match(line, @"^\s*[›❯•●◦■⎿✻✢✽✶]").Success)) return null;
        var message = Regex.Replace(start < 0 ? nativeFooter! : string.Join(" ", lines[start..prompt]), @"\s+", " ").Trim();
        if (message.Length > 1800) return null;
        var native = nativeFooter is not null || message.Contains("continuing automatically", StringComparison.OrdinalIgnoreCase);
        var marker = Match(message, @"(?:try again at|will reset at|\bresets|continuing automatically at)\s+(?<reset>.+)$");
        if (!marker.Success) return new(message, null, processTimeZone, native, "No unambiguous reset time was found. Enter a reset time to schedule a continuation.");
        var reset = marker.Groups["reset"].Value.Split('·')[0].Trim().TrimEnd('.');
        var zoneMatch = Match(reset, @"\s*\((?<zone>[^()]+)\)$");
        var zoneId = zoneMatch.Success ? zoneMatch.Groups["zone"].Value.Trim() : processTimeZone;
        if (zoneMatch.Success) reset = reset[..zoneMatch.Index].Trim();
        TimeZoneInfo zone;
        try
        {
            // Abbreviations such as CST are ambiguous. Only UTC/GMT or an actual IANA region is accepted.
            if (zoneId is null || !(zoneId.Contains('/') || zoneId is "UTC" or "Etc/UTC" or "GMT")) throw new TimeZoneNotFoundException();
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new(message, null, zoneId, native, "The timezone is unknown or ambiguous. Enter the reset in UTC.");
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var clock = Match(reset, @"(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)?$");
        if (!clock.Success || (!clock.Groups["m"].Success && !clock.Groups["ap"].Success)) return Unknown();
        var hour = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (minute > 59 || hour > 23) return Unknown();
        if (clock.Groups["ap"].Success)
        {
            if (hour is < 1 or > 12) return Unknown();
            hour = hour % 12 + (clock.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        }
        var dateText = reset[..clock.Index].Trim().TrimEnd(',').Trim();
        dateText = Regex.Replace(dateText, @"\s+at$", "", RegexOptions.IgnoreCase).Trim();
        DateTime date;
        if (dateText.Length == 0 || dateText.Equals("today", StringComparison.OrdinalIgnoreCase)) date = localNow.Date;
        else if (dateText.Equals("tomorrow", StringComparison.OrdinalIgnoreCase)) date = localNow.Date.AddDays(1);
        else
        {
            var formats = new[] { "MMM d, yyyy", "MMMM d, yyyy", "MMM d yyyy", "MMMM d yyyy", "yyyy-MM-dd", "MMM d", "MMMM d" };
            if (!DateTime.TryParseExact(dateText, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return Unknown();
            if (!Regex.IsMatch(dateText, @"\b\d{4}\b"))
            {
                try { date = new DateTime(localNow.Year, date.Month, date.Day); }
                catch (ArgumentOutOfRangeException) { return Unknown(); }
                if (date < localNow.Date.AddDays(-1)) date = date.AddYears(1);
            }
        }
        var localReset = DateTime.SpecifyKind(date.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        if (dateText.Length == 0)
        {
            if (localReset < localNow.AddMinutes(-5)) localReset = localReset.AddDays(1);
            // A time-only message cannot establish a weekly reset or the date of an old error.
            if (localReset > localNow.AddHours(6)) return Unknown();
        }
        if (zone.IsInvalidTime(localReset) || zone.IsAmbiguousTime(localReset))
            return new(message, null, zoneId, native, "This reset falls in a daylight-saving clock change. Enter the exact UTC time.");
        var utc = TimeZoneInfo.ConvertTimeToUtc(localReset, zone);
        if (utc < nowUtc.AddMinutes(-5) || utc > nowUtc.AddDays(32)) return Unknown();
        return new(message, utc, zoneId, native, null);

        ParsedUsageLimit Unknown() => new(message, null, zoneId, native,
            "The reset date or time is uncertain. Enter the exact reset in UTC before scheduling.");
    }
}
