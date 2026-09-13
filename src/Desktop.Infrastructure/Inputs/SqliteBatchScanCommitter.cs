using System.Data.Common;
using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Inputs;

public sealed class SqliteBatchScanCommitter(
    SqliteDesktopStore store,
    IAssetManifestStore manifests,
    TimeProvider timeProvider,
    IInputCommitJournalStore? journals = null) : IBatchScanCommitter
{
    public async Task<BatchScanCommitResult> CommitAsync(
        BatchScanCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ScanningDraft.ScanStatus != BatchScanStatus.Scanning
            || !string.Equals(request.Manifest.BatchId, request.ScanningDraft.BatchId, StringComparison.Ordinal))
        {
            throw new InputRuleViolationException("INPUT_SCAN_COMMIT_REQUEST_INVALID");
        }

        cancellationToken.ThrowIfCancellationRequested();
        StagedAssetManifest? staged = null;
        InputCommitJournal? journal = null;
        var journalPersisted = false;
        var databaseCommitted = false;
        var manifestCommitted = false;
        try
        {
            staged = await manifests.StageAsync(request.Manifest, cancellationToken).ConfigureAwait(false);
            var ready = request.ScanningDraft.MarkScanReady(
                request.InputType,
                request.Styles,
                request.Assets,
                staged.ManifestReference,
                staged.ManifestDigest,
                timeProvider.GetUtcNow());

            if (journals is not null)
            {
                journal = InputCommitJournal.Create(
                    staged,
                    ready,
                    timeProvider.GetUtcNow());
                // From this point on the operation is a durability barrier.
                await journals.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
                journalPersisted = true;
            }

            // Do not pass the UI cancellation token into the SQLite commit.
            // Once the journal exists, returning cancellation after SQLite has
            // committed would recreate the exact unknown-state failure this
            // journal is designed to repair.
            await store.CommitReadyAggregateAsync(ready, CancellationToken.None).ConfigureAwait(false);
            databaseCommitted = true;

            if (journal is not null)
            {
                journal = journal.WithState(
                    InputCommitJournalState.DatabaseCommitted,
                    timeProvider.GetUtcNow());
                await journals!.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
                journalPersisted = true;
            }

            await manifests.CommitAsync(staged, CancellationToken.None).ConfigureAwait(false);
            manifestCommitted = true;

            if (journal is not null)
            {
                journal = journal.WithState(
                    InputCommitJournalState.Committed,
                    timeProvider.GetUtcNow());
                await journals!.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
                journalPersisted = true;
            }

            await VerifyCommittedStateAsync(ready, staged).ConfigureAwait(false);
            return BatchScanCommitResult.Success(ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await HandleFailureAsync(
                request.ScanningDraft,
                staged,
                journal,
                databaseCommitted,
                manifestCommitted).ConfigureAwait(false);
            if (journalPersisted || databaseCommitted || manifestCommitted)
            {
                return BatchScanCommitResult.Blocked(
                    ScanBlockReason.PersistenceFailure(),
                    requiresRecovery: true);
            }

            throw;
        }
        catch (Exception exception) when (IsRecoverableCommitFailure(exception))
        {
            var crossedDurableBoundary = journalPersisted
                || databaseCommitted
                || manifestCommitted
                || journal is not null && journals is not null;
            await HandleFailureAsync(
                request.ScanningDraft,
                staged,
                journal,
                databaseCommitted,
                manifestCommitted).ConfigureAwait(false);
            var reason = journals is null
                ? ScanBlockReason.SystemFailure()
                : exception.Message == "INPUT_MANIFEST_DATABASE_MISMATCH"
                    ? ScanBlockReason.ManifestDatabaseMismatch()
                    : ScanBlockReason.PersistenceFailure();
            return BatchScanCommitResult.Blocked(reason, crossedDurableBoundary);
        }
    }

    private async Task HandleFailureAsync(
        BatchDraft scanningDraft,
        StagedAssetManifest? staged,
        InputCommitJournal? journal,
        bool databaseCommitted,
        bool manifestCommitted)
    {
        if (journal is not null && journals is not null)
        {
            try
            {
                await journals.SaveAsync(
                    journal.WithState(
                        InputCommitJournalState.RecoveryRequired,
                        timeProvider.GetUtcNow()),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableCommitFailure(exception))
            {
                // The original journal or manifest remains the diagnostic
                // evidence.  The caller receives a local persistence failure.
            }
        }

        // A fully known pre-commit failure can be cleaned up.  Once a journal
        // exists, leave any uncertain durable state for restart reconciliation
        // rather than overwriting a possible ScanReady graph.
        if (databaseCommitted && journals is null)
        {
            try
            {
                await store.RollbackReadyAggregateAsync(
                    scanningDraft,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableCommitFailure(exception))
            {
                // The repository will expose recovery on the next open.
            }
        }

        // After the journal has been persisted, SQLite has either committed or
        // may have committed with an exception after the transaction boundary.
        // Keep the staged manifest whenever that state is possible; restart
        // reconciliation can finish or reject it using the journal.  Deleting
        // it here would leave a ScanReady row with no evidence to recover.
        if (staged is not null
            && !manifestCommitted
            && (journals is null || journal is null))
        {
            try
            {
                await manifests.RollbackAsync(staged, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableCommitFailure(exception))
            {
                // Staged evidence remains inspectable for recovery.
            }
        }
    }

    private async Task VerifyCommittedStateAsync(
        BatchDraft ready,
        StagedAssetManifest staged)
    {
        var persisted = await store.GetAsync(
            ready.BatchId,
            CancellationToken.None).ConfigureAwait(false);
        if (persisted is null || !InputCommitJournalConsistency.BatchDraftMatches(ready, persisted))
        {
            throw new InvalidDataException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }

        var inspection = await manifests.InspectAsync(
            ready.BatchId,
            CancellationToken.None).ConfigureAwait(false);
        if (inspection is null
            || !inspection.IsCommitted
            || !string.Equals(inspection.ManifestReference, staged.ManifestReference, StringComparison.Ordinal)
            || !string.Equals(inspection.ManifestDigest, staged.ManifestDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("INPUT_MANIFEST_DATABASE_MISMATCH");
        }
    }

    private static bool IsRecoverableCommitFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        ObjectDisposedException or
        DbException;
}
