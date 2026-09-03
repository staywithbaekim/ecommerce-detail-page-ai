using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Desktop.Domain.SizeTables;

public static class SizeTableProtocol
{
    public const string Version = "size-table/v1";
    public const decimal ReliableConfidence = 0.85m;
    public const int MaximumColumns = 32;
    public const int MaximumRows = 200;
    public const int MaximumCellLength = 256;
}

public enum SizeTableStatus
{
    Extracting = 0,
    Valid,
    ReviewRequired,
    Invalid
}

public enum SizeTableIssueSeverity
{
    Review = 0,
    Blocking
}

public sealed record SizeTableSource
{
    private SizeTableSource(string assetId, string contentDigest)
    {
        AssetId = assetId;
        ContentDigest = contentDigest;
    }

    public string AssetId { get; }

    public string ContentDigest { get; }

    public static SizeTableSource Create(string assetId, string sha256) => new(
        SizeTableValueRules.Identifier(assetId, "SIZE_TABLE_SOURCE_ASSET_ID_INVALID"),
        SizeTableValueRules.Digest(sha256, "SIZE_TABLE_SOURCE_DIGEST_INVALID"));
}

public sealed record SizeTableCandidate
{
    private SizeTableCandidate(
        string candidateId,
        SizeTableSource source,
        decimal confidence,
        ImmutableArray<string> columns,
        ImmutableArray<ImmutableArray<string>> rows)
    {
        CandidateId = candidateId;
        Source = source;
        Confidence = confidence;
        Columns = columns;
        Rows = rows;
    }

    public string CandidateId { get; }

    public SizeTableSource Source { get; }

    public decimal Confidence { get; }

    public ImmutableArray<string> Columns { get; }

    public ImmutableArray<ImmutableArray<string>> Rows { get; }

    public bool IsRectangular => Columns.Length > 0
        && Rows.Length > 0
        && Rows.All(row => row.Length == Columns.Length);

    public static SizeTableCandidate Create(
        string candidateId,
        SizeTableSource source,
        decimal confidence,
        IEnumerable<string> columns,
        IEnumerable<IEnumerable<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        if (confidence is < 0m or > 1m)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_CONFIDENCE_INVALID");
        }

        var frozenColumns = columns
            .Select(value => SizeTableValueRules.SourceText(value, "SIZE_TABLE_COLUMN_TEXT_INVALID"))
            .ToImmutableArray();
        var frozenRows = rows.Select(row => row
                .Select(value => SizeTableValueRules.SourceText(value, "SIZE_TABLE_CELL_TEXT_INVALID"))
                .ToImmutableArray())
            .ToImmutableArray();
        if (frozenColumns.Length > SizeTableProtocol.MaximumColumns
            || frozenRows.Length > SizeTableProtocol.MaximumRows
            || frozenRows.Any(row => row.Length > SizeTableProtocol.MaximumColumns))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_SHAPE_LIMIT_EXCEEDED");
        }

        return new SizeTableCandidate(
            SizeTableValueRules.Identifier(candidateId, "SIZE_TABLE_CANDIDATE_ID_INVALID"),
            source,
            confidence,
            frozenColumns,
            frozenRows);
    }
}

public sealed record SizeTableColumn
{
    private SizeTableColumn(string columnId, string sourceText)
    {
        ColumnId = columnId;
        SourceText = sourceText;
    }

    public string ColumnId { get; }

    public string SourceText { get; }

    public static SizeTableColumn Create(string columnId, string sourceText) => new(
        SizeTableValueRules.Identifier(columnId, "SIZE_TABLE_COLUMN_ID_INVALID"),
        SizeTableValueRules.SourceText(sourceText, "SIZE_TABLE_COLUMN_TEXT_INVALID"));
}

public sealed record SizeTableCell
{
    private SizeTableCell(string columnId, string sourceText)
    {
        ColumnId = columnId;
        SourceText = sourceText;
    }

    public string ColumnId { get; }

    public string SourceText { get; }

    public static SizeTableCell Create(string columnId, string sourceText) => new(
        SizeTableValueRules.Identifier(columnId, "SIZE_TABLE_CELL_COLUMN_ID_INVALID"),
        SizeTableValueRules.SourceText(sourceText, "SIZE_TABLE_CELL_TEXT_INVALID"));
}

public sealed record SizeTableRow
{
    private SizeTableRow(string rowId, ImmutableArray<SizeTableCell> cells)
    {
        RowId = rowId;
        Cells = cells;
    }

    public string RowId { get; }

    public ImmutableArray<SizeTableCell> Cells { get; }

    public static SizeTableRow Create(string rowId, IEnumerable<SizeTableCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var frozen = cells.ToImmutableArray();
        if (frozen.Any(cell => cell is null)
            || frozen.Select(cell => cell.ColumnId).Distinct(StringComparer.Ordinal).Count() != frozen.Length)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_ROW_CELLS_INVALID");
        }

        return new SizeTableRow(
            SizeTableValueRules.Identifier(rowId, "SIZE_TABLE_ROW_ID_INVALID"),
            frozen);
    }
}

public sealed record SizeTableIssue
{
    private SizeTableIssue(string code, SizeTableIssueSeverity severity, string userAction)
    {
        Code = code;
        Severity = severity;
        UserAction = userAction;
    }

    public string Code { get; }

    public SizeTableIssueSeverity Severity { get; }

    public string UserAction { get; }

    public static SizeTableIssue Create(
        string code,
        SizeTableIssueSeverity severity,
        string userAction) => new(
            SizeTableValueRules.Identifier(code, "SIZE_TABLE_ISSUE_CODE_INVALID"),
            severity,
            SizeTableValueRules.RequiredText(userAction, 512, "SIZE_TABLE_ISSUE_ACTION_INVALID"));
}

public sealed record SizeTable
{
    private SizeTable(
        string tableId,
        string batchId,
        string styleId,
        string pageId,
        string candidateId,
        SizeTableSource source,
        SizeTableStatus status,
        long revision,
        ImmutableArray<SizeTableColumn> columns,
        ImmutableArray<SizeTableRow> rows,
        ImmutableArray<SizeTableIssue> issues,
        string dataDigest,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        TableId = tableId;
        BatchId = batchId;
        StyleId = styleId;
        PageId = pageId;
        CandidateId = candidateId;
        Source = source;
        Status = status;
        Revision = revision;
        Columns = columns;
        Rows = rows;
        Issues = issues;
        DataDigest = dataDigest;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public string TableId { get; }

    public string BatchId { get; }

    public string StyleId { get; }

    public string PageId { get; }

    public string CandidateId { get; }

    public SizeTableSource Source { get; }

    public SizeTableStatus Status { get; }

    public long Revision { get; }

    public ImmutableArray<SizeTableColumn> Columns { get; }

    public ImmutableArray<SizeTableRow> Rows { get; }

    public ImmutableArray<SizeTableIssue> Issues { get; }

    public string DataDigest { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public bool CanSave => Status == SizeTableStatus.Valid;

    public static SizeTable BeginExtraction(
        string tableId,
        string batchId,
        string styleId,
        string pageId,
        SizeTableSource source,
        DateTimeOffset createdAt)
    {
        if (createdAt == default)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_TIMESTAMP_INVALID");
        }

        return CreateValidated(
            SizeTableValueRules.Identifier(tableId, "SIZE_TABLE_ID_INVALID"),
            SizeTableValueRules.Identifier(batchId, "SIZE_TABLE_BATCH_ID_INVALID"),
            SizeTableValueRules.Identifier(styleId, "SIZE_TABLE_STYLE_ID_INVALID"),
            SizeTableValueRules.Identifier(pageId, "SIZE_TABLE_PAGE_ID_INVALID"),
            "candidate_pending",
            source ?? throw new ArgumentNullException(nameof(source)),
            SizeTableStatus.Extracting,
            0,
            [],
            [],
            [],
            createdAt,
            createdAt);
    }

    public static SizeTable FromCandidate(
        string tableId,
        string batchId,
        string styleId,
        string pageId,
        SizeTableCandidate candidate,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var validTableId = SizeTableValueRules.Identifier(tableId, "SIZE_TABLE_ID_INVALID");
        var validBatchId = SizeTableValueRules.Identifier(batchId, "SIZE_TABLE_BATCH_ID_INVALID");
        var validStyleId = SizeTableValueRules.Identifier(styleId, "SIZE_TABLE_STYLE_ID_INVALID");
        var validPageId = SizeTableValueRules.Identifier(pageId, "SIZE_TABLE_PAGE_ID_INVALID");
        if (createdAt == default)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_TIMESTAMP_INVALID");
        }

        if (!candidate.IsRectangular)
        {
            var issues = ImmutableArray.Create(SizeTableIssue.Create(
                "SIZE_TABLE_STRUCTURE_INVALID",
                SizeTableIssueSeverity.Blocking,
                "尺码表行列结构不完整，请重新识别或在编辑器中重建表格。"));
            return CreateValidated(
                validTableId,
                validBatchId,
                validStyleId,
                validPageId,
                candidate.CandidateId,
                candidate.Source,
                SizeTableStatus.Invalid,
                0,
                [],
                [],
                issues,
                createdAt,
                createdAt);
        }

        var columns = candidate.Columns.Select((text, index) =>
            SizeTableColumn.Create($"col_{index + 1:D3}", text)).ToImmutableArray();
        var rows = candidate.Rows.Select((values, rowIndex) => SizeTableRow.Create(
            $"row_{rowIndex + 1:D3}",
            values.Select((text, columnIndex) => SizeTableCell.Create(
                columns[columnIndex].ColumnId,
                text)))).ToImmutableArray();
        var issuesForCandidate = Assess(columns, rows, includeConfidence: true, candidate.Confidence);
        return CreateValidated(
            validTableId,
            validBatchId,
            validStyleId,
            validPageId,
            candidate.CandidateId,
            candidate.Source,
            StatusFor(issuesForCandidate),
            0,
            columns,
            rows,
            issuesForCandidate,
            createdAt,
            createdAt);
    }

    public SizeTable Edit(
        long expectedRevision,
        IEnumerable<SizeTableColumn> columns,
        IEnumerable<SizeTableRow> rows,
        DateTimeOffset updatedAt)
    {
        if (expectedRevision != Revision)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_REVISION_CONFLICT");
        }

        if (updatedAt < UpdatedAt)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_TIMESTAMP_INVALID");
        }

        var frozenColumns = columns?.ToImmutableArray()
            ?? throw new ArgumentNullException(nameof(columns));
        var frozenRows = rows?.ToImmutableArray()
            ?? throw new ArgumentNullException(nameof(rows));
        ValidateBoundedStructure(frozenColumns, frozenRows);
        var issues = Assess(frozenColumns, frozenRows, includeConfidence: false, 1m);
        return CreateValidated(
            TableId,
            BatchId,
            StyleId,
            PageId,
            CandidateId,
            Source,
            StatusFor(issues),
            checked(Revision + 1),
            frozenColumns,
            frozenRows,
            issues,
            CreatedAt,
            updatedAt);
    }

    public static SizeTable Restore(
        string tableId,
        string batchId,
        string styleId,
        string pageId,
        string candidateId,
        SizeTableSource source,
        SizeTableStatus status,
        long revision,
        IEnumerable<SizeTableColumn> columns,
        IEnumerable<SizeTableRow> rows,
        IEnumerable<SizeTableIssue> issues,
        string dataDigest,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var restored = CreateValidated(
            SizeTableValueRules.Identifier(tableId, "SIZE_TABLE_ID_INVALID"),
            SizeTableValueRules.Identifier(batchId, "SIZE_TABLE_BATCH_ID_INVALID"),
            SizeTableValueRules.Identifier(styleId, "SIZE_TABLE_STYLE_ID_INVALID"),
            SizeTableValueRules.Identifier(pageId, "SIZE_TABLE_PAGE_ID_INVALID"),
            SizeTableValueRules.Identifier(candidateId, "SIZE_TABLE_CANDIDATE_ID_INVALID"),
            source ?? throw new ArgumentNullException(nameof(source)),
            status,
            revision,
            columns?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(columns)),
            rows?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(rows)),
            issues?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(issues)),
            createdAt,
            updatedAt);
        if (!string.Equals(
                restored.DataDigest,
                SizeTableValueRules.Digest(dataDigest, "SIZE_TABLE_DATA_DIGEST_INVALID"),
                StringComparison.Ordinal))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_DATA_DIGEST_MISMATCH");
        }

        return restored;
    }

    private static SizeTable CreateValidated(
        string tableId,
        string batchId,
        string styleId,
        string pageId,
        string candidateId,
        SizeTableSource source,
        SizeTableStatus status,
        long revision,
        ImmutableArray<SizeTableColumn> columns,
        ImmutableArray<SizeTableRow> rows,
        ImmutableArray<SizeTableIssue> issues,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (revision < 0 || createdAt == default || updatedAt < createdAt)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_STATE_INVALID");
        }

        ValidateBoundedStructure(columns, rows);
        if (issues.Any(issue => issue is null)
            || issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal).Count() != issues.Length
            || (status == SizeTableStatus.Extracting
                ? !columns.IsDefaultOrEmpty || !rows.IsDefaultOrEmpty || !issues.IsDefaultOrEmpty
                : StatusFor(issues) != status))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_STATE_INVALID");
        }

        var digest = ComputeDigest(
            tableId,
            batchId,
            styleId,
            pageId,
            candidateId,
            source,
            status,
            revision,
            columns,
            rows,
            issues);
        return new SizeTable(
            tableId,
            batchId,
            styleId,
            pageId,
            candidateId,
            source,
            status,
            revision,
            columns,
            rows,
            issues,
            digest,
            createdAt,
            updatedAt);
    }

    private static void ValidateBoundedStructure(
        ImmutableArray<SizeTableColumn> columns,
        ImmutableArray<SizeTableRow> rows)
    {
        if (columns.Length > SizeTableProtocol.MaximumColumns
            || rows.Length > SizeTableProtocol.MaximumRows
            || columns.Any(column => column is null)
            || rows.Any(row => row is null)
            || columns.Select(column => column.ColumnId).Distinct(StringComparer.Ordinal).Count() != columns.Length
            || rows.Select(row => row.RowId).Distinct(StringComparer.Ordinal).Count() != rows.Length)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_STRUCTURE_INVALID");
        }

        var columnIds = columns.Select(column => column.ColumnId).ToArray();
        if (rows.Any(row => row.Cells.Length != columns.Length
            || !row.Cells.Select(cell => cell.ColumnId).SequenceEqual(columnIds, StringComparer.Ordinal)))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_ROW_COLUMN_MISMATCH");
        }
    }

    private static ImmutableArray<SizeTableIssue> Assess(
        ImmutableArray<SizeTableColumn> columns,
        ImmutableArray<SizeTableRow> rows,
        bool includeConfidence,
        decimal confidence)
    {
        var issues = new List<SizeTableIssue>();
        if (columns.IsDefaultOrEmpty || rows.IsDefaultOrEmpty)
        {
            issues.Add(SizeTableIssue.Create(
                "SIZE_TABLE_STRUCTURE_INVALID",
                SizeTableIssueSeverity.Blocking,
                "尺码表必须至少包含一列表头和一行尺码数据。"));
            return issues.ToImmutableArray();
        }

        if (columns.Any(column => column.SourceText.Length == 0))
        {
            issues.Add(SizeTableIssue.Create(
                "SIZE_TABLE_HEADER_MISSING",
                SizeTableIssueSeverity.Blocking,
                "请补全空白表头后再保存。"));
        }
        else if (columns.Select(column => column.SourceText).Distinct(StringComparer.Ordinal).Count() != columns.Length)
        {
            issues.Add(SizeTableIssue.Create(
                "SIZE_TABLE_HEADER_DUPLICATE",
                SizeTableIssueSeverity.Review,
                "存在重复表头，请确认或修改后再保存。"));
        }

        if (rows.SelectMany(row => row.Cells).Any(cell => cell.SourceText.Length == 0))
        {
            issues.Add(SizeTableIssue.Create(
                "SIZE_TABLE_VALUE_MISSING",
                SizeTableIssueSeverity.Blocking,
                "存在真正的空单元格；请填写来源中可确认的原文，无法确认时不要猜值。"));
        }

        if (columns.Any(column => string.IsNullOrWhiteSpace(column.SourceText))
            || rows.SelectMany(row => row.Cells).Any(cell => string.IsNullOrWhiteSpace(cell.SourceText)))
        {
            issues.Add(SizeTableIssue.Create(
                "SIZE_TABLE_WHITESPACE_ONLY",
                SizeTableIssueSeverity.Blocking,
                "表头或单元格不能只包含空白字符。"));
        }

        var rowKeys = rows.Select(row => row.Cells[0].SourceText).ToArray();
        if (rowKeys.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Count()
            != rowKeys.Count(value => value.Length > 0))
        {
            issues.Add(SizeTableIssue.Create(
                "SIZE_TABLE_SIZE_ROW_DUPLICATE",
                SizeTableIssueSeverity.Review,
                "存在重复尺码行，请确认后再保存。"));
        }

        if (includeConfidence && confidence < SizeTableProtocol.ReliableConfidence)
        {
            issues.Add(SizeTableIssue.Create(
                "SIZE_TABLE_CONFIDENCE_LOW",
                SizeTableIssueSeverity.Review,
                "识别置信度不足，请对照市场尺码图逐格确认。"));
        }

        return issues.ToImmutableArray();
    }

    private static SizeTableStatus StatusFor(ImmutableArray<SizeTableIssue> issues)
    {
        if (issues.Any(issue => issue.Severity == SizeTableIssueSeverity.Blocking))
        {
            return SizeTableStatus.Invalid;
        }

        return issues.IsDefaultOrEmpty ? SizeTableStatus.Valid : SizeTableStatus.ReviewRequired;
    }

    private static string ComputeDigest(
        string tableId,
        string batchId,
        string styleId,
        string pageId,
        string candidateId,
        SizeTableSource source,
        SizeTableStatus status,
        long revision,
        ImmutableArray<SizeTableColumn> columns,
        ImmutableArray<SizeTableRow> rows,
        ImmutableArray<SizeTableIssue> issues)
    {
        var builder = new StringBuilder();
        Append(builder, SizeTableProtocol.Version);
        Append(builder, tableId);
        Append(builder, batchId);
        Append(builder, styleId);
        Append(builder, pageId);
        Append(builder, candidateId);
        Append(builder, source.AssetId);
        Append(builder, source.ContentDigest);
        Append(builder, status.ToString());
        Append(builder, revision.ToString(CultureInfo.InvariantCulture));
        foreach (var column in columns)
        {
            Append(builder, column.ColumnId);
            Append(builder, column.SourceText);
        }
        foreach (var row in rows)
        {
            Append(builder, row.RowId);
            foreach (var cell in row.Cells)
            {
                Append(builder, cell.ColumnId);
                Append(builder, cell.SourceText);
            }
        }
        foreach (var issue in issues.OrderBy(issue => issue.Code, StringComparer.Ordinal))
        {
            Append(builder, issue.Code);
            Append(builder, issue.Severity.ToString());
            Append(builder, issue.UserAction);
        }

        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) => builder
        .Append(value.Length.ToString(CultureInfo.InvariantCulture))
        .Append(':')
        .Append(value);
}

public sealed class SizeTableRuleViolationException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

internal static class SizeTableValueRules
{
    public static string Identifier(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new SizeTableRuleViolationException(code);
        }
        return value;
    }

    public static string RequiredText(string value, int maximumLength, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new SizeTableRuleViolationException(code);
        }
        return value;
    }

    public static string SourceText(string value, string code)
    {
        if (value is null
            || value.Length > SizeTableProtocol.MaximumCellLength
            || value.Any(character => char.IsControl(character)))
        {
            throw new SizeTableRuleViolationException(code);
        }
        return value;
    }

    public static string Digest(string value, string code)
    {
        var normalized = value?.StartsWith("sha256:", StringComparison.Ordinal) == true
            ? value
            : "sha256:" + value;
        if (normalized is null
            || normalized.Length != 71
            || normalized[..7] != "sha256:"
            || normalized[7..].Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new SizeTableRuleViolationException(code);
        }
        return normalized.ToLowerInvariant();
    }
}
