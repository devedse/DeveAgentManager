namespace DeveAgentManager.Diagnostics;

public sealed record CpuTraceOptions(bool CaptureOnStartup = false);

public sealed record CpuTracePlan(TimeSpan Before, TimeSpan Profiling, TimeSpan After)
{
    public static CpuTracePlan Manual { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15));
    public static CpuTracePlan Startup { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(30));
    // CPU-clock captures arm immediately so a short spike or early migration is not missed waiting for a baseline.
    public static CpuTracePlan ManualCpu { get; } = new(TimeSpan.Zero, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15));
    public static CpuTracePlan StartupCpu { get; } = new(TimeSpan.Zero, TimeSpan.FromSeconds(270), TimeSpan.FromSeconds(30));
    public int DurationSeconds => (int)(Before + Profiling + After).TotalSeconds;
}
