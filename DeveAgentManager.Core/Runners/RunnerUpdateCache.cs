using System.Collections.Concurrent;
using DeveAgentManager.Contracts;

namespace DeveAgentManager.Core.Runners;

/// <summary>In-memory observations and refresh scheduling, separate from the five-second terminal monitor.</summary>
public sealed class RunnerUpdateCache
{
    private sealed class Entry
    {
        public RunnerUpdatesDto Value = new([]);
        public DateTime SoftwareAttempt, SystemAttempt;
        public long Revision;
        public bool Running, RefreshCatalog;
        public TaskCompletionSource? Finished;
    }
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    public RunnerUpdatesDto? Get(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return null;
        lock (entry) return entry.Value;
    }
    public void Request(Guid id)
    {
        var entry = _entries.GetOrAdd(id, _ => new());
        lock (entry)
        {
            entry.Revision++;
            entry.RefreshCatalog = true;
            entry.SoftwareAttempt = entry.SystemAttempt = default;
            entry.Value = entry.Value with { Checking = true };
        }
    }
    internal sealed record Check(long Revision, bool Software, bool System, bool RefreshCatalog);
    internal Check? Begin(Guid id, DateTime now)
    {
        var entry = _entries.GetOrAdd(id, _ => new());
        lock (entry)
        {
            if (entry.Running) return null;
            var software = now - entry.SoftwareAttempt >= (entry.Value.SoftwareError is null ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(10));
            var system = now - entry.SystemAttempt >= (entry.Value.SystemError is null ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(10));
            if (!software && !system) return null;
            entry.Running = true;
            entry.Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Value = entry.Value with { Checking = true };
            return new(entry.Revision, software, system, entry.RefreshCatalog);
        }
    }
    internal void Publish(Guid id, Check check, RunnerUpdatesDto value)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        lock (entry)
            if (entry.Revision == check.Revision) entry.Value = value with { Checking = true };
    }
    internal void Complete(Guid id, Check check, RunnerUpdatesDto value, DateTime now)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        lock (entry)
        {
            entry.Running = false;
            entry.Finished?.TrySetResult();
            // A setup/update or Check now happened during the observation. Discard it and check again.
            if (entry.Revision != check.Revision) return;
            entry.RefreshCatalog = false;
            if (check.Software) entry.SoftwareAttempt = now;
            if (check.System) entry.SystemAttempt = now;
            entry.Value = value with { Checking = false };
        }
    }
    internal Task WaitForCheckAsync(Guid id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry)) return Task.CompletedTask;
        lock (entry) return entry.Running ? entry.Finished!.Task.WaitAsync(ct) : Task.CompletedTask;
    }
    internal void RemoveMissing(HashSet<Guid> ids)
    {
        foreach (var id in _entries.Keys) if (!ids.Contains(id)) _entries.TryRemove(id, out _);
    }
}
