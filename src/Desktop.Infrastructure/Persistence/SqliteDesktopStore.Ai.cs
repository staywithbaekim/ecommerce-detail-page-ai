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
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var existing = await FindLedgerByRequestIdAsync(connection, transaction, entry.RequestId, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.Fingerprint, entry.Fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("AI_REQUEST_ID_CONFLICT");
                }

                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return existing;
            }

            var fingerprintOwner = await FindLedgerByFingerprintAsync(connection, transaction, entry.Fingerprint, token).ConfigureAwait(false);
            if (fingerprintOwner is not null)
            {
                throw new InvalidOperationException("AI_REQUEST_FINGERPRINT_CONFLICT");
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

    public Task SaveSessionAsync(AiAnalysisSession session, CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
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
            if (affected == 0)
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
            var requirements = JsonSerializer.Deserialize<string[]>(reader.GetString(7), AiJsonOptions)
                ?? throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT");
            var assets = JsonSerializer.Deserialize<string[]>(reader.GetString(8), AiJsonOptions)
                ?? throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT");
            var locks = JsonSerializer.Deserialize<AiLockedSelection[]>(reader.GetString(9), AiJsonOptions)
                ?? throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT");
            return AiAnalysisSession.Restore(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                Enum.Parse<AiAnalysisSessionStatus>(reader.GetString(5), false), reader.GetInt64(6), locks,
                requirements, assets, reader.GetInt64(10) == 1);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("AI_SESSION_RECORD_CORRUPT", exception);
        }
    }

    public Task SaveCacheReferenceAsync(AiCacheReference reference, CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            ValidateOpaqueReference(reference.CacheKey, "AI_CACHE_KEY_INVALID");
            ValidateOwnedReference(reference.ResultReference, "AI_CACHE_REFERENCE_INVALID");
            ValidateOpaqueReference(reference.ConsentId, "AI_CONSENT_ID_INVALID");
            ValidateOpaqueReference(reference.ModelConfigVersion, "AI_MODEL_CONFIG_INVALID");
            ValidateOpaqueReference(reference.UsageEventId, "AI_USAGE_ID_INVALID");
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ai_cache_references(cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at) VALUES($cache_key, $result_reference, $consent_id, $model_config_version, $usage_event_id, $created_at) ON CONFLICT(cache_key) DO NOTHING;";
            command.Parameters.AddWithValue("$cache_key", reference.CacheKey);
            command.Parameters.AddWithValue("$result_reference", reference.ResultReference);
            command.Parameters.AddWithValue("$consent_id", reference.ConsentId);
            command.Parameters.AddWithValue("$model_config_version", reference.ModelConfigVersion);
            command.Parameters.AddWithValue("$usage_event_id", reference.UsageEventId);
            command.Parameters.AddWithValue("$created_at", reference.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<AiCacheReference?> FindCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_key, result_reference, consent_id, model_config_version, usage_event_id, created_at FROM ai_cache_references WHERE cache_key = $cache_key;";
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AiCacheReference(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), ParseTime(reader.GetString(5)))
            : null;
    }

    public Task RecordUsageAsync(AiUsageEventRecord usage, CancellationToken cancellationToken) => _writer.ExecuteAsync(
        async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ai_usage_events(usage_event_id, request_id, model_config_version, input_units, output_units, cache_hit, retry_count, provider_cost_amount, provider_cost_currency, recorded_at) VALUES($usage_event_id, $request_id, $model_config_version, $input_units, $output_units, $cache_hit, $retry_count, $provider_cost_amount, $provider_cost_currency, $recorded_at) ON CONFLICT(request_id) DO NOTHING;";
            command.Parameters.AddWithValue("$usage_event_id", usage.UsageEventId);
            command.Parameters.AddWithValue("$request_id", usage.RequestId);
            command.Parameters.AddWithValue("$model_config_version", usage.ModelConfigVersion);
            command.Parameters.AddWithValue("$input_units", usage.InputUnits);
            command.Parameters.AddWithValue("$output_units", usage.OutputUnits);
            command.Parameters.AddWithValue("$cache_hit", usage.CacheHit ? 1 : 0);
            command.Parameters.AddWithValue("$retry_count", usage.RetryCount);
            command.Parameters.AddWithValue("$provider_cost_amount", (object?)usage.ProviderCostAmount ?? DBNull.Value);
            command.Parameters.AddWithValue("$provider_cost_currency", (object?)usage.ProviderCostCurrency ?? DBNull.Value);
            command.Parameters.AddWithValue("$recorded_at", usage.RecordedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyList<AiUsageEventRecord>> LoadUsageAsync(string requestId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT usage_event_id, request_id, model_config_version, input_units, output_units, cache_hit, retry_count, provider_cost_amount, provider_cost_currency, recorded_at FROM ai_usage_events WHERE request_id = $request_id ORDER BY recorded_at;";
        command.Parameters.AddWithValue("$request_id", requestId);
        var events = new List<AiUsageEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new AiUsageEventRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5) == 1, reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.IsDBNull(8) ? null : reader.GetString(8), ParseTime(reader.GetString(9))));
        }

        return events;
    }

    private static AiRequestLedgerEntry ReadLedger(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), ParseTime(reader.GetString(6)), ParseTime(reader.GetString(7)));

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateOpaqueReference(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512
            || value.Contains('\\') || value.Contains('\0'))
        {
            throw new InvalidOperationException(code);
        }
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

    private static async Task<AiRequestLedgerEntry?> FindLedgerByRequestIdAsync(SqliteConnection connection, SqliteTransaction transaction, string requestId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_id, fingerprint, batch_id, style_id, status, result_reference, created_at, updated_at FROM ai_request_ledger WHERE request_id = $request_id;";
        command.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLedger(reader) : null;
    }

    private static async Task<AiRequestLedgerEntry?> FindLedgerByFingerprintAsync(SqliteConnection connection, SqliteTransaction transaction, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_id, fingerprint, batch_id, style_id, status, result_reference, created_at, updated_at FROM ai_request_ledger WHERE fingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLedger(reader) : null;
    }
}
