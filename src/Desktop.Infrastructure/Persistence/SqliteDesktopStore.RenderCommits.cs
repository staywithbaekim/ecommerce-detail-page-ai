using System.Globalization;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    public Task PrepareRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(async (connection, token) =>
        {
            ValidateRenderCheckpoint(checkpoint);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var state = await ReadRenderCommit(connection, transaction, checkpoint, token).ConfigureAwait(false);
            if (state is not null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            }
            await using (var task = connection.CreateCommand())
            {
                task.Transaction = transaction;
                task.CommandText = "SELECT status FROM tasks WHERE task_id=$task;";
                task.Parameters.AddWithValue("$task", checkpoint.Page.TaskId);
                if (await task.ExecuteScalarAsync(token).ConfigureAwait(false) is not ("Rendering" or "RecoveryRequired"))
                    throw RenderConflict();
            }
            await using (var manifest = connection.CreateCommand())
            {
                manifest.Transaction = transaction;
                manifest.CommandText = """
                    INSERT INTO manifests(manifest_id, relative_path, length, sha256, state, updated_at)
                    VALUES($id,$path,$length,$hash,'render_verified',$time);
                    """;
                AddRenderManifestParameters(manifest, checkpoint);
                await manifest.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using (var page = connection.CreateCommand())
            {
                page.Transaction = transaction;
                page.CommandText = """
                    INSERT INTO render_page_checkpoints(task_id,style_id,page_id,output_variant_id,stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at)
                    VALUES($task_id,$style_id,$page_id,$variant,$stage,$attempt,$plan_digest,$bridge_execution_id,$output_commit_state,$updated_at);
                    """;
                AddCheckpointParameters(page, checkpoint.Page);
                page.Parameters.AddWithValue("$variant", checkpoint.Page.OutputVariantId ?? "");
                await page.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task CompleteRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(async (connection, token) =>
        {
            ValidateRenderCheckpoint(checkpoint);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var state = await ReadRenderCommit(connection, transaction, checkpoint, token).ConfigureAwait(false);
            if (state is null) throw RenderConflict();
            if (state == OutputCommitState.Committed)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            }
            var events = new List<TaskEvent>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT event_id,sequence_no,event_type,occurred_at,page_id,payload_digest
                    FROM task_events WHERE task_id=$task ORDER BY sequence_no;
                    """;
                read.Parameters.AddWithValue("$task", checkpoint.Page.TaskId);
                await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    events.Add(new(Guid.ParseExact(reader.GetString(0), "N"), checkpoint.Page.TaskId, reader.GetInt64(1),
                        Enum.Parse<TaskEventType>(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
            }
            var current = TaskStateReducer.Replay(checkpoint.Page.TaskId, events);
            // Page keys include product identity: the same template page in two styles
            // must contribute two outputs while duplicate recovery contributes only one.
            var pageKey = $"{checkpoint.Page.StyleId}/{checkpoint.Page.PageId}"
                + (checkpoint.Page.OutputVariantId is { } variant ? $"/{variant}" : "");
            var completed = TaskEvent.Create(checkpoint.Page.TaskId, current.LastSequence + 1,
                TaskEventType.PageCommitted, pageKey, checkpoint.ManifestSha256);
            var next = TaskStateReducer.Apply(current, completed);
            await InsertEventAsync(connection, transaction, completed, token).ConfigureAwait(false);
            await UpsertTaskAsync(connection, transaction, next, completed.OccurredAt, token).ConfigureAwait(false);
            await using (var write = connection.CreateCommand())
            {
                write.Transaction = transaction;
                write.CommandText = """
                    UPDATE render_page_checkpoints SET output_commit_state='Committed'
                    WHERE task_id=$task AND style_id=$style AND page_id=$page AND output_variant_id=$variant AND attempt=$attempt;
                    UPDATE manifests SET state='render_committed' WHERE manifest_id=$id;
                    """;
                write.Parameters.AddWithValue("$task", checkpoint.Page.TaskId);
                write.Parameters.AddWithValue("$style", checkpoint.Page.StyleId);
                write.Parameters.AddWithValue("$page", checkpoint.Page.PageId);
                write.Parameters.AddWithValue("$variant", checkpoint.Page.OutputVariantId ?? "");
                write.Parameters.AddWithValue("$attempt", checkpoint.Page.Attempt);
                write.Parameters.AddWithValue("$id", checkpoint.ManifestId);
                await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<OutputCommitState?> ReadRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ValidateRenderCheckpoint(checkpoint);
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRenderCommit(connection, transaction, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OutputCommitState?> ReadRenderCommit(SqliteConnection connection, SqliteTransaction transaction,
        RenderPageCommitCheckpoint expected, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at
            FROM render_page_checkpoints WHERE task_id=$task AND style_id=$style AND page_id=$page AND output_variant_id=$variant AND attempt=$attempt;
            """;
        command.Parameters.AddWithValue("$task", expected.Page.TaskId);
        command.Parameters.AddWithValue("$style", expected.Page.StyleId);
        command.Parameters.AddWithValue("$page", expected.Page.PageId);
        command.Parameters.AddWithValue("$variant", expected.Page.OutputVariantId ?? "");
        command.Parameters.AddWithValue("$attempt", expected.Page.Attempt);
        OutputCommitState? state = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != expected.Page.Stage || reader.GetInt32(1) != expected.Page.Attempt
                    || reader.GetString(2) != expected.Page.PlanDigest || reader.IsDBNull(3) || reader.GetString(3) != expected.Page.BridgeExecutionId
                    || reader.GetString(5) != expected.Page.UpdatedAt.ToString("O")
                    || reader.GetString(4) is not ("Verified" or "Committed")) throw RenderConflict();
                state = Enum.Parse<OutputCommitState>(reader.GetString(4));
            }
        }
        command.CommandText = "SELECT relative_path,length,sha256,state FROM manifests WHERE manifest_id=$id;";
        command.Parameters.Clear(); command.Parameters.AddWithValue("$id", expected.ManifestId);
        await using var manifest = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var exists = await manifest.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!exists && state is null) return null;
        if (!exists || state is null || manifest.GetString(0) != expected.ManifestRelativePath
            || manifest.GetInt64(1) != expected.ManifestByteLength || manifest.GetString(2) != expected.ManifestSha256
            || manifest.GetString(3) != (state == OutputCommitState.Committed ? "render_committed" : "render_verified")) throw RenderConflict();
        return state;
    }

    private static void AddRenderManifestParameters(SqliteCommand command, RenderPageCommitCheckpoint checkpoint)
    {
        command.Parameters.AddWithValue("$id", checkpoint.ManifestId);
        command.Parameters.AddWithValue("$path", checkpoint.ManifestRelativePath);
        command.Parameters.AddWithValue("$length", checkpoint.ManifestByteLength);
        command.Parameters.AddWithValue("$hash", checkpoint.ManifestSha256);
        command.Parameters.AddWithValue("$time", checkpoint.Page.UpdatedAt.ToString("O"));
    }

    private static void ValidateRenderCheckpoint(RenderPageCommitCheckpoint checkpoint)
    {
        static bool Id(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
        static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
        if (!Id(checkpoint.Page.TaskId) || !Id(checkpoint.Page.StyleId) || !Id(checkpoint.Page.PageId)
            || checkpoint.Page.OutputVariantId is { } variant && !Id(variant)
            || !Id(checkpoint.Page.BridgeExecutionId) || checkpoint.Page.Attempt < 1 || checkpoint.Page.Stage != "render_output"
            || checkpoint.Page.OutputState != OutputCommitState.Verified || checkpoint.Page.PlanDigest.Length != 71
            || !checkpoint.Page.PlanDigest.StartsWith("sha256:", StringComparison.Ordinal) || !Hash(checkpoint.Page.PlanDigest[7..])
            || checkpoint.ManifestId != "render:" + checkpoint.Page.PlanDigest[7..] || !Hash(checkpoint.ManifestSha256)
            || checkpoint.ManifestByteLength is < 1 or > 1048576 || Path.IsPathRooted(checkpoint.ManifestRelativePath)
            || checkpoint.ManifestRelativePath.Split('/', '\\').Any(part => part is "" or "." or ".." || part.Contains(':')))
            throw RenderConflict();
    }

    private static InvalidDataException RenderConflict() => new("RENDER_CHECKPOINT_CONFLICT");
}
