using Desktop.Application.SizeTables;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.Rendering;

/// <summary>Holds AI command admission closed while proving the batch has no unresolved background work.</summary>
public interface ISizeTableRepairAdmission
{
    Task<IDisposable> AcquireAsync(string batchId, CancellationToken cancellationToken);
}

/// <summary>Exact optimistic baseline; a null variant identifies the unvaried page only.</summary>
public sealed record SizeTableRepairTarget(string BatchId, string StyleId, string PageId, string? OutputVariantId,
    long ExpectedJournalRevision, int ExpectedAttempt, string BasePlanDigest,
    long ExpectedTableRevision, string ExpectedTableDataDigest);

public sealed record SizeTablePageRepairCommand(string RequestId, SizeTableRepairTarget Target, SizeTableEditorDraft Draft);

public sealed record SizeTableRepairBaseline(SizeTableRepairTarget Target, SizeTableEditorDocument Document);

/// <summary>
/// Immutable, bounded revision evidence in the journal transaction, not a second mutable current.json.
/// JSON snapshots use a strict repair-revision envelope and must match both plans cell for cell.
/// </summary>
public sealed record RenderSizeTableRepairOrigin(string RequestId, string RequestDigest,
    long ExpectedJournalRevision, int BaseAttempt, string BasePlanDigest, string BaseTableJson, string TableJson);

public sealed record PreparedSizeTableRepair(string PlanJson, RenderSizeTableRepairOrigin Origin);

/// <summary>Implemented by the existing coordinator so repairs share its single execution channel.</summary>
public interface ISizeTablePageRepairWorkflow
{
    Task<SizeTableRepairBaseline> LoadSizeTableRepairAsync(string batchId, string styleId, string pageId,
        string? outputVariantId, CancellationToken cancellationToken);
    Task<RenderBatchJournal> PrepareSizeTableRepairAsync(SizeTablePageRepairCommand command, CancellationToken cancellationToken);
}

/// <summary>Builds one candidate only; does not save current tables, append attempts, call AI or render.</summary>
public interface ISizeTablePageRepairPreparer
{
    Task<SizeTable> LoadBaseAsync(RenderBatchJournal journal, RenderBatchPage page, CancellationToken cancellationToken);
    Task<PreparedSizeTableRepair> PrepareAsync(SizeTablePageRepairCommand command, RenderBatchJournal journal,
        RenderBatchPage page, DateTimeOffset createdAt, CancellationToken cancellationToken);
}
