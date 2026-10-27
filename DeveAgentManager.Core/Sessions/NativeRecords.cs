using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Reads a saved native history one record at a time, for histories too large to hold: conversions, installs and exports.</summary>
internal static class NativeRecords
{
    /// <summary>
    /// The history's entries in native order. Each entry's content is only valid until the next one is read; the cursor keeps
    /// compact metadata (the tree, short snippets and the sources' header records), as the synchronization index does.
    /// </summary>
    public static IEnumerable<DeveEntry> Read(Func<TextReader> read, ISessionFormat format, SessionReadCursor cursor, CancellationToken ct)
    {
        using var reader = read();
        foreach (var line in JsonLines.Parse(reader))
        {
            ct.ThrowIfCancellationRequested();
            var start = cursor.Session.Entries.Count;
            format.AppendLines(cursor, [line]);
            for (var i = start; i < cursor.Session.Entries.Count; i++) yield return cursor.Session.Entries[i];
            cursor.Compact(start, keepHeaders: true);
        }
    }

    /// <summary>The history's metadata (sources with their header records, times, folder and tree), in one pass.</summary>
    public static DeveSession Metadata(Func<TextReader> read, ISessionFormat format, CancellationToken ct)
    {
        var cursor = new SessionReadCursor();
        foreach (var _ in Read(read, format, cursor, ct)) { }
        return cursor.Session;
    }
}
