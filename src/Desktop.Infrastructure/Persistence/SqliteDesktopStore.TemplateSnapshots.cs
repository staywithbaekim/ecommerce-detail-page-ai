using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore
{
    public Task SaveAsync(BatchTemplateSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var pagesJson = SerializeSnapshotPages(snapshot.Pages);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT OR IGNORE INTO batch_template_snapshots(
                        snapshot_id, batch_id, revision_id, pages_json, rules_digest, created_at)
                    VALUES(
                        $snapshot_id, $batch_id, $revision_id, $pages_json, $rules_digest, $created_at);
                    """;
                command.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
                command.Parameters.AddWithValue("$batch_id", snapshot.BatchId);
                command.Parameters.AddWithValue("$revision_id", snapshot.RevisionId);
                command.Parameters.AddWithValue("$pages_json", pagesJson);
                command.Parameters.AddWithValue("$rules_digest", snapshot.RulesDigest);
                command.Parameters.AddWithValue("$created_at", snapshot.CreatedAt.ToString("O"));
                var rows = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (rows == 0 && !await SnapshotMatchesAsync(
                        connection,
                        snapshot,
                        pagesJson,
                        token).ConfigureAwait(false))
                {
                    throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_FROZEN");
                }

                return true;
            },
            cancellationToken);
    }

    public async Task<BatchTemplateSnapshot?> GetForBatchAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(batchId, "TPL_BATCH_ID_INVALID");
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, revision_id, pages_json, rules_digest, created_at, context_version
            FROM batch_template_snapshots
            WHERE batch_id = $batch_id;
            """;
        command.Parameters.AddWithValue("$batch_id", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.GetString(5) != "batch-template-snapshot/v1")
            throw new TemplateRuleViolationException("TPL_RULE_INSTANCE_PIPELINE_REQUIRED");
        return BatchTemplateSnapshot.Restore(
            reader.GetString(0),
            batchId,
            reader.GetString(1),
            DeserializeSnapshotPages(reader.GetString(2)),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)));
    }

    public Task ReplaceDraftAsync(
        BatchTemplateSnapshot snapshot,
        string expectedSnapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        TemplateValueRules.Identifier(expectedSnapshotId, "TPL_SNAPSHOT_ID_INVALID");
        var pagesJson = SerializeSnapshotPages(snapshot.Pages);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE batch_template_snapshots
                    SET snapshot_id = $snapshot_id,
                        revision_id = $revision_id,
                        pages_json = $pages_json,
                        rules_digest = $rules_digest,
                        created_at = $created_at
                    WHERE batch_id = $batch_id AND snapshot_id = $expected_snapshot_id
                      AND context_version = 'batch-template-snapshot/v1';
                    """;
                command.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
                command.Parameters.AddWithValue("$revision_id", snapshot.RevisionId);
                command.Parameters.AddWithValue("$pages_json", pagesJson);
                command.Parameters.AddWithValue("$rules_digest", snapshot.RulesDigest);
                command.Parameters.AddWithValue("$created_at", snapshot.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue("$batch_id", snapshot.BatchId);
                command.Parameters.AddWithValue("$expected_snapshot_id", expectedSnapshotId);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_FROZEN");
                }
                return true;
            },
            cancellationToken);
    }

    private static string SerializeSnapshotPages(ImmutableArray<BatchTemplatePageSnapshot> pages) =>
        JsonSerializer.Serialize(
            pages.Select(page => new SnapshotPageDocument(
                page.PageId,
                page.Order,
                page.ContentType.ToString(),
                page.GenerationMode.ToString(),
                page.VariantCapacity,
                page.TemplateResourceId,
                page.FrameIds.ToArray())),
            TemplateJsonOptions);

    private static ImmutableArray<BatchTemplatePageSnapshot> DeserializeSnapshotPages(string json)
    {
        var documents = JsonSerializer.Deserialize<SnapshotPageDocument[]>(json, TemplateJsonOptions)
            ?? throw new InvalidDataException("TPL_BATCH_SNAPSHOT_INVALID");
        return documents.Select(page => new BatchTemplatePageSnapshot(
            page.PageId,
            page.Order,
            Enum.Parse<TemplatePageContentType>(page.ContentType, false),
            Enum.Parse<TemplateGenerationMode>(page.GenerationMode, false),
            page.VariantCapacity,
            page.TemplateResourceId,
            page.FrameIds.ToImmutableArray())).ToImmutableArray();
    }

    private static async Task<bool> SnapshotMatchesAsync(
        SqliteConnection connection,
        BatchTemplateSnapshot snapshot,
        string pagesJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, batch_id, revision_id, pages_json, rules_digest, created_at, context_version
            FROM batch_template_snapshots
            WHERE snapshot_id = $snapshot_id OR batch_id = $batch_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$batch_id", snapshot.BatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && reader.GetString(6) == "batch-template-snapshot/v1"
            && string.Equals(reader.GetString(0), snapshot.SnapshotId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(1), snapshot.BatchId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(2), snapshot.RevisionId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), pagesJson, StringComparison.Ordinal)
            && string.Equals(reader.GetString(4), snapshot.RulesDigest, StringComparison.Ordinal)
            && ParseTimestamp(reader.GetString(5)).Equals(snapshot.CreatedAt);
    }

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);

    private sealed record FrameBindingDocument(
        string FrameId,
        string PageId,
        int[] LayerPath,
        ExpectedLayerDocument ExpectedLayer,
        SupersededLayerDocument[] SupersededLayers,
        SelectionRequirementDocument SelectionRequirement,
        LayoutIntentDocument LayoutIntent);

    private sealed record ExpectedLayerDocument(
        int LayerId,
        string NameBefore,
        bool Visible,
        bool Clipping,
        bool UserMask,
        bool VectorMask,
        byte Opacity,
        string BlendMode,
        string StructureSignature);

    private sealed record SupersededLayerDocument(
        int[] LayerPath,
        bool ExpectedClipping,
        int? ExpectedLayerId);

    private sealed record SelectionRequirementDocument(
        string Description,
        SelectionCriterionDocument[] Criteria);

    private sealed record SelectionCriterionDocument(string Key, string Value);

    private sealed record LayoutIntentDocument(
        string Intent,
        double Scale,
        double OffsetX,
        double OffsetY,
        double Left,
        double Top,
        double Right,
        double Bottom);

    private sealed record SnapshotPageDocument(
        string PageId,
        int Order,
        string ContentType,
        string GenerationMode,
        int? VariantCapacity,
        string TemplateResourceId,
        string[] FrameIds);

}
