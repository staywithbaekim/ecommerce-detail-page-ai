using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    private static string RegenerationPageKey(RenderBatchPage page) => $"{page.StyleId}/{page.PageId}"
        + (page.OutputVariantId is { } variant ? $"/{variant}" : "");

    private static async Task SaveRegenerationOrigin(SqliteConnection connection, SqliteTransaction transaction,
        RenderBatchJournal journal, CancellationToken token)
    {
        var page = journal.Pages[^1];
        using var plan = JsonDocument.Parse(page.PlanJson!);
        var events = await ReadTaskEventsAsync(connection, transaction, journal.BatchId, token).ConfigureAwait(false);
        var current = TaskStateReducer.Replay(journal.BatchId, events);
        var requested = TaskEvent.Create(journal.BatchId, current.LastSequence + 1,
            page.SizeTableRepair is null ? TaskEventType.RenderPageRegenerationRequested : TaskEventType.RenderPageRepairRequested,
            RegenerationPageKey(page), plan.RootElement.GetProperty("plan_digest").GetString()!);
        var next = TaskStateReducer.Apply(current, requested);
        await InsertEventAsync(connection, transaction, requested, token).ConfigureAwait(false);
        await UpsertTaskAsync(connection, transaction, next, requested.OccurredAt, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO render_batch_attempt_origins(batch_id,page_index,birth_revision,event_id) VALUES($batch,$index,$revision,$event);";
        command.Parameters.AddWithValue("$batch", journal.BatchId);
        command.Parameters.AddWithValue("$index", journal.RegenerationPageIndex!.Value);
        command.Parameters.AddWithValue("$revision", journal.Revision);
        command.Parameters.AddWithValue("$event", requested.EventId.ToString("N"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (page.SizeTableRepair is { } repair)
        {
            await using var request = connection.CreateCommand();
            request.Transaction = transaction;
            request.CommandText = """
                INSERT INTO render_page_repairs(batch_id,request_id,page_index,birth_revision,request_digest)
                VALUES($batch,$request,$index,$revision,$digest);
                """;
            request.Parameters.AddWithValue("$batch", journal.BatchId);
            request.Parameters.AddWithValue("$request", repair.RequestId);
            request.Parameters.AddWithValue("$index", journal.RegenerationPageIndex.Value);
            request.Parameters.AddWithValue("$revision", journal.Revision);
            request.Parameters.AddWithValue("$digest", repair.RequestDigest);
            await request.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task ReadRegenerationOrigins(SqliteConnection connection, SqliteTransaction? transaction,
        RenderBatchJournal latest, List<RenderBatchPage> prepared, CancellationToken token)
    {
        var origins = new List<(int Index, long Revision, string? Kind, string? Task, string? Page, string? Digest)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT o.page_index,o.birth_revision,e.event_type,e.task_id,e.page_id,e.payload_digest
                FROM render_batch_attempt_origins o LEFT JOIN task_events e ON e.event_id=o.event_id
                WHERE o.batch_id=$batch AND o.birth_revision<=$revision ORDER BY o.page_index;
                """;
            command.Parameters.AddWithValue("$batch", latest.BatchId);
            command.Parameters.AddWithValue("$revision", latest.Revision);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                origins.Add((reader.GetInt32(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        var repairRequests = await ReadSizeRepairRequests(connection, transaction, latest, token).ConfigureAwait(false);
        foreach (var origin in origins)
        {
            if (origin.Index != prepared.Count || origin.Task != latest.BatchId) throw BatchInvalid();
            var parent = await ReadBatchRevision(connection, transaction, latest.BatchId, origin.Revision - 1, token).ConfigureAwait(false) ?? throw BatchInvalid();
            var birth = await ReadBatchRevision(connection, transaction, latest.BatchId, origin.Revision, token).ConfigureAwait(false) ?? throw BatchInvalid();
            parent = EnrichBatch(parent, prepared);
            if (birth.RegenerationPageIndex != origin.Index || birth.Pages.Count != prepared.Count + 1) throw BatchInvalid();
            // Legacy preparation enrichment is read-only, including the parent of the first new attempt.
            var added = birth.Pages[^1];
            var expectedKind = added.SizeTableRepair is null ? nameof(TaskEventType.RenderPageRegenerationRequested)
                : nameof(TaskEventType.RenderPageRepairRequested);
            if (origin.Kind != expectedKind) throw BatchInvalid();
            if (added.SizeTableRepair is { } repair)
            {
                if (!repairRequests.Remove(origin.Index, out var request) || request.Revision != origin.Revision
                    || request.Id != repair.RequestId || request.Digest != repair.RequestDigest) throw BatchInvalid();
            }
            else if (repairRequests.ContainsKey(origin.Index)) throw BatchInvalid();
            var birthOrigins = prepared.Append(added).ToArray();
            birth = EnrichBatch(birth, birthOrigins);
            RenderBatchJournalValidator.ValidateUpdate(parent, birth);
            using var plan = JsonDocument.Parse(added.PlanJson!);
            if (origin.Page != RegenerationPageKey(added) || origin.Digest != plan.RootElement.GetProperty("plan_digest").GetString()) throw BatchInvalid();
            prepared.Add(added);
        }
        if (repairRequests.Count != 0) throw BatchInvalid();
    }

    private static async Task<Dictionary<int, (string Id, long Revision, string Digest)>> ReadSizeRepairRequests(
        SqliteConnection connection, SqliteTransaction? transaction, RenderBatchJournal journal, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT page_index,request_id,birth_revision,request_digest FROM render_page_repairs
            WHERE batch_id=$batch AND birth_revision<=$revision;
            """;
        command.Parameters.AddWithValue("$batch", journal.BatchId);
        command.Parameters.AddWithValue("$revision", journal.Revision);
        var result = new Dictionary<int, (string, long, string)>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            result.Add(reader.GetInt32(0), (reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        return result;
    }
}
