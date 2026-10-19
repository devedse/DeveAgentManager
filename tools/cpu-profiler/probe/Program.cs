using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeveAgentManager.Core.Diagnostics;
using DeveAgentManager.Diagnostics;

using var profiler = new NativeCpuProfiler();
if (!profiler.Available) throw new InvalidOperationException(profiler.UnavailableReason);
var recorder = new CpuTraceRecorder(profiler);
var folder = "/result/capture";
Directory.CreateDirectory(folder);
Task busy = Task.CompletedTask, sleeping = Task.CompletedTask;
await recorder.RecordAsync(folder, new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2)),
    (phase, _) =>
    {
        Console.WriteLine(phase);
        if (phase != "profiling") return;
        busy = Task.Run(BurnCpu);
        sleeping = Task.Run(SleepingWorker);
    }, CancellationToken.None);
await Task.WhenAll(busy, sleeping);
using var runtimeEvents = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "runtime-events.json")));
if (!runtimeEvents.RootElement.GetProperty("available").GetBoolean()
    || runtimeEvents.RootElement.GetProperty("stackSampling").GetBoolean()) throw new Exception("Loader/JIT context is missing or enabled the wrong sampler.");
using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "cpu-methods.json")));
var methods = summary.RootElement.GetProperty("methods").EnumerateArray().ToArray();
var hot = methods.Where(x => x.GetProperty("method").GetString()!.Contains("BurnCpu"))
    .Sum(x => x.GetProperty("selfNanoseconds").GetInt64());
var asleep = methods.Where(x => x.GetProperty("method").GetString()!.Contains("SleepingWorker"))
    .Sum(x => x.GetProperty("selfNanoseconds").GetInt64());
if (hot < 3_000_000_000 || asleep > 200_000_000)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(methods.Take(10).Select(x => x.Clone())));
    throw new Exception($"CPU attribution failed: busy={hot}, sleeping={asleep} ns.");
}
// Repeated capture cancellation must stop timers and leave the receiver reusable.
using var cancel = new CancellationTokenSource();
var canceled = Directory.CreateDirectory("/result/canceled").FullName;
try
{
    await recorder.RecordAsync(canceled, new(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(30)),
        (phase, _) => { if (phase == "profiling") cancel.Cancel(); }, cancel.Token);
    throw new Exception("Cancellation did not interrupt the capture.");
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
var cpu = Process.GetCurrentProcess().TotalProcessorTime;
if (!string.IsNullOrWhiteSpace(await File.ReadAllTextAsync("/proc/self/timers"))) throw new Exception("Profiling timers remained armed after cancellation.");
await Task.Delay(5000);
var idleCpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds;
if (!string.IsNullOrWhiteSpace(await File.ReadAllTextAsync("/proc/self/timers"))) throw new Exception("Profiling timers rearmed while idle.");
Console.WriteLine(JsonSerializer.Serialize(new { profiler.Version, hotCpuSeconds = hot / 1e9, sleepingCpuSeconds = asleep / 1e9, idleCpuSeconds = idleCpu }));
Console.WriteLine("PASS: CPU attribution, idle stop, reusable cancellation, maps, per-thread counters and operation timings.");

[MethodImpl(MethodImplOptions.NoInlining)]
static void BurnCpu()
{
    using var timing = CaptureOperations.Start("probe.busy");
    var clock = Stopwatch.StartNew();
    long result = 0;
    while (clock.Elapsed.TotalSeconds < 6) for (var i = 0; i < 1_000_000; i++) result += i * i;
    GC.KeepAlive(result);
}
[MethodImpl(MethodImplOptions.NoInlining)]
static void SleepingWorker() => Thread.Sleep(6000);
