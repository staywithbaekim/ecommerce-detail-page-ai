using System.Globalization;
using System.Text.Json;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore : ITemplateInstanceProjectionSnapshotStore
{
    private static readonly JsonSerializerOptions InstanceJsonOptions = new(JsonSerializerDefaults.Web);

    public Task<FreezeTemplateInstanceProjectionResult> FreezeAsync(
        FreezeTemplateInstanceProjectionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        TemplateValueRules.Identifier(command.CommandId, "TPL_INSTANCE_COMMAND_INVALID");
        if (command.ExpectedSnapshotVersion < 0)
            throw new TemplateRuleViolationException("TPL_INSTANCE_VERSION_INVALID");
        TemplateInstanceProjectionSnapshotCodec.Validate(command.Snapshot);
        var commandDigest = TemplateInstanceProjectionSnapshotCodec.CommandDigest(command.Snapshot);
        var payload = TemplateInstanceProjectionSnapshotCodec.Serialize(command.Snapshot);
        return _writer.ExecuteAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await using (var commandRecord = connection.CreateCommand())
            {
                commandRecord.Transaction = transaction;
                commandRecord.CommandText = """
                    SELECT command_digest, result_version, projection_digest
                    FROM template_instance_projection_commands
                    WHERE batch_id=$batch AND style_id=$style AND command_id=$command;
                    """;
                commandRecord.Parameters.AddWithValue("$batch", command.Snapshot.BatchId);
                commandRecord.Parameters.AddWithValue("$style", command.Snapshot.StyleId);
                commandRecord.Parameters.AddWithValue("$command", command.CommandId);
                await using var reader = await commandRecord.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetString(0) != commandDigest)
                        throw new TemplateRuleViolationException("TPL_INSTANCE_COMMAND_REUSED");
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return new FreezeTemplateInstanceProjectionResult(
                        FreezeTemplateInstanceProjectionOutcome.AlreadyApplied, reader.GetInt64(1), reader.GetString(2));
                }
            }

            await using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT snapshot_version FROM template_instance_projection_snapshots WHERE batch_id=$batch AND style_id=$style;";
                current.Parameters.AddWithValue("$batch", command.Snapshot.BatchId);
                current.Parameters.AddWithValue("$style", command.Snapshot.StyleId);
                var currentVersion = await current.ExecuteScalarAsync(token).ConfigureAwait(false);
                var expected = currentVersion is null ? 0 : Convert.ToInt64(currentVersion, CultureInfo.InvariantCulture);
                if (command.ExpectedSnapshotVersion != expected)
                    throw new TemplateRuleViolationException("TPL_INSTANCE_VERSION_CONFLICT");
            }
            if (command.ExpectedSnapshotVersion != 0)
                throw new TemplateRuleViolationException("TPL_INSTANCE_VERSION_CONFLICT");
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO template_instance_projection_snapshots(
                        batch_id, style_id, schema_version, snapshot_version, snapshot_id,
                        revision_id, rule_digest, input_facts_digest, projection_digest,
                        payload_json, frozen_at, command_id, command_digest)
                    VALUES($batch,$style,$schema,$version,$snapshot,$revision,$rule,$facts,$projection,$payload,$frozen,$command,$command_digest);
                    """;
                Add(insert, command, commandDigest, payload);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            foreach (var instance in command.Snapshot.Instances)
            {
                await using var member = connection.CreateCommand();
                member.Transaction = transaction;
                member.CommandText = """
                    INSERT INTO template_instance_projection_members(
                        batch_id, style_id, page_instance_id, source_page_id, recipe_id, repetition_index, stable_order)
                    VALUES($batch,$style,$instance,$page,$recipe,$repetition,$order);
                    """;
                member.Parameters.AddWithValue("$batch", command.Snapshot.BatchId);
                member.Parameters.AddWithValue("$style", command.Snapshot.StyleId);
                member.Parameters.AddWithValue("$instance", instance.PageInstanceId);
                member.Parameters.AddWithValue("$page", instance.SourcePageId);
                member.Parameters.AddWithValue("$recipe", instance.RecipeId);
                member.Parameters.AddWithValue("$repetition", instance.RepetitionIndex);
                member.Parameters.AddWithValue("$order", instance.Order);
                await member.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using (var commandInsert = connection.CreateCommand())
            {
                commandInsert.Transaction = transaction;
                commandInsert.CommandText = """
                    INSERT INTO template_instance_projection_commands(
                        batch_id, style_id, command_id, command_digest, result_version, projection_digest)
                    VALUES($batch,$style,$command,$digest,$version,$projection);
                    """;
                commandInsert.Parameters.AddWithValue("$batch", command.Snapshot.BatchId);
                commandInsert.Parameters.AddWithValue("$style", command.Snapshot.StyleId);
                commandInsert.Parameters.AddWithValue("$command", command.CommandId);
                commandInsert.Parameters.AddWithValue("$digest", commandDigest);
                commandInsert.Parameters.AddWithValue("$version", command.Snapshot.SnapshotVersion);
                commandInsert.Parameters.AddWithValue("$projection", command.Snapshot.ProjectionDigest);
                await commandInsert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new FreezeTemplateInstanceProjectionResult(
                FreezeTemplateInstanceProjectionOutcome.Created, command.Snapshot.SnapshotVersion, command.Snapshot.ProjectionDigest);
        }, cancellationToken);
    }


    public async Task<TemplateInstanceProjectionSnapshot?> ReadAsync(
        string batchId, string styleId, CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(batchId, "TPL_BATCH_ID_INVALID");
        TemplateValueRules.Identifier(styleId, "TPL_STYLE_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, snapshot_version, snapshot_id, revision_id, rule_digest,
                   input_facts_digest, projection_digest, payload_json
            FROM template_instance_projection_snapshots
            WHERE batch_id=$batch AND style_id=$style;
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        command.Parameters.AddWithValue("$style", styleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var snapshot = TemplateInstanceProjectionSnapshotCodec.Deserialize(reader.GetString(7));
        if (snapshot.SchemaVersion != reader.GetInt32(0) || snapshot.SnapshotVersion != reader.GetInt64(1)
            || snapshot.SnapshotId != reader.GetString(2) || snapshot.RevisionId != reader.GetString(3)
            || snapshot.RuleDigest != reader.GetString(4) || snapshot.InputFactsDigest != reader.GetString(5)
            || snapshot.ProjectionDigest != reader.GetString(6)
            || snapshot.BatchId != batchId || snapshot.StyleId != styleId)
            throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID");
        return snapshot;
    }

    private static void Add(SqliteCommand command, FreezeTemplateInstanceProjectionCommand request,
        string commandDigest, string payload)
    {
        command.Parameters.AddWithValue("$batch", request.Snapshot.BatchId);
        command.Parameters.AddWithValue("$style", request.Snapshot.StyleId);
        command.Parameters.AddWithValue("$schema", request.Snapshot.SchemaVersion);
        command.Parameters.AddWithValue("$version", request.Snapshot.SnapshotVersion);
        command.Parameters.AddWithValue("$snapshot", request.Snapshot.SnapshotId);
        command.Parameters.AddWithValue("$revision", request.Snapshot.RevisionId);
        command.Parameters.AddWithValue("$rule", request.Snapshot.RuleDigest);
        command.Parameters.AddWithValue("$facts", request.Snapshot.InputFactsDigest);
        command.Parameters.AddWithValue("$projection", request.Snapshot.ProjectionDigest);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$frozen", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$command", request.CommandId);
        command.Parameters.AddWithValue("$command_digest", commandDigest);
    }
}
