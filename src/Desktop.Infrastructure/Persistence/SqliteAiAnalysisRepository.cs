using Desktop.Application.Ai;
using Desktop.Domain.Ai;

namespace Desktop.Infrastructure.Persistence;

/// <summary>
/// Application-facing AI persistence boundary. The legacy store remains
/// usable by recovery/compatibility tests, while composition no longer wires
/// AI workflows directly to the multi-domain SQLite aggregate.
/// </summary>
public sealed class SqliteAiAnalysisRepository(SqliteDesktopStore store) :
    IAiAnalysisStore,
    IAiAnalysisSchedulingStateStore,
    IAiAnalysisCheckpointDatabase
{
    private readonly SqliteDesktopStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<AiRequestLedgerEntry?> FindRequestByFingerprintAsync(string fingerprint, CancellationToken cancellationToken) =>
        _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken);

    public Task<AiRequestLedgerEntry> RecordRequestAsync(AiRequestLedgerEntry entry, CancellationToken cancellationToken) =>
        _store.RecordRequestAsync(entry, cancellationToken);

    public Task SaveSessionAsync(AiAnalysisSession session, CancellationToken cancellationToken, long? expectedSelectionRevision = null) =>
        _store.SaveSessionAsync(session, cancellationToken, expectedSelectionRevision);

    public Task<AiRequestLedgerEntry> UpdateRequestStatusAsync(
        string requestId,
        string expectedStatus,
        string nextStatus,
        string? resultReference,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken) =>
        _store.UpdateRequestStatusAsync(requestId, expectedStatus, nextStatus, resultReference, updatedAt, cancellationToken);

    public Task<AiAnalysisSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _store.LoadSessionAsync(sessionId, cancellationToken);

    public Task SaveCacheReferenceAsync(AiCacheReference reference, CancellationToken cancellationToken) =>
        _store.SaveCacheReferenceAsync(reference, cancellationToken);

    public Task<bool> InvalidateCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken) =>
        _store.InvalidateCacheReferenceAsync(cacheKey, cancellationToken);

    public Task<AiCacheReference?> FindCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken) =>
        _store.FindCacheReferenceAsync(cacheKey, cancellationToken);

    public Task RecordUsageAsync(AiUsageEventRecord usage, CancellationToken cancellationToken) =>
        _store.RecordUsageAsync(usage, cancellationToken);

    public Task<AiUsageEventRecord?> FindUsageEventAsync(string usageEventId, CancellationToken cancellationToken) =>
        _store.FindUsageEventAsync(usageEventId, cancellationToken);

    public Task<IReadOnlyList<AiUsageEventRecord>> LoadUsageAsync(string requestId, CancellationToken cancellationToken) =>
        _store.LoadUsageAsync(requestId, cancellationToken);

    public Task<AiAnalysisSchedulingState?> LoadSchedulingStateAsync(string stateKey, CancellationToken cancellationToken) =>
        _store.LoadSchedulingStateAsync(stateKey, cancellationToken);

    public Task SaveSchedulingStateAsync(string stateKey, AiAnalysisSchedulingState state, CancellationToken cancellationToken) =>
        _store.SaveSchedulingStateAsync(stateKey, state, cancellationToken);

    public Task<AiAnalysisCheckpointJournal?> FindCheckpointAsync(string checkpointId, CancellationToken cancellationToken) =>
        _store.FindCheckpointAsync(checkpointId, cancellationToken);

    public Task<AiAnalysisCheckpointJournal?> FindPendingCheckpointAsync(string batchId, CancellationToken cancellationToken) =>
        _store.FindPendingCheckpointAsync(batchId, cancellationToken);

    public Task<AiAnalysisCheckpointJournal?> FindLatestCommittedCheckpointAsync(string batchId, CancellationToken cancellationToken) =>
        _store.FindLatestCommittedCheckpointAsync(batchId, cancellationToken);

    public Task<AiAnalysisCheckpointJournal> PrepareCheckpointAsync(
        AiAnalysisCheckpointCommand command,
        StagedAiAnalysisStateManifest stagedManifest,
        string payloadDigest,
        CancellationToken cancellationToken) =>
        _store.PrepareCheckpointAsync(command, stagedManifest, payloadDigest, cancellationToken);

    public Task ValidateCheckpointGraphAsync(
        AiAnalysisCheckpointJournal journal,
        AiAnalysisStateManifestV1 manifest,
        CancellationToken cancellationToken) =>
        _store.ValidateCheckpointGraphAsync(journal, manifest, cancellationToken);

    public Task<AiAnalysisCheckpointJournal> MarkCheckpointCommittedAsync(
        AiAnalysisCheckpointJournal journal,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken) =>
        _store.MarkCheckpointCommittedAsync(journal, committedAt, cancellationToken);
}
