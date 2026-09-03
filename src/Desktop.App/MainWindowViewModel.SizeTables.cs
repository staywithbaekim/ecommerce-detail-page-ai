using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Desktop.Application.SizeTables;

namespace Desktop.App;

public sealed partial class MainWindowViewModel
{
    private ISizeTableEditingWorkflow? _sizeTableEditingWorkflow;
    private bool _isSizeTableEditorVisible;

    public SizeTableEditorState SizeTableEditor { get; } = new();

    public ICommand OpenSizeTableEditorCommand { get; private set; } = null!;

    public ICommand CloseSizeTableEditorCommand { get; private set; } = null!;

    public ICommand AddSizeTableRowCommand { get; private set; } = null!;

    public ICommand RemoveSizeTableRowCommand { get; private set; } = null!;

    public ICommand AddSizeTableColumnCommand { get; private set; } = null!;

    public ICommand RemoveSizeTableColumnCommand { get; private set; } = null!;

    public ICommand SaveSizeTableCommand { get; private set; } = null!;

    public bool IsSizeTableEditorVisible
    {
        get => _isSizeTableEditorVisible;
        private set
        {
            if (_isSizeTableEditorVisible == value) return;
            _isSizeTableEditorVisible = value;
            OnPropertyChanged();
        }
    }

    private void InitializeSizeTableEditor(ISizeTableEditingWorkflow? workflow)
    {
        _sizeTableEditingWorkflow = workflow;
        OpenSizeTableEditorCommand = new AsyncRelayCommand<ResultPageCardViewModel>(OpenSizeTableEditorAsync);
        CloseSizeTableEditorCommand = new RelayCommand(CloseSizeTableEditor);
        AddSizeTableRowCommand = new RelayCommand(SizeTableEditor.AddRow);
        RemoveSizeTableRowCommand = new RelayCommand(
            SizeTableEditor.RemoveSelectedRow,
            () => SizeTableEditor.SelectedRow is not null && SizeTableEditor.Rows.Count > 1);
        AddSizeTableColumnCommand = new RelayCommand(SizeTableEditor.AddColumn);
        RemoveSizeTableColumnCommand = new RelayCommand(
            SizeTableEditor.RemoveSelectedColumn,
            () => SizeTableEditor.SelectedColumn is not null && SizeTableEditor.Columns.Count > 1);
        SaveSizeTableCommand = new AsyncRelayCommand(
            SaveSizeTableAsync,
            () => SizeTableEditor.CanSave && _sizeTableEditingWorkflow is not null);
        SizeTableEditor.PropertyChanged += SizeTableEditor_PropertyChanged;
    }

    private async Task OpenSizeTableEditorAsync(ResultPageCardViewModel? page)
    {
        var batchId = _currentBatchId ?? _aiBatchProjection?.BatchId;
        if (page is null || !page.IsSizeTablePage || _sizeTableEditingWorkflow is null
            || string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        var loaded = await _sizeTableEditingWorkflow.LoadAsync(
            batchId,
            page.StyleId,
            page.PageId,
            CancellationToken.None).ConfigureAwait(true);
        if (loaded.Document is null)
        {
            AiStatusMessage = loaded.Status == SizeTableEditorLoadStatus.RecoveryRequired
                ? "尺码数据记录损坏，需要重新识别；旧输出未被覆盖。"
                : "尚无可编辑的结构化尺码数据。";
            return;
        }

        var sourcePreviewPath = _aiBatchProjection?.Styles
            .SingleOrDefault(style => string.Equals(style.StyleId, page.StyleId, StringComparison.Ordinal))
            ?.Assets?.SingleOrDefault(asset => string.Equals(
                asset.AssetId,
                loaded.Document.SourceAssetId,
                StringComparison.Ordinal))?.PreviewPath;
        SizeTableEditor.Load(loaded.Document, sourcePreviewPath);
        IsSizeTableEditorVisible = true;
    }

    private void CloseSizeTableEditor() => IsSizeTableEditorVisible = false;

    private async Task SaveSizeTableAsync()
    {
        if (_sizeTableEditingWorkflow is null || !SizeTableEditor.TryBuild(out var draft))
        {
            return;
        }

        SizeTableEditor.IsSaving = true;
        try
        {
            var source = SizeTableEditor.SourceDocument!;
            var save = await _sizeTableEditingWorkflow.SaveAsync(
                new SizeTableEditorSaveCommand(
                    source.BatchId,
                    source.StyleId,
                    source.PageId,
                    source.Revision,
                    draft),
                CancellationToken.None).ConfigureAwait(true);
            if (save.Status != SizeTableEditorSaveStatus.Saved || save.Document is null)
            {
                SizeTableEditor.StatusMessage = save.Status == SizeTableEditorSaveStatus.RevisionConflict
                    ? "数据已在其他操作中更新，请关闭后重新打开。"
                    : "保存失败，原尺码数据和旧输出保持不变。";
                return;
            }

            var saved = save.Document;
            SizeTableEditor.AcceptSaved(saved);
            if (_aiBatchWorkflow is not null)
            {
                var preparation = await _aiBatchWorkflow.PrepareRenderingAsync(
                    saved.BatchId,
                    CancellationToken.None).ConfigureAwait(true);
                ApplyRenderPreparation(preparation);
                SelectedResultPage = ResultStyleGroups.SelectMany(group => group.Pages)
                    .FirstOrDefault(page => string.Equals(page.StyleId, saved.StyleId, StringComparison.Ordinal)
                        && string.Equals(page.PageId, saved.PageId, StringComparison.Ordinal));
            }
            SizeTableEditor.StatusMessage = "已保存并为当前款当前尺码页生成新计划；其他页面未改动。";
        }
        catch (InvalidOperationException)
        {
            SizeTableEditor.StatusMessage = "当前尺码页重新生成失败；已保存的数据可恢复，旧计划和旧输出未覆盖。";
        }
        finally
        {
            SizeTableEditor.IsSaving = false;
        }
    }

    private void SizeTableEditor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SizeTableEditorState.CanSave)
            or nameof(SizeTableEditorState.SelectedRow)
            or nameof(SizeTableEditorState.SelectedColumn)
            or nameof(SizeTableEditorState.IsSaving))
        {
            (SaveSizeTableCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RemoveSizeTableRowCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveSizeTableColumnCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}

public sealed class SizeTableEditorState : INotifyPropertyChanged
{
    private SizeTableEditorDocument? _sourceDocument;
    private SizeTableEditorRow? _selectedRow;
    private SizeTableEditorColumn? _selectedColumn;
    private string _statusMessage = "";
    private bool _canSave;
    private bool _isSaving;
    private string? _baselineSignature;

    public ObservableCollection<SizeTableEditorColumn> Columns { get; } = [];

    public ObservableCollection<SizeTableEditorRow> Rows { get; } = [];

    public SizeTableEditorDocument? SourceDocument => _sourceDocument;

    public string SourcePreviewPath { get; private set; } = "";

    public bool HasSourcePreview => !string.IsNullOrWhiteSpace(SourcePreviewPath);

    public bool HasNoSourcePreview => !HasSourcePreview;

    public string SourceSummary => _sourceDocument is null
        ? "未加载来源"
        : $"来源 {ShortId(_sourceDocument.SourceAssetId)} · hash {ShortDigest(_sourceDocument.SourceContentDigest)}";

    public string RevisionText => _sourceDocument is null ? "" : $"结构 revision {_sourceDocument.Revision}";

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
            OnPropertyChanged(nameof(SaveButtonText));
            Revalidate();
        }
    }

    public string SaveButtonText => IsSaving ? "正在保存…" : "保存并重生成此页";

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
        if (_sourceDocument is null || Rows.Count >= SizeTableEditingLimits.MaximumRows) return;
        var row = new SizeTableEditorRow(NextId("row", Rows.Select(item => item.RowId)),
            Enumerable.Range(0, Columns.Count).Select(_ => new SizeTableEditorCell("")));
        AddRowViewModel(row);
        Rows.Add(row);
        SelectedRow = row;
        Revalidate();
    }

    public void RemoveSelectedRow()
    {
        if (SelectedRow is null || Rows.Count <= 1) return;
        var index = Rows.IndexOf(SelectedRow);
        Rows.Remove(SelectedRow);
        SelectedRow = Rows[Math.Clamp(index, 0, Rows.Count - 1)];
        Revalidate();
    }

    public void AddColumn()
    {
        if (_sourceDocument is null || Columns.Count >= SizeTableEditingLimits.MaximumColumns) return;
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
        if (SelectedColumn is null || Columns.Count <= 1) return;
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
            && !IsSaving
            && !string.Equals(_baselineSignature, CurrentSignature(), StringComparison.Ordinal);
        if (!valid && !IsSaving)
        {
            StatusMessage = "仍有空单元格、重复表头/尺码或非法结构，当前不能保存。";
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
