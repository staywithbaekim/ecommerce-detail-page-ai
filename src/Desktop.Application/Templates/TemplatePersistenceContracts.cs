using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public interface ITemplateRepository
{
    Task<IReadOnlyList<TemplateGroup>> ListGroupsAsync(CancellationToken cancellationToken);

    Task SaveGroupAsync(TemplateGroup group, CancellationToken cancellationToken);

    Task<TemplateGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken);

    Task SaveDraftRevisionAsync(TemplateRevision revision, CancellationToken cancellationToken);

    Task<TemplateRevision?> GetRevisionAsync(string revisionId, CancellationToken cancellationToken);

    Task<TemplateRevision?> GetLatestRevisionForGroupAsync(
        string groupId,
        CancellationToken cancellationToken);

    Task CommitRevisionAsync(
        TemplateRevision committedRevision,
        string? expectedCurrentRevisionId,
        CancellationToken cancellationToken);

    Task SaveCalibrationDraftAsync(CalibrationDraft draft, CancellationToken cancellationToken);

    Task<CalibrationDraft?> GetCalibrationDraftAsync(string draftId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(
        string groupId,
        CancellationToken cancellationToken);

    Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken);
}

public interface ITemplateOwnedStorage
{
    Task<TemplateOwnedDeletionStage?> StageGroupFilesForDeletionAsync(
        string groupId,
        CancellationToken cancellationToken);

    Task CommitGroupFilesDeletionAsync(
        TemplateOwnedDeletionStage stage,
        CancellationToken cancellationToken);

    Task RestoreGroupFilesAsync(
        TemplateOwnedDeletionStage stage,
        CancellationToken cancellationToken);

    Task DeleteGroupFilesAsync(string groupId, CancellationToken cancellationToken);
}

public sealed record TemplateOwnedDeletionStage(
    string GroupId,
    string OriginalRelativePath,
    string StagedRelativePath);

public sealed record CalibrationFrameState(
    string FrameId,
    string? AssetId,
    double Scale,
    double OffsetX,
    double OffsetY);

public sealed record CalibrationStateSnapshot(
    string DraftId,
    string PageId,
    long Sequence,
    string? SelectedAssetId,
    string? SelectedFrameId,
    IReadOnlyList<TemplateCalibrationAsset> Assets,
    IReadOnlyList<CalibrationFrameState> Frames);

public interface ICalibrationStateRepository
{
    Task SaveCalibrationStateAsync(
        CalibrationStateSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<CalibrationStateSnapshot?> GetCalibrationStateAsync(
        string draftId,
        string pageId,
        CancellationToken cancellationToken);
}

public interface ITemplatePreviewRepository
{
    Task SavePreviewStateAsync(
        TemplatePreviewState preview,
        CancellationToken cancellationToken);

    Task<TemplatePreviewState?> GetPreviewStateAsync(
        string revisionId,
        string pageId,
        CancellationToken cancellationToken);
}

public interface IBatchSnapshotRepository
{
    Task SaveAsync(BatchTemplateSnapshot snapshot, CancellationToken cancellationToken);

    Task<BatchTemplateSnapshot?> GetForBatchAsync(string batchId, CancellationToken cancellationToken);

    Task ReplaceDraftAsync(
        BatchTemplateSnapshot snapshot,
        string expectedSnapshotId,
        CancellationToken cancellationToken);
}
