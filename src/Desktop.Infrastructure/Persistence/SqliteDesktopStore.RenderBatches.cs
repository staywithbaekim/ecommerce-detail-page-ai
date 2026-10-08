using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    private static readonly JsonSerializerOptions RenderBatchJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 64
    };
    private static readonly RenderPlanValidator BatchPlanValidator = new(new JsonSchemaContractValidator());

    public async Task<RenderBatchJournal?> LoadRenderBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = await ReadBatch(connection, null, batchId, cancellationToken).ConfigureAwait(false);
        if (result?.Version == 4)
        {
            var initial = await ReadBatchRevision(connection, null, batchId, 1, cancellationToken).ConfigureAwait(false) ?? throw BatchInvalid();
            await ValidateInstanceRegistrationAsync(initial, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    public Task SaveRenderBatchAsync(RenderBatchJournal journal, long expectedRevision, CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(async (connection, token) =>
        {
            RenderBatchJournalValidator.Validate(journal, BatchPlanValidator);
            if (expectedRevision == 0) await ValidateInstanceRegistrationAsync(journal, token).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(journal, RenderBatchJson);
            if (Encoding.UTF8.GetByteCount(payload) > RenderBatchJournalValidator.MaxBytes) throw BatchInvalid();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var previous = await ReadBatch(connection, transaction, journal.BatchId, token).ConfigureAwait(false);
            if ((previous?.Revision ?? 0) != expectedRevision) throw BatchInvalid();
            RenderBatchJournalValidator.ValidateUpdate(previous, journal);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO render_batch_journals(batch_id,revision,payload_json,payload_sha256) VALUES($batch,$revision,$json,$hash);";
            command.Parameters.AddWithValue("$batch", journal.BatchId);
            command.Parameters.AddWithValue("$revision", journal.Revision);
            command.Parameters.AddWithValue("$json", payload);
            command.Parameters.AddWithValue("$hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (previous is not null && journal.Pages.Count == previous.Pages.Count + 1)
                await SaveRegenerationOrigin(connection, transaction, journal, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private static async Task<RenderBatchJournal?> ReadBatch(SqliteConnection connection, SqliteTransaction? transaction,
        string batchId, CancellationToken token)
    {
        var latest = await ReadBatchRevision(connection, transaction, batchId, null, token).ConfigureAwait(false);
        if (latest is null) return null;
        var initial = latest.Revision == 1 ? latest
            : await ReadBatchRevision(connection, transaction, batchId, 1, token).ConfigureAwait(false) ?? throw BatchInvalid();
        if (!(latest.InstanceSets ?? []).SequenceEqual(initial.InstanceSets ?? [])) throw BatchInvalid();
        RenderBatchJournalValidator.ValidateUpdate(null, initial);
        var prepared = initial.Pages.ToList();
        if (latest.Version >= 2)
            await ReadRegenerationOrigins(connection, transaction, latest, prepared, token).ConfigureAwait(false);
        return EnrichBatch(latest, prepared);
    }

    private static RenderBatchJournal EnrichBatch(RenderBatchJournal journal, IReadOnlyList<RenderBatchPage> origins)
    {
        if (origins.Count != journal.Pages.Count) throw BatchInvalid();
        var pages = journal.Pages.Select((page, index) =>
        {
            var prepared = origins[index];
            var reason = prepared.PreparationReasonCode ?? prepared.ReasonCode;
            if (page.Instance != prepared.Instance || page.StyleId != prepared.StyleId || page.PageId != prepared.PageId || page.OutputVariantId != prepared.OutputVariantId
                || page.Attempt != prepared.Attempt || page.PlanJson != prepared.PlanJson
                || page.PreparationReasonCode is not null && page.PreparationReasonCode != reason
                || prepared.PreparationReasonCode is not null && page.PreparationReasonCode is null
                || page.OutputVariantName != prepared.OutputVariantName || page.SizeTableRepair != prepared.SizeTableRepair) throw BatchInvalid();
            // Legacy v16 journals retain their exact bytes and checksums. Only the read projection is enriched.
            return page with { PreparationReasonCode = reason };
        }).ToArray();
        return journal with { Pages = pages };
    }

    private static async Task<RenderBatchJournal?> ReadBatchRevision(SqliteConnection connection, SqliteTransaction? transaction,
        string batchId, long? revision, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = revision is not null ? """
            SELECT revision,length(CAST(payload_json AS BLOB)),payload_json,payload_sha256
            FROM render_batch_journals WHERE batch_id=$batch AND revision=$revision;
            """ : """
            SELECT revision,length(CAST(payload_json AS BLOB)),payload_json,payload_sha256
            FROM render_batch_journals WHERE batch_id=$batch ORDER BY revision DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        if (revision is not null) command.Parameters.AddWithValue("$revision", revision.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        if (reader.GetInt64(1) is < 1 or > RenderBatchJournalValidator.MaxBytes) throw BatchInvalid();
        var payload = reader.GetString(2);
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))) != reader.GetString(3)) throw BatchInvalid();
        try
        {
            var journal = JsonSerializer.Deserialize<RenderBatchJournal>(payload, RenderBatchJson) ?? throw BatchInvalid();
            if (journal.Revision != reader.GetInt64(0) || journal.BatchId != batchId) throw BatchInvalid();
            RenderBatchJournalValidator.Validate(journal, BatchPlanValidator);
            return journal;
        }
        catch (JsonException) { throw BatchInvalid(); }
    }

    private static InvalidDataException BatchInvalid() => new("RENDER_BATCH_JOURNAL_INVALID");
}
