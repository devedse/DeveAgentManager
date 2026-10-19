using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;

namespace DeveAgentManager.Diagnostics;

/// <summary>Owns one manual capture at a time. Idle operation waits on a channel; no diagnostic polling runs.</summary>
public sealed class ManagerCpuTraces : BackgroundService
{
    public const int DurationSeconds = 60;
    private const int Retained = 3;
    private readonly string _root;
    private readonly ICpuTraceRecorder _recorder;
    private readonly ILogger<ManagerCpuTraces> _logger;
    private readonly object _gate = new();
    private readonly List<ManagerCpuTrace> _captures = [];
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(1);
    private ManagerCpuTrace? _active;
    private CancellationTokenSource? _cancel;
    private bool _stopping;

    public ManagerCpuTraces(ManagerOptions options, ICpuTraceRecorder recorder, ILogger<ManagerCpuTraces> logger)
    {
        _root = Path.Combine(options.DataDirectory, "diagnostics", "cpu");
        _recorder = recorder; _logger = logger;
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var id)) continue;
            try
            {
                var saved = JsonSerializer.Deserialize<ManagerCpuTrace>(File.ReadAllText(Path.Combine(folder, "state.json")));
                if (saved is null || saved.Id != id) { Directory.Delete(folder, true); continue; }
                if (saved.State is "recording" or "finishing")
                    saved = saved with { State = "interrupted", Error = "The Manager restarted before this capture finished." };
                if (saved.State == "ready" && !File.Exists(BundlePath(id)))
                    saved = saved with { State = "failed", Bytes = 0, Error = "The capture download is missing." };
                CleanupWorking(id);
                Persist(saved);
                _captures.Add(saved);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            { _logger.LogWarning("Removing an incomplete CPU capture {Capture}: {Failure}", id, ex.GetType().Name); Directory.Delete(folder, true); }
        }
        _captures.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        Prune(Retained);
    }

    public ManagerCpuTraceStatus Status()
    {
        lock (_gate) return new(_active, _captures.ToArray());
    }

    public ManagerCpuTrace Start()
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("The Manager is shutting down.");
            if (_active is not null) throw new InvalidOperationException("A CPU capture is already running.");
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(_root))! : _root);
            if (drive.AvailableFreeSpace < 2 * CpuTraceRecorder.MaxTraceBytes + 1024 * 1024)
                throw new InvalidOperationException("CPU capture needs at least 257 MiB of free disk space.");
            Prune(Retained - 1);
            var capture = new ManagerCpuTrace(Guid.NewGuid(), DateTimeOffset.UtcNow, DurationSeconds, "recording");
            Directory.CreateDirectory(Folder(capture.Id));
            Persist(capture);
            _active = capture; _captures.Insert(0, capture); _cancel = new();
            if (!_queue.Writer.TryWrite(capture.Id)) throw new InvalidOperationException("The capture queue is unavailable.");
            return capture;
        }
    }

    public void Cancel(Guid id)
    {
        lock (_gate)
        {
            if (_active?.Id != id) throw new KeyNotFoundException("No running capture with that ID.");
            _cancel!.Cancel();
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            if (_active?.Id == id) throw new InvalidOperationException("Cancel the running capture before deleting it.");
            var capture = _captures.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("Capture not found.");
            Directory.Delete(Folder(id), true); _captures.Remove(capture);
        }
    }

    public FileStream Download(Guid id)
    {
        lock (_gate)
        {
            if (!_captures.Any(c => c.Id == id && c.State == "ready")) throw new KeyNotFoundException("Completed capture not found.");
            return new FileStream(BundlePath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cancel!.Token);
                try
                {
                    await _recorder.RecordAsync(Folder(id), TimeSpan.FromSeconds(DurationSeconds), linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    Update(id, "finishing");
                    var temporary = BundlePath(id) + ".tmp";
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                        // Packaging happens after sampling; fastest compression keeps the added CPU cost short.
                        foreach (var name in new[] { "manager.nettrace", "process.jsonl", "capture.json" })
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            await using var input = File.OpenRead(Path.Combine(Folder(id), name));
                            await using var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                            await input.CopyToAsync(output, linked.Token);
                        }
                    }
                    linked.Token.ThrowIfCancellationRequested();
                    File.Move(temporary, BundlePath(id));
                    Update(id, "ready", new FileInfo(BundlePath(id)).Length);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                { Update(id, stoppingToken.IsCancellationRequested ? "interrupted" : "canceled"); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CPU capture {Capture} failed", id);
                    Update(id, "failed", error: ex is Microsoft.Diagnostics.NETCore.Client.ServerNotAvailableException
                        ? "Runtime diagnostics are unavailable. Check that .NET diagnostics are enabled and its temporary directory is writable."
                        : "Capture failed or reached its size/time limit. Check the Manager log.");
                }
                finally
                {
                    try { CleanupWorking(id); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { _logger.LogWarning(ex, "Could not remove temporary CPU capture files {Capture}", id); }
                    finally { lock (_gate) { _active = null; _cancel?.Dispose(); _cancel = null; } }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _stopping = true;
        return base.StopAsync(cancellationToken);
    }

    private void Update(Guid id, string state, long bytes = 0, string? error = null)
    {
        lock (_gate)
        {
            var updated = _captures.First(c => c.Id == id) with { State = state, Bytes = bytes, Error = error };
            _captures[_captures.FindIndex(c => c.Id == id)] = updated;
            _active = updated;
            try { Persist(updated); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _logger.LogWarning(ex, "Could not persist CPU capture status {Capture}", id); }
        }
    }
    private void Prune(int keep)
    {
        foreach (var capture in _captures.Skip(keep).ToArray()) { Directory.Delete(Folder(capture.Id), true); _captures.Remove(capture); }
    }
    private string Folder(Guid id) => Path.Combine(_root, id.ToString("N"));
    private string BundlePath(Guid id) => Path.Combine(Folder(id), "capture.zip");
    private void Persist(ManagerCpuTrace capture)
    {
        var path = Path.Combine(Folder(capture.Id), "state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(capture)); File.Move(path + ".tmp", path, true);
    }
    private void CleanupWorking(Guid id)
    {
        foreach (var name in new[] { "manager.nettrace", "process.jsonl", "capture.json", "capture.zip.tmp", "state.json.tmp" })
            File.Delete(Path.Combine(Folder(id), name));
    }
}
