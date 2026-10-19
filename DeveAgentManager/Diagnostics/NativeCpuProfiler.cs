using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Diagnostics.Profiles;
using Google.Protobuf;

namespace DeveAgentManager.Diagnostics;

/// <summary>Local, bounded receiver for CPU-clock samples from the pinned Pyroscope native profiler.</summary>
public sealed class NativeCpuProfiler : IDisposable
{
    private readonly HttpListener? _listener;
    private readonly Task? _receiver;
    private readonly string? _token;
    private readonly object _gate = new();
    private Capture? _capture;
    public bool Available { get; }
    public string? Version { get; }
    public string? UnavailableReason { get; }
    public const int SamplingIntervalMilliseconds = 19;
    public const int FlushSeconds = 6;
    internal const string NoSamplesMessage = "No CPU-clock samples were received. This capture cannot provide a method CPU ranking.";

    public NativeCpuProfiler()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("CPU_PROFILER_ENABLED") != "true")
        { UnavailableReason = "The native CPU profiler is not enabled in this host. The portable recorder includes waiting threads."; return; }
        try
        {
            _token = Environment.GetEnvironmentVariable("CPU_PROFILER_TOKEN");
            if (_token is null || _token.Length != 32) throw new InvalidOperationException("Missing local profiler receiver key.");
            var ready = Native.GetReadyPointer();
            if (ready == IntPtr.Zero || Marshal.ReadByte(ready) == 0) throw new InvalidOperationException("The CLR did not load the native profiler.");
            Version = Marshal.PtrToStringUTF8(Native.GetVersion());
            // Do not sample idle operation or the baselines. Loading symbols alone does not enable capture.
            Native.SetCPUTrackingEnabled(false);
            _listener = new();
            _listener.Prefixes.Add("http://127.0.0.1:4042/");
            _listener.Start();
            Available = true;
            _receiver = ReceiveAsync();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or InvalidOperationException or HttpListenerException)
        {
            _listener?.Close();
            UnavailableReason = "The native CPU profiler could not initialize (" + ex.GetType().Name + "). The portable recorder includes waiting threads.";
        }
    }

    public void Begin(string folder)
    {
        if (!Available) throw new InvalidOperationException(UnavailableReason);
        lock (_gate)
        {
            if (_capture is not null) throw new InvalidOperationException("CPU sampling is already active.");
            Directory.CreateDirectory(Path.Combine(folder, "profiles"));
            _capture = new(folder, DateTimeOffset.UtcNow);
            try { Native.SetCPUTrackingEnabled(true); }
            catch { _capture = null; throw; }
        }
    }

    public async Task EndAsync(CancellationToken ct)
    {
        // Stop timers before draining the uploader. Cancellation never leaves a profiler attached.
        try
        {
            Native.SetCPUTrackingEnabled(false);
            await Task.Delay(TimeSpan.FromSeconds(FlushSeconds), ct);
        }
        finally
        {
            lock (_gate)
            {
                if (_capture is { Error: { } error }) { _capture = null; throw new IOException(error); }
                _capture = null;
            }
        }
    }

    private async Task ReceiveAsync()
    {
        while (_listener!.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
            try
            {
                var request = context.Request;
                if (request.HttpMethod != "POST" || request.RemoteEndPoint?.Address is not { } remote || !IPAddress.IsLoopback(remote)
                    || request.Url?.AbsolutePath != $"/{_token}/push.v1.PusherService/Push")
                { context.Response.StatusCode = 404; continue; }
                byte[] body;
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[16384];
                    int n;
                    while ((n = await request.InputStream.ReadAsync(chunk)) != 0)
                    {
                        if (buffer.Length + n > 8 * 1024 * 1024) throw new IOException("Profiler upload exceeded 8 MiB.");
                        buffer.Write(chunk, 0, n);
                    }
                    body = buffer.ToArray();
                }
                var push = PushRequest.Parser.ParseFrom(body);
                lock (_gate)
                {
                    if (_capture is { } capture)
                    {
                        foreach (var series in push.Series)
                        foreach (var raw in series.Samples)
                        {
                            var bytes = raw.RawProfile.ToByteArray();
                            var profile = ReadProfile(bytes);
                            var end = profile.TimeNanos + profile.DurationNanos;
                            // Reject delayed uploads from a previous recording. The current profile may straddle
                            // an export boundary; timers were disabled throughout the preceding baseline.
                            if (end < capture.StartedAt.ToUnixTimeMilliseconds() * 1_000_000) continue;
                            capture.Bytes += bytes.Length;
                            if (capture.Bytes > CpuTraceRecorder.MaxTraceBytes) throw new IOException("CPU profiles exceeded 128 MiB.");
                            var name = $"cpu-{++capture.Files:D5}.pprof";
                            using var output = File.Create(Path.Combine(capture.Folder, "profiles", name));
                            if (bytes.AsSpan().StartsWith(new byte[] { 0x1f, 0x8b })) output.Write(bytes);
                            else { using var gzip = new GZipStream(output, CompressionLevel.Fastest); gzip.Write(bytes); }
                        }
                    }
                }
                context.Response.StatusCode = 200;
            }
            catch (Exception ex) when (ex is IOException or InvalidProtocolBufferException or ArgumentException or OverflowException)
            {
                lock (_gate) if (_capture is { } capture) capture.Error = "The native profiler sent an invalid or oversized profile (" + ex.GetType().Name + ").";
                context.Response.StatusCode = 400;
            }
            finally { context.Response.Close(); }
        }
    }

    internal static Profile ReadProfile(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith(new byte[] { 0x1f, 0x8b })) return Profile.Parser.ParseFrom(bytes);
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = gzip.Read(buffer)) != 0)
        {
            if (output.Length + n > 16 * 1024 * 1024) throw new IOException("Expanded CPU profile exceeded 16 MiB.");
            output.Write(buffer, 0, n);
        }
        return Profile.Parser.ParseFrom(output.ToArray());
    }

    internal static async Task WriteSummaryAsync(string folder, CancellationToken ct)
    {
        var methods = new Dictionary<string, MethodTotals>();
        var profiles = new List<object>();
        long total = 0, unresolved = 0, sampleCount = 0;
        var files = Directory.EnumerateFiles(Path.Combine(folder, "profiles"), "*.pprof").Order().ToArray();
        foreach (var path in files)
        {
            var profile = ReadProfile(await File.ReadAllBytesAsync(path, ct));
            string Text(long index) => index >= 0 && index < profile.StringTable.Count ? profile.StringTable[(int)index] : "[invalid string]";
            var cpuIndex = profile.SampleType.Select((s, i) => (s, i))
                .Where(x => Text(x.s.Type) == "cpu" && Text(x.s.Unit) == "nanoseconds").Select(x => (int?)x.i).FirstOrDefault();
            if (cpuIndex is null) continue; // GC/process profiles use distinct units and are retained without mixing denominators.
            var functions = profile.Function.ToDictionary(f => f.Id, f => Text(f.Name));
            var mappings = profile.Mapping.ToDictionary(m => m.Id, m => Text(m.Filename));
            var locations = profile.Location.ToDictionary(l => l.Id, l => l.Line.Count > 0
                ? l.Line.Select(line => functions.GetValueOrDefault(line.FunctionId, "[unresolved]")).ToArray()
                : new[] { $"[unresolved] {mappings.GetValueOrDefault(l.MappingId, "unknown module")}+0x{l.Address:x}" });
            long profileTotal = 0;
            foreach (var sample in profile.Sample)
            {
                if (sample.Value.Count <= cpuIndex.Value || sample.Value[cpuIndex.Value] < 0) throw new InvalidDataException("Invalid CPU sample weight.");
                var ns = sample.Value[cpuIndex.Value];
                if (ns == 0) continue;
                var frames = sample.LocationId.SelectMany(id => locations.GetValueOrDefault(id, ["[unresolved]"])).ToArray();
                if (frames.Length == 0) frames = ["[unresolved]"];
                checked { total += ns; profileTotal += ns; sampleCount++; }
                if (frames[0].StartsWith("[unresolved]", StringComparison.Ordinal) || frames[0].Contains("NotResolvedFrame", StringComparison.Ordinal)
                    || frames[0].StartsWith("Unknown-", StringComparison.Ordinal)) unresolved += ns;
                foreach (var frame in frames.Distinct())
                {
                    if (!methods.TryGetValue(frame, out var value)) methods[frame] = value = new();
                    checked { value.InclusiveNanoseconds += ns; }
                }
                checked { methods[frames[0]].SelfNanoseconds += ns; }
            }
            profiles.Add(new { file = "profiles/" + Path.GetFileName(path), profile.TimeNanos, profile.DurationNanos, cpuNanoseconds = profileTotal });
        }
        if (total == 0) throw new IOException(NoSamplesMessage);
        var ranked = methods.OrderByDescending(x => x.Value.SelfNanoseconds).ThenByDescending(x => x.Value.InclusiveNanoseconds)
            .Select(x => new { method = x.Key, selfNanoseconds = x.Value.SelfNanoseconds, inclusiveNanoseconds = x.Value.InclusiveNanoseconds,
                selfPercentOfSampledCpu = x.Value.SelfNanoseconds / (double)total * 100 }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(folder, "cpu-methods.json"), JsonSerializer.Serialize(new
        {
            method = "Per-managed-thread CPU-clock timers; 19 ms of thread CPU per sample, including CPU spent in syscalls",
            interpretation = "Statistical CPU-time estimates, not exact timings. Sleeping threads do not accrue CPU samples. Percentages use sampled CPU only; compare against process and thread CPU counters for missing coverage. This profiler retains managed caller frames and omits native/kernel frames: native and syscall work can be charged to the nearest retained managed caller. Threads never registered with the CLR are not sampled. Inclusive values overlap; unresolved retained frames remain explicit.",
            sampledCpuNanoseconds = total, unresolvedSelfNanoseconds = unresolved, sampleRecordCount = sampleCount, profiles, methods = ranked
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        var csv = new StringBuilder("method,self_cpu_ms,inclusive_cpu_ms,self_percent_of_sampled_cpu\n");
        foreach (var row in ranked)
            csv.Append('"').Append(row.method.Replace("\"", "\"\"")).Append("\",")
                .Append((row.selfNanoseconds / 1e6).ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append((row.inclusiveNanoseconds / 1e6).ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.selfPercentOfSampledCpu.ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
        await File.WriteAllTextAsync(Path.Combine(folder, "cpu-methods.csv"), csv.ToString(), ct);
    }

    public void Dispose()
    {
        if (Available) Native.SetCPUTrackingEnabled(false);
        _listener?.Close();
        // The listener task owns no persistent work after Close interrupts GetContextAsync.
        _ = _receiver;
    }

    private sealed class Capture(string folder, DateTimeOffset startedAt)
    {
        public string Folder { get; } = folder;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long Bytes;
        public int Files;
        public string? Error;
    }
    private sealed class MethodTotals { public long SelfNanoseconds; public long InclusiveNanoseconds; }
    private static class Native
    {
        [DllImport("Pyroscope.Profiler.Native", EntryPoint = "GetNativeProfilerIsReadyPtr")]
        internal static extern IntPtr GetReadyPointer();
        [DllImport("Pyroscope.Profiler.Native", EntryPoint = "GetPyroscopeProfilerVersion")]
        internal static extern IntPtr GetVersion();
        [DllImport("Pyroscope.Profiler.Native")]
        internal static extern void SetCPUTrackingEnabled([MarshalAs(UnmanagedType.Bool)] bool enabled);
    }
}
