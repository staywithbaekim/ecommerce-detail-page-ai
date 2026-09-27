using System.Data.Common;
using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Inputs;

public sealed class RecoveringBatchRepository(
    SqliteDesktopStore store,
    IAssetManifestStore manifests,
    TimeProvider timeProvider,
    IInputCommitJournalStore? journals = null) : IBatchRepository
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

    public async Task<BatchDraft?> GetAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        try
        {
            var draft = await store.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            var journal = journals is null
                ? null
                : await journals.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                if (journal is not null)
                {
                    return await RecoverJournalOnlyAsync(journal, cancellationToken).ConfigureAwait(false);
                }

                // This branch is important for old broken batches: a
                // committed manifest without a SQLite row is not “no batch”
                // and must not be presented as a product/image mismatch.
                var orphanManifest = await manifests.InspectAsync(batchId, cancellationToken)
                    .ConfigureAwait(false);
                if (orphanManifest is not null)
                {
                    throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
                }

                return null;
            }

            if (journal is not null)
            {
                draft = await ReconcileJournalAsync(
                    draft,
                    journal,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (draft.ScanStatus == BatchScanStatus.ScanReady && draft.OutputSelection is not null)
            {
                throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
            }

            return await RecoverAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            throw new InvalidOperationException("INPUT_PERSISTENCE_FAILURE", exception);
        }
    }

    public async Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var batchIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var draft in await store.ListRecoverableAsync(cancellationToken).ConfigureAwait(false))
            {
                batchIds.Add(draft.BatchId);
            }

            if (journals is not null)
            {
                foreach (var batchId in await journals.ListBatchIdsAsync(cancellationToken).ConfigureAwait(false))
                {
                    batchIds.Add(batchId);
                }
            }

            var recovered = new List<BatchDraft>(batchIds.Count);
            foreach (var batchId in batchIds.Order(StringComparer.Ordinal))
            {
                var draft = await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
                if (draft is not null)
                {
                    recovered.Add(draft);
                }
            }

            return recovered
                .OrderByDescending(draft => draft.UpdatedAt)
                .ThenBy(draft => draft.BatchId, StringComparer.Ordinal)
                .ToArray();
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

    private async Task<BatchDraft?> RecoverJournalOnlyAsync(
        InputCommitJournal journal,
        CancellationToken cancellationToken)
    {
        var inspection = await manifests.InspectAsync(
            journal.BatchId,
            cancellationToken).ConfigureAwait(false);
        if (inspection is null)
        {
            if (journal.State == InputCommitJournalState.RecoveryRequired)
            {
                // The staged files may already have been rolled back after a
                // pre-commit failure.  There is no evidence from which to
                // rebuild a batch, so leave the orphan journal inert instead
                // of turning a normal retryable draft into a false success.
                return null;
            }

            throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }

        if (!inspection.IsCommitted)
        {
            // A staged manifest alone is not enough evidence to create a
            // ScanReady batch.  Never guess that SQLite committed.
            throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }

        EnsureManifestMatchesJournal(inspection, journal);
        await store.SaveAsync(journal.ReadyDraft, CancellationToken.None).ConfigureAwait(false);
        var restored = await store.GetAsync(journal.BatchId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("INPUT_PERSISTENCE_FAILURE");
        if (!InputCommitJournalConsistency.BatchDraftMatches(journal.ReadyDraft, restored))
        {
            throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }

        await MarkCommittedAsync(journal).ConfigureAwait(false);
        return restored;
    }

    private async Task<BatchDraft> ReconcileJournalAsync(
        BatchDraft draft,
        InputCommitJournal journal,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(draft.BatchId, journal.BatchId, StringComparison.Ordinal)
            || !string.Equals(journal.ReadyDraft.BatchId, journal.BatchId, StringComparison.Ordinal)
            || draft.OutputSelection != journal.ReadyDraft.OutputSelection)
        {
            throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }

        AssetManifestInspection? inspection;
        try
        {
            inspection = await manifests.InspectAsync(
                journal.BatchId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH", exception);
        }

        if (inspection is null)
        {
            if (journal.State == InputCommitJournalState.RecoveryRequired
                && draft.ScanStatus != BatchScanStatus.ScanReady)
            {
                return draft;
            }

            throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }

        if (!inspection.IsCommitted)
        {
            // The journal contains the staged token, so completing the
            // manifest commit is safe and idempotent.  It is only attempted
            // after the file store has validated the staged bytes/marker.
            await manifests.CommitAsync(
                journal.StagedManifest,
                CancellationToken.None).ConfigureAwait(false);
            inspection = await manifests.InspectAsync(
                journal.BatchId,
                CancellationToken.None).ConfigureAwait(false);
        }

        EnsureManifestMatchesJournal(inspection, journal);
        if (draft.ScanStatus == BatchScanStatus.ScanReady)
        {
            if (!InputCommitJournalConsistency.BatchDraftMatches(journal.ReadyDraft, draft))
            {
                throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
            }
        }
        else
        {
            // Draft/Scanning/blocked rows may be the last SQLite image before
            // the durable commit.  The journal is the complete, validated
            // source for rebuilding that graph.
            await store.SaveAsync(journal.ReadyDraft, CancellationToken.None).ConfigureAwait(false);
            draft = await store.GetAsync(journal.BatchId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException("INPUT_PERSISTENCE_FAILURE");
            if (!InputCommitJournalConsistency.BatchDraftMatches(journal.ReadyDraft, draft))
            {
                throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
            }
        }

        if (journals is not null && journal.State != InputCommitJournalState.Committed)
        {
            await MarkCommittedAsync(journal).ConfigureAwait(false);
        }

        return draft;
    }

    private async Task<BatchDraft> RecoverAsync(
        BatchDraft draft,
        CancellationToken cancellationToken)
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
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
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
            ScanBlockReason.ManifestDatabaseMismatch(),
            draft.CreatedAt,
            timeProvider.GetUtcNow(),
            draft.OutputSelection);
        await store.SaveAsync(blocked, cancellationToken).ConfigureAwait(false);
        return blocked;
    }

    private async Task MarkCommittedAsync(InputCommitJournal journal)
    {
        if (journals is null || journal.State == InputCommitJournalState.Committed)
        {
            return;
        }

        await journals.SaveAsync(
            journal.WithState(
                InputCommitJournalState.Committed,
                timeProvider.GetUtcNow()),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static void EnsureManifestMatchesJournal(
        AssetManifestInspection? inspection,
        InputCommitJournal journal)
    {
        if (inspection is null
            || !inspection.IsCommitted
            || !string.Equals(inspection.ManifestReference, journal.StagedManifest.ManifestReference, StringComparison.Ordinal)
            || !string.Equals(inspection.ManifestDigest, journal.StagedManifest.ManifestDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }
    }
}
