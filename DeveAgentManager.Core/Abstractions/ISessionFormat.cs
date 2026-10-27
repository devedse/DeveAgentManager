using DeveAgentManager.Core.Sessions;
using System.Text.Json.Nodes;

namespace DeveAgentManager.Core.Abstractions;

/// <summary>Reads and writes a harness's own session files, converting to and from <see cref="DeveSession"/>.</summary>
public interface ISessionFormat
{
    string HarnessId { get; }

    /// <summary>
    /// Bash that prints "&lt;size-in-bytes&gt; &lt;path&gt;" of the harness's session file for this session id, or nothing
    /// when it doesn't exist (yet). Incremental formats also leave the located path in shell variable f,
    /// allowing the caller to capture inode and nanosecond modification/change timestamps in the same batch.
    /// </summary>
    string LocateCommand(string sessionId);

    /// <summary>Where to write a session file so the harness finds it for this id in this working directory.</summary>
    string GetPath(string sessionId, string workingDirectory, DateTime createdAt);

    DeveSession Read(string content);

    // Implementations may opt into streaming and incremental metadata parsing.
    DeveSession Read(TextReader reader) => Read(reader.ReadToEnd());
    bool SupportsIncrementalRead => false;
    /// <summary>
    /// True when an entry is complete once its native line is appended: later lines never change it. A conversion can
    /// then read the history one record at a time instead of holding it whole.
    /// </summary>
    bool AppendsCompleteEntries => false;
    /// <summary>False for a companion stream that needs exact byte synchronization without a second conversation index.</summary>
    bool IndexNativeRecords => true;
    /// <summary>Replay files may change after the latest log event and must be checked on otherwise idle passes.</summary>
    bool HasCompanionFiles => false;
    void AppendLines(SessionReadCursor cursor, IEnumerable<JsonObject> lines) => throw new NotSupportedException();

    /// <summary>
    /// Writes the session in the harness's own format, under <paramref name="sessionId"/> and for
    /// <paramref name="workingDirectory"/>. Entries that came from this harness are written back unchanged.
    /// </summary>
    string Write(DeveSession session, string sessionId, string workingDirectory);

    /// <summary>
    /// <see cref="Write(DeveSession, string, string)"/> to <paramref name="output"/>. Line formats write line by line, so
    /// a large conversation is never held as one string as well.
    /// </summary>
    void Write(DeveSession session, string sessionId, string workingDirectory, TextWriter output) => output.Write(Write(session, sessionId, workingDirectory));

    /// <summary>Small metadata and immutable replay checkpoints beside the authoritative log.</summary>
    IReadOnlyList<SessionCompanionFile> CompanionFiles(DeveSession metadata) => [];

    /// <summary>Companions to restore before publishing a copied log. Contents come from the saved native source header.</summary>
    IReadOnlyDictionary<string, string> WriteCompanionFiles(DeveSession session, string sessionId, string workingDirectory) => new Dictionary<string, string>();

    /// <summary>Applies saved sidecar metadata without interpreting it as conversation instructions.</summary>
    void ApplyCompanionFiles(DeveSession session, IReadOnlyDictionary<string, string> contents) { }

    string Export(DeveSession session, string sessionId, string workingDirectory) => Write(session, sessionId, workingDirectory);

    /// <summary><see cref="Export(DeveSession, string, string)"/> to <paramref name="output"/>.</summary>
    void Export(DeveSession session, string sessionId, string workingDirectory, TextWriter output) => output.Write(Export(session, sessionId, workingDirectory));
    string ExportExtension => "jsonl";
    string ExportContentType => "application/x-ndjson";
}

public sealed record SessionCompanionFile(string Path, bool Required = false, bool Immutable = false, bool Incremental = false);
