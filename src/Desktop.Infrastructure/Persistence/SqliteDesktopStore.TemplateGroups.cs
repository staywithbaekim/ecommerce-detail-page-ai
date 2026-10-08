using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    private static readonly JsonSerializerOptions TemplateJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] DeleteTemplateGroupSql =
    [
        """
        DELETE FROM batch_template_snapshots
        WHERE revision_id IN (
            SELECT revision_id FROM template_revisions WHERE group_id = $group_id);
        """,
        """
        DELETE FROM calibration_drafts
        WHERE base_revision_id IN (
            SELECT revision_id FROM template_revisions WHERE group_id = $group_id);
        """,
        """
        DELETE FROM template_import_commands
        WHERE resource_id IN (
            SELECT resource_id FROM template_resources WHERE group_id = $group_id);
        """,
        "DELETE FROM template_resources WHERE group_id = $group_id;",
        "DELETE FROM template_revisions WHERE group_id = $group_id;",
        "DELETE FROM template_groups WHERE group_id = $group_id;"
    ];

    public async Task<IReadOnlyList<TemplateGroup>> ListGroupsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_id, name, current_revision_id, status, created_at, updated_at
            FROM template_groups
            ORDER BY updated_at DESC, group_id;
            """;
        var groups = new List<TemplateGroup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(TemplateGroup.Restore(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Enum.Parse<TemplateGroupStatus>(reader.GetString(3), ignoreCase: false),
                ParseTimestamp(reader.GetString(4)),
                ParseTimestamp(reader.GetString(5))));
        }

        return groups;
    }

    public Task SaveGroupAsync(TemplateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO template_groups(
                        group_id, name, current_revision_id, status, created_at, updated_at)
                    VALUES(
                        $group_id, $name, $current_revision_id, $status, $created_at, $updated_at)
                    ON CONFLICT(group_id) DO UPDATE SET
                        name = excluded.name,
                        status = excluded.status,
                        updated_at = excluded.updated_at;
                    """;
                AddGroupParameters(command, group);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<TemplateGroup?> GetGroupAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, current_revision_id, status, created_at, updated_at
            FROM template_groups
            WHERE group_id = $group_id;
            """;
        command.Parameters.AddWithValue("$group_id", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return TemplateGroup.Restore(
            groupId,
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            Enum.Parse<TemplateGroupStatus>(reader.GetString(2), ignoreCase: false),
            ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)));
    }

    public async Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot.batch_id
            FROM batch_template_snapshots snapshot
            JOIN template_revisions revision ON revision.revision_id = snapshot.revision_id
            LEFT JOIN tasks task ON task.task_id = snapshot.batch_id
            WHERE revision.group_id = $group_id
              AND (task.task_id IS NULL OR task.status NOT IN (
                   'Failed', 'Cancelled', 'Completed', 'CompletedWithIssues'))
            ORDER BY snapshot.batch_id;
            """;
        command.Parameters.AddWithValue("$group_id", groupId);
        var batchIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            batchIds.Add(reader.GetString(0));
        }
        return batchIds;
    }

    public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID");
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token).ConfigureAwait(false);
                var blockers = await GetBlockingBatchIdsAsync(
                    connection,
                    transaction,
                    groupId,
                    token).ConfigureAwait(false);
                if (blockers.Count > 0)
                {
                    throw new TemplateRuleViolationException("TPL_DELETE_BLOCKED_BY_ACTIVE_TASK");
                }

                foreach (var sql in DeleteTemplateGroupSql)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$group_id", groupId);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    private static void AddGroupParameters(SqliteCommand command, TemplateGroup group)
    {
        command.Parameters.AddWithValue("$group_id", group.GroupId);
        command.Parameters.AddWithValue("$name", group.Name);
        command.Parameters.AddWithValue("$current_revision_id", (object?)group.CurrentRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", group.Status.ToString());
        command.Parameters.AddWithValue("$created_at", group.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", group.UpdatedAt.ToString("O"));
    }

    private static async Task EnsureGroupExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM template_groups WHERE group_id = $group_id;";
        command.Parameters.AddWithValue("$group_id", groupId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        }
    }

    private static async Task<IReadOnlyList<string>> GetBlockingBatchIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot.batch_id
            FROM batch_template_snapshots snapshot
            JOIN template_revisions revision ON revision.revision_id = snapshot.revision_id
            LEFT JOIN tasks task ON task.task_id = snapshot.batch_id
            WHERE revision.group_id = $group_id
              AND (task.task_id IS NULL OR task.status NOT IN (
                   'Failed', 'Cancelled', 'Completed', 'CompletedWithIssues'))
            ORDER BY snapshot.batch_id;
            """;
        command.Parameters.AddWithValue("$group_id", groupId);
        var batchIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            batchIds.Add(reader.GetString(0));
        }
        return batchIds;
    }

    private static async Task<string?> GetCurrentRevisionIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT current_revision_id FROM template_groups WHERE group_id = $group_id;";
        command.Parameters.AddWithValue("$group_id", groupId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        }

        return result is DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<TemplateRevisionState?> GetRevisionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string revisionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM template_revisions WHERE revision_id = $revision_id;";
        command.Parameters.AddWithValue("$revision_id", revisionId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null
            ? null
            : Enum.Parse<TemplateRevisionState>(Convert.ToString(result, CultureInfo.InvariantCulture)!, false);
    }


}
