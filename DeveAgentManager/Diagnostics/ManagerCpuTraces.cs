using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Api;
using Microsoft.AspNetCore.SignalR;

namespace DeveAgentManager.Diagnostics;

/// <summary>Owns startup and manual captures. Idle operation waits on a channel.</summary>
public sealed class ManagerCpuTraces : BackgroundService
{
    public const int DurationSeconds = 60;
    private const int Retained = 3;
    private readonly string _root;
    private readonly ICpuTraceRecorder _recorder;
    private readonly ILogger<ManagerCpuTraces> _logger;
    private readonly CpuTraceOptions _options;
    private readonly IHubContext<LiveUpdatesHub>? _hub;
    private readonly object _gate = new();
    private readonly List<ManagerCpuTrace> _captures = [];
    private readonly Channel<(Guid Id, CpuTracePlan Plan)> _queue = Channel.CreateBounded<(Guid, CpuTracePlan)>(1);
    private ManagerCpuTrace? _active;
    private CancellationTokenSource? _cancel;
    private bool _stopping;
    private bool _started;
    private string? _startupError;

    public ManagerCpuTraces(ManagerOptions options, ICpuTraceRecorder recorder, ILogger<ManagerCpuTraces> logger,
        CpuTraceOptions? captureOptions = null, IHubContext<LiveUpdatesHub>? hub = null)
    {
        _root = Path.Combine(options.DataDirectory, "diagnostics", "cpu");
        _recorder = recorder; _logger = logger;
        _options = captureOptions ?? new(); _hub = hub;
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
        lock (_gate) return new(_active, _captures.ToArray(), DateTimeOffset.UtcNow, _startupError);
    }

    public ManagerCpuTrace Start() => Start(CpuTracePlan.Manual, "manual");

    private ManagerCpuTrace Start(CpuTracePlan plan, string trigger)
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("The Manager is shutting down.");
            if (_active is not null) throw new InvalidOperationException("A CPU capture is already running.");
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(_root))! : _root);
            if (drive.AvailableFreeSpace < 2 * CpuTraceRecorder.MaxTraceBytes + 1024 * 1024)
                throw new InvalidOperationException("CPU capture needs at least 257 MiB of free disk space.");
            Prune(Retained - 1);
            var now = DateTimeOffset.UtcNow;
            var capture = new ManagerCpuTrace(Guid.NewGuid(), now, plan.DurationSeconds, "recording",
                Trigger: trigger, Phase: "baseline-before", PhaseStartedAt: now, PhaseSeconds: (int)plan.Before.TotalSeconds);
            Directory.CreateDirectory(Folder(capture.Id));
            Persist(capture);
            _active = capture; _captures.Insert(0, capture); _cancel = new();
            if (!_queue.Writer.TryWrite((capture.Id, plan))) throw new InvalidOperationException("The capture queue is unavailable.");
            _logger.LogInformation("Starting {Trigger} CPU capture {Capture} for {Seconds} seconds", trigger, capture.Id, plan.DurationSeconds);
            Publish();
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
            Publish();
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

    // Program starts us before database migration/backfill. The host's later StartAsync must not start a second worker.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
            if (_options.CaptureOnStartup)
            {
                try { Start(CpuTracePlan.Startup, "startup"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _startupError = "The startup CPU capture could not start. Check the Manager log.";
                    _logger.LogWarning(ex, "Could not start the automatic startup CPU capture");
                }
            }
            return base.StartAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (id, plan) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cancel!.Token);
                try
                {
                    await _recorder.RecordAsync(Folder(id), plan, (phase, seconds) => UpdatePhase(id, phase, seconds), linked.Token);
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
                    finally { lock (_gate) { _active = null; _cancel?.Dispose(); _cancel = null; Publish(); } }
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
            Publish();
        }
    }
    private void UpdatePhase(Guid id, string phase, int seconds)
    {
        lock (_gate)
        {
            var index = _captures.FindIndex(c => c.Id == id);
            var updated = _captures[index] with { Phase = phase, PhaseStartedAt = DateTimeOffset.UtcNow, PhaseSeconds = seconds };
            _captures[index] = updated; _active = updated;
            // Persist phase boundaries only; progress comes from timestamps, without a server-side polling loop.
            try { Persist(updated); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _logger.LogWarning(ex, "Could not persist CPU capture phase {Capture}", id); }
            _logger.LogInformation("CPU capture {Capture}: {Phase}", id, phase);
            Publish();
        }
    }
    private void Publish()
    {
        if (_hub is null) return;
        _ = _hub.Clients.Group(LiveUpdatesHub.CpuCaptureGroup).SendAsync(LiveHub.CpuTraceChanged)
            .ContinueWith(t => _logger.LogDebug(t.Exception, "Could not send CPU capture status"), TaskContinuationOptions.OnlyOnFaulted);
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
