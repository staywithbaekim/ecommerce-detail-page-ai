using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Domain.Ai;
using Desktop.Infrastructure.Ai;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    private static async Task PersistLedgerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiAnalysisCheckpointGraph graph,
        CancellationToken cancellationToken)
    {
        ValidateLedgerEntry(graph.Request);
        ValidateStoredLedger(graph.Request);
        var existing = await FindLedgerByRequestIdAsync(
            connection, transaction, graph.Request.RequestId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            if (!string.Equals(graph.ExpectedRequestStatus, AiAnalysisSessionStatus.Queued.ToString(), StringComparison.Ordinal)
                || !string.Equals(graph.Request.Status, AiAnalysisSessionStatus.Queued.ToString(), StringComparison.Ordinal)
                || graph.Request.ResultReference is not null
                || graph.Request.CreatedAt != graph.Request.UpdatedAt)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_REQUEST_NOT_FOUND");
            }

            var fingerprintOwner = await FindLedgerByFingerprintAsync(
                connection, transaction, graph.Request.Fingerprint, cancellationToken).ConfigureAwait(false);
            if (fingerprintOwner is not null)
            {
                throw new InvalidOperationException("AI_REQUEST_FINGERPRINT_CONFLICT");
            }

            await InsertLedgerAsync(connection, transaction, graph.Request, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Equals(existing, graph.Request))
        {
            if (!string.Equals(existing.Status, graph.ExpectedRequestStatus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_CHECKPOINT_REQUEST_CAS_CONFLICT");
            }

            return;
        }

        if (!LedgerIdentityMatches(existing, graph.Request)
            || existing.CreatedAt != graph.Request.CreatedAt
            || !string.Equals(existing.Status, graph.ExpectedRequestStatus, StringComparison.Ordinal)
            || !IsAllowedStatusTransition(existing.Status, graph.Request.Status)
            || graph.Request.UpdatedAt < existing.UpdatedAt)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_REQUEST_CAS_CONFLICT");
        }

        if ((existing.Status is "Validated" or "ManualReview" or "PlanClosed")
            && !string.Equals(existing.ResultReference, graph.Request.ResultReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_REQUEST_RESULT_REFERENCE_CONFLICT");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ai_request_ledger
            SET status = $next_status, result_reference = $result_reference, updated_at = $next_updated_at
            WHERE request_id = $request_id
              AND status = $expected_status
              AND updated_at = $expected_updated_at
              AND (($expected_result IS NULL AND result_reference IS NULL) OR result_reference = $expected_result);
            """;
        update.Parameters.AddWithValue("$next_status", graph.Request.Status);
        update.Parameters.AddWithValue("$result_reference", (object?)graph.Request.ResultReference ?? DBNull.Value);
        update.Parameters.AddWithValue("$next_updated_at", graph.Request.UpdatedAt.ToString("O"));
        update.Parameters.AddWithValue("$request_id", graph.Request.RequestId);
        update.Parameters.AddWithValue("$expected_status", existing.Status);
        update.Parameters.AddWithValue("$expected_updated_at", existing.UpdatedAt.ToString("O"));
        update.Parameters.AddWithValue("$expected_result", (object?)existing.ResultReference ?? DBNull.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_REQUEST_CAS_CONFLICT");
        }
    }

    private static async Task PersistSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiAnalysisCheckpointGraph graph,
        CancellationToken cancellationToken)
    {
        var incoming = graph.Session;
        var existing = await LoadSessionForWriteAsync(
            connection, incoming.SessionId, cancellationToken, transaction).ConfigureAwait(false);
        if (existing is null)
        {
            if (graph.ExpectedSessionStatus != AiAnalysisSessionStatus.Queued
                || graph.ExpectedSelectionRevision != 0
                || incoming.Status != AiAnalysisSessionStatus.Queued
                || incoming.SelectionRevision != 0)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_SESSION_NOT_FOUND");
            }

            await UpsertSessionAsync(connection, transaction, incoming, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (SessionPayloadEquals(existing, incoming))
        {
            if (existing.Status != graph.ExpectedSessionStatus
                || existing.SelectionRevision != graph.ExpectedSelectionRevision)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_SESSION_CAS_CONFLICT");
            }

            return;
        }

        if (!IdentityMatches(existing, incoming)
            || existing.Status != graph.ExpectedSessionStatus
            || existing.SelectionRevision != graph.ExpectedSelectionRevision
            || !existing.AllowedRequirementIds.SetEquals(incoming.AllowedRequirementIds)
            || !existing.AllowedAssetIds.SetEquals(incoming.AllowedAssetIds))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_SESSION_CAS_CONFLICT");
        }

        var statusAdvanced = existing.Status != incoming.Status && existing.CanAdvanceTo(incoming.Status);
        var selectionAdvanced = existing.Status is (AiAnalysisSessionStatus.ManualReview
                or AiAnalysisSessionStatus.AiResultUnknown)
            && incoming.Status == existing.Status
            && incoming.SelectionRevision == existing.SelectionRevision + 1;
        if (!statusAdvanced && !selectionAdvanced)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_SESSION_TRANSITION_INVALID");
        }

        if (statusAdvanced
            && incoming.SelectionRevision == existing.SelectionRevision
            && !SessionSelectionPayloadEquals(existing, incoming))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_SESSION_SELECTION_CONFLICT");
        }

        if (statusAdvanced
            && incoming.SelectionRevision != existing.SelectionRevision
            && !(existing.Status == AiAnalysisSessionStatus.Validated
                && incoming.Status == AiAnalysisSessionStatus.ManualReview
                && incoming.SelectionRevision == existing.SelectionRevision + 1))
        {
            throw new InvalidOperationException("AI_CHECKPOINT_SESSION_SELECTION_TRANSITION_INVALID");
        }

        await UpsertSessionAsync(
            connection,
            transaction,
            incoming,
            cancellationToken,
            existing.Status,
            existing.SelectionRevision).ConfigureAwait(false);
    }

    private static async Task PersistUsageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiAnalysisCheckpointGraph graph,
        CancellationToken cancellationToken)
    {
        var existingForRequest = await FindUsageForWriteAsync(
            connection, graph.Request.RequestId, cancellationToken, transaction).ConfigureAwait(false);
        if (graph.Usage is null)
        {
            if (existingForRequest is not null)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_USAGE_OMITTED_CONFLICT");
            }

            return;
        }

        ValidateUsage(graph.Usage);
        if (existingForRequest is not null)
        {
            if (!Equals(existingForRequest, graph.Usage))
            {
                throw new InvalidOperationException("AI_USAGE_REQUEST_CONFLICT");
            }

            return;
        }

        var existingById = await FindUsageByIdForWriteAsync(
            connection, graph.Usage.UsageEventId, cancellationToken, transaction).ConfigureAwait(false);
        if (existingById is not null)
        {
            throw new InvalidOperationException("AI_USAGE_ID_CONFLICT");
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ai_usage_events(
                usage_event_id, request_id, model_config_version, input_units, output_units,
                cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at)
            VALUES(
                $usage_event_id, $request_id, $model_config_version, $input_units, $output_units,
                $cache_hit, $retry_count, $provider_cost_amount_text, $provider_cost_currency, $recorded_at);
            """;
        insert.Parameters.AddWithValue("$usage_event_id", graph.Usage.UsageEventId);
        insert.Parameters.AddWithValue("$request_id", graph.Usage.RequestId);
        insert.Parameters.AddWithValue("$model_config_version", graph.Usage.ModelConfigVersion);
        insert.Parameters.AddWithValue("$input_units", graph.Usage.InputUnits);
        insert.Parameters.AddWithValue("$output_units", graph.Usage.OutputUnits);
        insert.Parameters.AddWithValue("$cache_hit", graph.Usage.CacheHit ? 1 : 0);
        insert.Parameters.AddWithValue("$retry_count", graph.Usage.RetryCount);
        insert.Parameters.AddWithValue("$provider_cost_amount_text", (object?)graph.Usage.ProviderCostAmount?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$provider_cost_currency", (object?)graph.Usage.ProviderCostCurrency ?? DBNull.Value);
        insert.Parameters.AddWithValue("$recorded_at", graph.Usage.RecordedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistCacheAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiAnalysisCheckpointGraph graph,
        CancellationToken cancellationToken)
    {
        if (graph.Usage is not null && graph.Cache is null)
        {
            if (await FindCacheByUsageIdAsync(connection, transaction, graph.Usage.UsageEventId, cancellationToken)
                .ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("AI_CHECKPOINT_CACHE_OMITTED_CONFLICT");
            }

            return;
        }

        if (graph.Cache is null)
        {
            return;
        }

        ValidateCacheReference(graph.Cache);
        var existing = await FindCacheReferenceForWriteAsync(
            connection, graph.Cache.CacheKey, cancellationToken, transaction).ConfigureAwait(false);
        if (existing is not null)
        {
            if (Equals(existing, graph.Cache))
            {
                return;
            }

            if (!CachePayloadEqualsExceptReuse(existing, graph.Cache)
                || !existing.IsReusable
                || graph.Cache.IsReusable)
            {
                throw new InvalidOperationException("AI_CACHE_KEY_CONFLICT");
            }

            await using var invalidate = connection.CreateCommand();
            invalidate.Transaction = transaction;
            invalidate.CommandText = "UPDATE ai_cache_references SET is_reusable = 0 WHERE cache_key = $cache_key AND is_reusable = 1;";
            invalidate.Parameters.AddWithValue("$cache_key", graph.Cache.CacheKey);
            if (await invalidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("AI_CACHE_INVALIDATION_CAS_CONFLICT");
            }

            return;
        }

        if (!graph.Cache.IsReusable)
        {
            throw new InvalidOperationException("AI_CACHE_TOMBSTONE_ORIGIN_MISSING");
        }

        var usageOwner = await FindCacheByUsageIdAsync(
            connection, transaction, graph.Cache.UsageEventId, cancellationToken).ConfigureAwait(false);
        if (usageOwner is not null)
        {
            throw new InvalidOperationException("AI_CACHE_USAGE_CONFLICT");
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ai_cache_references(
                cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at, is_reusable)
            VALUES(
                $cache_key, $result_reference, $consent_id, $model_config_version, $usage_event_id, $created_at, $is_reusable);
            """;
        insert.Parameters.AddWithValue("$cache_key", graph.Cache.CacheKey);
        insert.Parameters.AddWithValue("$result_reference", graph.Cache.ResultReference);
        insert.Parameters.AddWithValue("$consent_id", graph.Cache.ConsentId);
        insert.Parameters.AddWithValue("$model_config_version", graph.Cache.ModelConfigVersion);
        insert.Parameters.AddWithValue("$usage_event_id", graph.Cache.UsageEventId);
        insert.Parameters.AddWithValue("$created_at", graph.Cache.CreatedAt.ToString("O"));
        insert.Parameters.AddWithValue("$is_reusable", graph.Cache.IsReusable ? 1 : 0);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool CachePayloadEqualsExceptReuse(AiCacheReference left, AiCacheReference right) =>
        string.Equals(left.CacheKey, right.CacheKey, StringComparison.Ordinal)
        && string.Equals(left.ResultReference, right.ResultReference, StringComparison.Ordinal)
        && string.Equals(left.ConsentId, right.ConsentId, StringComparison.Ordinal)
        && string.Equals(left.ModelConfigVersion, right.ModelConfigVersion, StringComparison.Ordinal)
        && string.Equals(left.UsageEventId, right.UsageEventId, StringComparison.Ordinal)
        && left.CreatedAt == right.CreatedAt;

    private static async Task<AiAnalysisCheckpointCommand> RehydrateCheckpointCommandAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AiAnalysisCheckpointJournal journal,
        AiAnalysisStateManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(manifest.ManifestId, journal.ManifestId, StringComparison.Ordinal)
            || !string.Equals(manifest.BatchId, journal.BatchId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_CHECKPOINT_MANIFEST_JOURNAL_MISMATCH");
        }

        var graphs = new List<AiAnalysisCheckpointGraph>(journal.Items.Count);
        foreach (var item in journal.Items.OrderBy(item => item.RequestId, StringComparer.Ordinal))
        {
            var request = await FindLedgerByRequestIdAsync(
                connection, transaction, item.RequestId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI_CHECKPOINT_REQUEST_MISSING");
            var session = await LoadSessionForWriteAsync(
                connection, item.SessionId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI_CHECKPOINT_SESSION_MISSING");
            var usage = item.UsageEventId is null
                ? null
                : await FindUsageByIdForWriteAsync(
                    connection, item.UsageEventId, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("AI_CHECKPOINT_USAGE_MISSING");
            var cache = item.CacheKey is null
                ? null
                : await FindCacheReferenceForWriteAsync(
                    connection, item.CacheKey, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("AI_CHECKPOINT_CACHE_MISSING");

            var usageForRequest = await FindUsageForWriteAsync(
                connection, request.RequestId, cancellationToken, transaction).ConfigureAwait(false);
            if ((usage is null) != (usageForRequest is null)
                || (usage is not null && !Equals(usage, usageForRequest)))
            {
                throw new InvalidDataException("AI_CHECKPOINT_USAGE_GRAPH_INCOMPLETE");
            }

            if (usage is not null)
            {
                var cacheForUsage = await FindCacheByUsageIdAsync(
                    connection, transaction, usage.UsageEventId, cancellationToken).ConfigureAwait(false);
                if ((cache is null) != (cacheForUsage is null)
                    || (cache is not null && !Equals(cache, cacheForUsage)))
                {
                    throw new InvalidDataException("AI_CHECKPOINT_CACHE_GRAPH_INCOMPLETE");
                }
            }

            graphs.Add(new AiAnalysisCheckpointGraph(
                request,
                item.ExpectedRequestStatus,
                session,
                item.ExpectedSessionStatus,
                item.ExpectedSelectionRevision,
                usage,
                cache));
        }

        AiAnalysisSchedulingState? schedulerState = null;
        if (journal.SchedulerStateKey is not null)
        {
            var persistedSchedulerState = await LoadSchedulingStateForWriteAsync(
                connection,
                transaction,
                journal.SchedulerStateKey,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI_CHECKPOINT_SCHEDULER_STATE_MISSING");
            if (journal.SchedulerStateDigest is null
                || !string.Equals(
                    journal.SchedulerStateDigest,
                    AiAnalysisCheckpointRules.ComputeSchedulerStateDigest(persistedSchedulerState),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("AI_CHECKPOINT_SCHEDULER_STATE_MISMATCH");
            }

            if (journal.SchedulerState is not null
                && !SchedulingStateEquals(journal.SchedulerState, persistedSchedulerState))
            {
                throw new InvalidDataException("AI_CHECKPOINT_SCHEDULER_STATE_MISMATCH");
            }

            schedulerState = persistedSchedulerState;
        }

        return new AiAnalysisCheckpointCommand(
            journal.CheckpointId,
            manifest,
            graphs,
            journal.CreatedAt,
            journal.SchedulerStateKey,
            schedulerState);
    }

    private static async Task InsertLedgerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiRequestLedgerEntry entry,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ai_request_ledger(
                request_id, fingerprint, batch_id, style_id, status, result_reference, created_at, updated_at)
            VALUES(
                $request_id, $fingerprint, $batch_id, $style_id, $status, $result_reference, $created_at, $updated_at);
            """;
        insert.Parameters.AddWithValue("$request_id", entry.RequestId);
        insert.Parameters.AddWithValue("$fingerprint", entry.Fingerprint);
        insert.Parameters.AddWithValue("$batch_id", entry.BatchId);
        insert.Parameters.AddWithValue("$style_id", entry.StyleId);
        insert.Parameters.AddWithValue("$status", entry.Status);
        insert.Parameters.AddWithValue("$result_reference", (object?)entry.ResultReference ?? DBNull.Value);
        insert.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
        insert.Parameters.AddWithValue("$updated_at", entry.UpdatedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiAnalysisSession session,
        CancellationToken cancellationToken,
        AiAnalysisSessionStatus? expectedStatus = null,
        long? expectedRevision = null)
    {
        await RequireUnfrozenSelectionChangeAsync(connection, transaction, session, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedStatus is null
            ? """
                INSERT INTO ai_analysis_sessions(
                    session_id, batch_id, style_id, template_snapshot_id, analysis_key, status,
                    selection_revision, allowed_requirement_ids_json, allowed_asset_ids_json,
                    locked_selections_json, has_verified_result, updated_at)
                VALUES($session_id, $batch_id, $style_id, $template_snapshot_id, $analysis_key, $status,
                    $selection_revision, $allowed_requirement_ids_json, $allowed_asset_ids_json,
                    $locked_selections_json, $has_verified_result, $updated_at);
                """
            : """
                UPDATE ai_analysis_sessions
                SET status = $status,
                    selection_revision = $selection_revision,
                    allowed_requirement_ids_json = $allowed_requirement_ids_json,
                    allowed_asset_ids_json = $allowed_asset_ids_json,
                    locked_selections_json = $locked_selections_json,
                    has_verified_result = $has_verified_result,
                    updated_at = $updated_at
                WHERE session_id = $session_id
                  AND status = $expected_status
                  AND selection_revision = $expected_selection_revision
                  AND batch_id = $batch_id
                  AND style_id = $style_id
                  AND template_snapshot_id = $template_snapshot_id
                  AND analysis_key = $analysis_key;
                """;
        command.Parameters.AddWithValue("$session_id", session.SessionId);
        command.Parameters.AddWithValue("$batch_id", session.BatchId);
        command.Parameters.AddWithValue("$style_id", session.StyleId);
        command.Parameters.AddWithValue("$template_snapshot_id", session.TemplateSnapshotId);
        command.Parameters.AddWithValue("$analysis_key", session.AnalysisKey);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$selection_revision", session.SelectionRevision);
        command.Parameters.AddWithValue("$allowed_requirement_ids_json", System.Text.Json.JsonSerializer.Serialize(session.AllowedRequirementIds.Order(StringComparer.Ordinal), AiJsonOptions));
        command.Parameters.AddWithValue("$allowed_asset_ids_json", System.Text.Json.JsonSerializer.Serialize(session.AllowedAssetIds.Order(StringComparer.Ordinal), AiJsonOptions));
        command.Parameters.AddWithValue("$locked_selections_json", System.Text.Json.JsonSerializer.Serialize(session.LockedSelections.Values.OrderBy(item => item.RequirementId, StringComparer.Ordinal), AiJsonOptions));
        command.Parameters.AddWithValue("$has_verified_result", session.HasVerifiedResult ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        if (expectedStatus is not null)
        {
            command.Parameters.AddWithValue("$expected_status", expectedStatus.Value.ToString());
            command.Parameters.AddWithValue("$expected_selection_revision", expectedRevision!.Value);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("AI_CHECKPOINT_SESSION_CAS_CONFLICT");
        }
    }

    private static async Task InsertJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiAnalysisCheckpointJournal journal,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ai_analysis_checkpoint_journal(
                    checkpoint_id, batch_id, manifest_id, stage_token, manifest_reference,
                    manifest_digest, payload_digest, state, created_at, updated_at,
                    scheduler_state_key, scheduler_state_digest, scheduler_state_json)
                VALUES(
                    $checkpoint_id, $batch_id, $manifest_id, $stage_token, $manifest_reference,
                    $manifest_digest, $payload_digest, $state, $created_at, $updated_at,
                    $scheduler_state_key, $scheduler_state_digest, $scheduler_state_json);
                """;
            AddJournalParameters(insert, journal);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var index = 0;
        foreach (var item in journal.Items.OrderBy(item => item.RequestId, StringComparer.Ordinal))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ai_analysis_checkpoint_items(
                    checkpoint_id, order_index, request_id, session_id, usage_event_id, cache_key,
                    expected_request_status, expected_session_status, expected_selection_revision)
                VALUES(
                    $checkpoint_id, $order_index, $request_id, $session_id, $usage_event_id, $cache_key,
                    $expected_request_status, $expected_session_status, $expected_selection_revision);
                """;
            insert.Parameters.AddWithValue("$checkpoint_id", journal.CheckpointId);
            insert.Parameters.AddWithValue("$order_index", index++);
            insert.Parameters.AddWithValue("$request_id", item.RequestId);
            insert.Parameters.AddWithValue("$session_id", item.SessionId);
            insert.Parameters.AddWithValue("$usage_event_id", (object?)item.UsageEventId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cache_key", (object?)item.CacheKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("$expected_request_status", item.ExpectedRequestStatus);
            insert.Parameters.AddWithValue("$expected_session_status", item.ExpectedSessionStatus.ToString());
            insert.Parameters.AddWithValue("$expected_selection_revision", item.ExpectedSelectionRevision);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AiAnalysisCheckpointJournal?> LoadCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string predicate,
        string value,
        CancellationToken cancellationToken,
        string? orderBy = null)
    {
        try
        {
            string? checkpointId = null;
            string? batchId = null;
            string? manifestId = null;
            string? stageToken = null;
            string? manifestReference = null;
            string? manifestDigest = null;
            string? payloadDigest = null;
            string? state = null;
            string? createdAt = null;
            string? updatedAt = null;
            string? schedulerStateKey = null;
            string? schedulerStateDigest = null;
            string? schedulerStateJson = null;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT checkpoint_id, batch_id, manifest_id, stage_token, manifest_reference,
                           manifest_digest, payload_digest, state, created_at, updated_at,
                           scheduler_state_key, scheduler_state_digest, scheduler_state_json
                    FROM ai_analysis_checkpoint_journal
                    WHERE {predicate}
                    {(orderBy is null ? string.Empty : $"ORDER BY {orderBy}")}
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$value", value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                checkpointId = reader.GetString(0);
                batchId = reader.GetString(1);
                manifestId = reader.GetString(2);
                stageToken = reader.GetString(3);
                manifestReference = reader.GetString(4);
                manifestDigest = reader.GetString(5);
                payloadDigest = reader.GetString(6);
                state = reader.GetString(7);
                createdAt = reader.GetString(8);
                updatedAt = reader.GetString(9);
                schedulerStateKey = reader.IsDBNull(10) ? null : reader.GetString(10);
                schedulerStateDigest = reader.IsDBNull(11) ? null : reader.GetString(11);
                schedulerStateJson = reader.IsDBNull(12) ? null : reader.GetString(12);
            }

            var items = new List<AiAnalysisCheckpointJournalItem>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT request_id, session_id, usage_event_id, cache_key,
                           expected_request_status, expected_session_status, expected_selection_revision
                    FROM ai_analysis_checkpoint_items
                    WHERE checkpoint_id = $checkpoint_id
                    ORDER BY order_index;
                    """;
                command.Parameters.AddWithValue("$checkpoint_id", checkpointId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var expectedSessionStatus = Enum.Parse<AiAnalysisSessionStatus>(reader.GetString(5), false);
                    if (!Enum.IsDefined(expectedSessionStatus))
                    {
                        throw new InvalidDataException("AI_CHECKPOINT_JOURNAL_CORRUPT");
                    }

                    items.Add(new AiAnalysisCheckpointJournalItem(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetString(4),
                        expectedSessionStatus,
                        reader.GetInt64(6)));
                }
            }

            var parsedState = Enum.Parse<AiAnalysisCheckpointJournalState>(state!, false);
            AiAnalysisSchedulingState? schedulerState = null;
            if (schedulerStateJson is not null)
            {
                try
                {
                    schedulerState = System.Text.Json.JsonSerializer.Deserialize<AiAnalysisSchedulingState>(schedulerStateJson, AiJsonOptions)
                        ?? throw new InvalidDataException("AI_CHECKPOINT_JOURNAL_CORRUPT");
                    ValidateSchedulingState(schedulerState);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("AI_CHECKPOINT_JOURNAL_CORRUPT", exception);
                }
            }

            var journal = new AiAnalysisCheckpointJournal(
                checkpointId!, batchId!, manifestId!, stageToken!, manifestReference!, manifestDigest!, payloadDigest!,
                items, parsedState, ParseTime(createdAt!), ParseTime(updatedAt!),
                schedulerStateKey, schedulerStateDigest, schedulerState);
            ValidateStoredJournal(journal);
            return journal;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException
            or FormatException or OverflowException or ArgumentException or SqliteException)
        {
            throw new InvalidDataException("AI_CHECKPOINT_JOURNAL_CORRUPT", exception);
        }
    }


}
