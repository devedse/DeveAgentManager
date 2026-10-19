using DeveAgentManager.Contracts;

namespace DeveAgentManager.Client.Shared;

public static class CpuTraceDisplay
{
    public static string Name(ManagerCpuTrace capture) => capture.Trigger == "startup" ? "Startup CPU capture" : "CPU capture";
    public static string Label(ManagerCpuTrace capture) => capture.State switch
    {
        "recording" => capture.Phase switch
        {
            "baseline-before" => "Baseline before profiling",
            "profiler-starting" => "Starting profiler",
            "profiling" => capture.Method == "cpu-timer" ? "Sampling CPU" : "Sampling stacks",
            "profiler-stopping" => "Stopping profiler",
            "baseline-after" => "Baseline after profiling",
            "complete" => "Preparing download",
            _ => "Recording"
        },
        "finishing" => "Preparing download", "ready" => "Ready to download",
        "canceled" => "Canceled", "interrupted" => "Interrupted", _ => "Failed"
    };
    public static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");
}
