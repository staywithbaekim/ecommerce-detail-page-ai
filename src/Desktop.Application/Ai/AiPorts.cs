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
    AiTaskProviderIdentity TemplateRulesIdentity => AiTaskProviderIdentity.LegacyTemplateRules;

    Task<Templates.TemplateRuleAiExecution> InterpretTemplateRulesAsync(Templates.TemplateRuleAiRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new Templates.TemplateRuleAiExecution(
            AiProviderOutcome.TerminalError, null, null, new ContractError("TPL_RULE_AI_UNSUPPORTED", false, "provider", "当前适配器不支持模板规则解释。")));
    Task<AiAnalysisExecutionResult> AnalyzeAsync(
        AiAnalysisRequestV1 request,
        CancellationToken cancellationToken);

    Task<ReferenceLearningAiExecutionResult> AnalyzeReferenceLearningAsync(
        ReferenceLearningAiRequestV2 request,
        CancellationToken cancellationToken) => Task.FromResult(
            ReferenceLearningAiExecutionResult.Failure(
                AiProviderOutcome.TerminalError,
                new ContractError(
                    "TPL_LEARN_AI_CONTRACT_UNSUPPORTED",
                    false,
                    "provider",
                    "当前 AI 适配器不支持人工结果学习 v2，请更新应用后重试。"),
                TimeSpan.Zero));
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
    /// <summary>
    /// The production WIC profile after the real-request-body correction.
    /// Opaque PNG input may be emitted as bounded JPEG; PNG with meaningful
    /// alpha remains PNG.  The version is part of every request/cache
    /// fingerprint so copies produced by the old profile cannot be reused.
    /// </summary>
    public const string WicBoundedV2 = "wic-bounded/max1600/jpeg-q82/png-opaque-jpeg/v2";
    public const string WicBoundedCandidateV2 = "wic-bounded/max1600/jpeg-q82/png-opaque-jpeg/v2-candidate";
    /// <summary>
    /// Transport-bounded production profile for multi-image visual analysis.
    /// The lower analysis-only resolution keeps a 24-image request well below
    /// the HTTP body ceiling on constrained uplinks; original media and render
    /// assets are never changed.
    /// </summary>
    public const string WicBoundedV3 = "wic-bounded/max1280/jpeg-q76/png-opaque-jpeg/v3";
    public const string WicTransportCandidate1440Q78V3 =
        "wic-bounded/max1440/jpeg-q78/png-opaque-jpeg/v3-candidate";
    public const string WicTransportCandidate1280Q76V3 =
        "wic-bounded/max1280/jpeg-q76/png-opaque-jpeg/v3-candidate";
    /// <summary>
    /// Current production transport profile. It keeps the V3 geometry bound,
    /// restores the approved q82 quality used by the legacy demo, and remains
    /// a distinct fingerprint so no V3/V2 bytes or cache entries are reused.
    /// </summary>
    public const string WicBoundedV4 = "wic-bounded/max1280/jpeg-q82/png-opaque-jpeg/v4";
    public const string WicBoundedCandidateV4 =
        "wic-bounded/max1280/jpeg-q82/png-opaque-jpeg/v4-candidate";

    public static bool IsWicBoundedV1(string? profile) => profile is
        WicBoundedV1 or WicBoundedCandidateV1;

    public static bool IsWicBoundedV2(string? profile) => profile is
        WicBoundedV2 or WicBoundedCandidateV2;

    public static bool IsWicBoundedV3(string? profile) => profile is
        WicBoundedV3 or WicTransportCandidate1280Q76V3;

    public static bool IsWicBoundedV4(string? profile) => profile is
        WicBoundedV4 or WicBoundedCandidateV4;

    public static bool ConvertsOpaquePngToJpeg(string? profile) =>
        IsWicBoundedV2(profile)
        || IsWicBoundedV3(profile)
        || IsWicBoundedV4(profile)
        || profile is WicTransportCandidate1440Q78V3;

    public static bool IsApproved(string? profile) =>
        IsWicBoundedV1(profile)
        || IsWicBoundedV2(profile)
        || IsWicBoundedV3(profile)
        || IsWicBoundedV4(profile);
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
    bool CacheHit,
    int CachedInputTokens = 0)
{
    public int BillableInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    public AiProviderUsage Normalize()
    {
        var input = Math.Max(0, InputTokens);
        var output = Math.Max(0, OutputTokens);
        var total = Math.Max(input + output, Math.Max(0, TotalTokens));
        var cached = Math.Clamp(CachedInputTokens, 0, input);
        return this with { InputTokens = input, OutputTokens = output, TotalTokens = total, CachedInputTokens = cached, CacheHit = CacheHit || cached > 0 };
    }
}


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
        TimeSpan duration,
        AiProviderUsage? usage = null) => new(
            outcome is AiProviderOutcome.Completed
                ? throw new ArgumentOutOfRangeException(nameof(outcome))
                : outcome,
            null,
            usage,
            error ?? throw new ArgumentNullException(nameof(error)),
            duration);
}

public sealed record ReferenceLearningAiExecutionResult(
    AiProviderOutcome Outcome,
    ReferenceLearningAiResultV2? Result,
    AiProviderUsage? Usage,
    ContractError? Error,
    TimeSpan Duration)
{
    public bool IsSuccess => Outcome == AiProviderOutcome.Completed && Result is not null;

    public static ReferenceLearningAiExecutionResult Completed(
        ReferenceLearningAiResultV2 result,
        AiProviderUsage? usage,
        TimeSpan duration) => new(
            AiProviderOutcome.Completed,
            result ?? throw new ArgumentNullException(nameof(result)),
            usage,
            null,
            duration);

    public static ReferenceLearningAiExecutionResult Failure(
        AiProviderOutcome outcome,
        ContractError error,
        TimeSpan duration,
        AiProviderUsage? usage = null) => new(
            outcome is AiProviderOutcome.Completed
                ? throw new ArgumentOutOfRangeException(nameof(outcome))
                : outcome,
            null,
            usage,
            error ?? throw new ArgumentNullException(nameof(error)),
            duration);
}

public interface IAiCredentialSource
{
    ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken);
}

public enum AiCredentialState
{
    NotConfigured = 0,
    Configured,
    Unavailable
}

/// <summary>
/// A redacted local status snapshot.  The API key itself must never cross
/// this boundary or be persisted by the application layer.
/// </summary>
public sealed record AiCredentialStatusSnapshot(
    AiCredentialState State,
    string? MaskedSuffix,
    string? ErrorCode = null)
{
    public bool IsConfigured => State == AiCredentialState.Configured;
}

/// <summary>
/// Local credential-management boundary for the settings page.  Implementations
/// must use the operating system's secure credential store and must not log,
/// persist, or return the secret from status operations.
/// </summary>
public interface IAiCredentialManagementPort
{
    Task<AiCredentialStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);

    Task SaveAsync(string apiKey, CancellationToken cancellationToken);

    Task RemoveAsync(CancellationToken cancellationToken);
}

public static class AiCredentialRules
{
    /// <summary>
    /// Windows Generic Credential Manager entries have a small bounded blob.
    /// This is deliberately stricter than the current read compatibility cap;
    /// it rejects malformed or unexpectedly large input before native I/O.
    /// </summary>
    public const int MaxUtf8Bytes = 512;

    public static string NormalizeAndValidate(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("AI API Key 不能为空。", nameof(apiKey));
        }

        var value = apiKey.Trim();
        if (value.Length == 0
            || value.Any(char.IsControl)
            || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("AI API Key 不能包含空白或控制字符。", nameof(apiKey));
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxUtf8Bytes)
        {
            throw new ArgumentException("AI API Key 超出本机凭据安全长度上限。", nameof(apiKey));
        }

        return value;
    }

    public static string MaskSuffix(string apiKey)
    {
        var value = NormalizeAndValidate(apiKey);
        var suffix = value.Length <= 4 ? value : value[^4..];
        return $"••••{suffix}";
    }
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
public interface IConsentPort : IAiUploadAdmissionPort
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

/// <summary>
/// Atomic, provider-neutral request-admission read. Implementations must
/// linearize this read with <see cref="IConsentPort.RevokeAsync"/> and must
/// not perform network I/O. A granted record is the point at which a request
/// is admitted to the local scheduler; a revocation committed after that
/// point does not retroactively cancel that admission. Every consent producer
/// implements this operation; there is no legacy read-only fallback at the
/// scheduler boundary.
/// </summary>
public interface IAiUploadAdmissionPort
{
    Task<AiUploadConsentRecord?> TryAcquireAsync(
        string scope,
        CancellationToken cancellationToken);
}

public interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageEvent usageEvent, CancellationToken cancellationToken);
}
