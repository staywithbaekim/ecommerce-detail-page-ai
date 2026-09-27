using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Bridge;
using Desktop.Application.Tasks;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    internal async Task<IReadOnlyList<string>> ListRenderOutputRegistrationIdsAsync(CancellationToken token)
    {
        await using var connection = await OpenReadConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT batch_id FROM render_output_registrations ORDER BY batch_id;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    internal async Task<RenderStyleOutputRegistration?> LoadRenderOutputRegistrationAsync(string batchId, CancellationToken token)
    {
        var batch = await GetAsync(batchId, token).ConfigureAwait(false);
        await using var connection = await OpenReadConnectionAsync(token).ConfigureAwait(false);
        var registration = await ReadOutputRegistration(connection, null, batchId, token).ConfigureAwait(false);
        if (registration is null) return null;
        var initial = await ReadBatchRevision(connection, null, batchId, 1, token).ConfigureAwait(false)
            ?? throw RenderStyleOutputRegistrationData.Invalid();
        RenderStyleOutputRegistrationData.Validate(registration, batch ?? throw RenderStyleOutputRegistrationData.Invalid(), initial);
        return registration;
    }

    internal Task RegisterRenderOutputsAsync(RenderStyleOutputRegistration value, CancellationToken token) =>
        _writer.ExecuteAsync(async (connection, cancellation) =>
        {
            // All input writers use this queue. Re-read the frozen graph inside its ownership,
            // then bind the initial render journal and the entire registration in one transaction.
            var batch = await GetAsync(value.BatchId, cancellation).ConfigureAwait(false)
                ?? throw RenderStyleOutputRegistrationData.Invalid();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);
            var initial = await ReadBatchRevision(connection, transaction, value.BatchId, 1, cancellation).ConfigureAwait(false)
                ?? throw RenderStyleOutputRegistrationData.Invalid();
            RenderStyleOutputRegistrationData.Validate(value, batch, initial);
            var previous = await ReadOutputRegistration(connection, transaction, value.BatchId, cancellation).ConfigureAwait(false);
            var json = RenderStyleOutputRegistrationData.Encode(value);
            if (previous is not null)
            {
                if (RenderStyleOutputRegistrationData.Encode(previous) != json) throw RenderStyleOutputRegistrationData.Invalid();
            }
            else
            {
                var latest = await ReadBatch(connection, transaction, value.BatchId, cancellation).ConfigureAwait(false);
                if (latest?.Revision != 1) throw RenderStyleOutputRegistrationData.Invalid();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO render_output_registrations(batch_id,render_revision,payload_json,payload_sha256)
                    VALUES($batch,1,$json,$hash);
                    """;
                AddOutputPayload(command, value.BatchId, json);
                await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellation).ConfigureAwait(false);
            return true;
        }, token);

    internal async Task<RenderStyleOutputStage?> LoadRenderOutputStageAsync(string batchId, string styleId, CancellationToken token)
    {
        await using var connection = await OpenReadConnectionAsync(token).ConfigureAwait(false);
        return await ReadOutputStage(connection, null, batchId, styleId, token).ConfigureAwait(false);
    }

    internal async Task<RenderStyleOutputPublication?> LoadRenderOutputPublicationAsync(string batchId, string styleId,
        CancellationToken token)
    {
        await using var connection = await OpenReadConnectionAsync(token).ConfigureAwait(false);
        return await ReadOutputPublication(connection, null, batchId, styleId, token).ConfigureAwait(false);
    }

    internal Task SaveRenderOutputPublicationAsync(RenderStyleOutputPublication value, CancellationToken token) =>
        _writer.ExecuteAsync(async (connection, cancellation) =>
        {
            RenderStyleOutputPublicationData.Validate(value);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);
            var registration = await ReadOutputRegistration(connection, transaction, value.BatchId, cancellation).ConfigureAwait(false)
                ?? throw RenderStyleOutputPublicationData.Invalid("RENDER_OUTPUT_REGISTRATION_MISSING");
            var journal = await ReadBatch(connection, transaction, value.BatchId, cancellation).ConfigureAwait(false)
                ?? throw RenderStyleOutputPublicationData.Invalid();
            var stage = await ReadOutputStage(connection, transaction, value.BatchId, value.StyleId, cancellation).ConfigureAwait(false)
                ?? throw RenderStyleOutputPublicationData.Invalid();
            RenderStyleOutputPublicationData.ValidateAgainst(value, registration, journal, stage);
            foreach (var page in RenderStyleOutputPublicationData.LatestPagesForStyle(journal, value.StyleId))
                if (page.Commit is null || await ReadRenderCommit(connection, transaction, page.Commit, cancellation).ConfigureAwait(false) != OutputCommitState.Committed)
                    throw RenderStyleOutputPublicationData.Invalid();
            var previous = await ReadOutputPublication(connection, transaction, value.BatchId, value.StyleId, cancellation).ConfigureAwait(false);
            if (previous is null && value.State != RenderStyleOutputPublicationData.Copying) throw RenderStyleOutputPublicationData.Invalid();
            if (previous is not null)
            {
                if (!RenderStyleOutputPublicationData.Equal(previous with { State = value.State, FinalIdentity = value.FinalIdentity }, value)
                    || (previous.State, value.State) is not (("Copying", "Copying") or ("Copying", "Verified")
                        or ("Verified", "Verified") or ("Verified", "Committed") or ("Committed", "Committed")))
                    throw RenderStyleOutputPublicationData.Invalid("RENDER_OUTPUT_PUBLICATION_FACT_MISMATCH");
                if (RenderStyleOutputPublicationData.Equal(previous, value)) { await transaction.CommitAsync(cancellation).ConfigureAwait(false); return true; }
            }
            if (value.State != RenderStyleOutputPublicationData.Copying)
            {
                var files = await ReadPublicFiles(connection, transaction, value.BatchId, value.StyleId, cancellation).ConfigureAwait(false);
                var expected = RenderStyleOutputPublicationData.ExpectedFiles(value);
                if (files.Count != expected.Count || files.Any(file => !file.Verified || !expected.Any(item =>
                    item.FileName == file.FileName && item.ByteLength == file.ByteLength && item.Sha256 == file.Sha256)))
                    throw RenderStyleOutputPublicationData.Invalid();
            }
            var json = RenderStyleOutputPublicationData.Encode(value);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO render_output_publications(batch_id,style_id,state,payload_json,payload_sha256)
                VALUES($batch,$style,$state,$json,$hash)
                ON CONFLICT(batch_id,style_id) DO UPDATE SET state=excluded.state,payload_json=excluded.payload_json,payload_sha256=excluded.payload_sha256;
                """;
            command.Parameters.AddWithValue("$batch", value.BatchId);
            command.Parameters.AddWithValue("$style", value.StyleId);
            command.Parameters.AddWithValue("$state", value.State);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$hash", RenderStyleOutputPublicationData.Hash(json));
            await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            await transaction.CommitAsync(cancellation).ConfigureAwait(false);
            return true;
        }, token);

    internal Task RegisterRenderOutputStageAsync(RenderStyleOutputStage value, CancellationToken token) =>
        _writer.ExecuteAsync(async (connection, cancellation) =>
        {
            RenderStyleOutputRegistrationData.ValidateIdentity(value.Identity);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);
            var registration = await ReadOutputRegistration(connection, transaction, value.BatchId, cancellation).ConfigureAwait(false);
            var style = registration?.Styles.SingleOrDefault(item => item.Target.StyleId == value.StyleId);
            if (style is null || style.ParentIdentity.Volume != value.Identity.Volume || !style.Pages.Any(page => page.PlanDigest is not null))
                throw RenderStyleOutputRegistrationData.Invalid();
            var previous = await ReadOutputStage(connection, transaction, value.BatchId, value.StyleId, cancellation).ConfigureAwait(false);
            if (previous is not null)
            {
                if (previous != value) throw RenderStyleOutputRegistrationData.Invalid();
            }
            else
            {
                var latest = await ReadBatch(connection, transaction, value.BatchId, cancellation).ConfigureAwait(false);
                if (latest?.Revision != 1) throw RenderStyleOutputRegistrationData.Invalid();
                var json = RenderStyleOutputRegistrationData.Encode(value);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO render_output_stages(batch_id,style_id,payload_json,payload_sha256)
                    VALUES($batch,$style,$json,$hash);
                    """;
                AddOutputPayload(command, value.BatchId, json);
                command.Parameters.AddWithValue("$style", value.StyleId);
                await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellation).ConfigureAwait(false);
            return true;
        }, token);

    private static async Task<RenderStyleOutputRegistration?> ReadOutputRegistration(SqliteConnection connection,
        SqliteTransaction? transaction, string batchId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT length(CAST(payload_json AS BLOB)),payload_json,payload_sha256,render_revision
            FROM render_output_registrations WHERE batch_id=$batch;
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        if (reader.GetInt64(0) is < 1 or > RenderStyleOutputRegistrationData.MaxBytes || reader.GetInt64(3) != 1)
            throw RenderStyleOutputRegistrationData.Invalid();
        var value = RenderStyleOutputRegistrationData.Decode<RenderStyleOutputRegistration>(reader.GetString(1), reader.GetString(2));
        if (value.BatchId != batchId || value.Version != 1 || value.Styles.IsDefaultOrEmpty || value.Styles.Any(style => style?.Target is null))
            throw RenderStyleOutputRegistrationData.Invalid();
        return value;
    }

    private static async Task<RenderStyleOutputStage?> ReadOutputStage(SqliteConnection connection,
        SqliteTransaction? transaction, string batchId, string styleId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT length(CAST(payload_json AS BLOB)),payload_json,payload_sha256
            FROM render_output_stages WHERE batch_id=$batch AND style_id=$style;
            """;
        command.Parameters.AddWithValue("$batch", batchId); command.Parameters.AddWithValue("$style", styleId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        if (reader.GetInt64(0) is < 1 or > RenderStyleOutputRegistrationData.MaxBytes) throw RenderStyleOutputRegistrationData.Invalid();
        var value = RenderStyleOutputRegistrationData.Decode<RenderStyleOutputStage>(reader.GetString(1), reader.GetString(2));
        if (value.BatchId != batchId || value.StyleId != styleId) throw RenderStyleOutputRegistrationData.Invalid();
        RenderStyleOutputRegistrationData.ValidateIdentity(value.Identity);
        return value;
    }

    private static async Task<RenderStyleOutputPublication?> ReadOutputPublication(SqliteConnection connection,
        SqliteTransaction? transaction, string batchId, string styleId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT length(CAST(payload_json AS BLOB)),payload_json,payload_sha256,state
            FROM render_output_publications WHERE batch_id=$batch AND style_id=$style;
            """;
        command.Parameters.AddWithValue("$batch", batchId); command.Parameters.AddWithValue("$style", styleId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        if (reader.GetInt64(0) is < 1 or > RenderStyleOutputRegistrationData.MaxBytes
            || reader.GetString(3) is not (RenderStyleOutputPublicationData.Copying or RenderStyleOutputPublicationData.Verified or RenderStyleOutputPublicationData.Committed))
            throw RenderStyleOutputPublicationData.Invalid();
        var value = RenderStyleOutputPublicationData.Decode<RenderStyleOutputPublication>(reader.GetString(1), reader.GetString(2));
        if (value.BatchId != batchId || value.StyleId != styleId || value.State != reader.GetString(3))
            throw RenderStyleOutputPublicationData.Invalid();
        RenderStyleOutputPublicationData.Validate(value);
        return value;
    }

    internal async Task<IReadOnlyList<RenderPublicFileOwnership>> LoadRenderPublicFilesAsync(string batchId, string styleId, CancellationToken token)
    {
        await using var connection = await OpenReadConnectionAsync(token).ConfigureAwait(false);
        return await ReadPublicFiles(connection, null, batchId, styleId, token).ConfigureAwait(false);
    }

    internal Task SaveRenderPublicFileAsync(RenderPublicFileOwnership value, CancellationToken token) =>
        _writer.ExecuteAsync(async (connection, cancellation) =>
        {
            ValidatePublicOwnership(value);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);
            var publication = await ReadOutputPublication(connection, transaction, value.BatchId, value.StyleId, cancellation).ConfigureAwait(false)
                ?? throw RenderStyleOutputPublicationData.Invalid();
            if (publication.State != RenderStyleOutputPublicationData.Copying || value.Identity.Volume != publication.StageIdentity.Volume
                || !RenderStyleOutputPublicationData.ExpectedFiles(publication).Any(file => file.FileName == value.FileName
                    && file.ByteLength == value.ByteLength && file.Sha256 == value.Sha256)) throw RenderStyleOutputPublicationData.Invalid();
            var files = await ReadPublicFiles(connection, transaction, value.BatchId, value.StyleId, cancellation).ConfigureAwait(false);
            var prior = files.SingleOrDefault(file => file.FileName == value.FileName);
            if (prior is null && value.Verified || prior is not null && (prior with { Verified = value.Verified } != value
                || prior.Verified && !value.Verified)) throw RenderStyleOutputPublicationData.Invalid();
            if (prior == value) { await transaction.CommitAsync(cancellation).ConfigureAwait(false); return true; }
            var json = RenderStyleOutputPublicationData.Encode(value);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO render_output_publication_files(batch_id,style_id,file_name,payload_json,payload_sha256)
                VALUES($batch,$style,$name,$json,$hash)
                ON CONFLICT(batch_id,style_id,file_name) DO UPDATE SET payload_json=excluded.payload_json,payload_sha256=excluded.payload_sha256;
                """;
            command.Parameters.AddWithValue("$batch", value.BatchId); command.Parameters.AddWithValue("$style", value.StyleId);
            command.Parameters.AddWithValue("$name", value.FileName); command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$hash", RenderStyleOutputPublicationData.Hash(json));
            await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            await transaction.CommitAsync(cancellation).ConfigureAwait(false);
            return true;
        }, token);

    private static async Task<IReadOnlyList<RenderPublicFileOwnership>> ReadPublicFiles(SqliteConnection connection,
        SqliteTransaction? transaction, string batchId, string styleId, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT file_name,length(CAST(payload_json AS BLOB)),payload_json,payload_sha256
            FROM render_output_publication_files WHERE batch_id=$batch AND style_id=$style ORDER BY file_name;
            """;
        command.Parameters.AddWithValue("$batch", batchId); command.Parameters.AddWithValue("$style", styleId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var files = new List<RenderPublicFileOwnership>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (files.Count > 20002 || reader.GetInt64(1) is < 1 or > 32768) throw RenderStyleOutputPublicationData.Invalid();
            var value = RenderStyleOutputPublicationData.Decode<RenderPublicFileOwnership>(reader.GetString(2), reader.GetString(3));
            ValidatePublicOwnership(value);
            if (value.BatchId != batchId || value.StyleId != styleId || value.FileName != reader.GetString(0)) throw RenderStyleOutputPublicationData.Invalid();
            files.Add(value);
        }
        return files;
    }

    private static void ValidatePublicOwnership(RenderPublicFileOwnership value)
    {
        if (value.Version != 1 || value.Identity is null || value.Identity.Created <= 0 || value.Identity.High == 0 && value.Identity.Low == 0
            || value.ByteLength is < 1 or > 2147483648 || value.Sha256 is not { Length: 64 } || !value.Sha256.All(char.IsAsciiHexDigit))
            throw RenderStyleOutputPublicationData.Invalid();
    }

    private static void AddOutputPayload(SqliteCommand command, string batch, string json)
    {
        command.Parameters.AddWithValue("$batch", batch); command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$hash", RenderStyleOutputRegistrationData.Hash(json));
    }
}
