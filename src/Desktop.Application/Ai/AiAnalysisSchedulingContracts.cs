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

public sealed record AiAnalysisQueueOptions(
    int Capacity = 32,
    int MaxAttempts = 2,
    int MaxConcurrentRequests = 8,
    int MaxAssetsPerRequest = 24,
    int MaxLogicalRequestsPerStyle = 3)
{
    public void Validate()
    {
        if (Capacity is < 1 or > AiAnalysisProductionPolicy.MaxQueueCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(Capacity));
        }

        if (MaxAttempts is < 1 or > AiAnalysisProductionPolicy.MaxAttemptsPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }

        if (MaxConcurrentRequests is < 1 or > AiAnalysisProductionPolicy.MaxConcurrentRequests)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRequests));
        }

        if (MaxAssetsPerRequest is < 1 or > AiAnalysisProductionPolicy.MaxAssetsPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAssetsPerRequest));
        }

        if (MaxLogicalRequestsPerStyle is < 1 or > AiAnalysisProductionPolicy.MaxLogicalRequestsPerStyle)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLogicalRequestsPerStyle));
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
        if (!string.Equals(Currency, AiAnalysisProductionPolicy.PricingCurrency, StringComparison.Ordinal)
            || InputPerThousandTokens < AiAnalysisProductionPolicy.MinimumInputPricePerThousandTokens
            || OutputPerThousandTokens < AiAnalysisProductionPolicy.MinimumOutputPricePerThousandTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InputPerThousandTokens),
                "AI 价格必须使用 CNY，且不得低于 V1 冻结刊例价。");
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

        if (MaxRequests > AiAnalysisProductionPolicy.MaxRequestsPerBatch
            || MaxInputTokens > AiAnalysisProductionPolicy.MaxInputTokensPerBatch
            || MaxOutputTokens > AiAnalysisProductionPolicy.MaxOutputTokensPerBatch
            || MaxEstimatedCost > AiAnalysisProductionPolicy.MaxEstimatedCostPerBatch
            || WarningThreshold > AiAnalysisProductionPolicy.MaxWarningThresholdPerBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRequests), "AI 预算不得放宽 V1 批次硬门。");
        }

        ArgumentNullException.ThrowIfNull(Pricing);
        Pricing.Validate();
    }
}

public static class AiAnalysisProductionPolicy
{
    public const int MaxStylesPerBatch = 200;
    public const int MaxQueueCapacity = 32;
    public const int MaxAttemptsPerRequest = 2;
    public const int MaxConcurrentRequests = 8;
    public const int MaxAssetsPerRequest = 24;
    public const int MaxLogicalRequestsPerStyle = 3;
    public const int MaxRequestsPerBatch = 1_200;
    public const int MaxInputTokensPerBatch = 10_000_000;
    public const int MaxOutputTokensPerBatch = 2_000_000;
    public const decimal MaxWarningThresholdPerBatch = 10m;
    public const decimal MaxEstimatedCostPerBatch = 100m;
    public const string PricingCurrency = "CNY";
    public const decimal MinimumInputPricePerThousandTokens = 0.006m;
    public const decimal MinimumOutputPricePerThousandTokens = 0.030m;

    public static AiAnalysisQueueOptions QueueV1 => new(
        Capacity: MaxQueueCapacity,
        MaxAttempts: MaxAttemptsPerRequest,
        MaxConcurrentRequests: MaxConcurrentRequests,
        MaxAssetsPerRequest: MaxAssetsPerRequest,
        MaxLogicalRequestsPerStyle: MaxLogicalRequestsPerStyle);

    public static AiAnalysisBudgetPolicy BudgetV1 => new(
        MaxRequests: MaxRequestsPerBatch,
        MaxInputTokens: MaxInputTokensPerBatch,
        MaxOutputTokens: MaxOutputTokensPerBatch,
        MaxEstimatedCost: MaxEstimatedCostPerBatch,
        WarningThreshold: MaxWarningThresholdPerBatch,
        Pricing: new AiAnalysisPricing(
            InputPerThousandTokens: MinimumInputPricePerThousandTokens,
            OutputPerThousandTokens: MinimumOutputPricePerThousandTokens,
            Currency: PricingCurrency));
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
