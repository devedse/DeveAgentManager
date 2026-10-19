namespace DeveAgentManager.Diagnostics;

public sealed record CpuTraceOptions(bool CaptureOnStartup = false);

public sealed record CpuTracePlan(TimeSpan Before, TimeSpan Profiling, TimeSpan After)
{
    public static CpuTracePlan Manual { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15));
    public static CpuTracePlan Startup { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(30));
    public int DurationSeconds => (int)(Before + Profiling + After).TotalSeconds;
}
