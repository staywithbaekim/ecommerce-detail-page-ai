using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Ai;

public interface IAiAnalyzer
{
    Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral application port for the versioned AI analysis contract.
/// Provider SDKs, endpoints, authentication headers and raw responses must not
/// cross this boundary.
/// </summary>
public interface IAiServicePort
{
    Task<AiAnalysisExecutionResult> AnalyzeAsync(
        AiAnalysisRequestV1 request,
        CancellationToken cancellationToken);
}

public interface IAiAnalysisCopySource
{
    Task<AiAnalysisCopy> OpenAsync(
        string analysisCopyReference,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        string analysisCopyReference,
        CancellationToken cancellationToken);
}

public sealed record AiAnalysisCopy(
    string Reference,
    string MimeType,
    int PixelWidth,
    int PixelHeight,
    ReadOnlyMemory<byte> Content);

public enum AiProviderOutcome
{
    Completed = 0,
    RetryableError,
    TerminalError,
    AiResultUnknown,
    Cancelled
}

public sealed record AiProviderUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    bool CacheHit);

public sealed record AiAnalysisExecutionResult(
    AiProviderOutcome Outcome,
    AiAnalysisResultV1? Result,
    AiProviderUsage? Usage,
    ContractError? Error,
    TimeSpan Duration)
{
    public bool IsSuccess => Outcome == AiProviderOutcome.Completed && Result is not null;

    public static AiAnalysisExecutionResult Completed(
        AiAnalysisResultV1 result,
        AiProviderUsage? usage,
        TimeSpan duration) => new(
            AiProviderOutcome.Completed,
            result ?? throw new ArgumentNullException(nameof(result)),
            usage,
            null,
            duration);

    public static AiAnalysisExecutionResult Failure(
        AiProviderOutcome outcome,
        ContractError error,
        TimeSpan duration) => new(
            outcome is AiProviderOutcome.Completed
                ? throw new ArgumentOutOfRangeException(nameof(outcome))
                : outcome,
            null,
            null,
            error ?? throw new ArgumentNullException(nameof(error)),
            duration);
}

public interface IAiCredentialSource
{
    ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken);
}

public interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageEvent usageEvent, CancellationToken cancellationToken);
}
