using System.Globalization;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Domain.Ai;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    private static readonly JsonSerializerOptions AiJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AiRequestLedgerEntry?> FindRequestByFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken)
    {
        ValidateDigest(fingerprint, "AI_FINGERPRINT_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_id, fingerprint, batch_id, style_id, status, result_reference, created_at, updated_at FROM ai_request_ledger WHERE fingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLedger(reader) : null;
    }

    public Task<AiRequestLedgerEntry> RecordRequestAsync(
        AiRequestLedgerEntry entry,
        CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ValidateInitialLedgerEntry(entry);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var existing = await FindLedgerByRequestIdAsync(connection, transaction, entry.RequestId, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!LedgerIdentityMatches(existing, entry))
                {
                    throw new InvalidOperationException("AI_REQUEST_ID_CONFLICT");
                }

                if (!string.Equals(existing.Status, AiAnalysisSessionStatus.Queued.ToString(), StringComparison.Ordinal)
                    || existing.ResultReference is not null
                    || existing.CreatedAt != entry.CreatedAt
                    || existing.UpdatedAt != entry.UpdatedAt)
                {
                    throw new InvalidOperationException("AI_REQUEST_INITIAL_STATE_CONFLICT");
                }

                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return existing;
            }

            var fingerprintOwner = await FindLedgerByFingerprintAsync(connection, transaction, entry.Fingerprint, token).ConfigureAwait(false);
            if (fingerprintOwner is not null)
            {
                if (!string.Equals(fingerprintOwner.BatchId, entry.BatchId, StringComparison.Ordinal)
                    || !string.Equals(fingerprintOwner.StyleId, entry.StyleId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("AI_REQUEST_FINGERPRINT_SEMANTIC_CONFLICT");
                }

                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return fingerprintOwner;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ai_request_ledger(request_id, fingerprint, batch_id, style_id, status, result_reference, created_at, updated_at)
                VALUES($request_id, $fingerprint, $batch_id, $style_id, $status, $result_reference, $created_at, $updated_at);
                """;
            command.Parameters.AddWithValue("$request_id", entry.RequestId);
            command.Parameters.AddWithValue("$fingerprint", entry.Fingerprint);
            command.Parameters.AddWithValue("$batch_id", entry.BatchId);
            command.Parameters.AddWithValue("$style_id", entry.StyleId);
            command.Parameters.AddWithValue("$status", entry.Status);
            command.Parameters.AddWithValue("$result_reference", (object?)entry.ResultReference ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated_at", entry.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return entry;
        }, cancellationToken);

    public Task<AiRequestLedgerEntry> UpdateRequestStatusAsync(
        string requestId,
        string expectedStatus,
        string nextStatus,
        string? resultReference,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ValidateIdentifier(requestId, "AI_REQUEST_ID_INVALID");
            ValidateStatus(expectedStatus);
            ValidateStatus(nextStatus);
            ValidateResultReference(resultReference);
            // The admission check and Queued -> Uploading reservation must be one
            // SQLite write transaction, including across separate store instances.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var existing = await FindLedgerByRequestIdAsync(connection, transaction, requestId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_REQUEST_NOT_FOUND");
            if (!string.Equals(existing.Status, expectedStatus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_REQUEST_STATUS_CONFLICT");
            }

            if (!IsAllowedStatusTransition(expectedStatus, nextStatus))
            {
                throw new InvalidOperationException("AI_REQUEST_STATUS_TRANSITION_INVALID");
            }
            if (existing.StyleId is "template_rules" or "reference_learning" && nextStatus == "Queued")
                throw new InvalidOperationException(existing.StyleId == "template_rules"
                    ? "TPL_RULE_AI_REQUEST_ALREADY_FINISHED" : "TPL_LEARN_AI_REQUEST_ALREADY_FINISHED");

            if (updatedAt < existing.UpdatedAt)
            {
                throw new InvalidOperationException("AI_LEDGER_TIME_INVALID");
            }

            var requiresResult = nextStatus is "Validated" or "ManualReview" or "PlanClosed";
            if (requiresResult != (resultReference is not null))
            {
                throw new InvalidOperationException("AI_REQUEST_RESULT_REFERENCE_STATE_INVALID");
            }

            if ((existing.Status is "Validated" or "ManualReview" or "PlanClosed")
                && (nextStatus is "ManualReview" or "PlanClosed")
                && !string.Equals(existing.ResultReference, resultReference, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_REQUEST_RESULT_REFERENCE_CONFLICT");
            }

            if (existing.StyleId is "template_rules" or "reference_learning" && expectedStatus == "Queued" && nextStatus == "Uploading")
                await RequireTemplateRequestBudgetAsync(connection, transaction, existing.BatchId, existing.StyleId, token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE ai_request_ledger SET status = $next_status, result_reference = $result_reference, updated_at = $updated_at WHERE request_id = $request_id AND status = $expected_status;";
            command.Parameters.AddWithValue("$next_status", nextStatus);
            command.Parameters.AddWithValue("$result_reference", (object?)resultReference ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
            command.Parameters.AddWithValue("$request_id", requestId);
            command.Parameters.AddWithValue("$expected_status", expectedStatus);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("AI_REQUEST_STATUS_CONFLICT");
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return existing with { Status = nextStatus, ResultReference = resultReference, UpdatedAt = updatedAt };
        }, cancellationToken);

    private static async Task RequireTemplateRequestBudgetAsync(SqliteConnection connection,
        SqliteTransaction transaction, string batchId, string styleId, CancellationToken token)
    {
        // Rule editing has no analysis scheduler/session. Reuse its frozen V1
        // limits and pricing against the existing durable ledger, not a second
        // in-memory counter. Unknown/failed attempts never lose their reservation.
        var policy = AiAnalysisProductionPolicy.BudgetV1;
        // A different model/config/prompt produces a different cache identity, but
        // must never turn an unresolved paid request into an automatic resubmission.
        // Check the original draft scope in the same transaction as admission; this
        // includes legacy requests and survives reopening SQLite/provider changes.
        await using var unresolved = connection.CreateCommand();
        unresolved.Transaction = transaction;
        unresolved.CommandText = "SELECT COUNT(*) FROM ai_request_ledger WHERE batch_id = $batch_id AND style_id = $style_id AND status IN ('Uploading', 'Analysing', 'AiResultUnknown');";
        unresolved.Parameters.AddWithValue("$batch_id", batchId);
        unresolved.Parameters.AddWithValue("$style_id", styleId);
        if (Convert.ToInt64(await unresolved.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            throw new Desktop.Domain.Templates.TemplateRuleViolationException(styleId == "template_rules"
                ? "TPL_RULE_AI_RESULT_UNKNOWN" : "TPL_LEARN_AI_RESULT_UNKNOWN");
        await using var totals = connection.CreateCommand();
        totals.Transaction = transaction;
        totals.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN r.status <> 'Queued' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(u.input_units), 0), COALESCE(SUM(u.output_units), 0)
            FROM ai_request_ledger r LEFT JOIN ai_usage_events u ON u.request_id = r.request_id
            WHERE r.batch_id = $batch_id AND r.style_id = $style_id;
            """;
        totals.Parameters.AddWithValue("$batch_id", batchId);
        totals.Parameters.AddWithValue("$style_id", styleId);
        long requests, input, output;
        await using (var reader = await totals.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("AI_BUDGET_STATE_INVALID");
            requests = reader.GetInt64(0); input = reader.GetInt64(1); output = reader.GetInt64(2);
        }
        var estimated = input / 1000m * policy.Pricing.InputPerThousandTokens
            + output / 1000m * policy.Pricing.OutputPerThousandTokens;
        if (requests >= policy.MaxRequests || input >= policy.MaxInputTokens
            || output >= policy.MaxOutputTokens || estimated >= policy.MaxEstimatedCost)
            throw new Desktop.Domain.Templates.TemplateRuleViolationException("AI_BUDGET_HARD_STOP");

        await using var active = connection.CreateCommand();
        active.Transaction = transaction;
        active.CommandText = "SELECT COUNT(*) FROM ai_request_ledger WHERE style_id IN ('template_rules', 'reference_learning') AND status IN ('Uploading', 'Analysing');";
        if (Convert.ToInt64(await active.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture)
            >= AiAnalysisProductionPolicy.MaxConcurrentRequests)
            throw new Desktop.Domain.Templates.TemplateRuleViolationException("AI_CONCURRENCY_LIMIT");
    }

    public Task SaveSessionAsync(AiAnalysisSession session, CancellationToken cancellationToken, long? expectedSelectionRevision = null) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            await RequireUnfrozenSelectionChangeAsync(connection, null, session, token).ConfigureAwait(false);
            var existing = await LoadSessionForWriteAsync(connection, session.SessionId, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!IdentityMatches(existing, session))
                {
                    throw new InvalidOperationException("AI_SESSION_IDENTITY_CONFLICT");
                }

                if (expectedSelectionRevision is null)
                {
                    if (SessionPayloadEquals(existing, session))
                    {
                        return true;
                    }

                    throw new InvalidOperationException("AI_SESSION_EXPECTED_REVISION_REQUIRED");
                }

                if (expectedSelectionRevision.Value != existing.SelectionRevision)
                {
                    throw new InvalidOperationException("AI_SESSION_REVISION_CONFLICT");
                }

                if (!existing.AllowedRequirementIds.SetEquals(session.AllowedRequirementIds)
                    || !existing.AllowedAssetIds.SetEquals(session.AllowedAssetIds))
                {
                    throw new InvalidOperationException("AI_SESSION_INPUT_CLOSURE_CONFLICT");
                }

                var payloadEquals = SessionPayloadEquals(existing, session);
                if (payloadEquals)
                {
                    return true;
                }

                if (session.SelectionRevision < existing.SelectionRevision)
                {
                    throw new InvalidOperationException("AI_SESSION_STALE_WRITE");
                }

                if (session.SelectionRevision == existing.SelectionRevision
                    && (existing.Status == session.Status || !SessionSelectionPayloadEquals(existing, session)))
                {
                    throw new InvalidOperationException("AI_SESSION_STALE_WRITE");
                }

                if (existing.Status != session.Status && !existing.CanAdvanceTo(session.Status))
                {
                    throw new InvalidOperationException("AI_SESSION_STATUS_TRANSITION_INVALID");
                }
            }
            else if (expectedSelectionRevision is not null and not 0)
            {
                throw new InvalidOperationException("AI_SESSION_REVISION_CONFLICT");
            }

            if (existing is null && session.Status != AiAnalysisSessionStatus.Queued)
            {
                throw new InvalidOperationException("AI_SESSION_INITIAL_STATE_INVALID");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ai_analysis_sessions(
                    session_id, batch_id, style_id, template_snapshot_id, analysis_key, status,
                    selection_revision, allowed_requirement_ids_json, allowed_asset_ids_json,
                    locked_selections_json, has_verified_result, updated_at)
                VALUES($session_id, $batch_id, $style_id, $template_snapshot_id, $analysis_key, $status,
                    $selection_revision, $allowed_requirement_ids_json, $allowed_asset_ids_json,
                    $locked_selections_json, $has_verified_result, $updated_at)
                ON CONFLICT(session_id) DO UPDATE SET
                    status = excluded.status,
                    selection_revision = excluded.selection_revision,
                    allowed_requirement_ids_json = excluded.allowed_requirement_ids_json,
                    allowed_asset_ids_json = excluded.allowed_asset_ids_json,
                    locked_selections_json = excluded.locked_selections_json,
                    has_verified_result = excluded.has_verified_result,
                    updated_at = excluded.updated_at
                WHERE ai_analysis_sessions.batch_id = excluded.batch_id
                  AND ai_analysis_sessions.style_id = excluded.style_id
                  AND ai_analysis_sessions.template_snapshot_id = excluded.template_snapshot_id
                  AND ai_analysis_sessions.analysis_key = excluded.analysis_key;
                """;
            command.Parameters.AddWithValue("$session_id", session.SessionId);
            command.Parameters.AddWithValue("$batch_id", session.BatchId);
            command.Parameters.AddWithValue("$style_id", session.StyleId);
            command.Parameters.AddWithValue("$template_snapshot_id", session.TemplateSnapshotId);
            command.Parameters.AddWithValue("$analysis_key", session.AnalysisKey);
            command.Parameters.AddWithValue("$status", session.Status.ToString());
            command.Parameters.AddWithValue("$selection_revision", session.SelectionRevision);
            command.Parameters.AddWithValue("$allowed_requirement_ids_json", JsonSerializer.Serialize(session.AllowedRequirementIds.Order(StringComparer.Ordinal), AiJsonOptions));
            command.Parameters.AddWithValue("$allowed_asset_ids_json", JsonSerializer.Serialize(session.AllowedAssetIds.Order(StringComparer.Ordinal), AiJsonOptions));
            command.Parameters.AddWithValue("$locked_selections_json", JsonSerializer.Serialize(session.LockedSelections.Values.OrderBy(item => item.RequirementId, StringComparer.Ordinal), AiJsonOptions));
            command.Parameters.AddWithValue("$has_verified_result", session.HasVerifiedResult ? 1 : 0);
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            var affected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (affected == 0 && existing is null)
            {
                throw new InvalidOperationException("AI_SESSION_IDENTITY_CONFLICT");
            }
            return true;
        }, cancellationToken);

    public async Task<AiAnalysisSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, batch_id, style_id, template_snapshot_id, analysis_key, status, selection_revision, allowed_requirement_ids_json, allowed_asset_ids_json, locked_selections_json, has_verified_result FROM ai_analysis_sessions WHERE session_id = $session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return ReadSession(reader);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or InvalidCastException or ArgumentException)
        {
            throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT", exception);
        }
    }

    public Task SaveCacheReferenceAsync(AiCacheReference reference, CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ValidateCacheReference(reference);
            if (!reference.IsReusable)
            {
                throw new InvalidOperationException("AI_CACHE_INITIAL_STATE_INVALID");
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var usage = await FindUsageByIdForWriteAsync(connection, reference.UsageEventId, token, transaction).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_CACHE_USAGE_NOT_FOUND");
            ValidateUsage(usage);
            var ledger = await FindLedgerByRequestIdAsync(connection, transaction, usage.RequestId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_CACHE_REQUEST_NOT_FOUND");
            ValidateStoredUsageLedgerClosure(usage, ledger);
            if (ledger.ResultReference is null
                || !string.Equals(ledger.ResultReference, reference.ResultReference, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_CACHE_RESULT_REFERENCE_CONFLICT");
            }
            if (!string.Equals(usage.ModelConfigVersion, reference.ModelConfigVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_CACHE_MODEL_CONFIG_CONFLICT");
            }
            if (reference.CreatedAt < usage.RecordedAt)
            {
                throw new InvalidOperationException("AI_CACHE_TIME_INVALID");
            }
            var existing = await FindCacheReferenceForWriteAsync(connection, reference.CacheKey, token, transaction).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!Equals(existing, reference))
                {
                    throw new InvalidOperationException("AI_CACHE_KEY_CONFLICT");
                }

                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return true;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO ai_cache_references(cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at, is_reusable) VALUES($cache_key, $result_reference, $consent_id, $model_config_version, $usage_event_id, $created_at, $is_reusable);";
            command.Parameters.AddWithValue("$cache_key", reference.CacheKey);
            command.Parameters.AddWithValue("$result_reference", reference.ResultReference);
            command.Parameters.AddWithValue("$consent_id", reference.ConsentId);
            command.Parameters.AddWithValue("$model_config_version", reference.ModelConfigVersion);
            command.Parameters.AddWithValue("$usage_event_id", reference.UsageEventId);
            command.Parameters.AddWithValue("$created_at", reference.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$is_reusable", reference.IsReusable ? 1 : 0);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<AiCacheReference?> FindCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken)
    {
        ValidateDigest(cacheKey, "AI_CACHE_KEY_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at, is_reusable FROM ai_cache_references WHERE cache_key = $cache_key;";
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var reference = ReadCacheReference(reader);
        var usage = await FindUsageByIdForWriteAsync(connection, reference.UsageEventId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_CACHE_USAGE_NOT_FOUND");
        ValidateUsage(usage);
        var ledger = await FindLedgerByRequestIdAsync(connection, null, usage.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_CACHE_REQUEST_NOT_FOUND");
        ValidateStoredUsageLedgerClosure(usage, ledger);
        if (ledger.ResultReference is null
            || !string.Equals(ledger.ResultReference, reference.ResultReference, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_CACHE_RESULT_REFERENCE_CONFLICT");
        }
        if (!string.Equals(usage.ModelConfigVersion, reference.ModelConfigVersion, StringComparison.Ordinal)
            || reference.CreatedAt < usage.RecordedAt)
        {
            throw new InvalidDataException("AI_CACHE_CLOSURE_INVALID");
        }

        return reference;
    }

    public Task<bool> InvalidateCacheReferenceAsync(
        string cacheKey,
        CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ValidateDigest(cacheKey, "AI_CACHE_KEY_INVALID");
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var existing = await FindCacheReferenceForWriteAsync(
                connection,
                cacheKey,
                token,
                transaction).ConfigureAwait(false);
            if (existing is null)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return false;
            }

            var usage = await FindUsageByIdForWriteAsync(
                connection,
                existing.UsageEventId,
                token,
                transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI_CACHE_USAGE_NOT_FOUND");
            var ledger = await FindLedgerByRequestIdAsync(
                connection,
                transaction,
                usage.RequestId,
                token).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI_CACHE_REQUEST_NOT_FOUND");
            ValidateStoredUsageLedgerClosure(usage, ledger);
            if (ledger.ResultReference is null
                || !string.Equals(ledger.ResultReference, existing.ResultReference, StringComparison.Ordinal)
                || !string.Equals(usage.ModelConfigVersion, existing.ModelConfigVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("AI_CACHE_CLOSURE_INVALID");
            }

            if (!existing.IsReusable)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return false;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE ai_cache_references SET is_reusable = 0 WHERE cache_key = $cache_key AND is_reusable = 1;";
            command.Parameters.AddWithValue("$cache_key", cacheKey);
            var invalidated = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return invalidated;
        }, cancellationToken);

    private static AiRequestLedgerEntry ReadLedger(SqliteDataReader reader)
    {
        try
        {
            var entry = new AiRequestLedgerEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), ParseTime(reader.GetString(6)), ParseTime(reader.GetString(7)));
            ValidateStoredLedger(entry);
            return entry;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("AI_LEDGER_RECORD_CORRUPT", exception);
        }
    }

    private static DateTimeOffset ParseTime(string value)
    {
        try
        {
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("AI_TIME_RECORD_CORRUPT", exception);
        }
    }

    private static AiCacheReference ReadCacheReference(SqliteDataReader reader)
    {
        try
        {
            var reusableValue = reader.GetInt64(6);
            if (reusableValue is not (0 or 1))
            {
                throw new InvalidDataException("AI_CACHE_RECORD_CORRUPT");
            }

            var reference = new AiCacheReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseTime(reader.GetString(5)),
                reusableValue == 1);
            ValidateCacheReference(reference);
            return reference;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("AI_CACHE_RECORD_CORRUPT", exception);
        }
    }

    private static AiAnalysisSession ReadSession(SqliteDataReader reader)
    {
        var requirements = JsonSerializer.Deserialize<string[]>(reader.GetString(7), AiJsonOptions)
            ?? throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT");
        var assets = JsonSerializer.Deserialize<string[]>(reader.GetString(8), AiJsonOptions)
            ?? throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT");
        var locks = JsonSerializer.Deserialize<AiLockedSelection[]>(reader.GetString(9), AiJsonOptions)
            ?? throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT");
        var hasVerifiedResult = reader.GetInt64(10);
        if (hasVerifiedResult is not (0 or 1))
        {
            throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT");
        }

        return AiAnalysisSession.Restore(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            Enum.Parse<AiAnalysisSessionStatus>(reader.GetString(5), false), reader.GetInt64(6), locks,
            requirements, assets, hasVerifiedResult == 1);
    }

    private static async Task<AiAnalysisSession?> LoadSessionForWriteAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id, batch_id, style_id, template_snapshot_id, analysis_key, status, selection_revision, allowed_requirement_ids_json, allowed_asset_ids_json, locked_selections_json, has_verified_result FROM ai_analysis_sessions WHERE session_id = $session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return ReadSession(reader);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or InvalidCastException or ArgumentException)
        {
            throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT", exception);
        }
    }

    private static bool IdentityMatches(AiAnalysisSession left, AiAnalysisSession right) =>
        string.Equals(left.BatchId, right.BatchId, StringComparison.Ordinal)
        && string.Equals(left.StyleId, right.StyleId, StringComparison.Ordinal)
        && string.Equals(left.TemplateSnapshotId, right.TemplateSnapshotId, StringComparison.Ordinal)
        && string.Equals(left.AnalysisKey, right.AnalysisKey, StringComparison.Ordinal);

    private static bool SessionPayloadEquals(AiAnalysisSession left, AiAnalysisSession right) =>
        left.Status == right.Status
        && left.SelectionRevision == right.SelectionRevision
        && left.HasVerifiedResult == right.HasVerifiedResult
        && left.AllowedRequirementIds.SetEquals(right.AllowedRequirementIds)
        && left.AllowedAssetIds.SetEquals(right.AllowedAssetIds)
        && left.LockedSelections.Count == right.LockedSelections.Count
        && left.LockedSelections.All(item => right.LockedSelections.TryGetValue(item.Key, out var other) && Equals(item.Value, other));

    private static bool SessionSelectionPayloadEquals(AiAnalysisSession left, AiAnalysisSession right) =>
        left.SelectionRevision == right.SelectionRevision
        && left.AllowedRequirementIds.SetEquals(right.AllowedRequirementIds)
        && left.AllowedAssetIds.SetEquals(right.AllowedAssetIds)
        && left.LockedSelections.Count == right.LockedSelections.Count
        && left.LockedSelections.All(item => right.LockedSelections.TryGetValue(item.Key, out var other) && Equals(item.Value, other));

    private static bool LedgerIdentityMatches(AiRequestLedgerEntry left, AiRequestLedgerEntry right) =>
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal)
        && string.Equals(left.BatchId, right.BatchId, StringComparison.Ordinal)
        && string.Equals(left.StyleId, right.StyleId, StringComparison.Ordinal);

    private static void ValidateOpaqueReference(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512
            || value.Contains('\\') || value.Contains('\0'))
        {
            throw new InvalidOperationException(code);
        }

        var normalized = value.Replace('\\', '/');
        if (Path.IsPathFullyQualified(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException(code);
        }
    }

    private static void ValidateIdentifier(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new InvalidOperationException(code);
        }
    }

    private static void ValidateStatus(string value)
    {
        if (!Enum.TryParse<AiAnalysisSessionStatus>(value, false, out var status)
            || !Enum.IsDefined(status))
        {
            throw new InvalidOperationException("AI_STATUS_INVALID");
        }
    }

    private static void ValidateResultReference(string? value)
    {
        if (value is not null)
        {
            ValidateOwnedReference(value, "AI_RESULT_REFERENCE_INVALID");
        }
    }

    private static void ValidateLedgerEntry(AiRequestLedgerEntry entry)
    {
        ValidateIdentifier(entry.RequestId, "AI_REQUEST_ID_INVALID");
        ValidateDigest(entry.Fingerprint, "AI_FINGERPRINT_INVALID");
        ValidateIdentifier(entry.BatchId, "AI_BATCH_ID_INVALID");
        ValidateIdentifier(entry.StyleId, "AI_STYLE_ID_INVALID");
        ValidateStatus(entry.Status);
        ValidateResultReference(entry.ResultReference);
        ValidateTimestamp(entry.CreatedAt, "AI_LEDGER_TIME_INVALID");
        ValidateTimestamp(entry.UpdatedAt, "AI_LEDGER_TIME_INVALID");
        if (entry.UpdatedAt < entry.CreatedAt)
        {
            throw new InvalidOperationException("AI_LEDGER_TIME_INVALID");
        }
    }

    private static void ValidateInitialLedgerEntry(AiRequestLedgerEntry entry)
    {
        ValidateLedgerEntry(entry);
        if (!string.Equals(entry.Status, AiAnalysisSessionStatus.Queued.ToString(), StringComparison.Ordinal)
            || entry.ResultReference is not null
            || entry.CreatedAt != entry.UpdatedAt)
        {
            throw new InvalidOperationException("AI_REQUEST_INITIAL_STATE_INVALID");
        }
    }

    private static void ValidateStoredLedger(AiRequestLedgerEntry entry)
    {
        try
        {
            ValidateLedgerEntry(entry);
            var requiresResult = entry.Status is "Validated" or "ManualReview" or "PlanClosed";
            if (requiresResult != (entry.ResultReference is not null))
            {
                throw new InvalidDataException("AI_LEDGER_RECORD_CORRUPT");
            }
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("AI_LEDGER_RECORD_CORRUPT", exception);
        }
    }

    private static void ValidateCacheReference(AiCacheReference reference)
    {
        ValidateDigest(reference.CacheKey, "AI_CACHE_KEY_INVALID");
        ValidateOwnedReference(reference.ResultReference, "AI_CACHE_REFERENCE_INVALID");
        ValidateOpaqueReference(reference.ConsentId, "AI_CONSENT_ID_INVALID");
        ValidateOpaqueReference(reference.ModelConfigVersion, "AI_MODEL_CONFIG_INVALID");
        ValidateOpaqueReference(reference.UsageEventId, "AI_USAGE_ID_INVALID");
        ValidateTimestamp(reference.CreatedAt, "AI_CACHE_TIME_INVALID");
    }

    private static void ValidateUsage(AiUsageEventRecord usage)
    {
        ValidateIdentifier(usage.UsageEventId, "AI_USAGE_ID_INVALID");
        ValidateIdentifier(usage.RequestId, "AI_REQUEST_ID_INVALID");
        ValidateOpaqueReference(usage.ModelConfigVersion, "AI_MODEL_CONFIG_INVALID");
        if (usage.InputUnits < 0 || usage.OutputUnits < 0 || usage.RetryCount < 0
            || (usage.ProviderCostAmount is null) != (usage.ProviderCostCurrency is null)
            || usage.ProviderCostAmount < 0
            || (usage.ProviderCostCurrency is not null
                && !System.Text.RegularExpressions.Regex.IsMatch(usage.ProviderCostCurrency, "^[A-Z]{3}$")))
        {
            throw new InvalidOperationException("AI_USAGE_INVALID");
        }

        ValidateTimestamp(usage.RecordedAt, "AI_USAGE_TIME_INVALID");
    }

    private static void ValidateStoredUsageLedgerClosure(
        AiUsageEventRecord usage,
        AiRequestLedgerEntry ledger)
    {
        if (!string.Equals(usage.RequestId, ledger.RequestId, StringComparison.Ordinal)
            || usage.RecordedAt < ledger.CreatedAt)
        {
            throw new InvalidDataException("AI_USAGE_LEDGER_CLOSURE_INVALID");
        }
    }

    private static void ValidateTimestamp(DateTimeOffset value, string code)
    {
        if (value == default)
        {
            throw new InvalidOperationException(code);
        }
    }

    private static void ValidateDigest(string value, string code)
    {
        if (value is null
            || value.Length != 71
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value.Skip(7).Any(character => !((character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f'))))
        {
            throw new InvalidOperationException(code);
        }
    }

    private static bool IsAllowedStatusTransition(string current, string next)
    {
        var pair = (Enum.Parse<AiAnalysisSessionStatus>(current, false), Enum.Parse<AiAnalysisSessionStatus>(next, false));
        return pair is
            (AiAnalysisSessionStatus.Queued, AiAnalysisSessionStatus.Uploading)
            or (AiAnalysisSessionStatus.Uploading, AiAnalysisSessionStatus.Analysing)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.Validated)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.RetryableError)
            or (AiAnalysisSessionStatus.Uploading, AiAnalysisSessionStatus.TerminalError)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.TerminalError)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.AiResultUnknown)
            or (AiAnalysisSessionStatus.Validated, AiAnalysisSessionStatus.ManualReview)
            or (AiAnalysisSessionStatus.Validated, AiAnalysisSessionStatus.PlanClosed)
            or (AiAnalysisSessionStatus.ManualReview, AiAnalysisSessionStatus.PlanClosed)
            or (AiAnalysisSessionStatus.RetryableError, AiAnalysisSessionStatus.Queued)
            or (AiAnalysisSessionStatus.AiResultUnknown, AiAnalysisSessionStatus.TerminalError)
            or (AiAnalysisSessionStatus.AiResultUnknown, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.Queued, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.Uploading, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.Analysing, AiAnalysisSessionStatus.Cancelled)
            or (AiAnalysisSessionStatus.RetryableError, AiAnalysisSessionStatus.Cancelled);
    }

    private static void ValidateOwnedReference(string value, string code)
    {
        ValidateOpaqueReference(value, code);
        var normalized = value.Replace('\\', '/');
        if (Path.IsPathFullyQualified(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
        {
            throw new InvalidOperationException(code);
        }
    }

    private static async Task<AiRequestLedgerEntry?> FindLedgerByRequestIdAsync(SqliteConnection connection, SqliteTransaction? transaction, string requestId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_id, fingerprint, batch_id, style_id, status, result_reference, created_at, updated_at FROM ai_request_ledger WHERE request_id = $request_id;";
        command.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLedger(reader) : null;
    }

    private static async Task<AiRequestLedgerEntry?> FindLedgerByFingerprintAsync(SqliteConnection connection, SqliteTransaction? transaction, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_id, fingerprint, batch_id, style_id, status, result_reference, created_at, updated_at FROM ai_request_ledger WHERE fingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLedger(reader) : null;
    }

    private static async Task<AiCacheReference?> FindCacheReferenceForWriteAsync(
        SqliteConnection connection,
        string cacheKey,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at, is_reusable FROM ai_cache_references WHERE cache_key = $cache_key;";
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCacheReference(reader)
            : null;
    }


}
