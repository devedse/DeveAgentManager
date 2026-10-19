using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using DeveAgentManager.Core.Diagnostics;

namespace DeveAgentManager.Diagnostics;

/// <summary>Bounded, capture-only wall-time correlation. It never records request paths, SQL or payloads.</summary>
internal sealed class CaptureOperationWriter : IAsyncDisposable
{
    private readonly Channel<object> _events = Channel.CreateBounded<object>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ActivityListener _listener;
    private readonly Task _write;
    private long _dropped;
    private const int MaxEvents = 100_000;
    private int _eventsSeen;

    public CaptureOperationWriter(string path)
    {
        _write = WriteAsync(path);
        _listener = new()
        {
            ShouldListenTo = source => source.Name == CaptureOperations.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => Record(activity, "start"),
            ActivityStopped = activity => Record(activity, "stop")
        };
        ActivitySource.AddActivityListener(_listener);

        void Record(Activity activity, string kind)
        {
            // Only allow values assigned by these explicit instrumentation sites. Never serialize all tags/baggage.
            var row = new
            {
                utc = DateTimeOffset.UtcNow, @event = kind, startedAt = activity.StartTimeUtc, operation = activity.OperationName,
                elapsedMilliseconds = activity.GetTagItem("elapsed_ms") is double ms ? ms : activity.Duration.TotalMilliseconds,
                trace = activity.TraceId.ToHexString(), span = activity.SpanId.ToHexString(), parent = activity.ParentSpanId.ToHexString(),
                session = activity.GetTagItem("session"), runner = activity.GetTagItem("runner"), bytes = activity.GetTagItem("bytes"),
                endpoint = activity.GetTagItem("endpoint"), status = activity.GetTagItem("status"), failed = activity.GetTagItem("failed")
            };
            if (Interlocked.Increment(ref _eventsSeen) > MaxEvents || !_events.Writer.TryWrite(row)) Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteAsync(string path)
    {
        await using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true));
        await foreach (var row in _events.Reader.ReadAllAsync()) await output.WriteLineAsync(JsonSerializer.Serialize(row));
        await output.WriteLineAsync(JsonSerializer.Serialize(new { operation = "collector.summary", dropped = Interlocked.Read(ref _dropped), limit = MaxEvents }));
    }
    public async ValueTask DisposeAsync()
    {
        _listener.Dispose(); _events.Writer.TryComplete(); await _write;
    }
}
