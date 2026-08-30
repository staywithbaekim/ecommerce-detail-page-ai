using Desktop.Domain.Ai;
using Desktop.Application.Files;

namespace Desktop.Application.Ai;

public sealed record AiRequestLedgerEntry(
    string RequestId,
    string Fingerprint,
    string BatchId,
    string StyleId,
    string Status,
    string? ResultReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AiCacheReference(
    string CacheKey,
    string ResultReference,
    string ConsentId,
    string ModelConfigVersion,
    string UsageEventId,
    DateTimeOffset CreatedAt,
    bool IsReusable = true);

public sealed record AiUsageEventRecord(
    string UsageEventId,
    string RequestId,
    string ModelConfigVersion,
    int InputUnits,
    int OutputUnits,
    bool CacheHit,
    int RetryCount,
    decimal? ProviderCostAmount,
    string? ProviderCostCurrency,
    DateTimeOffset RecordedAt);

public interface IAiAnalysisStore
{
    Task<AiRequestLedgerEntry?> FindRequestByFingerprintAsync(string fingerprint, CancellationToken cancellationToken);
    Task<AiRequestLedgerEntry> RecordRequestAsync(AiRequestLedgerEntry entry, CancellationToken cancellationToken);
    Task SaveSessionAsync(AiAnalysisSession session, CancellationToken cancellationToken, long? expectedSelectionRevision = null);
    Task<AiRequestLedgerEntry> UpdateRequestStatusAsync(
        string requestId,
        string expectedStatus,
        string nextStatus,
        string? resultReference,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
    Task<AiAnalysisSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task SaveCacheReferenceAsync(AiCacheReference reference, CancellationToken cancellationToken);
    Task<bool> InvalidateCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken);
    Task<AiCacheReference?> FindCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken);
    Task RecordUsageAsync(AiUsageEventRecord usage, CancellationToken cancellationToken);
    Task<AiUsageEventRecord?> FindUsageEventAsync(string usageEventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AiUsageEventRecord>> LoadUsageAsync(string requestId, CancellationToken cancellationToken);
}

public static class AiAnalysisStateProtocol
{
    public const string Version = "ai-analysis-state/v1";
}

public sealed record AiAnalysisStateManifestV1(
    string SchemaVersion,
    string ManifestId,
    string BatchId,
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<string> RequestIds,
    IReadOnlyList<string> CacheKeys,
    DateTimeOffset CreatedAt,
    string? SchedulerStateDigest = null);

public sealed record StagedAiAnalysisStateManifest(
    string BatchId,
    string ManifestReference,
    string ManifestDigest,
    string StageToken);

public sealed record AiAnalysisStateManifestInspection(
    string BatchId,
    string? CommittedManifestReference,
    string? CommittedManifestDigest,
    AiAnalysisStateManifestV1? CommittedManifest,
    StagedAiAnalysisStateManifest? StagedManifest,
    AiAnalysisStateManifestV1? StagedStateManifest)
{
    public bool IsStaged => StagedManifest is not null;
    public bool IsCommitted => CommittedManifestReference is not null;
}

public interface IAiAnalysisStateManifestStore
{
    Task<StagedAiAnalysisStateManifest> StageAsync(
        AiAnalysisStateManifestV1 manifest,
        CancellationToken cancellationToken);

    Task CommitAsync(
        StagedAiAnalysisStateManifest stagedManifest,
        CancellationToken cancellationToken);

    Task RollbackAsync(
        StagedAiAnalysisStateManifest stagedManifest,
        CancellationToken cancellationToken);

    Task<AiAnalysisStateManifestInspection?> InspectAsync(
        string batchId,
        CancellationToken cancellationToken);
}
