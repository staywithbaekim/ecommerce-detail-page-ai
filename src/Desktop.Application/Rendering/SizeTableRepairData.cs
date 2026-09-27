using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.SizeTables;
using Desktop.Contracts.Validation;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.Rendering;

/// <summary>Immutable journal evidence, deliberately separate from the mutable size-table/v1 file.</summary>
public static class SizeTableRepairData
{
    public const string SnapshotVersion = "size-table-repair-snapshot/v1";
    public const int MaximumSnapshotBytes = 4 * 1024 * 1024;
    internal const int MaximumPlanBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly RenderPlanValidator Plans = new(new JsonSchemaContractValidator());

    public static string Serialize(SizeTable table)
    {
        ValidateTable(table);
        var json = JsonSerializer.Serialize(new
        {
            schema_version = SnapshotVersion,
            table.TableId, table.BatchId, table.StyleId, table.PageId, table.CandidateId,
            source = new { table.Source.AssetId, table.Source.ContentDigest },
            status = "Valid", table.Revision,
            columns = table.Columns.Select(column => new { column.ColumnId, column.SourceText }),
            rows = table.Rows.Select(row => new { row.RowId,
                cells = row.Cells.Select(cell => new { cell.ColumnId, cell.SourceText }) }),
            issues = Array.Empty<object>(), table.DataDigest, table.CreatedAt, table.UpdatedAt
        }, Options);
        CheckSize(json, MaximumSnapshotBytes);
        return json;
    }

    public static SizeTable Deserialize(string json)
    {
        try
        {
            using var document = ReadStrictJson(json, MaximumSnapshotBytes);
            var root = document.RootElement;
            Properties(root, "schema_version", "table_id", "batch_id", "style_id", "page_id", "candidate_id",
                "source", "status", "revision", "columns", "rows", "issues", "data_digest", "created_at", "updated_at");
            if (Text(root, "schema_version") != SnapshotVersion || Text(root, "status") != "Valid"
                || root.GetProperty("issues").ValueKind != JsonValueKind.Array || root.GetProperty("issues").GetArrayLength() != 0)
                throw Invalid();
            var source = root.GetProperty("source");
            Properties(source, "asset_id", "content_digest");
            var sourceDigest = Text(source, "content_digest");
            var dataDigest = Text(root, "data_digest");
            if (!IsDigest(sourceDigest) || !IsDigest(dataDigest)) throw Invalid();
            var columnsJson = BoundedArray(root.GetProperty("columns"), SizeTableProtocol.MaximumColumns);
            var rowsJson = BoundedArray(root.GetProperty("rows"), SizeTableProtocol.MaximumRows);
            var columns = columnsJson.Select(column =>
            {
                Properties(column, "column_id", "source_text");
                return SizeTableColumn.Create(Text(column, "column_id"), Text(column, "source_text"));
            }).ToArray();
            var rows = rowsJson.Select(row =>
            {
                Properties(row, "row_id", "cells");
                var cells = BoundedArray(row.GetProperty("cells"), SizeTableProtocol.MaximumColumns);
                if (cells.Length != columns.Length) throw Invalid();
                return SizeTableRow.Create(Text(row, "row_id"), cells.Select(cell =>
                {
                    Properties(cell, "column_id", "source_text");
                    return SizeTableCell.Create(Text(cell, "column_id"), Text(cell, "source_text"));
                }));
            }).ToArray();
            var table = SizeTable.Restore(Text(root, "table_id"), Text(root, "batch_id"), Text(root, "style_id"),
                Text(root, "page_id"), Text(root, "candidate_id"),
                SizeTableSource.Create(Text(source, "asset_id"), sourceDigest), SizeTableStatus.Valid,
                root.GetProperty("revision").GetInt64(), columns, rows, [], dataDigest,
                Timestamp(root.GetProperty("created_at")), Timestamp(root.GetProperty("updated_at")));
            ValidateTable(table);
            return table;
        }
        catch (Exception error) when (IsMalformed(error)) { throw Invalid(); }
    }

    public static SizeTableEditorDocument ReadEditorDocument(string snapshotJson) => ToEditorDocument(Deserialize(snapshotJson));

    public static SizeTableEditorDocument ToEditorDocument(SizeTable table)
    {
        ValidateTable(table);
        return new(table.TableId, table.BatchId, table.StyleId, table.PageId, table.Source.AssetId,
            table.Source.ContentDigest, table.Revision, SizeTableEditorStatus.Valid,
            table.Columns.Select(column => new SizeTableEditorColumnDocument(column.ColumnId, column.SourceText)).ToImmutableArray(),
            table.Rows.Select(row => new SizeTableEditorRowDocument(row.RowId,
                row.Cells.Select(cell => cell.SourceText).ToImmutableArray())).ToImmutableArray());
    }

    public static SizeTable ApplyDraft(SizeTable table, SizeTableEditorDraft draft, DateTimeOffset updatedAt)
    {
        ValidateTable(table);
        var (columns, rows) = FreezeDraft(draft);
        var edited = table.Edit(table.Revision, columns, rows, updatedAt);
        ValidateTable(edited);
        if (!ContentChanged(table, edited)) throw new InvalidOperationException("SIZE_REPAIR_NO_CHANGES");
        return edited;
    }

    /// <summary>Includes every request/target/draft field, in stable order, without clock or normalized text.</summary>
    public static string RequestDigest(SizeTablePageRepairCommand command)
    {
        try
        {
            if (command is null || command.Target is not { } target || !IsRequestId(command.RequestId)
                || !IsDomainId(target.BatchId) || !IsDomainId(target.StyleId) || !IsDomainId(target.PageId)
                || target.OutputVariantId is { } variant && !IsRequestId(variant)
                || target.ExpectedJournalRevision is < 1 or long.MaxValue
                || target.ExpectedAttempt is < 1 or int.MaxValue
                || target.ExpectedTableRevision is < 0 or long.MaxValue
                || !IsDigest(target.BasePlanDigest) || !IsDigest(target.ExpectedTableDataDigest)) throw Invalid();
            var (columns, rows) = FreezeDraft(command.Draft);
            var json = JsonSerializer.Serialize(new
            {
                command.RequestId, target,
                draft = new
                {
                    columns = columns.Select(column => new { column.ColumnId, column.SourceText }),
                    rows = rows.Select(row => new { row.RowId, cells = row.Cells.Select(cell => cell.SourceText) })
                }
            }, Options);
            CheckSize(json, MaximumSnapshotBytes);
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(json)));
        }
        catch (Exception error) when (IsMalformed(error) || error is InvalidDataException)
        { throw new InvalidDataException("SIZE_REPAIR_REQUEST_INVALID"); }
    }

    public static bool MatchesPlan(SizeTable table, string planJson)
    {
        try
        {
            ValidateTable(table);
            using var document = ReadPlan(planJson);
            return MatchesPlan(table, document.RootElement);
        }
        catch (Exception error) when (IsMalformed(error) || error is InvalidDataException) { return false; }
    }

    internal static bool MatchesPlan(SizeTable table, JsonElement plan)
    {
        var document = plan.GetProperty("document");
        var size = plan.GetProperty("size_table");
        if (Text(plan, "batch_id") != table.BatchId || Text(plan, "product_id") != table.StyleId
            || Text(document, "page_id") != table.PageId || Text(document, "content_type") != "dynamic_size_table"
            || size.ValueKind != JsonValueKind.Object || Text(size, "size_table_id") != table.TableId
            || Text(size, "structured_data_digest") != table.DataDigest) return false;
        var columns = size.GetProperty("columns");
        var rows = size.GetProperty("rows");
        if (columns.GetArrayLength() != table.Columns.Length || rows.GetArrayLength() != table.Rows.Length) return false;
        for (var i = 0; i < table.Columns.Length; i++)
            if (columns[i].GetProperty("index").GetInt32() != i
                || Text(columns[i], "column_id") != table.Columns[i].ColumnId
                || Text(columns[i], "label") != table.Columns[i].SourceText) return false;
        for (var i = 0; i < table.Rows.Length; i++)
        {
            var row = rows[i];
            var cells = row.GetProperty("cells");
            if (row.GetProperty("index").GetInt32() != i || Text(row, "row_id") != table.Rows[i].RowId
                || cells.EnumerateObject().Count() != table.Columns.Length) return false;
            foreach (var cell in table.Rows[i].Cells)
                if (!cells.TryGetProperty(cell.ColumnId, out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() != cell.SourceText) return false;
        }
        return true;
    }

    internal static JsonDocument ReadPlan(string json)
    {
        var document = ReadStrictJson(json, MaximumPlanBytes);
        try
        {
            if (!Plans.Validate(json).IsValid) throw Invalid();
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    internal static JsonDocument ReadStrictJson(string json, int maximumBytes)
    {
        CheckSize(json, maximumBytes);
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        try { NoDuplicateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void NoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                NoDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) NoDuplicateProperties(item);
    }

    private static void CheckSize(string json, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > maximumBytes || Utf8.GetByteCount(json) > maximumBytes)
            throw Invalid();
    }

    private static void Properties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length
            || names.Any(name => !value.TryGetProperty(name, out _))) throw Invalid();
    }

    private static JsonElement[] BoundedArray(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 1 || value.GetArrayLength() > maximum) throw Invalid();
        return value.EnumerateArray().ToArray();
    }

    internal static DateTimeOffset Timestamp(JsonElement value)
    {
        var text = value.GetString();
        if (text is null || !(text.EndsWith('Z') || text.Length >= 6 && (text[^6] is '+' or '-') && text[^3] == ':')
            || !value.TryGetDateTimeOffset(out var result) || result == default) throw Invalid();
        return result;
    }

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString() ?? throw Invalid();

    internal static bool IsRequestId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');

    private static bool IsDomainId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.Skip(7).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static (SizeTableColumn[] Columns, SizeTableRow[] Rows) FreezeDraft(SizeTableEditorDraft draft)
    {
        if (draft is null || draft.Columns is not { Count: > 0 and <= SizeTableProtocol.MaximumColumns } columns
            || draft.Rows is not { Count: > 0 and <= SizeTableProtocol.MaximumRows } rows) throw Invalid();
        var frozenColumns = columns.Select(column => column is null ? throw Invalid()
            : SizeTableColumn.Create(column.ColumnId, column.SourceText)).ToArray();
        var frozenRows = rows.Select(row =>
        {
            if (row is null || row.Cells is null || row.Cells.Count != frozenColumns.Length) throw Invalid();
            return SizeTableRow.Create(row.RowId, row.Cells.Select((text, index) => SizeTableCell.Create(frozenColumns[index].ColumnId, text)));
        }).ToArray();
        if (frozenColumns.Select(column => column.ColumnId).Distinct(StringComparer.Ordinal).Count() != frozenColumns.Length
            || frozenRows.Select(row => row.RowId).Distinct(StringComparer.Ordinal).Count() != frozenRows.Length) throw Invalid();
        ValidateUnicode(frozenColumns.Select(column => column.SourceText).Concat(frozenRows.SelectMany(row => row.Cells.Select(cell => cell.SourceText))));
        return (frozenColumns, frozenRows);
    }

    internal static bool ContentChanged(SizeTable before, SizeTable after) =>
        !before.Columns.Select(column => column.SourceText).SequenceEqual(after.Columns.Select(column => column.SourceText), StringComparer.Ordinal)
        || before.Rows.Length != after.Rows.Length
        || before.Rows.Where((row, index) => !row.Cells.Select(cell => cell.SourceText)
            .SequenceEqual(after.Rows[index].Cells.Select(cell => cell.SourceText), StringComparer.Ordinal)).Any();

    private static void ValidateTable(SizeTable table)
    {
        if (table is null || table.Status != SizeTableStatus.Valid || !table.CanSave || table.Revision == long.MaxValue
            || !table.Edit(table.Revision, table.Columns, table.Rows, table.UpdatedAt).CanSave) throw Invalid();
        ValidateUnicode(table.Columns.Select(column => column.SourceText).Concat(table.Rows.SelectMany(row => row.Cells.Select(cell => cell.SourceText))));
    }

    private static void ValidateUnicode(IEnumerable<string> texts)
    {
        foreach (var text in texts) _ = Utf8.GetByteCount(text);
    }

    internal static bool IsMalformed(Exception error) => error is JsonException or InvalidOperationException
        or ArgumentException or KeyNotFoundException or FormatException or OverflowException;
    private static InvalidDataException Invalid() => new("SIZE_REPAIR_SNAPSHOT_INVALID");
}
