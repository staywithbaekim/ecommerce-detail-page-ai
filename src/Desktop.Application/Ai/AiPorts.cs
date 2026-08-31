using System.Text.Json.Serialization;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;

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

public sealed record AiAnalysisCopyPreparationRequest(
    string AnalysisCopyReference,
    string InputRoot,
    SourceAssetMetadata SourceAsset,
    string CompressionProfileVersion);

public static class AiAnalysisCopyProfiles
{
    public const string BytePreservingProvisionalV1 = "byte-preserving/provisional-v1";
    public const string WicBoundedV1 = "wic-bounded/max1600/jpeg-q82/png/v1";
    public const string WicBoundedCandidateV1 = "wic-bounded/max1600/jpeg-q82/png/v1-candidate";
}

public sealed record AiAnalysisCopyMetadata(
    string Reference,
    string Digest,
    long ByteLength,
    string MimeType,
    int PixelWidth,
    int PixelHeight,
    string CompressionProfileVersion,
    string LeaseId,
    IReadOnlyList<string>? LeaseIds = null)
{
    /// <summary>
    /// LeaseIds is the durable ownership set.  LeaseId remains the original
    /// v1 field for wire compatibility; readers normalize old sidecars to a
    /// single-token set.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveLeaseIds =>
        LeaseIds is { Count: > 0 } ? LeaseIds : [LeaseId];
}

public interface IAiAnalysisCopyPreparer
{
    Task<AiAnalysisCopyMetadata> PrepareAsync(
        AiAnalysisCopyPreparationRequest request,
        CancellationToken cancellationToken);
}

public interface IAiAnalysisCopyLeaseManager
{
    Task AcquireAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken);
    Task ReleaseAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken);
}

public sealed record AiAnalysisCopy(
    string Reference,
    string MimeType,
    int PixelWidth,
    int PixelHeight,
    ReadOnlyMemory<byte> Content,
    string? ContentDigest = null,
    long? ByteLength = null,
    string? CompressionProfileVersion = null,
    string? LeaseId = null);

public interface IAiAnalysisResultStore
{
    Task SaveAsync(string resultReference, AiAnalysisResultV1 result, CancellationToken cancellationToken);
    Task<AiAnalysisResultV1?> LoadAsync(string resultReference, CancellationToken cancellationToken);
}

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

public static class AiConsentScopes
{
    public const string AnalysisCopy = "ai-analysis-copy";
}

public static class AiConsentPolicy
{
    public const string Version = "ai-upload-consent/v1";
}

public enum AiUploadConsentState
{
    Granted = 0,
    Revoked
}

/// <summary>
/// The local, provider-neutral record of the user's permission to send
/// application-owned analysis copies to the configured AI service. It carries
/// no credentials, source paths or image data.
/// </summary>
public sealed record AiUploadConsentRecord(
    string Scope,
    string ConsentId,
    AiUploadConsentState State,
    string PolicyVersion,
    DateTimeOffset ChangedAt)
{
    public bool IsGranted => State == AiUploadConsentState.Granted;
}

/// <summary>
/// V1 consent boundary. Implementations are local and durable; a future
/// gateway may replace the producer without changing AI request contracts.
/// </summary>
public interface IConsentPort
{
    Task<AiUploadConsentRecord?> GetConsentAsync(
        string scope,
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord> GrantAsync(
        string scope,
        string policyVersion,
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord> RevokeAsync(
        string scope,
        CancellationToken cancellationToken);
}

public interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageEvent usageEvent, CancellationToken cancellationToken);
}
