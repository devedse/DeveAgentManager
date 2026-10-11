using System.Text.Json;
using System.Text.RegularExpressions;
using DeveAgentManager.Contracts;
using Microsoft.Extensions.Logging;
namespace DeveAgentManager.Logging;

/// <summary>A bounded application log independent of the Runner setup logs.</summary>
public sealed class ManagerLogStore
{
    private const int Capacity = 5000;
    private const long FileLimit = 5 * 1024 * 1024;
    private readonly Queue<ManagerLogEntry> _entries = new();
    private readonly string _path;
    private long _id;
    public ManagerLogStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "manager.jsonl");
        foreach (var path in new[] { _path + ".previous", _path })
        {
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                try { if (JsonSerializer.Deserialize<ManagerLogEntry>(line) is { } entry) { _id = Math.Max(_id, entry.Id); Enqueue(entry); } }
                catch (JsonException) { } // A interrupted final line must not prevent startup.
            }
        }
    }
    public void Append(LogLevel level, string category, string message)
    {
        lock (_entries)
        {
            var entry = new ManagerLogEntry(++_id, DateTime.UtcNow, level.ToString(), category, Redact(message));
            Enqueue(entry);
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length >= FileLimit) File.Move(_path, _path + ".previous", true);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // Logging cannot stop the Manager.
        }
    }
    private void Enqueue(ManagerLogEntry entry) { _entries.Enqueue(entry); while (_entries.Count > Capacity) _entries.Dequeue(); }
    public IReadOnlyList<ManagerLogEntry> Read(LogLevel minimum, string? search, int take)
    {
        lock (_entries)
            return _entries.Reverse().Where(e => Enum.TryParse<LogLevel>(e.Level, out var level) && level >= minimum &&
                (string.IsNullOrWhiteSpace(search) || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .Take(Math.Clamp(take, 1, 1000)).ToArray();
    }
    internal static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/-]+=*", "$1[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, "(?i)([\\\"']?(?:password|token|secret|authorization|cookie|api[_-]?key)[\\\"']?\\s*[:=]\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)", "$1[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return text.Length > 8192 ? text[..8192] + "…" : text;
    }
}

public sealed class ManagerLogProvider(ManagerLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Writer(store, categoryName);
    public void Dispose() { }
    private sealed class Writer(ManagerLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += "\n" + exception;
            try { store.Append(level, category, message); }
            catch (RegexMatchTimeoutException) { store.Append(level, category, "[Log omitted: redaction exceeded its time limit]"); }
        }
    }
}
