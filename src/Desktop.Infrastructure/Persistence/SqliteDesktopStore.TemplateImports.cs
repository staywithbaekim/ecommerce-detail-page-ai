using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore : ITemplateResourceRepository
{
    public async Task<ImportedTemplateResource?> GetByResourceIdAsync(
        string resourceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        try
        {
            await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await ReadResourceAsync(
                connection,
                transaction: null,
                "r.resource_id = $value",
                resourceId,
                joinCommands: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new IOException("TPL_TEMPLATE_RESOURCE_READ_FAILED", exception);
        }
    }

    public async Task<ImportedTemplateResource?> GetByCommandIdAsync(
        string commandId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        try
        {
            await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await ReadResourceAsync(
                connection,
                transaction: null,
                "c.command_id = $value",
                commandId,
                joinCommands: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new IOException("TPL_TEMPLATE_RESOURCE_READ_FAILED", exception);
        }
    }

    public async Task<IReadOnlyList<ImportedTemplateResource>> FindBySha256Async(
        string sha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        try
        {
            await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT resource_id, group_id, draft_id, internal_relative_path,
                       length, sha256, structure_signature, inspection_json, created_at
                FROM template_resources
                WHERE sha256 = $sha256
                ORDER BY created_at, resource_id;
                """;
            command.Parameters.AddWithValue("$sha256", sha256.ToLowerInvariant());
            var matches = new List<ImportedTemplateResource>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                matches.Add(MapResource(reader));
            }

            return matches;
        }
        catch (SqliteException exception)
        {
            throw new IOException("TPL_TEMPLATE_RESOURCE_READ_FAILED", exception);
        }
    }

    public async Task<ImportedTemplateResource> SaveImportedAsync(
        string commandId,
        ImportedTemplateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(resource);
        try
        {
            return await _writer.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var transaction = (SqliteTransaction)await connection
                        .BeginTransactionAsync(token)
                        .ConfigureAwait(false);
                    var completed = await ReadResourceAsync(
                        connection,
                        transaction,
                        "c.command_id = $value",
                        commandId,
                        joinCommands: true,
                        token).ConfigureAwait(false);
                    if (completed is not null)
                    {
                        if (!ResourceMatches(completed, resource))
                        {
                            throw new InvalidOperationException("TPL_IMPORT_COMMAND_CONFLICT");
                        }

                        await transaction.CommitAsync(token).ConfigureAwait(false);
                        return completed;
                    }

                    var existing = await ReadResourceAsync(
                        connection,
                        transaction,
                        "r.resource_id = $value",
                        resource.ResourceId,
                        joinCommands: false,
                        token).ConfigureAwait(false);
                    if (existing is not null && !ResourceMatches(existing, resource))
                    {
                        throw new InvalidOperationException("TPL_TEMPLATE_RESOURCE_IMMUTABLE");
                    }

                    if (existing is null)
                    {
                        await InsertResourceAsync(connection, transaction, resource, token).ConfigureAwait(false);
                        existing = resource;
                    }

                    await InsertCommandAsync(
                        connection,
                        transaction,
                        commandId,
                        existing.ResourceId,
                        existing.CreatedAt,
                        token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return existing;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new IOException("TPL_TEMPLATE_RESOURCE_WRITE_FAILED", exception);
        }
    }

    public async Task<ImportedTemplateResource> RecordExistingSelectionAsync(
        string commandId,
        ImportedTemplateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(resource);
        try
        {
            return await _writer.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var transaction = (SqliteTransaction)await connection
                        .BeginTransactionAsync(token)
                        .ConfigureAwait(false);
                    var completed = await ReadResourceAsync(
                        connection,
                        transaction,
                        "c.command_id = $value",
                        commandId,
                        joinCommands: true,
                        token).ConfigureAwait(false);
                    if (completed is not null)
                    {
                        if (!string.Equals(completed.ResourceId, resource.ResourceId, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("TPL_IMPORT_COMMAND_CONFLICT");
                        }

                        await transaction.CommitAsync(token).ConfigureAwait(false);
                        return completed;
                    }

                    var stored = await ReadResourceAsync(
                        connection,
                        transaction,
                        "r.resource_id = $value",
                        resource.ResourceId,
                        joinCommands: false,
                        token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
                    if (!ResourceMatches(stored, resource))
                    {
                        throw new InvalidOperationException("TPL_TEMPLATE_RESOURCE_IMMUTABLE");
                    }

                    await InsertCommandAsync(
                        connection,
                        transaction,
                        commandId,
                        stored.ResourceId,
                        DateTimeOffset.UtcNow,
                        token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return stored;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new IOException("TPL_TEMPLATE_RESOURCE_WRITE_FAILED", exception);
        }
    }

    private static async Task<ImportedTemplateResource?> ReadResourceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string predicate,
        string value,
        bool joinCommands,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = joinCommands
            ? $"""
                SELECT r.resource_id, r.group_id, r.draft_id, r.internal_relative_path,
                       r.length, r.sha256, r.structure_signature, r.inspection_json, r.created_at
                FROM template_resources r
                INNER JOIN template_import_commands c ON c.resource_id = r.resource_id
                WHERE {predicate}
                LIMIT 1;
                """
            : $"""
                SELECT r.resource_id, r.group_id, r.draft_id, r.internal_relative_path,
                       r.length, r.sha256, r.structure_signature, r.inspection_json, r.created_at
                FROM template_resources r
                WHERE {predicate}
                LIMIT 1;
                """;
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapResource(reader)
            : null;
    }

    private static async Task InsertResourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportedTemplateResource resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO template_resources(
                resource_id, group_id, draft_id, internal_relative_path, length,
                sha256, structure_signature, inspection_json, created_at)
            VALUES(
                $resource_id, $group_id, $draft_id, $internal_relative_path, $length,
                $sha256, $structure_signature, $inspection_json, $created_at);
            """;
        command.Parameters.AddWithValue("$resource_id", resource.ResourceId);
        command.Parameters.AddWithValue("$group_id", resource.GroupId);
        command.Parameters.AddWithValue("$draft_id", resource.DraftId);
        command.Parameters.AddWithValue("$internal_relative_path", resource.InternalRelativePath);
        command.Parameters.AddWithValue("$length", resource.Length);
        command.Parameters.AddWithValue("$sha256", resource.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue("$structure_signature", resource.StructureSignature.ToLowerInvariant());
        command.Parameters.AddWithValue(
            "$inspection_json",
            JsonSerializer.Serialize(resource.Inspection, TemplateJsonOptions));
        command.Parameters.AddWithValue("$created_at", resource.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandId,
        string resourceId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO template_import_commands(command_id, resource_id, completed_at)
            VALUES($command_id, $resource_id, $completed_at);
            """;
        command.Parameters.AddWithValue("$command_id", commandId);
        command.Parameters.AddWithValue("$resource_id", resourceId);
        command.Parameters.AddWithValue("$completed_at", completedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ImportedTemplateResource MapResource(SqliteDataReader reader)
    {
        var inspection = JsonSerializer.Deserialize<PsdInspectionDocument>(
            reader.GetString(7),
            TemplateJsonOptions)
            ?? throw new InvalidDataException("TPL_PSD_INSPECTION_INVALID");
        return new ImportedTemplateResource(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            inspection,
            ParseTimestamp(reader.GetString(8)));
    }

    private static bool ResourceMatches(ImportedTemplateResource left, ImportedTemplateResource right) =>
        string.Equals(left.ResourceId, right.ResourceId, StringComparison.Ordinal)
        && string.Equals(left.GroupId, right.GroupId, StringComparison.Ordinal)
        && string.Equals(left.DraftId, right.DraftId, StringComparison.Ordinal)
        && string.Equals(left.InternalRelativePath, right.InternalRelativePath, StringComparison.Ordinal)
        && left.Length == right.Length
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.StructureSignature, right.StructureSignature, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            JsonSerializer.Serialize(left.Inspection, TemplateJsonOptions),
            JsonSerializer.Serialize(right.Inspection, TemplateJsonOptions),
            StringComparison.Ordinal)
        && left.CreatedAt.Equals(right.CreatedAt);
}
