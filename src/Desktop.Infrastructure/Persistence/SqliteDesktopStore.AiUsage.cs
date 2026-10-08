using System.Globalization;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Domain.Ai;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
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
            command.CommandText = "INSERT INTO ai_usage_events(usage_event_id, request_id, model_config_version, input_units, cached_input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at) VALUES($usage_event_id, $request_id, $model_config_version, $input_units, $cached_input_units, $output_units, $cache_hit, $retry_count, $provider_cost_amount_text, $provider_cost_currency, $recorded_at);";
            command.Parameters.AddWithValue("$usage_event_id", usage.UsageEventId);
            command.Parameters.AddWithValue("$request_id", usage.RequestId);
            command.Parameters.AddWithValue("$model_config_version", usage.ModelConfigVersion);
            command.Parameters.AddWithValue("$input_units", usage.InputUnits);
            command.Parameters.AddWithValue("$cached_input_units", usage.CachedInputUnits);
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
        command.CommandText = "SELECT usage_event_id, request_id, model_config_version, input_units, cached_input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at FROM ai_usage_events WHERE request_id = $request_id ORDER BY recorded_at;";
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

    private static AiUsageEventRecord ReadUsageEvent(SqliteDataReader reader)
    {
        try
        {
            var cacheHit = reader.GetInt32(6);
            if (cacheHit is not (0 or 1))
            {
                throw new InvalidDataException("AI_USAGE_RECORD_CORRUPT");
            }

            var usage = new AiUsageEventRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(5),
                cacheHit == 1, reader.GetInt32(7),
                reader.IsDBNull(8) ? null : decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : reader.GetString(9), ParseTime(reader.GetString(10)), reader.GetInt32(4));
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

    private static async Task<AiUsageEventRecord?> FindUsageForWriteAsync(
        SqliteConnection connection,
        string requestId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT usage_event_id, request_id, model_config_version, input_units, cached_input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at FROM ai_usage_events WHERE request_id = $request_id;";
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
        command.CommandText = "SELECT usage_event_id, request_id, model_config_version, input_units, cached_input_units, output_units, cache_hit, retry_count, provider_cost_amount_text, provider_cost_currency, recorded_at FROM ai_usage_events WHERE usage_event_id = $usage_event_id;";
        command.Parameters.AddWithValue("$usage_event_id", usageEventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadUsageEvent(reader)
            : null;
    }

}
