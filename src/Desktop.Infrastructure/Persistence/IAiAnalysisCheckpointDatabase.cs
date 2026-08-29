using Desktop.Application.Ai;

namespace Desktop.Infrastructure.Persistence;

public interface IAiAnalysisCheckpointDatabase
{
    Task<AiAnalysisCheckpointJournal?> FindCheckpointAsync(
        string checkpointId,
        CancellationToken cancellationToken);

    Task<AiAnalysisCheckpointJournal?> FindPendingCheckpointAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiAnalysisCheckpointJournal?> FindLatestCommittedCheckpointAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiAnalysisCheckpointJournal> PrepareCheckpointAsync(
        AiAnalysisCheckpointCommand command,
        StagedAiAnalysisStateManifest stagedManifest,
        string payloadDigest,
        CancellationToken cancellationToken);

    Task ValidateCheckpointGraphAsync(
        AiAnalysisCheckpointJournal journal,
        AiAnalysisStateManifestV1 manifest,
        CancellationToken cancellationToken);

    Task<AiAnalysisCheckpointJournal> MarkCheckpointCommittedAsync(
        AiAnalysisCheckpointJournal journal,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken);
}
