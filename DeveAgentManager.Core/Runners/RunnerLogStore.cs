using System.Collections.Concurrent;
using System.Globalization;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Events;

namespace DeveAgentManager.Core.Runners;

/// <summary>Keeps the setup log of every Runner in /data/logs and the last lines in memory for the Portal.</summary>
public sealed class RunnerLogStore(ManagerOptions options, ManagerEvents events)
{
    private const int MaxLinesInMemory = 3000;

    private readonly ConcurrentDictionary<Guid, Buffer> _buffers = new();

    public IRunnerLog For(Guid runnerId) => new Writer(this, runnerId);

    public IReadOnlyList<RunnerLogLine> GetRecent(Guid runnerId)
    {
        var buffer = GetBuffer(runnerId);
        lock (buffer)
        {
            return buffer.Lines.ToArray();
        }
    }

    public void Delete(Guid runnerId)
    {
        _buffers.TryRemove(runnerId, out _);
        var path = PathFor(runnerId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void Append(Guid runnerId, RunnerLogLevel level, string text)
    {
        var line = new RunnerLogLine(DateTime.UtcNow, level, text.TrimEnd());
        var buffer = GetBuffer(runnerId);
        lock (buffer)
        {
            buffer.Lines.Enqueue(line);
            while (buffer.Lines.Count > MaxLinesInMemory)
            {
                buffer.Lines.Dequeue();
            }

            Directory.CreateDirectory(options.LogsDirectory);
            File.AppendAllText(PathFor(runnerId), Format(line) + "\n");
        }

        events.PublishLog(runnerId, line);
    }

    private Buffer GetBuffer(Guid runnerId) => _buffers.GetOrAdd(runnerId, id =>
    {
        var buffer = new Buffer();
        var path = PathFor(id);
        if (File.Exists(path))
        {
            foreach (var raw in File.ReadLines(path).TakeLast(MaxLinesInMemory))
            {
                if (TryParse(raw, out var line))
                {
                    buffer.Lines.Enqueue(line);
                }
            }
        }

        return buffer;
    });

    private string PathFor(Guid runnerId) => Path.Combine(options.LogsDirectory, $"runner-{runnerId:N}.log");

    private static string Format(RunnerLogLine line) =>
        $"{line.Timestamp.ToString("O", CultureInfo.InvariantCulture)}|{line.Level}|{line.Text.Replace('\n', ' ')}";

    private static bool TryParse(string raw, out RunnerLogLine line)
    {
        line = null!;
        var parts = raw.Split('|', 3);
        if (parts.Length != 3
            || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            || !Enum.TryParse<RunnerLogLevel>(parts[1], out var level))
        {
            return false;
        }

        line = new RunnerLogLine(timestamp, level, parts[2]);
        return true;
    }

    private sealed class Buffer
    {
        public Queue<RunnerLogLine> Lines { get; } = new();
    }

    private sealed class Writer(RunnerLogStore store, Guid runnerId) : IRunnerLog
    {
        public void Write(RunnerLogLevel level, string text) => store.Append(runnerId, level, text);
    }
}
