using System.Globalization;
using Desktop.Application.Diagnostics;
using Desktop.Application.Ai;
using Desktop.Application.Tasks;
using Desktop.Application.Rendering;
using Desktop.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore :
    ITaskEventStore,
    ICheckpointStore,
    IRenderPageCommitStore,
    IRenderBatchStore,
    IAppInstanceStore,
    IAiAnalysisStore,
    IAiAnalysisSchedulingStateStore,
    IAiAnalysisCheckpointDatabase,
    IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SqliteWriteQueue _writer;

    public SqliteDesktopStore(DesktopDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _writer = new SqliteWriteQueue(_connectionString);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await ConfigureConnectionAsync(connection, token).ConfigureAwait(false);
                await ApplyMigrationsAsync(connection, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public async Task<IReadOnlyList<TaskEvent>> LoadEventsAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadTaskEventsAsync(connection, null, taskId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TaskEvent>> ReadTaskEventsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, sequence_no, event_type, occurred_at, page_id, payload_digest
            FROM task_events
            WHERE task_id = $task_id
            ORDER BY sequence_no ASC;
            """;
        command.Parameters.AddWithValue("$task_id", taskId);

        var events = new List<TaskEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new TaskEvent(
                Guid.ParseExact(reader.GetString(0), "N"),
                taskId,
                reader.GetInt64(1),
                Enum.Parse<TaskEventType>(reader.GetString(2), ignoreCase: false),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }

        return events;
    }

    public Task<AppendEventOutcome> AppendAsync(
        TaskEvent taskEvent,
        TaskState resultingState,
        CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(
            async (connection, token) =>
            {
                // Reopening a render task requires the same transaction as its new attempt and origin.
                if (taskEvent.Type is TaskEventType.RenderPageRegenerationRequested or TaskEventType.RenderPageRepairRequested) throw BatchInvalid();
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token)
                    .ConfigureAwait(false);
                if (await EventExistsAsync(connection, transaction, taskEvent.EventId, token).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return AppendEventOutcome.Duplicate;
                }

                var lastSequence = await GetLastSequenceAsync(connection, transaction, taskEvent.TaskId, token)
                    .ConfigureAwait(false);
                if (lastSequence + 1 != taskEvent.Sequence)
                {
                    throw new InvalidOperationException("TASK_EVENT_CONCURRENCY_CONFLICT");
                }

                await InsertEventAsync(connection, transaction, taskEvent, token).ConfigureAwait(false);
                await UpsertTaskAsync(connection, transaction, resultingState, taskEvent.OccurredAt, token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return AppendEventOutcome.Appended;
            },
            cancellationToken);

    public Task SaveAsync(TaskCheckpoint checkpoint, CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(
            async (connection, token) =>
            {
                // Render manifests and checkpoints are one journal. Generic callers may
                // neither create half of it nor replace its page index during recovery.
                if (checkpoint.Stage == "render_output" || checkpoint.OutputVariantId is not null) throw RenderConflict();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM render_page_checkpoints WHERE task_id=$task_id AND style_id=$style_id AND page_id=$page_id;";
                AddCheckpointParameters(command, checkpoint);
                if ((long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0) throw RenderConflict();
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO checkpoints (
                        task_id, style_id, page_id, stage, attempt, plan_digest,
                        bridge_execution_id, output_commit_state, updated_at)
                    VALUES (
                        $task_id, $style_id, $page_id, $stage, $attempt, $plan_digest,
                        $bridge_execution_id, $output_commit_state, $updated_at)
                    ON CONFLICT(task_id, style_id, page_id) DO UPDATE SET
                        stage = excluded.stage,
                        attempt = excluded.attempt,
                        plan_digest = excluded.plan_digest,
                        bridge_execution_id = excluded.bridge_execution_id,
                        output_commit_state = excluded.output_commit_state,
                        updated_at = excluded.updated_at
                    WHERE checkpoints.stage <> 'render_output';
                    """;
                AddCheckpointParameters(command, checkpoint);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw RenderConflict();
                return true;
            },
            cancellationToken);

    public async Task<IReadOnlyList<TaskCheckpoint>> LoadAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT style_id, page_id, stage, attempt, plan_digest,
                   bridge_execution_id, output_commit_state, updated_at, '' AS output_variant_id
            FROM checkpoints
            WHERE task_id = $task_id AND stage <> 'render_output'
            UNION ALL
            SELECT style_id,page_id,stage,attempt,plan_digest,bridge_execution_id,output_commit_state,updated_at,output_variant_id
            FROM render_page_checkpoints WHERE task_id=$task_id
            ORDER BY style_id, page_id, output_variant_id, attempt;
            """;
        command.Parameters.AddWithValue("$task_id", taskId);

        var checkpoints = new List<TaskCheckpoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            checkpoints.Add(new TaskCheckpoint(
                taskId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Enum.Parse<OutputCommitState>(reader.GetString(6), ignoreCase: false),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(8).Length == 0 ? null : reader.GetString(8)));
        }

        return checkpoints;
    }

    public Task RecordStartAsync(AppInstanceRecord instance, CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO app_instance(instance_id, started_at, exited_at, safe_exit)
                    VALUES($instance_id, $started_at, NULL, 0);
                    """;
                command.Parameters.AddWithValue("$instance_id", instance.InstanceId);
                command.Parameters.AddWithValue("$started_at", instance.StartedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public Task RecordSafeExitAsync(
        string instanceId,
        DateTimeOffset exitedAt,
        CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE app_instance
                    SET exited_at = $exited_at, safe_exit = 1
                    WHERE instance_id = $instance_id;
                    """;
                command.Parameters.AddWithValue("$instance_id", instanceId);
                command.Parameters.AddWithValue("$exited_at", exitedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    private async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> EventExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM task_events WHERE event_id = $event_id LIMIT 1;";
        command.Parameters.AddWithValue("$event_id", eventId.ToString("N"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<long> GetLastSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence_no), 0) FROM task_events WHERE task_id = $task_id;";
        command.Parameters.AddWithValue("$task_id", taskId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TaskEvent taskEvent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO task_events(
                event_id, task_id, sequence_no, event_type, occurred_at, page_id, payload_digest)
            VALUES(
                $event_id, $task_id, $sequence_no, $event_type, $occurred_at, $page_id, $payload_digest);
            """;
        command.Parameters.AddWithValue("$event_id", taskEvent.EventId.ToString("N"));
        command.Parameters.AddWithValue("$task_id", taskEvent.TaskId);
        command.Parameters.AddWithValue("$sequence_no", taskEvent.Sequence);
        command.Parameters.AddWithValue("$event_type", taskEvent.Type.ToString());
        command.Parameters.AddWithValue("$occurred_at", taskEvent.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$page_id", (object?)taskEvent.PageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_digest", taskEvent.PayloadDigest);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TaskState state,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks(task_id, status, version, output_count, issue_count, updated_at)
            VALUES($task_id, $status, $version, $output_count, $issue_count, $updated_at)
            ON CONFLICT(task_id) DO UPDATE SET
                status = excluded.status,
                version = excluded.version,
                output_count = excluded.output_count,
                issue_count = excluded.issue_count,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$task_id", state.TaskId);
        command.Parameters.AddWithValue("$status", state.Status.ToString());
        command.Parameters.AddWithValue("$version", state.LastSequence);
        command.Parameters.AddWithValue("$output_count", state.OutputCount);
        command.Parameters.AddWithValue("$issue_count", state.IssueCount);
        command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddCheckpointParameters(SqliteCommand command, TaskCheckpoint checkpoint)
    {
        command.Parameters.AddWithValue("$task_id", checkpoint.TaskId);
        command.Parameters.AddWithValue("$style_id", checkpoint.StyleId);
        command.Parameters.AddWithValue("$page_id", checkpoint.PageId);
        command.Parameters.AddWithValue("$stage", checkpoint.Stage);
        command.Parameters.AddWithValue("$attempt", checkpoint.Attempt);
        command.Parameters.AddWithValue("$plan_digest", checkpoint.PlanDigest);
        command.Parameters.AddWithValue("$bridge_execution_id", (object?)checkpoint.BridgeExecutionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$output_commit_state", checkpoint.OutputState.ToString());
        command.Parameters.AddWithValue("$updated_at", checkpoint.UpdatedAt.ToString("O"));
    }
}
