using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

public enum AiAnalysisWorkStage
{
    ProductJointUnderstanding = 0,
    SelectedAssetGeometry,
    SizeTableExtraction
}

public enum AiAnalysisWorkDisposition
{
    Enqueued = 0,
    QueueFull,
    Duplicate,
    CacheHit,
    Completed,
    RetryScheduled,
    ResultUnknown,
    Cancelled,
    BudgetStopped,
    TerminalError,
    AlreadySettled,
    Paused,
    WaitingForNetwork
}

/// <summary>
/// Durable batch-level control state.  It is deliberately separate from the
/// per-request analysis state: a pause or offline condition must not rewrite a
/// provider result or make an in-flight request look successful.
/// </summary>
public enum AiAnalysisSchedulingControlState
{
    Running = 0,
    PauseRequested,
    Paused,
    WaitingForNetwork,
    CancelRequested,
    Cancelled
}

public sealed record AiAnalysisWorkItem(
    string WorkItemId,
    AiAnalysisRequestV1 Request,
    AiAnalysisWorkStage Stage,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    string? ParentRequestId = null);

public sealed record AiAnalysisScheduleResult(
    AiAnalysisWorkDisposition Disposition,
    string RequestId,
    string? ResultReference,
    string? ErrorCode,
    int Attempt);

public sealed record AiAnalysisQueueOptions(int Capacity = 32, int MaxAttempts = 2)
{
    public void Validate()
    {
        if (Capacity is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Capacity));
        }

        if (MaxAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }
    }
}

public sealed record AiAnalysisPricing(
    decimal InputPerThousandTokens,
    decimal OutputPerThousandTokens,
    string Currency = "CNY")
{
    public void Validate()
    {
        if (InputPerThousandTokens < 0 || OutputPerThousandTokens < 0
            || string.IsNullOrWhiteSpace(Currency)
            || Currency.Length != 3
            || Currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("AI 价格配置无效。", nameof(InputPerThousandTokens));
        }
    }

    public decimal Estimate(AiProviderUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        Validate();
        return (usage.InputTokens / 1000m * InputPerThousandTokens)
            + (usage.OutputTokens / 1000m * OutputPerThousandTokens);
    }
}

public sealed record AiAnalysisBudgetPolicy(
    int MaxRequests,
    int MaxInputTokens,
    int MaxOutputTokens,
    decimal MaxEstimatedCost,
    decimal WarningThreshold,
    AiAnalysisPricing Pricing)
{
    public void Validate()
    {
        if (MaxRequests < 1 || MaxInputTokens < 1 || MaxOutputTokens < 1
            || MaxEstimatedCost <= 0 || WarningThreshold < 0 || WarningThreshold > MaxEstimatedCost)
        {
            throw new ArgumentException("AI 预算配置必须为正数且告警线不得超过硬停止线。", nameof(MaxRequests));
        }

        ArgumentNullException.ThrowIfNull(Pricing);
        Pricing.Validate();
    }
}

public sealed record AiAnalysisBudgetSnapshot(
    int RequestCount,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    bool WarningReached,
    bool HardStopped)
{
    public static AiAnalysisBudgetSnapshot Empty => new(0, 0, 0, 0m, false, false);
}

/// <summary>
/// Durable scheduler state.  The snapshot contains only structured request
/// metadata and relative analysis-copy references; it never contains provider
/// credentials, raw responses, image bytes or machine paths.
/// </summary>
public sealed record AiAnalysisSchedulingState(
    IReadOnlyList<AiAnalysisWorkItem> PendingItems,
    AiAnalysisBudgetSnapshot Budget,
    DateTimeOffset UpdatedAt,
    AiAnalysisSchedulingControlState ControlState = AiAnalysisSchedulingControlState.Running,
    string? ControlReason = null,
    IReadOnlyList<AiAnalysisRequestV1>? HistoricalRequests = null);

public sealed record AiAnalysisBatchScheduleResult(
    string BatchId,
    IReadOnlyList<AiAnalysisScheduleResult> StyleResults,
    int EnqueuedCount,
    int DuplicateCount,
    int CacheHitCount,
    int AlreadySettledCount);

public interface IAiAnalysisSchedulingStateStore
{
    Task<AiAnalysisSchedulingState?> LoadSchedulingStateAsync(
        string stateKey,
        CancellationToken cancellationToken);

    Task SaveSchedulingStateAsync(
        string stateKey,
        AiAnalysisSchedulingState state,
        CancellationToken cancellationToken);
}

public interface IAiAnalysisWorkBuffer
{
    int Count { get; }

    bool IsActive(string workItemId);

    bool TryEnqueue(AiAnalysisWorkItem item);

    ValueTask<AiAnalysisWorkItem> DequeueAsync(CancellationToken cancellationToken);

    void Complete(AiAnalysisWorkItem item);
}

public sealed class BoundedAiAnalysisWorkBuffer : IAiAnalysisWorkBuffer, IDisposable
{
    private readonly System.Threading.Channels.Channel<AiAnalysisWorkItem> _channel;
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    private int _count;

    public BoundedAiAnalysisWorkBuffer(AiAnalysisQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _channel = System.Threading.Channels.Channel.CreateBounded<AiAnalysisWorkItem>(
            new System.Threading.Channels.BoundedChannelOptions(options.Capacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
    }

    public int Count => Volatile.Read(ref _count);

    public bool IsActive(string workItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        return _active.ContainsKey(workItemId);
    }

    public bool TryEnqueue(AiAnalysisWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_active.TryAdd(item.WorkItemId, 0))
        {
            return false;
        }

        if (!_channel.Writer.TryWrite(item))
        {
            _active.TryRemove(item.WorkItemId, out _);
            return false;
        }

        Interlocked.Increment(ref _count);
        return true;
    }

    public async ValueTask<AiAnalysisWorkItem> DequeueAsync(CancellationToken cancellationToken)
    {
        var item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Decrement(ref _count);
        return item;
    }

    public void Complete(AiAnalysisWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _active.TryRemove(item.WorkItemId, out _);
    }

    public void Dispose() => _channel.Writer.TryComplete();
}

public static class AiAnalysisCacheKeyFactory
{
    public static string Create(AiAnalysisRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = JsonSerializer.Serialize(new
        {
            assets = request.Assets
                .OrderBy(asset => asset.AssetId, StringComparer.Ordinal)
                .Select(asset => new { asset.AssetId, asset.ContentHash }),
            request.AnalysisKind,
            request.ModelConfigVersion,
            request.PromptContractVersion,
            request.ResponseSchemaVersion,
            request.CompressionProfileVersion,
            request.RequirementsDigest
        });
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
