using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Persistence;
using System.Data.Common;

namespace Desktop.Infrastructure.Inputs;

public sealed class RecoveringBatchRepository(
    SqliteDesktopStore store,
    IAssetManifestStore manifests,
    TimeProvider timeProvider) : IBatchRepository
{
    public async Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            throw new InvalidOperationException("INPUT_PERSISTENCE_FAILURE", exception);
        }
    }

    public async Task<BatchDraft?> GetAsync(string batchId, CancellationToken cancellationToken)
    {
        try
        {
            var draft = await store.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            return draft is null ? null : await RecoverAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            throw new InvalidOperationException("INPUT_PERSISTENCE_FAILURE", exception);
        }
    }

    public async Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await store.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
            var recovered = new List<BatchDraft>(stored.Count);
            foreach (var draft in stored)
            {
                recovered.Add(await RecoverAsync(draft, cancellationToken).ConfigureAwait(false));
            }
            return recovered;
        }
        catch (DbException exception)
        {
            throw new InvalidOperationException("INPUT_PERSISTENCE_FAILURE", exception);
        }
    }

    public async Task DeleteDraftAsync(string batchId, CancellationToken cancellationToken)
    {
        try
        {
            await store.DeleteDraftAsync(batchId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            throw new InvalidOperationException("INPUT_PERSISTENCE_FAILURE", exception);
        }
    }

    private async Task<BatchDraft> RecoverAsync(BatchDraft draft, CancellationToken cancellationToken)
    {
        if (draft.ScanStatus == BatchScanStatus.Scanning)
        {
            var interrupted = draft.Block(ScanBlockReason.Interrupted(), timeProvider.GetUtcNow());
            await store.SaveAsync(interrupted, cancellationToken).ConfigureAwait(false);
            return interrupted;
        }

        if (draft.ScanStatus != BatchScanStatus.ScanReady)
        {
            return draft;
        }

        try
        {
            var inspection = await manifests.InspectAsync(draft.BatchId, cancellationToken).ConfigureAwait(false);
            if (inspection is { IsCommitted: true }
                && string.Equals(inspection.ManifestReference, draft.ManifestReference, StringComparison.Ordinal)
                && string.Equals(inspection.ManifestDigest, draft.ManifestDigest, StringComparison.Ordinal))
            {
                return draft;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Fall through to a persisted, retryable blocked state.
        }

        var blocked = BatchDraft.Restore(
            draft.BatchId,
            BatchScanStatus.ScanBlocked,
            draft.InputType,
            draft.InputRoot,
            draft.OutputRoot,
            draft.TemplateSnapshot,
            draft.SelectionMode,
            draft.IncludeEditablePsd,
            [],
            [],
            null,
            null,
            ScanBlockReason.Interrupted(),
            draft.CreatedAt,
            timeProvider.GetUtcNow());
        await store.SaveAsync(blocked, cancellationToken).ConfigureAwait(false);
        return blocked;
    }
}
