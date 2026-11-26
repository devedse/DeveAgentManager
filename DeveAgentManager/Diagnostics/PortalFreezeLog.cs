using System.Globalization;
using System.Text;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Diagnostics;

/// <summary>
/// Writes the Portal's freeze reports (<see cref="PortalFreezeReport"/>, from freeze-watch.ts) to the Manager's log as
/// warnings, so a freeze that can't be reproduced on demand still leaves a trace of what the page was doing. Each person
/// sends at most <see cref="PerHour"/> an hour. See docs/portal-freezes.md.
/// </summary>
public sealed class PortalFreezeLog
{
    internal const int PerHour = 10;
    internal const int MaxNotes = 50;
    private readonly ILogger<PortalFreezeLog> _logger;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _sent = [];

    public PortalFreezeLog(ILogger<PortalFreezeLog> logger) : this(logger, TimeProvider.System) { }

    internal PortalFreezeLog(ILogger<PortalFreezeLog> logger, TimeProvider clock)
    {
        _logger = logger;
        _clock = clock;
    }

    /// <returns>False when this person already sent <see cref="PerHour"/> reports in the past hour: not written.</returns>
    public bool Record(CurrentUser user, PortalFreezeReport report)
    {
        var now = _clock.GetUtcNow();
        lock (_sent)
        {
            if (!_sent.TryGetValue(user.Id, out var sent)) _sent[user.Id] = sent = new();
            while (sent.TryPeek(out var at) && now - at >= TimeSpan.FromHours(1)) sent.Dequeue();
            if (sent.Count >= PerHour) return false;
            sent.Enqueue(now);
        }

        _logger.LogWarning("{Report}", Describe(user, report));
        return true;
    }

    internal static string Describe(CurrentUser user, PortalFreezeReport report)
    {
        var text = new StringBuilder();
        var seconds = Math.Clamp(report.Seconds, 0, 1e7);
        text.Append(report.Recovered
            ? $"The Portal stopped responding for {Duration(seconds)} in {user.Username}'s browser, then responded again"
            : $"The Portal stopped responding in {user.Username}'s browser: no answer for {Duration(seconds)}, still frozen");
        text.Append($" (page {Clip(report.Page, 200) ?? "unknown"}");
        if (report.OpenForSeconds is { } open) text.Append($", open for {Duration(Math.Clamp(open, 0, 1e9))}");
        text.Append(").");

        var memory = new List<string>();
        if (report.WasmBytes is { } wasm) memory.Add($".NET {Megabytes(wasm)}");
        if (report.JsHeapBytes is { } js) memory.Add($"JavaScript {Megabytes(js)}");
        if (report.Elements is { } elements) memory.Add(string.Create(CultureInfo.InvariantCulture, $"{elements:N0} elements on the page"));
        if (memory.Count > 0) text.Append("\nMemory: ").Append(string.Join(", ", memory)).Append('.');
        if (Clip(report.Browser, 300) is { } browser) text.Append("\nBrowser: ").Append(browser);

        var notes = (report.Notes ?? []).TakeLast(MaxNotes).ToList();
        if (notes.Count > 0)
        {
            text.Append(report.Recovered
                ? "\nWhat happened, in seconds from its last answer before it stopped:"
                : "\nWhat happened before, in seconds before its last answer:");
            foreach (var note in notes)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n{Math.Clamp(note.Seconds, -1e6, 1e6):+0.0;-0.0;0.0} s  {Clip(note.Kind, 40) ?? "?"}");
                if (Clip(note.Detail, 200) is { } detail) text.Append(' ').Append(detail);
            }
        }
        return text.ToString();
    }

    // One line each: what the browser sent can't add lines of its own to the log.
    private static string? Clip(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var line = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length <= maximum ? line : line[..maximum] + "…";
    }

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes) / 1048576.0:N0} MB");

    private static string Duration(double seconds) => seconds switch
    {
        < 60 => string.Create(CultureInfo.InvariantCulture, $"{seconds:0} s"),
        < 3600 => $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s",
        _ => $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min",
    };
}
