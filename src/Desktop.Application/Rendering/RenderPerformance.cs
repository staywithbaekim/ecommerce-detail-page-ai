using System.Diagnostics;

namespace Desktop.Application.Rendering;

/// <summary>Optional local diagnostics. No listener means no records or persistence.</summary>
public static class RenderPerformance
{
    public const string SourceName = "EcommerceDetailPage.Render";
    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? Start(string phase, string? pageId = null)
    {
        var activity = Source.StartActivity(phase);
        if (pageId is not null) activity?.SetTag("render.page_id", pageId);
        return activity;
    }

    public static Activity? StartBatch(string phase, string batchId)
    {
        var activity = Source.StartActivity(phase);
        activity?.SetTag("render.batch_id", batchId);
        return activity;
    }

    public static async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> operation)
    {
        using var activity = Start(phase);
        return await operation().ConfigureAwait(false);
    }

    public static async Task MeasureAsync(string phase, Func<Task> operation)
    {
        using var activity = Start(phase);
        await operation().ConfigureAwait(false);
    }
}
