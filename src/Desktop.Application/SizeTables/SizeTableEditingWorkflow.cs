using Desktop.Domain.SizeTables;

namespace Desktop.Application.SizeTables;

public static class SizeTableEditingLimits
{
    public const int MaximumColumns = SizeTableProtocol.MaximumColumns;
    public const int MaximumRows = SizeTableProtocol.MaximumRows;
    public const int MaximumCellLength = SizeTableProtocol.MaximumCellLength;
}

public enum SizeTableEditorStatus
{
    Valid = 0,
    ReviewRequired,
    Invalid
}

public sealed record SizeTableEditorColumnDocument(string ColumnId, string SourceText);

public sealed record SizeTableEditorRowDocument(
    string RowId,
    IReadOnlyList<string> Cells);

public sealed record SizeTableEditorDocument(
    string TableId,
    string BatchId,
    string StyleId,
    string PageId,
    string SourceAssetId,
    string SourceContentDigest,
    long Revision,
    SizeTableEditorStatus Status,
    IReadOnlyList<SizeTableEditorColumnDocument> Columns,
    IReadOnlyList<SizeTableEditorRowDocument> Rows);

public sealed record SizeTableEditorDraft(
    IReadOnlyList<SizeTableEditorColumnDocument> Columns,
    IReadOnlyList<SizeTableEditorRowDocument> Rows);

public enum SizeTableEditorLoadStatus
{
    NotFound = 0,
    Loaded,
    RecoveryRequired
}

public sealed record SizeTableEditorLoadResult(
    SizeTableEditorLoadStatus Status,
    SizeTableEditorDocument? Document,
    string? ReasonCode);

public enum SizeTableEditorSaveStatus
{
    Saved = 0,
    Invalid,
    RevisionConflict,
    RecoveryRequired
}

public sealed record SizeTableEditorSaveCommand(
    string BatchId,
    string StyleId,
    string PageId,
    long ExpectedRevision,
    SizeTableEditorDraft Draft);

public sealed record SizeTableEditorSaveResult(
    SizeTableEditorSaveStatus Status,
    SizeTableEditorDocument? Document,
    string? ReasonCode);

public interface ISizeTableEditingWorkflow
{
    Task<SizeTableEditorLoadResult> LoadAsync(
        string batchId,
        string styleId,
        string pageId,
        CancellationToken cancellationToken);

    Task<SizeTableEditorSaveResult> SaveAsync(
        SizeTableEditorSaveCommand command,
        CancellationToken cancellationToken);
}

public sealed class SizeTableEditingWorkflow(
    ISizeTableRepository repository,
    TimeProvider timeProvider) : ISizeTableEditingWorkflow
{
    public async Task<SizeTableEditorLoadResult> LoadAsync(
        string batchId,
        string styleId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var loaded = await repository.LoadAsync(
            batchId,
            styleId,
            pageId,
            cancellationToken).ConfigureAwait(false);
        return loaded.Status switch
        {
            SizeTableLoadStatus.Loaded => new SizeTableEditorLoadResult(
                SizeTableEditorLoadStatus.Loaded,
                Project(loaded.Table!),
                null),
            SizeTableLoadStatus.RecoveryRequired => new SizeTableEditorLoadResult(
                SizeTableEditorLoadStatus.RecoveryRequired,
                null,
                loaded.ReasonCode),
            _ => new SizeTableEditorLoadResult(
                SizeTableEditorLoadStatus.NotFound,
                null,
                null)
        };
    }

    public async Task<SizeTableEditorSaveResult> SaveAsync(
        SizeTableEditorSaveCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Draft);
        var loaded = await repository.LoadAsync(
            command.BatchId,
            command.StyleId,
            command.PageId,
            cancellationToken).ConfigureAwait(false);
        if (loaded.Status == SizeTableLoadStatus.RecoveryRequired)
        {
            return new SizeTableEditorSaveResult(
                SizeTableEditorSaveStatus.RecoveryRequired,
                null,
                loaded.ReasonCode);
        }
        if (loaded.Table is null || loaded.Table.Revision != command.ExpectedRevision)
        {
            return new SizeTableEditorSaveResult(
                SizeTableEditorSaveStatus.RevisionConflict,
                null,
                "SIZE_TABLE_REVISION_CONFLICT");
        }

        try
        {
            var columns = command.Draft.Columns.Select(column => SizeTableColumn.Create(
                column.ColumnId,
                column.SourceText)).ToArray();
            var rows = command.Draft.Rows.Select(row => SizeTableRow.Create(
                row.RowId,
                row.Cells.Select((cell, index) => SizeTableCell.Create(
                    columns[index].ColumnId,
                    cell)))).ToArray();
            var edited = loaded.Table.Edit(
                command.ExpectedRevision,
                columns,
                rows,
                timeProvider.GetUtcNow());
            if (!edited.CanSave)
            {
                return new SizeTableEditorSaveResult(
                    SizeTableEditorSaveStatus.Invalid,
                    Project(edited),
                    edited.Status == SizeTableStatus.ReviewRequired
                        ? "SIZE_TABLE_REVIEW_REQUIRED"
                        : "SIZE_TABLE_INVALID");
            }

            var saved = await repository.SaveAsync(
                edited,
                command.ExpectedRevision,
                cancellationToken).ConfigureAwait(false);
            return saved.Status switch
            {
                SizeTableSaveStatus.Saved => new SizeTableEditorSaveResult(
                    SizeTableEditorSaveStatus.Saved,
                    Project(edited),
                    null),
                SizeTableSaveStatus.RevisionConflict => new SizeTableEditorSaveResult(
                    SizeTableEditorSaveStatus.RevisionConflict,
                    null,
                    saved.ReasonCode),
                _ => new SizeTableEditorSaveResult(
                    SizeTableEditorSaveStatus.RecoveryRequired,
                    null,
                    saved.ReasonCode)
            };
        }
        catch (Exception exception) when (exception is SizeTableRuleViolationException
            or ArgumentOutOfRangeException
            or IndexOutOfRangeException)
        {
            return new SizeTableEditorSaveResult(
                SizeTableEditorSaveStatus.Invalid,
                null,
                "SIZE_TABLE_INVALID");
        }
    }

    private static SizeTableEditorDocument Project(SizeTable table) => new(
        table.TableId,
        table.BatchId,
        table.StyleId,
        table.PageId,
        table.Source.AssetId,
        table.Source.ContentDigest,
        table.Revision,
        table.Status switch
        {
            SizeTableStatus.Valid => SizeTableEditorStatus.Valid,
            SizeTableStatus.ReviewRequired => SizeTableEditorStatus.ReviewRequired,
            _ => SizeTableEditorStatus.Invalid
        },
        table.Columns.Select(column => new SizeTableEditorColumnDocument(
            column.ColumnId,
            column.SourceText)).ToArray(),
        table.Rows.Select(row => new SizeTableEditorRowDocument(
            row.RowId,
            row.Cells.Select(cell => cell.SourceText).ToArray())).ToArray());
}
