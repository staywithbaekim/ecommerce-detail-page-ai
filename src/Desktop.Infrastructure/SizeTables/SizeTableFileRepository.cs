using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.SizeTables;
using Desktop.Domain.SizeTables;

namespace Desktop.Infrastructure.SizeTables;

public sealed class SizeTableFileRepository(IFileStore files) : ISizeTableRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SizeTableLoadResult> LoadAsync(
        string batchId,
        string styleId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var path = StatePath(batchId, styleId, pageId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(path, batchId, styleId, pageId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SizeTableSaveResult> SaveAsync(
        SizeTable table,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        var path = StatePath(table.BatchId, table.StyleId, table.PageId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(
                path,
                table.BatchId,
                table.StyleId,
                table.PageId,
                cancellationToken).ConfigureAwait(false);
            if (current.Status == SizeTableLoadStatus.RecoveryRequired)
            {
                return new SizeTableSaveResult(
                    SizeTableSaveStatus.RecoveryRequired,
                    current.ReasonCode);
            }

            if (current.Status == SizeTableLoadStatus.NotFound)
            {
                if (expectedRevision is not null || table.Revision != 0)
                {
                    return new SizeTableSaveResult(
                        SizeTableSaveStatus.RevisionConflict,
                        "SIZE_TABLE_REVISION_CONFLICT");
                }
            }
            else
            {
                var persisted = current.Table!;
                if (expectedRevision != persisted.Revision)
                {
                    return new SizeTableSaveResult(
                        SizeTableSaveStatus.RevisionConflict,
                        "SIZE_TABLE_REVISION_CONFLICT");
                }

                if (table.Revision == persisted.Revision
                    && string.Equals(table.DataDigest, persisted.DataDigest, StringComparison.Ordinal))
                {
                    return new SizeTableSaveResult(SizeTableSaveStatus.Saved);
                }

                if (table.Revision != persisted.Revision + 1)
                {
                    return new SizeTableSaveResult(
                        SizeTableSaveStatus.RevisionConflict,
                        "SIZE_TABLE_REVISION_CONFLICT");
                }
            }

            var envelope = Envelope.From(table, string.Empty);
            envelope = envelope with { DocumentDigest = Digest(Serialize(envelope)) };
            await files.WriteAtomicallyAsync(path, Serialize(envelope), cancellationToken).ConfigureAwait(false);
            return new SizeTableSaveResult(SizeTableSaveStatus.Saved);
        }
        catch (SizeTableRuleViolationException)
        {
            return new SizeTableSaveResult(
                SizeTableSaveStatus.InvalidState,
                "SIZE_TABLE_STATE_INVALID");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<SizeTableLoadResult> LoadCoreAsync(
        string path,
        string batchId,
        string styleId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var content = await files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return SizeTableLoadResult.NotFound();
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(content, JsonOptions)
                ?? throw new InvalidDataException("SIZE_TABLE_STATE_CORRUPT");
            if (!string.Equals(envelope.SchemaVersion, SizeTableProtocol.Version, StringComparison.Ordinal)
                || !string.Equals(envelope.BatchId, batchId, StringComparison.Ordinal)
                || !string.Equals(envelope.StyleId, styleId, StringComparison.Ordinal)
                || !string.Equals(envelope.PageId, pageId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SIZE_TABLE_STATE_CORRUPT");
            }

            var expectedDocumentDigest = Digest(Serialize(envelope with { DocumentDigest = string.Empty }));
            if (!string.Equals(envelope.DocumentDigest, expectedDocumentDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SIZE_TABLE_STATE_DIGEST_MISMATCH");
            }

            var columns = envelope.Columns.Select(column => SizeTableColumn.Create(
                column.ColumnId,
                column.SourceText)).ToArray();
            var rows = envelope.Rows.Select(row => SizeTableRow.Create(
                row.RowId,
                row.Cells.Select(cell => SizeTableCell.Create(cell.ColumnId, cell.SourceText)))).ToArray();
            var issues = envelope.Issues.Select(issue => SizeTableIssue.Create(
                issue.Code,
                issue.Severity,
                issue.UserAction)).ToArray();
            var table = SizeTable.Restore(
                envelope.TableId,
                envelope.BatchId,
                envelope.StyleId,
                envelope.PageId,
                envelope.CandidateId,
                SizeTableSource.Create(envelope.Source.AssetId, envelope.Source.ContentDigest),
                envelope.Status,
                envelope.Revision,
                columns,
                rows,
                issues,
                envelope.DataDigest,
                envelope.CreatedAt,
                envelope.UpdatedAt);
            return SizeTableLoadResult.Loaded(table);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or SizeTableRuleViolationException
            or ArgumentException
            or OverflowException)
        {
            return SizeTableLoadResult.RecoveryRequired("SIZE_TABLE_STATE_CORRUPT");
        }
    }

    private static byte[] Serialize(Envelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string StatePath(string batchId, string styleId, string pageId)
    {
        ValidateId(batchId);
        ValidateId(styleId);
        ValidateId(pageId);
        return $"batches/{batchId}/size-tables/{styleId}/{pageId}/current.json";
    }

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_ID_INVALID");
        }
    }

    private sealed record Envelope(
        string SchemaVersion,
        string TableId,
        string BatchId,
        string StyleId,
        string PageId,
        string CandidateId,
        SourceDto Source,
        SizeTableStatus Status,
        long Revision,
        IReadOnlyList<ColumnDto> Columns,
        IReadOnlyList<RowDto> Rows,
        IReadOnlyList<IssueDto> Issues,
        string DataDigest,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string DocumentDigest)
    {
        public static Envelope From(SizeTable table, string documentDigest) => new(
            SizeTableProtocol.Version,
            table.TableId,
            table.BatchId,
            table.StyleId,
            table.PageId,
            table.CandidateId,
            new SourceDto(table.Source.AssetId, table.Source.ContentDigest),
            table.Status,
            table.Revision,
            table.Columns.Select(column => new ColumnDto(column.ColumnId, column.SourceText)).ToArray(),
            table.Rows.Select(row => new RowDto(
                row.RowId,
                row.Cells.Select(cell => new CellDto(cell.ColumnId, cell.SourceText)).ToArray())).ToArray(),
            table.Issues.Select(issue => new IssueDto(
                issue.Code,
                issue.Severity,
                issue.UserAction)).ToArray(),
            table.DataDigest,
            table.CreatedAt,
            table.UpdatedAt,
            documentDigest);
    }

    private sealed record SourceDto(string AssetId, string ContentDigest);
    private sealed record ColumnDto(string ColumnId, string SourceText);
    private sealed record CellDto(string ColumnId, string SourceText);
    private sealed record RowDto(string RowId, IReadOnlyList<CellDto> Cells);
    private sealed record IssueDto(string Code, SizeTableIssueSeverity Severity, string UserAction);
}
