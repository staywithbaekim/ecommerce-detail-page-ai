using Desktop.Contracts.Bridge;

namespace Desktop.Application.Rendering;

/// <summary>Recovery metadata only: never persist tokens, handles, paths or the transport request.</summary>
public sealed record BridgeExecutionRecord(
    string TaskId, string ExecutionId, string IdempotencyKey, string PlanDigest,
    string PageId, string State, bool CancelRequested, int EventSeq,
    IReadOnlyList<BridgeEventV1> Events);

public sealed record BridgeExecutionJournal(int Version, IReadOnlyList<BridgeExecutionRecord> Executions);

public interface IBridgeExecutionJournalStore
{
    Task<BridgeExecutionJournal?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(BridgeExecutionJournal journal, CancellationToken cancellationToken);
}
