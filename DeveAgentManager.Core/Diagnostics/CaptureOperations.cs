using System.Diagnostics;

namespace DeveAgentManager.Core.Diagnostics;

/// <summary>Operation timings are observed only while a diagnostic capture has a listener.</summary>
public static class CaptureOperations
{
    public const string SourceName = "DeveAgentManager.CaptureOperations";
    public static readonly ActivitySource Source = new(SourceName);

    public static Activity? Start(string name, Guid? session = null, Guid? runner = null, long? bytes = null)
    {
        if (!Source.HasListeners()) return null;
        return Source.StartActivity(name, ActivityKind.Internal, Activity.Current?.Context ?? default,
            tags: new ActivityTagsCollection { ["session"] = session?.ToString("N"), ["runner"] = runner?.ToString("N"), ["bytes"] = bytes });
    }

    internal static void Database(string kind, TimeSpan duration, bool failed = false)
    {
        if (!Source.HasListeners()) return;
        var end = DateTimeOffset.UtcNow;
        using var activity = Source.StartActivity("database." + kind, ActivityKind.Internal,
            Activity.Current?.Context ?? default, startTime: end - duration);
        activity?.SetEndTime(end.UtcDateTime);
        activity?.SetTag("elapsed_ms", duration.TotalMilliseconds);
        activity?.SetTag("failed", failed);
    }
}
