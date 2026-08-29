using Desktop.Domain.Ai;

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
    DateTimeOffset CreatedAt);

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
    Task SaveSessionAsync(AiAnalysisSession session, CancellationToken cancellationToken);
    Task<AiAnalysisSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task SaveCacheReferenceAsync(AiCacheReference reference, CancellationToken cancellationToken);
    Task<AiCacheReference?> FindCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken);
    Task RecordUsageAsync(AiUsageEventRecord usage, CancellationToken cancellationToken);
    Task<IReadOnlyList<AiUsageEventRecord>> LoadUsageAsync(string requestId, CancellationToken cancellationToken);
}
