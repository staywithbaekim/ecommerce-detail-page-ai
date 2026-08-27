using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public interface ITemplateRepository
{
    Task SaveGroupAsync(TemplateGroup group, CancellationToken cancellationToken);

    Task<TemplateGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken);

    Task SaveDraftRevisionAsync(TemplateRevision revision, CancellationToken cancellationToken);

    Task<TemplateRevision?> GetRevisionAsync(string revisionId, CancellationToken cancellationToken);

    Task CommitRevisionAsync(
        TemplateRevision committedRevision,
        string? expectedCurrentRevisionId,
        CancellationToken cancellationToken);

    Task SaveCalibrationDraftAsync(CalibrationDraft draft, CancellationToken cancellationToken);

    Task<CalibrationDraft?> GetCalibrationDraftAsync(string draftId, CancellationToken cancellationToken);
}

public interface IBatchSnapshotRepository
{
    Task SaveAsync(BatchTemplateSnapshot snapshot, CancellationToken cancellationToken);

    Task<BatchTemplateSnapshot?> GetForBatchAsync(string batchId, CancellationToken cancellationToken);
}
