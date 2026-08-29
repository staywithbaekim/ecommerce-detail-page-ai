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
            var existing = await FindLedgerByRequestIdAsync(connection, null, requestId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_REQUEST_NOT_FOUND");
            if (!string.Equals(existing.Status, expectedStatus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_REQUEST_STATUS_CONFLICT");
            }

            if (!IsAllowedStatusTransition(expectedStatus, nextStatus))
            {
                throw new InvalidOperationException("AI_REQUEST_STATUS_TRANSITION_INVALID");
            }

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

            await using var command = connection.CreateCommand();
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

            return existing with { Status = nextStatus, ResultReference = resultReference, UpdatedAt = updatedAt };
        }, cancellationToken);

    public Task SaveSessionAsync(AiAnalysisSession session, CancellationToken cancellationToken, long? expectedSelectionRevision = null) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
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
            command.CommandText = "INSERT INTO ai_cache_references(cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at) VALUES($cache_key, $result_reference, $consent_id, $model_config_version, $usage_event_id, $created_at);";
            command.Parameters.AddWithValue("$cache_key", reference.CacheKey);
            command.Parameters.AddWithValue("$result_reference", reference.ResultReference);
            command.Parameters.AddWithValue("$consent_id", reference.ConsentId);
            command.Parameters.AddWithValue("$model_config_version", reference.ModelConfigVersion);
            command.Parameters.AddWithValue("$usage_event_id", reference.UsageEventId);
            command.Parameters.AddWithValue("$created_at", reference.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<AiCacheReference?> FindCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken)
    {
        ValidateDigest(cacheKey, "AI_CACHE_KEY_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at FROM ai_cache_references WHERE cache_key = $cache_key;";
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

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ai_cache_references WHERE cache_key = $cache_key;";
            command.Parameters.AddWithValue("$cache_key", cacheKey);
            var removed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return removed;
        }, cancellationToken);

    public Task RecordUsageAsync(AiUsageEventRecord usage, CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ValidateUsage(usage);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var ledger = await FindLedgerByRequestIdAsync(connection, transaction, usage.RequestId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_USAGE_REQUEST_NOT_FOUND");
            if (usage.RecordedAt < ledger.CreatedAt)
            {
                throw new InvalidOperationException("AI_USAGE_TIME_INVALID");
            }
            var existing = await FindUsageForWriteAsync(connection, usage.RequestId, token, transaction).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!Equals(existing, usage))
                {
                    throw new InvalidOperationException("AI_USAGE_REQUEST_CONFLICT");
                }

                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return true;
            }

            var existingById = await FindUsageByIdForWriteAsync(connection, usage.UsageEventId, token, transaction).ConfigureAwait(false);
            if (existingById is not null)
            {
                if (!Equals(existingById, usage))
                {
                    throw new InvalidOperationException("AI_USAGE_ID_CONFLICT");
                }

                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return true;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO ai_usage_events(usage_event_id, request_id, model_config_version, input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at) VALUES($usage_event_id, $request_id, $model_config_version, $input_units, $output_units, $cache_hit, $retry_count, $provider_cost_amount_text, $provider_cost_currency, $recorded_at);";
            command.Parameters.AddWithValue("$usage_event_id", usage.UsageEventId);
            command.Parameters.AddWithValue("$request_id", usage.RequestId);
            command.Parameters.AddWithValue("$model_config_version", usage.ModelConfigVersion);
            command.Parameters.AddWithValue("$input_units", usage.InputUnits);
            command.Parameters.AddWithValue("$output_units", usage.OutputUnits);
            command.Parameters.AddWithValue("$cache_hit", usage.CacheHit ? 1 : 0);
            command.Parameters.AddWithValue("$retry_count", usage.RetryCount);
            command.Parameters.AddWithValue("$provider_cost_amount_text", (object?)usage.ProviderCostAmount?.ToString("G29", CultureInfo.InvariantCulture) ?? DBNull.Value);
            command.Parameters.AddWithValue("$provider_cost_currency", (object?)usage.ProviderCostCurrency ?? DBNull.Value);
            command.Parameters.AddWithValue("$recorded_at", usage.RecordedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyList<AiUsageEventRecord>> LoadUsageAsync(string requestId, CancellationToken cancellationToken)
    {
        ValidateIdentifier(requestId, "AI_REQUEST_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var ledger = await FindLedgerByRequestIdAsync(connection, null, requestId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_USAGE_REQUEST_NOT_FOUND");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT usage_event_id, request_id, model_config_version, input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at FROM ai_usage_events WHERE request_id = $request_id ORDER BY recorded_at;";
        command.Parameters.AddWithValue("$request_id", requestId);
        var events = new List<AiUsageEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var usage = ReadUsageEvent(reader);
            ValidateStoredUsageLedgerClosure(usage, ledger);
            events.Add(usage);
        }

        return events;
    }

    public async Task<AiUsageEventRecord?> FindUsageEventAsync(
        string usageEventId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(usageEventId, "AI_USAGE_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var usage = await FindUsageByIdForWriteAsync(connection, usageEventId, cancellationToken).ConfigureAwait(false);
        if (usage is null)
        {
            return null;
        }

        var ledger = await FindLedgerByRequestIdAsync(connection, null, usage.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_USAGE_REQUEST_NOT_FOUND");
        ValidateStoredUsageLedgerClosure(usage, ledger);
        return usage;
    }

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
            var reference = new AiCacheReference(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), ParseTime(reader.GetString(5)));
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

    private static AiUsageEventRecord ReadUsageEvent(SqliteDataReader reader)
    {
        try
        {
            var cacheHit = reader.GetInt32(5);
            if (cacheHit is not (0 or 1))
            {
                throw new InvalidDataException("AI_USAGE_RECORD_CORRUPT");
            }

            var usage = new AiUsageEventRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                cacheHit == 1, reader.GetInt32(6),
                reader.IsDBNull(7) ? null : decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : reader.GetString(8), ParseTime(reader.GetString(9)));
            ValidateUsage(usage);
            return usage;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("AI_USAGE_RECORD_CORRUPT", exception);
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
        command.CommandText = "SELECT cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at FROM ai_cache_references WHERE cache_key = $cache_key;";
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCacheReference(reader)
            : null;
    }

    private static async Task<AiUsageEventRecord?> FindUsageForWriteAsync(
        SqliteConnection connection,
        string requestId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT usage_event_id, request_id, model_config_version, input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at FROM ai_usage_events WHERE request_id = $request_id;";
        command.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadUsageEvent(reader)
            : null;
    }

    private static async Task<AiUsageEventRecord?> FindUsageByIdForWriteAsync(
        SqliteConnection connection,
        string usageEventId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT usage_event_id, request_id, model_config_version, input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at FROM ai_usage_events WHERE usage_event_id = $usage_event_id;";
        command.Parameters.AddWithValue("$usage_event_id", usageEventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadUsageEvent(reader)
            : null;
    }
}
