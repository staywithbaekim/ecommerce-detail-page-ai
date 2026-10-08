using Desktop.Domain.Templates;
using Desktop.Application.Files;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desktop.Application.Templates;

public sealed partial class TemplateWorkspaceService
{
    public async Task<TemplateBatchSnapshotView> CreateBatchSnapshotAsync(
        string groupId,
        string batchId,
        IReadOnlyList<string> selectedPageIds,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var revisionId = group.CurrentRevisionId
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var revision = await templates.GetRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var snapshot = BatchTemplateSnapshot.Create(
            $"snapshot_{Guid.NewGuid():N}",
            batchId,
            revision,
            selectedPageIds,
            timeProvider.GetUtcNow());
        if (templates is IBatchSnapshotRepository snapshots)
        {
            await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        return new TemplateBatchSnapshotView(
            snapshot.SnapshotId,
            snapshot.BatchId,
            snapshot.RevisionId,
            snapshot.Pages.Select(page => page.PageId).ToArray(),
            snapshot.RulesDigest,
            snapshot.CreatedAt);
    }

    public async Task<TemplateBatchSnapshotView> UpdateDraftBatchSnapshotAsync(
        string groupId,
        string batchId,
        IReadOnlyList<string> selectedPageIds,
        bool analysisStarted,
        CancellationToken cancellationToken)
    {
        if (templates is not IBatchSnapshotRepository snapshots)
        {
            throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_REPOSITORY_UNAVAILABLE");
        }

        var existing = await snapshots.GetForBatchAsync(batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_NOT_FOUND");
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var revisionId = group.CurrentRevisionId
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var revision = await templates.GetRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var replacement = existing.ReplaceForDraftBatch(
            $"snapshot_{Guid.NewGuid():N}",
            revision,
            selectedPageIds,
            analysisStarted,
            timeProvider.GetUtcNow());
        await snapshots.ReplaceDraftAsync(
            replacement,
            existing.SnapshotId,
            cancellationToken).ConfigureAwait(false);
        return new TemplateBatchSnapshotView(
            replacement.SnapshotId,
            replacement.BatchId,
            replacement.RevisionId,
            replacement.Pages.Select(page => page.PageId).ToArray(),
            replacement.RulesDigest,
            replacement.CreatedAt);
    }


}
