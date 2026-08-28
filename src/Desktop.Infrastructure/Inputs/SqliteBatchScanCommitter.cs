using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Persistence;
using System.Data.Common;

namespace Desktop.Infrastructure.Inputs;

public sealed class SqliteBatchScanCommitter(
    SqliteDesktopStore store,
    IAssetManifestStore manifests,
    TimeProvider timeProvider) : IBatchScanCommitter
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

        StagedAssetManifest? staged = null;
        var databaseCommitted = false;
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
            await store.CommitReadyAggregateAsync(ready, cancellationToken).ConfigureAwait(false);
            databaseCommitted = true;
            await manifests.CommitAsync(staged, cancellationToken).ConfigureAwait(false);
            return BatchScanCommitResult.Success(ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CompensateAsync(request.ScanningDraft, staged, databaseCommitted).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsRecoverableCommitFailure(exception))
        {
            await CompensateAsync(request.ScanningDraft, staged, databaseCommitted).ConfigureAwait(false);
            return BatchScanCommitResult.Blocked(ScanBlockReason.SystemFailure());
        }
    }

    private async Task CompensateAsync(
        BatchDraft scanningDraft,
        StagedAssetManifest? staged,
        bool databaseCommitted)
    {
        if (databaseCommitted)
        {
            try
            {
                await store.RollbackReadyAggregateAsync(scanningDraft, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableCommitFailure(exception))
            {
                // Restart recovery rejects ScanReady when its committed marker is absent.
            }
        }

        if (staged is not null)
        {
            try
            {
                await manifests.RollbackAsync(staged, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableCommitFailure(exception))
            {
                // Staging is application-owned and remains inspectable for restart recovery.
            }
        }
    }

    private static bool IsRecoverableCommitFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DbException;
}
