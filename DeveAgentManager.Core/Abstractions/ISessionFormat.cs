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
    void AppendLines(SessionReadCursor cursor, IEnumerable<JsonObject> lines) => throw new NotSupportedException();

    /// <summary>
    /// Writes the session in the harness's own format, under <paramref name="sessionId"/> and for
    /// <paramref name="workingDirectory"/>. Entries that came from this harness are written back unchanged.
    /// </summary>
    string Write(DeveSession session, string sessionId, string workingDirectory);
}
