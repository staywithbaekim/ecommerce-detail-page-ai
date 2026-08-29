using Desktop.Domain.Ai;

namespace Desktop.Application.Ai;

public static class AiAnalysisCheckpointProtocol
{
    public const string Version = "ai-analysis-checkpoint/v1";
}

public enum AiAnalysisCheckpointOutcome
{
    Committed = 0,
    RolledBackOrphan,
    NothingToRecover,
    RecoveryRequired
}

public enum AiAnalysisCheckpointJournalState
{
    Pending = 0,
    Committed
}

public sealed record AiAnalysisCheckpointGraph(
    AiRequestLedgerEntry Request,
    string ExpectedRequestStatus,
    AiAnalysisSession Session,
    AiAnalysisSessionStatus ExpectedSessionStatus,
    long ExpectedSelectionRevision,
    AiUsageEventRecord? Usage,
    AiCacheReference? Cache);

/// <summary>
/// Represents one versioned, full-batch checkpoint. <see cref="Graphs"/> contains every
/// request/session graph retained by the batch manifest, not only the style being changed.
/// </summary>
public sealed record AiAnalysisCheckpointCommand(
    string CheckpointId,
    AiAnalysisStateManifestV1 Manifest,
    IReadOnlyList<AiAnalysisCheckpointGraph> Graphs,
    DateTimeOffset CreatedAt);

public sealed record AiAnalysisCheckpointJournalItem(
    string RequestId,
    string SessionId,
    string? UsageEventId,
    string? CacheKey,
    string ExpectedRequestStatus,
    AiAnalysisSessionStatus ExpectedSessionStatus,
    long ExpectedSelectionRevision);

public sealed record AiAnalysisCheckpointJournal(
    string CheckpointId,
    string BatchId,
    string ManifestId,
    string StageToken,
    string ManifestReference,
    string ManifestDigest,
    string PayloadDigest,
    IReadOnlyList<AiAnalysisCheckpointJournalItem> Items,
    AiAnalysisCheckpointJournalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AiAnalysisCheckpointResult(
    string? CheckpointId,
    string BatchId,
    AiAnalysisCheckpointOutcome Outcome,
    string ReasonCode);

public interface IAiAnalysisCheckpointPort
{
    Task<AiAnalysisCheckpointResult> CommitAsync(
        AiAnalysisCheckpointCommand command,
        CancellationToken cancellationToken);

    Task<AiAnalysisCheckpointResult> RecoverAsync(
        string batchId,
        CancellationToken cancellationToken);
}
