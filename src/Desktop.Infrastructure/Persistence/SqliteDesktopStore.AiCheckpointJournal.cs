using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Domain.Ai;
using Desktop.Infrastructure.Ai;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    public async Task<AiAnalysisCheckpointJournal?> FindCheckpointAsync(
        string checkpointId,
        CancellationToken cancellationToken)
    {
        AiAnalysisCheckpointRules.ValidateIdentifier(checkpointId, "AI_CHECKPOINT_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadCheckpointAsync(connection, null, "checkpoint_id = $value", checkpointId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AiAnalysisCheckpointJournal?> FindPendingCheckpointAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        AiAnalysisCheckpointRules.ValidateIdentifier(batchId, "AI_BATCH_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadCheckpointAsync(
            connection,
            null,
            "batch_id = $value AND state = 'Pending'",
            batchId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiAnalysisCheckpointJournal?> FindLatestCommittedCheckpointAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        AiAnalysisCheckpointRules.ValidateIdentifier(batchId, "AI_BATCH_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadCheckpointAsync(
            connection,
            null,
            "batch_id = $value AND state = 'Committed'",
            batchId,
            cancellationToken,
            "created_at DESC, checkpoint_id DESC").ConfigureAwait(false);
    }

    public Task<AiAnalysisCheckpointJournal> PrepareCheckpointAsync(
        AiAnalysisCheckpointCommand command,
        StagedAiAnalysisStateManifest stagedManifest,
        string payloadDigest,
        CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            AiAnalysisCheckpointRules.Validate(command);
            ValidateStagedManifest(command, stagedManifest, payloadDigest);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var existing = await LoadCheckpointAsync(
                connection,
                transaction,
                "checkpoint_id = $value",
                command.CheckpointId,
                token).ConfigureAwait(false);
            if (existing is not null)
            {
                AiAnalysisCheckpointRules.EnsureCommandMatchesJournal(command, payloadDigest, existing);
                if (!AiAnalysisCheckpointRules.StageMatches(existing, stagedManifest))
                {
                    throw new InvalidOperationException("AI_CHECKPOINT_STAGE_JOURNAL_CONFLICT");
                }

                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return existing;
            }

            var pending = await LoadCheckpointAsync(
                connection,
                transaction,
                "batch_id = $value AND state = 'Pending'",
                command.Manifest.BatchId,
                token).ConfigureAwait(false);
            if (pending is not null)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_BATCH_PENDING");
            }

            var latestCommitted = await LoadCheckpointAsync(
                connection,
                transaction,
                "batch_id = $value AND state = 'Committed'",
                command.Manifest.BatchId,
                token,
                "created_at DESC, checkpoint_id DESC").ConfigureAwait(false);
            if (latestCommitted is not null && command.CreatedAt <= latestCommitted.CreatedAt)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_STALE_VERSION");
            }

            if (latestCommitted is not null)
            {
                EnsurePriorGraphRetained(command, latestCommitted);
                EnsurePriorSchedulerStateRetained(command, latestCommitted);
            }

            foreach (var graph in command.Graphs.OrderBy(item => item.Request.RequestId, StringComparer.Ordinal))
            {
                await PersistLedgerAsync(connection, transaction, graph, token).ConfigureAwait(false);
                await PersistSessionAsync(connection, transaction, graph, token).ConfigureAwait(false);
                await PersistUsageAsync(connection, transaction, graph, token).ConfigureAwait(false);
                await PersistCacheAsync(connection, transaction, graph, token).ConfigureAwait(false);
            }

            if (command.SchedulerState is not null)
            {
                await UpsertSchedulingStateAsync(
                    connection,
                    transaction,
                    command.SchedulerStateKey!,
                    command.SchedulerState,
                    token).ConfigureAwait(false);
            }

            var schedulerDigest = command.SchedulerState is null
                ? null
                : AiAnalysisCheckpointRules.ComputeSchedulerStateDigest(command.SchedulerState);
            var journal = new AiAnalysisCheckpointJournal(
                command.CheckpointId,
                command.Manifest.BatchId,
                command.Manifest.ManifestId,
                stagedManifest.StageToken,
                stagedManifest.ManifestReference,
                stagedManifest.ManifestDigest,
                payloadDigest,
                command.Graphs.Select(AiAnalysisCheckpointRules.ToJournalItem).ToArray(),
                AiAnalysisCheckpointJournalState.Pending,
                command.CreatedAt,
                command.CreatedAt,
                command.SchedulerStateKey,
                schedulerDigest,
                command.SchedulerState);

            await InsertJournalAsync(connection, transaction, journal, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return journal;
        }, cancellationToken);

    public async Task ValidateCheckpointGraphAsync(
        AiAnalysisCheckpointJournal journal,
        AiAnalysisStateManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(manifest);
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = await RehydrateCheckpointCommandAsync(connection, null, journal, manifest, cancellationToken)
            .ConfigureAwait(false);
        var digest = AiAnalysisCheckpointRules.ComputePayloadDigest(command);
        AiAnalysisCheckpointRules.EnsureCommandMatchesJournal(command, digest, journal);
    }

    public Task<AiAnalysisCheckpointJournal> MarkCheckpointCommittedAsync(
        AiAnalysisCheckpointJournal journal,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ArgumentNullException.ThrowIfNull(journal);
            if (committedAt == default
                || committedAt.Offset != TimeSpan.Zero
                || committedAt < journal.CreatedAt)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_COMMITTED_TIME_INVALID");
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var persisted = await LoadCheckpointAsync(
                connection,
                transaction,
                "checkpoint_id = $value",
                journal.CheckpointId,
                token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_CHECKPOINT_JOURNAL_NOT_FOUND");
            EnsureSameJournalIdentity(journal, persisted);
            if (persisted.State == AiAnalysisCheckpointJournalState.Committed)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return persisted;
            }

            var manifest = ManifestFromJournal(persisted);
            var command = await RehydrateCheckpointCommandAsync(
                connection,
                transaction,
                persisted,
                manifest,
                token).ConfigureAwait(false);
            var payloadDigest = AiAnalysisCheckpointRules.ComputePayloadDigest(command);
            AiAnalysisCheckpointRules.EnsureCommandMatchesJournal(command, payloadDigest, persisted);

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ai_analysis_checkpoint_journal
                SET state = 'Committed', updated_at = $updated_at
                WHERE checkpoint_id = $checkpoint_id
                  AND state = 'Pending'
                  AND stage_token = $stage_token
                  AND manifest_digest = $manifest_digest
                  AND payload_digest = $payload_digest;
                """;
            update.Parameters.AddWithValue("$updated_at", committedAt.ToString("O"));
            update.Parameters.AddWithValue("$checkpoint_id", journal.CheckpointId);
            update.Parameters.AddWithValue("$stage_token", journal.StageToken);
            update.Parameters.AddWithValue("$manifest_digest", journal.ManifestDigest);
            update.Parameters.AddWithValue("$payload_digest", journal.PayloadDigest);
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_JOURNAL_CAS_CONFLICT");
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return journal with { State = AiAnalysisCheckpointJournalState.Committed, UpdatedAt = committedAt };
        }, cancellationToken);


}
