using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore : IBatchRepository
{
    public Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token).ConfigureAwait(false);
                await SaveBatchGraphAsync(connection, transaction, draft, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    internal Task CommitReadyAggregateAsync(BatchDraft ready, CancellationToken cancellationToken)
    {
        if (ready.ScanStatus != BatchScanStatus.ScanReady)
        {
            throw new InputRuleViolationException("INPUT_COMMIT_RESULT_NOT_READY");
        }

        // Once the manifest journal has been staged, the input commit is a
        // non-cancellable durability barrier.  A caller cancellation must
        // not turn an already committed SQLite transaction into an unknown
        // result that can be mistaken for a failed scan.
        return _writer.ExecuteAsync(
            async (connection, _) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                await SaveBatchGraphAsync(
                    connection,
                    transaction,
                    ready,
                    CancellationToken.None).ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return true;
            },
            CancellationToken.None);
    }

    internal Task RollbackReadyAggregateAsync(
        BatchDraft scanningDraft,
        CancellationToken cancellationToken) => SaveAsync(scanningDraft, CancellationToken.None);

    public async Task<BatchDraft?> GetAsync(string batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        BatchRow? row;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT scan_status, input_type, input_root, output_root, template_snapshot_id,
                       selection_mode, include_editable_psd, manifest_reference, manifest_digest,
                       block_reason_code, block_reason_category, block_reason_action,
                       created_at, updated_at
                FROM input_batches
                WHERE batch_id = $batch_id;
                """;
            command.Parameters.AddWithValue("$batch_id", batchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            row = new BatchRow(
                Enum.Parse<BatchScanStatus>(reader.GetString(0), false),
                reader.IsDBNull(1) ? null : Enum.Parse<BatchInputType>(reader.GetString(1), false),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                Enum.Parse<BatchSelectionMode>(reader.GetString(5), false),
                reader.GetInt32(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                ParseTimestamp(reader.GetString(12)),
                ParseTimestamp(reader.GetString(13)));
        }

        var snapshot = await LoadSnapshotByIdAsync(
            connection,
            row.TemplateSnapshotId,
            batchId,
            cancellationToken).ConfigureAwait(false);
        var assets = await LoadInputAssetsAsync(connection, batchId, cancellationToken).ConfigureAwait(false);
        var styles = await LoadInputStylesAsync(connection, batchId, assets, cancellationToken).ConfigureAwait(false);
        var blockReason = row.BlockReasonCode is null
            ? null
            : ScanBlockReason.Create(
                row.BlockReasonCode,
                Enum.Parse<ScanBlockCategory>(row.BlockReasonCategory!, false),
                row.BlockReasonAction!);
        return BatchDraft.Restore(
            batchId,
            row.Status,
            row.InputType,
            row.InputRoot,
            row.OutputRoot,
            snapshot,
            row.SelectionMode,
            row.IncludeEditablePsd,
            styles,
            assets,
            row.ManifestReference,
            row.ManifestDigest,
            blockReason,
            row.CreatedAt,
            row.UpdatedAt);
    }

    public async Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(
        CancellationToken cancellationToken)
    {
        var batchIds = new List<string>();
        await using (var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT batch_id
                FROM input_batches
                ORDER BY updated_at DESC, batch_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                batchIds.Add(reader.GetString(0));
            }
        }

        var drafts = new List<BatchDraft>(batchIds.Count);
        foreach (var batchId in batchIds)
        {
            var draft = await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (draft is not null)
            {
                drafts.Add(draft);
            }
        }
        return drafts;
    }

    public Task DeleteDraftAsync(string batchId, CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token).ConfigureAwait(false);
                string? status;
                await using (var statusCommand = connection.CreateCommand())
                {
                    statusCommand.Transaction = transaction;
                    statusCommand.CommandText = "SELECT scan_status FROM input_batches WHERE batch_id = $batch_id;";
                    statusCommand.Parameters.AddWithValue("$batch_id", batchId);
                    status = (string?)await statusCommand.ExecuteScalarAsync(token).ConfigureAwait(false);
                }

                if (string.Equals(status, BatchScanStatus.ScanReady.ToString(), StringComparison.Ordinal))
                {
                    throw new InputRuleViolationException("INPUT_READY_BATCH_DELETE_FORBIDDEN");
                }

                await using (var batchCommand = connection.CreateCommand())
                {
                    batchCommand.Transaction = transaction;
                    batchCommand.CommandText = "DELETE FROM input_batches WHERE batch_id = $batch_id;";
                    batchCommand.Parameters.AddWithValue("$batch_id", batchId);
                    await batchCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await using (var snapshotCommand = connection.CreateCommand())
                {
                    snapshotCommand.Transaction = transaction;
                    snapshotCommand.CommandText = "DELETE FROM batch_template_snapshots WHERE batch_id = $batch_id;";
                    snapshotCommand.Parameters.AddWithValue("$batch_id", batchId);
                    await snapshotCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private static async Task SaveBatchGraphAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchDraft draft,
        CancellationToken cancellationToken)
    {
        await EnsureInputSnapshotAsync(
            connection,
            transaction,
            draft.TemplateSnapshot,
            cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO input_batches(
                    batch_id, scan_status, input_type, input_root, output_root,
                    template_snapshot_id, selection_mode, include_editable_psd,
                    manifest_reference, manifest_digest,
                    block_reason_code, block_reason_category, block_reason_action,
                    created_at, updated_at)
                VALUES(
                    $batch_id, $scan_status, $input_type, $input_root, $output_root,
                    $template_snapshot_id, $selection_mode, $include_editable_psd,
                    $manifest_reference, $manifest_digest,
                    $block_reason_code, $block_reason_category, $block_reason_action,
                    $created_at, $updated_at)
                ON CONFLICT(batch_id) DO UPDATE SET
                    scan_status = excluded.scan_status,
                    input_type = excluded.input_type,
                    input_root = excluded.input_root,
                    output_root = excluded.output_root,
                    template_snapshot_id = excluded.template_snapshot_id,
                    selection_mode = excluded.selection_mode,
                    include_editable_psd = excluded.include_editable_psd,
                    manifest_reference = excluded.manifest_reference,
                    manifest_digest = excluded.manifest_digest,
                    block_reason_code = excluded.block_reason_code,
                    block_reason_category = excluded.block_reason_category,
                    block_reason_action = excluded.block_reason_action,
                    updated_at = excluded.updated_at;
                """;
            AddBatchParameters(command, draft);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteBatchDeleteAsync(
            connection,
            transaction,
            "DELETE FROM input_assets WHERE batch_id = $batch_id;",
            draft.BatchId,
            cancellationToken).ConfigureAwait(false);
        await ExecuteBatchDeleteAsync(
            connection,
            transaction,
            "DELETE FROM input_styles WHERE batch_id = $batch_id;",
            draft.BatchId,
            cancellationToken).ConfigureAwait(false);

        foreach (var style in draft.Styles.OrderBy(style => style.Order))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO input_styles(
                    style_id, batch_id, default_style_code, source_relative_directory, order_index)
                VALUES($style_id, $batch_id, $default_style_code, $source_relative_directory, $order_index);
                """;
            command.Parameters.AddWithValue("$style_id", style.StyleId);
            command.Parameters.AddWithValue("$batch_id", draft.BatchId);
            command.Parameters.AddWithValue("$default_style_code", style.DefaultStyleCode);
            command.Parameters.AddWithValue("$source_relative_directory", style.SourceDirectoryRelativePath);
            command.Parameters.AddWithValue("$order_index", style.Order);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var asset in draft.Assets)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO input_assets(
                    asset_id, batch_id, style_id, source_relative_path, file_name,
                    length, sha256, pixel_width, pixel_height, image_format, mime_type, readability)
                VALUES(
                    $asset_id, $batch_id, $style_id, $source_relative_path, $file_name,
                    $length, $sha256, $pixel_width, $pixel_height, $image_format, $mime_type, $readability);
                """;
            command.Parameters.AddWithValue("$asset_id", asset.AssetId);
            command.Parameters.AddWithValue("$batch_id", draft.BatchId);
            command.Parameters.AddWithValue("$style_id", asset.StyleId);
            command.Parameters.AddWithValue("$source_relative_path", asset.SourceRelativePath);
            command.Parameters.AddWithValue("$file_name", asset.FileName);
            command.Parameters.AddWithValue("$length", asset.Length);
            command.Parameters.AddWithValue("$sha256", asset.Sha256);
            command.Parameters.AddWithValue("$pixel_width", asset.PixelWidth);
            command.Parameters.AddWithValue("$pixel_height", asset.PixelHeight);
            command.Parameters.AddWithValue("$image_format", asset.Format.ToString());
            command.Parameters.AddWithValue("$mime_type", asset.MimeType);
            command.Parameters.AddWithValue("$readability", asset.Readability.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureInputSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchTemplateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var pagesJson = SerializeSnapshotPages(snapshot.Pages);
        if (await InputSnapshotMatchesAsync(
                connection,
                transaction,
                snapshot,
                pagesJson,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var existingSnapshot = await FindInputSnapshotAsync(
            connection,
            transaction,
            snapshot,
            cancellationToken).ConfigureAwait(false);
        if (existingSnapshot is null)
        {
            await InsertInputSnapshotAsync(
                connection,
                transaction,
                snapshot,
                pagesJson,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(existingSnapshot.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(existingSnapshot.BatchId, snapshot.BatchId, StringComparison.Ordinal)
            || !await IsBatchSnapshotMutableAsync(
                connection,
                transaction,
                snapshot.BatchId,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InputRuleViolationException("INPUT_TEMPLATE_SNAPSHOT_CONFLICT");
        }

        await UpdateInputSnapshotAsync(
            connection,
            transaction,
            snapshot,
            pagesJson,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> InputSnapshotMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchTemplateSnapshot snapshot,
        string pagesJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_id, batch_id, revision_id, pages_json, rules_digest, created_at
            FROM batch_template_snapshots
            WHERE snapshot_id = $snapshot_id OR batch_id = $batch_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$batch_id", snapshot.BatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && string.Equals(reader.GetString(0), snapshot.SnapshotId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(1), snapshot.BatchId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(2), snapshot.RevisionId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), pagesJson, StringComparison.Ordinal)
            && string.Equals(reader.GetString(4), snapshot.RulesDigest, StringComparison.Ordinal)
            && ParseTimestamp(reader.GetString(5)).Equals(snapshot.CreatedAt);
    }

    private static async Task<InputSnapshotIdentity?> FindInputSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchTemplateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_id, batch_id
            FROM batch_template_snapshots
            WHERE snapshot_id = $snapshot_id OR batch_id = $batch_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$batch_id", snapshot.BatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new InputSnapshotIdentity(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task InsertInputSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchTemplateSnapshot snapshot,
        string pagesJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO batch_template_snapshots(
                snapshot_id, batch_id, revision_id, pages_json, rules_digest, created_at)
            VALUES($snapshot_id, $batch_id, $revision_id, $pages_json, $rules_digest, $created_at);
            """;
        AddInputSnapshotParameters(command, snapshot, pagesJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsBatchSnapshotMutableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT scan_status FROM input_batches WHERE batch_id = $batch_id;";
        command.Parameters.AddWithValue("$batch_id", batchId);
        var status = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(status, BatchScanStatus.Draft.ToString(), StringComparison.Ordinal)
            || string.Equals(status, BatchScanStatus.ScanBlocked.ToString(), StringComparison.Ordinal);
    }

    private static async Task UpdateInputSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchTemplateSnapshot snapshot,
        string pagesJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE batch_template_snapshots
            SET revision_id = $revision_id,
                pages_json = $pages_json,
                rules_digest = $rules_digest,
                created_at = $created_at
            WHERE snapshot_id = $snapshot_id AND batch_id = $batch_id;
            """;
        AddInputSnapshotParameters(command, snapshot, pagesJson);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InputRuleViolationException("INPUT_TEMPLATE_SNAPSHOT_CONFLICT");
        }
    }

    private static void AddInputSnapshotParameters(
        SqliteCommand command,
        BatchTemplateSnapshot snapshot,
        string pagesJson)
    {
        command.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$batch_id", snapshot.BatchId);
        command.Parameters.AddWithValue("$revision_id", snapshot.RevisionId);
        command.Parameters.AddWithValue("$pages_json", pagesJson);
        command.Parameters.AddWithValue("$rules_digest", snapshot.RulesDigest);
        command.Parameters.AddWithValue("$created_at", snapshot.CreatedAt.ToString("O"));
    }

    private static async Task<BatchTemplateSnapshot> LoadSnapshotByIdAsync(
        SqliteConnection connection,
        string snapshotId,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision_id, pages_json, rules_digest, created_at
            FROM batch_template_snapshots
            WHERE snapshot_id = $snapshot_id AND batch_id = $batch_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        command.Parameters.AddWithValue("$batch_id", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("INPUT_TEMPLATE_SNAPSHOT_MISSING");
        }

        return BatchTemplateSnapshot.Restore(
            snapshotId,
            batchId,
            reader.GetString(0),
            DeserializeSnapshotPages(reader.GetString(1)),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)));
    }

    private static async Task<IReadOnlyList<SourceAssetMetadata>> LoadInputAssetsAsync(
        SqliteConnection connection,
        string batchId,
        CancellationToken cancellationToken)
    {
        var assets = new List<SourceAssetMetadata>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT asset_id, style_id, source_relative_path, file_name, length, sha256,
                   pixel_width, pixel_height, image_format, mime_type
            FROM input_assets
            WHERE batch_id = $batch_id
            ORDER BY source_relative_path;
            """;
        command.Parameters.AddWithValue("$batch_id", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assets.Add(SourceAssetMetadata.CreateReadable(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                Enum.Parse<SourceImageFormat>(reader.GetString(8), false),
                reader.GetString(9)));
        }
        return assets;
    }

    private static async Task<IReadOnlyList<StyleDraft>> LoadInputStylesAsync(
        SqliteConnection connection,
        string batchId,
        IReadOnlyList<SourceAssetMetadata> assets,
        CancellationToken cancellationToken)
    {
        var assetsByStyle = assets.ToLookup(asset => asset.StyleId, asset => asset.AssetId, StringComparer.Ordinal);
        var styles = new List<StyleDraft>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT style_id, default_style_code, source_relative_directory, order_index
            FROM input_styles
            WHERE batch_id = $batch_id
            ORDER BY order_index;
            """;
        command.Parameters.AddWithValue("$batch_id", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var styleId = reader.GetString(0);
            styles.Add(StyleDraft.Create(
                styleId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                assetsByStyle[styleId]));
        }
        return styles;
    }

    private static async Task ExecuteBatchDeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batch_id", batchId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddBatchParameters(SqliteCommand command, BatchDraft draft)
    {
        command.Parameters.AddWithValue("$batch_id", draft.BatchId);
        command.Parameters.AddWithValue("$scan_status", draft.ScanStatus.ToString());
        command.Parameters.AddWithValue("$input_type", (object?)draft.InputType?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$input_root", draft.InputRoot);
        command.Parameters.AddWithValue("$output_root", draft.OutputRoot);
        command.Parameters.AddWithValue("$template_snapshot_id", draft.TemplateSnapshot.SnapshotId);
        command.Parameters.AddWithValue("$selection_mode", draft.SelectionMode.ToString());
        command.Parameters.AddWithValue("$include_editable_psd", draft.IncludeEditablePsd ? 1 : 0);
        command.Parameters.AddWithValue("$manifest_reference", (object?)draft.ManifestReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$manifest_digest", (object?)draft.ManifestDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$block_reason_code", (object?)draft.BlockReason?.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("$block_reason_category", (object?)draft.BlockReason?.Category.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$block_reason_action", (object?)draft.BlockReason?.UserAction ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", draft.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", draft.UpdatedAt.ToString("O"));
    }

    private sealed record BatchRow(
        BatchScanStatus Status,
        BatchInputType? InputType,
        string InputRoot,
        string OutputRoot,
        string TemplateSnapshotId,
        BatchSelectionMode SelectionMode,
        bool IncludeEditablePsd,
        string? ManifestReference,
        string? ManifestDigest,
        string? BlockReasonCode,
        string? BlockReasonCategory,
        string? BlockReasonAction,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record InputSnapshotIdentity(string SnapshotId, string BatchId);
}
