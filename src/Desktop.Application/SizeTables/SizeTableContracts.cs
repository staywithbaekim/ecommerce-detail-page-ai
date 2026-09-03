using Desktop.Application.Inputs;
using Desktop.Contracts.Ai;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.SizeTables;

public enum SizeTableSourceResolutionStatus
{
    Missing = 0,
    Selected,
    Conflict
}

public sealed record SizeTableSourceOption(
    string AssetId,
    string ContentDigest,
    long Length,
    string MimeType,
    IReadOnlyList<string> DuplicateAssetIds);

public sealed record SizeTableSourceResolution(
    SizeTableSourceResolutionStatus Status,
    IReadOnlyList<SizeTableSourceOption> Options,
    SizeTableSourceOption? Selected,
    long SelectionRevision,
    string ReasonCode)
{
    public SizeTableSourceResolution Select(string assetId, long expectedRevision)
    {
        if (expectedRevision != SelectionRevision)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_SOURCE_SELECTION_REVISION_CONFLICT");
        }

        var selected = Options.SingleOrDefault(option => option.DuplicateAssetIds.Contains(assetId, StringComparer.Ordinal))
            ?? throw new SizeTableRuleViolationException("SIZE_TABLE_SOURCE_SELECTION_INVALID");
        return this with
        {
            Status = SizeTableSourceResolutionStatus.Selected,
            Selected = selected,
            SelectionRevision = checked(SelectionRevision + 1),
            ReasonCode = "SIZE_TABLE_SOURCE_SELECTED"
        };
    }
}

public sealed record PrepareSizeTableCommand(
    AssetManifestV1 Manifest,
    string StyleId,
    string PageId,
    IReadOnlyList<AiSizeTableCandidateV1> Candidates,
    string? SelectedSourceAssetId,
    long SelectionRevision,
    DateTimeOffset CreatedAt);

public sealed record EditSizeTableCommand(
    SizeTable Current,
    long ExpectedRevision,
    IReadOnlyList<SizeTableColumn> Columns,
    IReadOnlyList<SizeTableRow> Rows,
    DateTimeOffset UpdatedAt);

public sealed record RegenerateSizeTablePageCommand(
    string BatchId,
    string StyleId,
    string PageId,
    string PreviousPlanId,
    string NewPlanId,
    long ExpectedTableRevision);

public sealed record SizeTablePreparationResult(
    SizeTableSourceResolution SourceResolution,
    SizeTableCandidate? Candidate,
    SizeTable? Table,
    string ReasonCode)
{
    public bool RequiresUserAction => SourceResolution.Status != SizeTableSourceResolutionStatus.Selected
        || Table?.Status is SizeTableStatus.ReviewRequired or SizeTableStatus.Invalid;
}

public enum SizeTableLoadStatus
{
    NotFound = 0,
    Loaded,
    RecoveryRequired
}

public sealed record SizeTableLoadResult(
    SizeTableLoadStatus Status,
    SizeTable? Table,
    string? ReasonCode)
{
    public static SizeTableLoadResult NotFound() => new(SizeTableLoadStatus.NotFound, null, null);

    public static SizeTableLoadResult Loaded(SizeTable table) => new(
        SizeTableLoadStatus.Loaded,
        table,
        null);

    public static SizeTableLoadResult RecoveryRequired(string reasonCode) => new(
        SizeTableLoadStatus.RecoveryRequired,
        null,
        reasonCode);
}

public enum SizeTableSaveStatus
{
    Saved = 0,
    RevisionConflict,
    InvalidState,
    RecoveryRequired
}

public sealed record SizeTableSaveResult(SizeTableSaveStatus Status, string? ReasonCode = null);

public interface ISizeTableRepository
{
    Task<SizeTableLoadResult> LoadAsync(
        string batchId,
        string styleId,
        string pageId,
        CancellationToken cancellationToken);

    Task<SizeTableSaveResult> SaveAsync(
        SizeTable table,
        long? expectedRevision,
        CancellationToken cancellationToken);
}
