using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Infrastructure.Persistence;

/// <summary>
/// Template-domain persistence boundary. SQL remains transactionally owned by
/// the desktop store, but Application receives a narrow template repository
/// instead of the cross-domain aggregate.
/// </summary>
public sealed class SqliteTemplateRepository(SqliteDesktopStore store) :
    ITemplateRepository,
    ITemplateResourceRepository,
    ICalibrationStateRepository,
    ITemplatePreviewRepository,
    IBatchSnapshotRepository
{
    private readonly SqliteDesktopStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<IReadOnlyList<TemplateGroup>> ListGroupsAsync(CancellationToken cancellationToken) =>
        _store.ListGroupsAsync(cancellationToken);

    public Task SaveGroupAsync(TemplateGroup group, CancellationToken cancellationToken) =>
        _store.SaveGroupAsync(group, cancellationToken);

    public Task<TemplateGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken) =>
        _store.GetGroupAsync(groupId, cancellationToken);

    public Task SaveDraftRevisionAsync(TemplateRevision revision, CancellationToken cancellationToken) =>
        _store.SaveDraftRevisionAsync(revision, cancellationToken);

    public Task<TemplateRevision?> GetRevisionAsync(string revisionId, CancellationToken cancellationToken) =>
        _store.GetRevisionAsync(revisionId, cancellationToken);

    public Task<TemplateRevision?> GetLatestRevisionForGroupAsync(string groupId, CancellationToken cancellationToken) =>
        _store.GetLatestRevisionForGroupAsync(groupId, cancellationToken);

    public Task CommitRevisionAsync(TemplateRevision committedRevision, string? expectedCurrentRevisionId, CancellationToken cancellationToken) =>
        _store.CommitRevisionAsync(committedRevision, expectedCurrentRevisionId, cancellationToken);

    public Task SaveCalibrationDraftAsync(CalibrationDraft draft, CancellationToken cancellationToken) =>
        _store.SaveCalibrationDraftAsync(draft, cancellationToken);

    public Task<CalibrationDraft?> GetCalibrationDraftAsync(string draftId, CancellationToken cancellationToken) =>
        _store.GetCalibrationDraftAsync(draftId, cancellationToken);

    public Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(string groupId, CancellationToken cancellationToken) =>
        _store.GetBlockingBatchIdsAsync(groupId, cancellationToken);

    public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken) =>
        _store.DeleteGroupAsync(groupId, cancellationToken);

    public Task<ImportedTemplateResource?> GetByResourceIdAsync(string resourceId, CancellationToken cancellationToken) =>
        _store.GetByResourceIdAsync(resourceId, cancellationToken);

    public Task<ImportedTemplateResource?> GetByCommandIdAsync(string commandId, CancellationToken cancellationToken) =>
        _store.GetByCommandIdAsync(commandId, cancellationToken);

    public Task<IReadOnlyList<ImportedTemplateResource>> FindBySha256Async(string sha256, CancellationToken cancellationToken) =>
        _store.FindBySha256Async(sha256, cancellationToken);

    public Task<ImportedTemplateResource> SaveImportedAsync(string commandId, ImportedTemplateResource resource, CancellationToken cancellationToken) =>
        _store.SaveImportedAsync(commandId, resource, cancellationToken);

    public Task<ImportedTemplateResource> RecordExistingSelectionAsync(string commandId, ImportedTemplateResource resource, CancellationToken cancellationToken) =>
        _store.RecordExistingSelectionAsync(commandId, resource, cancellationToken);

    public Task SaveCalibrationStateAsync(CalibrationStateSnapshot snapshot, CancellationToken cancellationToken) =>
        _store.SaveCalibrationStateAsync(snapshot, cancellationToken);

    public Task<CalibrationStateSnapshot?> GetCalibrationStateAsync(string draftId, string pageId, CancellationToken cancellationToken) =>
        _store.GetCalibrationStateAsync(draftId, pageId, cancellationToken);

    public Task SavePreviewStateAsync(TemplatePreviewState preview, CancellationToken cancellationToken) =>
        _store.SavePreviewStateAsync(preview, cancellationToken);

    public Task<TemplatePreviewState?> GetPreviewStateAsync(string revisionId, string pageId, CancellationToken cancellationToken) =>
        _store.GetPreviewStateAsync(revisionId, pageId, cancellationToken);

    public Task SaveAsync(BatchTemplateSnapshot snapshot, CancellationToken cancellationToken) =>
        _store.SaveAsync(snapshot, cancellationToken);

    public Task<BatchTemplateSnapshot?> GetForBatchAsync(string batchId, CancellationToken cancellationToken) =>
        _store.GetForBatchAsync(batchId, cancellationToken);

    public Task ReplaceDraftAsync(BatchTemplateSnapshot snapshot, string expectedSnapshotId, CancellationToken cancellationToken) =>
        _store.ReplaceDraftAsync(snapshot, expectedSnapshotId, cancellationToken);
}
