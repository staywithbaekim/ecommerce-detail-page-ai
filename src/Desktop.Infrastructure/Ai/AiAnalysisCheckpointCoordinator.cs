using System.Runtime.ExceptionServices;
using Desktop.Application.Ai;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Ai;

public sealed class AiAnalysisCheckpointCoordinator(
    IAiAnalysisCheckpointDatabase database,
    IAiAnalysisStateManifestStore manifests,
    TimeProvider timeProvider) : IAiAnalysisCheckpointPort, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AiAnalysisCheckpointResult> CommitAsync(
        AiAnalysisCheckpointCommand command,
        CancellationToken cancellationToken)
    {
        AiAnalysisCheckpointRules.Validate(command);
        var payloadDigest = AiAnalysisCheckpointRules.ComputePayloadDigest(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await database.FindCheckpointAsync(
                command.CheckpointId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                AiAnalysisCheckpointRules.EnsureCommandMatchesJournal(command, payloadDigest, existing);
                if (existing.State == AiAnalysisCheckpointJournalState.Committed)
                {
                    var latest = await database.FindLatestCommittedCheckpointAsync(
                        command.Manifest.BatchId,
                        cancellationToken).ConfigureAwait(false);
                    if (latest is not null
                        && !string.Equals(latest.CheckpointId, existing.CheckpointId, StringComparison.Ordinal))
                    {
                        return Result(existing, AiAnalysisCheckpointOutcome.Committed, "AI_CHECKPOINT_COMMITTED_SUPERSEDED");
                    }
                }

                return await ResumeJournalAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            var pending = await database.FindPendingCheckpointAsync(
                command.Manifest.BatchId,
                cancellationToken).ConfigureAwait(false);
            if (pending is not null)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_BATCH_PENDING");
            }

            var orphanResult = await ValidateCommittedStateAndRemoveOrphanStageAsync(
                command,
                cancellationToken).ConfigureAwait(false);
            if (orphanResult is not null)
            {
                return orphanResult;
            }

            var staged = await manifests.StageAsync(command.Manifest, cancellationToken).ConfigureAwait(false);
            AiAnalysisCheckpointJournal journal;
            try
            {
                journal = await database.PrepareCheckpointAsync(
                    command,
                    staged,
                    payloadDigest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var persisted = await TryFindCheckpointAsync(command.CheckpointId).ConfigureAwait(false);
                if (persisted is not null)
                {
                    AiAnalysisCheckpointRules.EnsureCommandMatchesJournal(command, payloadDigest, persisted);
                    if (!AiAnalysisCheckpointRules.StageMatches(persisted, staged))
                    {
                        await TryRollbackStageAsync(staged).ConfigureAwait(false);
                        throw new InvalidOperationException("AI_CHECKPOINT_STAGE_JOURNAL_CONFLICT", exception);
                    }

                    journal = persisted;
                }
                else
                {
                    if (!await TryRollbackStageAsync(staged).ConfigureAwait(false))
                    {
                        return new AiAnalysisCheckpointResult(
                            command.CheckpointId,
                            command.Manifest.BatchId,
                            AiAnalysisCheckpointOutcome.RecoveryRequired,
                            "AI_CHECKPOINT_DATABASE_FAILURE_STAGE_REMAINS");
                    }

                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw;
                }
            }

            return await ResumeJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiAnalysisCheckpointResult> RecoverAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        AiAnalysisCheckpointRules.ValidateIdentifier(batchId, "AI_BATCH_ID_INVALID");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AiAnalysisCheckpointJournal? pending;
            AiAnalysisCheckpointJournal? committed;
            try
            {
                pending = await database.FindPendingCheckpointAsync(batchId, cancellationToken).ConfigureAwait(false);
                committed = pending is null
                    ? await database.FindLatestCommittedCheckpointAsync(batchId, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return new AiAnalysisCheckpointResult(
                    null,
                    batchId,
                    AiAnalysisCheckpointOutcome.RecoveryRequired,
                    "AI_CHECKPOINT_JOURNAL_READ_FAILED");
            }

            if (pending is not null)
            {
                return await ResumeJournalAsync(pending, cancellationToken).ConfigureAwait(false);
            }

            if (committed is not null)
            {
                return await ResumeJournalAsync(committed, cancellationToken).ConfigureAwait(false);
            }

            AiAnalysisStateManifestInspection? inspection;
            try
            {
                inspection = await manifests.InspectAsync(batchId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsManifestFailure(exception))
            {
                return new AiAnalysisCheckpointResult(
                    null,
                    batchId,
                    AiAnalysisCheckpointOutcome.RecoveryRequired,
                    "AI_CHECKPOINT_MANIFEST_INSPECTION_FAILED");
            }

            if (inspection?.StagedManifest is not null)
            {
                if (!await TryRollbackStageAsync(inspection.StagedManifest).ConfigureAwait(false))
                {
                    return new AiAnalysisCheckpointResult(
                        null,
                        batchId,
                        AiAnalysisCheckpointOutcome.RecoveryRequired,
                        "AI_CHECKPOINT_ORPHAN_STAGE_ROLLBACK_FAILED");
                }

                var afterRollback = await TryInspectAsync(batchId, cancellationToken).ConfigureAwait(false);
                if (afterRollback.Failed || afterRollback.Inspection?.IsStaged == true)
                {
                    return new AiAnalysisCheckpointResult(
                        null,
                        batchId,
                        AiAnalysisCheckpointOutcome.RecoveryRequired,
                        "AI_CHECKPOINT_ORPHAN_STAGE_REMAINS");
                }

                return new AiAnalysisCheckpointResult(
                    null,
                    batchId,
                    AiAnalysisCheckpointOutcome.RolledBackOrphan,
                    "AI_CHECKPOINT_ORPHAN_STAGE_ROLLED_BACK");
            }

            if (inspection?.IsCommitted == true)
            {
                return new AiAnalysisCheckpointResult(
                    null,
                    batchId,
                    AiAnalysisCheckpointOutcome.RecoveryRequired,
                    "AI_CHECKPOINT_COMMITTED_MANIFEST_WITHOUT_JOURNAL");
            }

            return new AiAnalysisCheckpointResult(
                null,
                batchId,
                AiAnalysisCheckpointOutcome.NothingToRecover,
                "AI_CHECKPOINT_NOT_FOUND");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AiAnalysisCheckpointResult?> ValidateCommittedStateAndRemoveOrphanStageAsync(
        AiAnalysisCheckpointCommand command,
        CancellationToken cancellationToken)
    {
        var batchId = command.Manifest.BatchId;
        var inspected = await TryInspectAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (inspected.Failed)
        {
            return new AiAnalysisCheckpointResult(
                null,
                batchId,
                AiAnalysisCheckpointOutcome.RecoveryRequired,
                "AI_CHECKPOINT_MANIFEST_INSPECTION_FAILED");
        }

        var inspection = inspected.Inspection;
        AiAnalysisCheckpointJournal? latestCommitted;
        try
        {
            latestCommitted = await database.FindLatestCommittedCheckpointAsync(
                batchId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return new AiAnalysisCheckpointResult(
                null,
                batchId,
                AiAnalysisCheckpointOutcome.RecoveryRequired,
                "AI_CHECKPOINT_JOURNAL_READ_FAILED");
        }

        var hasCommittedManifest = inspection?.IsCommitted == true;
        if (latestCommitted is null && hasCommittedManifest)
        {
            return new AiAnalysisCheckpointResult(
                null,
                batchId,
                AiAnalysisCheckpointOutcome.RecoveryRequired,
                "AI_CHECKPOINT_COMMITTED_MANIFEST_WITHOUT_JOURNAL");
        }

        if (latestCommitted is not null && !hasCommittedManifest)
        {
            return Result(
                latestCommitted,
                AiAnalysisCheckpointOutcome.RecoveryRequired,
                "AI_CHECKPOINT_COMMITTED_JOURNAL_WITHOUT_MANIFEST");
        }

        if (latestCommitted is not null)
        {
            if (!AiAnalysisCheckpointRules.CommittedMatches(latestCommitted, inspection!))
            {
                return Result(
                    latestCommitted,
                    AiAnalysisCheckpointOutcome.RecoveryRequired,
                    "AI_CHECKPOINT_COMMITTED_MANIFEST_MISMATCH");
            }

            try
            {
                await database.ValidateCheckpointGraphAsync(
                    latestCommitted,
                    inspection!.CommittedManifest!,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return Result(
                    latestCommitted,
                    AiAnalysisCheckpointOutcome.RecoveryRequired,
                    "AI_CHECKPOINT_COMMITTED_GRAPH_INVALID");
            }

            AiAnalysisCheckpointRules.EnsureManifestHistoryRetained(
                inspection!.CommittedManifest!,
                command.Manifest);
        }

        var staged = inspection?.StagedManifest;
        if (staged is null)
        {
            return null;
        }

        if (latestCommitted is not null
            && AiAnalysisCheckpointRules.StageMatches(latestCommitted, staged)
            && AiAnalysisCheckpointRules.CommittedMatches(latestCommitted, inspection!))
        {
            try
            {
                await manifests.CommitAsync(staged, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (Exception exception) when (IsManifestFailure(exception))
            {
                return Result(latestCommitted, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_COMMITTED_STAGE_CLEANUP_FAILED");
            }
        }

        if (!await TryRollbackStageAsync(staged).ConfigureAwait(false))
        {
            return new AiAnalysisCheckpointResult(
                null,
                batchId,
                AiAnalysisCheckpointOutcome.RecoveryRequired,
                "AI_CHECKPOINT_ORPHAN_STAGE_ROLLBACK_FAILED");
        }

        return null;
    }

    private async Task<AiAnalysisCheckpointResult> ResumeJournalAsync(
        AiAnalysisCheckpointJournal journal,
        CancellationToken cancellationToken)
    {
        var inspected = await TryInspectAsync(journal.BatchId, cancellationToken).ConfigureAwait(false);
        if (inspected.Failed)
        {
            return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_MANIFEST_INSPECTION_FAILED");
        }

        var inspection = inspected.Inspection;
        if (journal.State == AiAnalysisCheckpointJournalState.Committed)
        {
            if (inspection is null || !AiAnalysisCheckpointRules.CommittedMatches(journal, inspection))
            {
                return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_COMMITTED_MANIFEST_MISMATCH");
            }

            try
            {
                await database.ValidateCheckpointGraphAsync(
                    journal,
                    inspection.CommittedManifest!,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_COMMITTED_GRAPH_INVALID");
            }

            if (inspection.StagedManifest is not null)
            {
                try
                {
                    if (AiAnalysisCheckpointRules.StageMatches(journal, inspection.StagedManifest))
                    {
                        await manifests.CommitAsync(inspection.StagedManifest, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await manifests.RollbackAsync(inspection.StagedManifest, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (IsManifestFailure(exception))
                {
                    return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_COMMITTED_STAGE_CLEANUP_FAILED");
                }
            }

            return Result(journal, AiAnalysisCheckpointOutcome.Committed, "AI_CHECKPOINT_COMMITTED");
        }

        AiAnalysisCheckpointJournal? newerCommitted;
        try
        {
            newerCommitted = await database.FindLatestCommittedCheckpointAsync(
                journal.BatchId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_LATEST_VERSION_UNKNOWN");
        }

        if (newerCommitted is not null
            && !string.Equals(newerCommitted.CheckpointId, journal.CheckpointId, StringComparison.Ordinal)
            && newerCommitted.CreatedAt >= journal.CreatedAt)
        {
            return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_PENDING_SUPERSEDED");
        }

        if (inspection is not null && AiAnalysisCheckpointRules.CommittedMatches(journal, inspection))
        {
            try
            {
                await database.ValidateCheckpointGraphAsync(
                    journal,
                    inspection.CommittedManifest!,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_PENDING_GRAPH_INVALID");
            }

            return await FinalizeJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        }

        if (inspection is null || !AiAnalysisCheckpointRules.StagedMatches(journal, inspection))
        {
            return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_PENDING_MANIFEST_MISMATCH");
        }

        try
        {
            await database.ValidateCheckpointGraphAsync(
                journal,
                inspection.StagedStateManifest!,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_PENDING_GRAPH_INVALID");
        }

        try
        {
            await manifests.CommitAsync(inspection.StagedManifest!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsManifestFailure(exception))
        {
            var afterFailure = await TryInspectAsync(journal.BatchId, CancellationToken.None).ConfigureAwait(false);
            if (afterFailure.Failed
                || afterFailure.Inspection is null
                || !AiAnalysisCheckpointRules.CommittedMatches(journal, afterFailure.Inspection))
            {
                return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_MANIFEST_COMMIT_UNKNOWN");
            }

            try
            {
                await database.ValidateCheckpointGraphAsync(
                    journal,
                    afterFailure.Inspection.CommittedManifest!,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception validationException) when (IsPersistenceFailure(validationException))
            {
                return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_PENDING_GRAPH_INVALID");
            }

            return await FinalizeJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        }

        var committedInspection = await TryInspectAsync(journal.BatchId, cancellationToken).ConfigureAwait(false);
        if (committedInspection.Failed
            || committedInspection.Inspection is null
            || !AiAnalysisCheckpointRules.CommittedMatches(journal, committedInspection.Inspection))
        {
            return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_COMMITTED_MARKER_NOT_CONFIRMED");
        }

        return await FinalizeJournalAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AiAnalysisCheckpointResult> FinalizeJournalAsync(
        AiAnalysisCheckpointJournal journal,
        CancellationToken cancellationToken)
    {
        try
        {
            var committed = await database.MarkCheckpointCommittedAsync(
                journal,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return Result(committed, AiAnalysisCheckpointOutcome.Committed, "AI_CHECKPOINT_COMMITTED");
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            var persisted = await TryFindCheckpointAsync(journal.CheckpointId).ConfigureAwait(false);
            if (persisted is not null
                && persisted.State == AiAnalysisCheckpointJournalState.Committed
                && string.Equals(persisted.PayloadDigest, journal.PayloadDigest, StringComparison.Ordinal)
                && string.Equals(persisted.StageToken, journal.StageToken, StringComparison.Ordinal))
            {
                return Result(persisted, AiAnalysisCheckpointOutcome.Committed, "AI_CHECKPOINT_COMMITTED_AFTER_DATABASE_UNKNOWN");
            }

            return Result(journal, AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_JOURNAL_FINALIZE_UNKNOWN");
        }
    }

    private async Task<AiAnalysisCheckpointJournal?> TryFindCheckpointAsync(string checkpointId)
    {
        try
        {
            return await database.FindCheckpointAsync(checkpointId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return null;
        }
    }

    private async Task<(AiAnalysisStateManifestInspection? Inspection, bool Failed)> TryInspectAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await manifests.InspectAsync(batchId, cancellationToken).ConfigureAwait(false), false);
        }
        catch (Exception exception) when (IsManifestFailure(exception))
        {
            return (null, true);
        }
    }

    private async Task<bool> TryRollbackStageAsync(StagedAiAnalysisStateManifest staged)
    {
        try
        {
            await manifests.RollbackAsync(staged, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsManifestFailure(exception))
        {
            return false;
        }
    }

    private static bool IsManifestFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException;

    private static bool IsPersistenceFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
        or System.Data.Common.DbException;

    private static AiAnalysisCheckpointResult Result(
        AiAnalysisCheckpointJournal journal,
        AiAnalysisCheckpointOutcome outcome,
        string reasonCode) => new(journal.CheckpointId, journal.BatchId, outcome, reasonCode);

    public void Dispose() => _gate.Dispose();
}
