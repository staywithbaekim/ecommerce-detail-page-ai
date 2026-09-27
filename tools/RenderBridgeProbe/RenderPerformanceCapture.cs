using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Desktop.Application.Rendering;

/// <summary>Bounded, local diagnostics for an explicitly requested performance run.</summary>
internal sealed class RenderPerformanceCapture : IDisposable
{
    private const int Limit = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ActivityListener _listener;
    private readonly string _outputPath;
    private readonly ConcurrentQueue<object> _spans = new();
    private int _count;
    private int _dropped;

    private RenderPerformanceCapture(string root)
    {
        _outputPath = Path.Combine(root, "render-performance.json");
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RenderPerformance.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                var index = Interlocked.Increment(ref _count);
                if (index > Limit)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }

                _spans.Enqueue(new
                {
                    phase = activity.DisplayName,
                    activity_id = activity.Id,
                    parent_activity_id = activity.ParentId,
                    page_id = BoundedTag(activity, "render.page_id"),
                    batch_id = BoundedTag(activity, "render.batch_id"),
                    duration_ms = activity.Duration.TotalMilliseconds
                });
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public static RenderPerformanceCapture Start(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute capture root is required.", nameof(root));
        if (File.Exists(Path.Combine(root, "render-performance.json")))
            throw new IOException("The render performance report already exists.");
        return new(root);
    }

    public void Dispose()
    {
        _listener.Dispose();
        try
        {
            using var file = new FileStream(_outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new
            {
                schema = "render-performance/v1",
                clock = "Stopwatch",
                span_limit = Limit,
                dropped_spans = _dropped,
                spans = _spans.ToArray()
            }, JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("PERFORMANCE_REPORT_WRITE_FAILED");
        }
    }

    private static string? BoundedTag(Activity activity, string key) =>
        activity.GetTagItem(key) is string value && value.Length <= 128 ? value : null;
}
