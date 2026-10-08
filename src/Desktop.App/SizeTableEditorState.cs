using System.Collections.ObjectModel;
using System.ComponentModel;
using Desktop.Application.SizeTables;

namespace Desktop.App;

public sealed class SizeTableEditorState : INotifyPropertyChanged
{
    private const string ValidationErrorMessage =
        "仍有空单元格、重复表头/尺码或非法结构，当前不能保存。";

    private SizeTableEditorDocument? _sourceDocument;
    private SizeTableEditorRow? _selectedRow;
    private SizeTableEditorColumn? _selectedColumn;
    private string _statusMessage = "";
    private bool _canSave;
    private bool _isSaving;
    private bool _isRepair;
    private bool _isRepairPending;
    private string? _baselineSignature;
    private string? _statusBeforeValidationError;

    public ObservableCollection<SizeTableEditorColumn> Columns { get; } = [];

    public ObservableCollection<SizeTableEditorRow> Rows { get; } = [];

    public SizeTableEditorDocument? SourceDocument => _sourceDocument;

    public string SourcePreviewPath { get; private set; } = "";

    public bool HasSourcePreview => !string.IsNullOrWhiteSpace(SourcePreviewPath);

    public bool HasNoSourcePreview => !HasSourcePreview;

    public string SourceSummary => _sourceDocument is null
        ? "未加载来源"
        : "来源已登记 · 仅供对照";

    public string RevisionText => _sourceDocument is null ? "" : "当前内容已保存到本批次";

    public SizeTableEditorRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value)) return;
            _selectedRow = value;
            OnPropertyChanged(nameof(SelectedRow));
        }
    }

    public SizeTableEditorColumn? SelectedColumn
    {
        get => _selectedColumn;
        set
        {
            if (ReferenceEquals(_selectedColumn, value)) return;
            _selectedColumn = value;
            OnPropertyChanged(nameof(SelectedColumn));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    public bool CanSave
    {
        get => _canSave;
        private set
        {
            if (_canSave == value) return;
            _canSave = value;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set
        {
            if (_isSaving == value) return;
            _isSaving = value;
            OnPropertyChanged(nameof(IsSaving));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(SaveButtonText));
            Revalidate();
        }
    }

    public bool IsRepair
    {
        get => _isRepair;
        set { _isRepair = value; OnPropertyChanged(nameof(IsRepair)); OnPropertyChanged(nameof(SaveButtonText)); }
    }

    public bool IsRepairPending
    {
        get => _isRepairPending;
        set
        {
            _isRepairPending = value;
            OnPropertyChanged(nameof(IsRepairPending)); OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(SaveButtonText)); Revalidate();
        }
    }
    public bool CanEdit => !IsSaving && !IsRepairPending;
    public string SaveButtonText => IsSaving ? "正在保存…" : IsRepairPending ? "确认同一修复请求"
        : IsRepair ? "保存修复并生成当前页" : "保存并重生成此页";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Load(SizeTableEditorDocument document, string? sourcePreviewPath)
    {
        _sourceDocument = document ?? throw new ArgumentNullException(nameof(document));
        SourcePreviewPath = sourcePreviewPath ?? "";
        Columns.Clear();
        Rows.Clear();
        foreach (var column in document.Columns)
        {
            var editorColumn = new SizeTableEditorColumn(column.ColumnId, column.SourceText);
            AddColumnViewModel(editorColumn);
            Columns.Add(editorColumn);
        }
        foreach (var row in document.Rows)
        {
            var editorRow = new SizeTableEditorRow(
                row.RowId,
                row.Cells.Select(cell => new SizeTableEditorCell(cell)));
            AddRowViewModel(editorRow);
            Rows.Add(editorRow);
        }
        SelectedColumn = Columns.FirstOrDefault();
        SelectedRow = Rows.FirstOrDefault();
        _baselineSignature = document.Status == SizeTableEditorStatus.Valid ? CurrentSignature() : null;
        _statusBeforeValidationError = null;
        StatusMessage = document.Status switch
        {
            SizeTableEditorStatus.Valid => "逐格修改后保存；不会改动来源图。",
            SizeTableEditorStatus.ReviewRequired => "请对照来源图逐格确认；不确定的值不要猜。",
            _ => "存在缺值或非法结构，请补全后再保存。"
        };
        OnPropertyChanged(nameof(SourcePreviewPath));
        OnPropertyChanged(nameof(HasSourcePreview));
        OnPropertyChanged(nameof(HasNoSourcePreview));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(RevisionText));
        Revalidate();
    }

    public void AddRow()
    {
        if (!CanEdit || _sourceDocument is null || Rows.Count >= SizeTableEditingLimits.MaximumRows) return;
        var row = new SizeTableEditorRow(NextId("row", Rows.Select(item => item.RowId)),
            Enumerable.Range(0, Columns.Count).Select(_ => new SizeTableEditorCell("")));
        AddRowViewModel(row);
        Rows.Add(row);
        SelectedRow = row;
        Revalidate();
    }

    public void RemoveSelectedRow()
    {
        if (!CanEdit || SelectedRow is null || Rows.Count <= 1) return;
        var index = Rows.IndexOf(SelectedRow);
        Rows.Remove(SelectedRow);
        SelectedRow = Rows[Math.Clamp(index, 0, Rows.Count - 1)];
        Revalidate();
    }

    public void AddColumn()
    {
        if (!CanEdit || _sourceDocument is null || Columns.Count >= SizeTableEditingLimits.MaximumColumns) return;
        var column = new SizeTableEditorColumn(
            NextId("col", Columns.Select(item => item.ColumnId)),
            "新列");
        AddColumnViewModel(column);
        Columns.Add(column);
        foreach (var row in Rows)
        {
            var cell = new SizeTableEditorCell("");
            cell.PropertyChanged += (_, _) => Revalidate();
            row.Cells.Add(cell);
        }
        SelectedColumn = column;
        Revalidate();
    }

    public void RemoveSelectedColumn()
    {
        if (!CanEdit || SelectedColumn is null || Columns.Count <= 1) return;
        var index = Columns.IndexOf(SelectedColumn);
        Columns.Remove(SelectedColumn);
        foreach (var row in Rows) row.Cells.RemoveAt(index);
        SelectedColumn = Columns[Math.Clamp(index, 0, Columns.Count - 1)];
        Revalidate();
    }

    public bool TryBuild(out SizeTableEditorDraft draft)
    {
        draft = null!;
        if (_sourceDocument is null
            || Columns.Count is < 1 or > SizeTableEditingLimits.MaximumColumns
            || Rows.Count is < 1 or > SizeTableEditingLimits.MaximumRows
            || Columns.Select(column => column.ColumnId).Distinct(StringComparer.Ordinal).Count() != Columns.Count
            || Rows.Select(row => row.RowId).Distinct(StringComparer.Ordinal).Count() != Rows.Count
            || Columns.Any(column => InvalidText(column.SourceText))
            || Columns.Select(column => column.SourceText).Distinct(StringComparer.Ordinal).Count() != Columns.Count
            || Rows.Any(row => row.Cells.Count != Columns.Count
                || row.Cells.Any(cell => InvalidText(cell.SourceText))))
        {
            return false;
        }

        var rowKeys = Rows.Select(row => row.Cells[0].SourceText).ToArray();
        if (rowKeys.Distinct(StringComparer.Ordinal).Count() != rowKeys.Length)
        {
            return false;
        }

        draft = new SizeTableEditorDraft(
            Columns.Select(column => new SizeTableEditorColumnDocument(
                column.ColumnId,
                column.SourceText)).ToArray(),
            Rows.Select(row => new SizeTableEditorRowDocument(
                row.RowId,
                row.Cells.Select(cell => cell.SourceText).ToArray())).ToArray());
        return true;
    }

    public void AcceptSaved(SizeTableEditorDocument document)
    {
        _sourceDocument = document;
        _baselineSignature = CurrentSignature();
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(RevisionText));
        Revalidate();
    }

    private void Revalidate()
    {
        var valid = TryBuild(out var edited);
        CanSave = valid
            && CanEdit
            && !string.Equals(_baselineSignature, CurrentSignature(), StringComparison.Ordinal);
        if (!valid && !IsSaving)
        {
            if (!string.Equals(StatusMessage, ValidationErrorMessage, StringComparison.Ordinal))
            {
                _statusBeforeValidationError = StatusMessage;
            }
            StatusMessage = ValidationErrorMessage;
        }
        else if (valid && string.Equals(StatusMessage, ValidationErrorMessage, StringComparison.Ordinal))
        {
            StatusMessage = _statusBeforeValidationError ?? "逐格修改后保存；不会改动来源图。";
            _statusBeforeValidationError = null;
        }
    }

    private void AddColumnViewModel(SizeTableEditorColumn column)
    {
        column.PropertyChanged += (_, _) => Revalidate();
    }

    private void AddRowViewModel(SizeTableEditorRow row)
    {
        foreach (var cell in row.Cells) cell.PropertyChanged += (_, _) => Revalidate();
    }

    private static string NextId(string prefix, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.Ordinal);
        for (var index = 1; index <= 999; index++)
        {
            var candidate = $"{prefix}_{index:D3}";
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("SIZE_TABLE_ID_LIMIT_EXCEEDED");
    }

    private static bool InvalidText(string value) => string.IsNullOrWhiteSpace(value)
        || value.Length > SizeTableEditingLimits.MaximumCellLength
        || value.Any(char.IsControl);

    private string CurrentSignature()
    {
        var parts = Columns.Select(column => $"H{column.SourceText.Length}:{column.SourceText}")
            .Concat(Rows.SelectMany(row => row.Cells.Select(cell =>
                $"C{cell.SourceText.Length}:{cell.SourceText}")));
        return string.Join('|', parts);
    }

    private static string ShortId(string value) => value.Length <= 18 ? value : value[..18] + "…";

    private static string ShortDigest(string value) => value.Length < 19
        ? value
        : string.Concat(value.AsSpan(7, 12), "…");

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SizeTableEditorColumn(string columnId, string sourceText) : INotifyPropertyChanged
{
    private string _sourceText = sourceText;
    public string ColumnId { get; } = columnId;
    public string SourceText
    {
        get => _sourceText;
        set
        {
            if (_sourceText == value) return;
            _sourceText = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceText)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SizeTableEditorRow
{
    public SizeTableEditorRow(string rowId, IEnumerable<SizeTableEditorCell> cells)
    {
        RowId = rowId;
        Cells = new ObservableCollection<SizeTableEditorCell>(cells);
    }
    public string RowId { get; }
    public ObservableCollection<SizeTableEditorCell> Cells { get; }
    public string AccessibilityName => $"尺码行 {RowId}";
}

public sealed class SizeTableEditorCell(string sourceText) : INotifyPropertyChanged
{
    private string _sourceText = sourceText;
    public string SourceText
    {
        get => _sourceText;
        set
        {
            if (_sourceText == value) return;
            _sourceText = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceText)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
