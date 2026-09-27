using System.ComponentModel;
using System.Windows.Input;
using Desktop.Application.SizeTables;
using Desktop.Application.Rendering;

namespace Desktop.App;

public sealed partial class SizeTableEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISizeTableEditingWorkflow? _sizeTableEditingWorkflow;
    private readonly ISizeTableEditorHost _host;
    private readonly IRenderBatchWorkflow? _rendering;
    private const string FrozenPlanMessage = "当前批次已有冻结的渲染计划，暂不能保存尺码修改。重新生成仅使用原计划；请保留旧结果，在新批次生成前修正尺码。";
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSizeTableEditorVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SizeTableEditorViewModel(ISizeTableEditingWorkflow? workflow, ISizeTableEditorHost host,
        IRenderBatchWorkflow? rendering = null, ISizeTablePageRepairWorkflow? repairs = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _sizeTableEditingWorkflow = workflow;
        _rendering = rendering;
        _repairs = repairs ?? rendering as ISizeTablePageRepairWorkflow;
        OpenSizeTableEditorCommand = new AsyncRelayCommand<ResultPageCardViewModel>(OpenSizeTableEditorAsync);
        CloseSizeTableEditorCommand = new RelayCommand(CloseSizeTableEditor, () => !SizeTableEditor.IsSaving);
        AddSizeTableRowCommand = new RelayCommand(SizeTableEditor.AddRow, () => SizeTableEditor.CanEdit);
        RemoveSizeTableRowCommand = new RelayCommand(
            SizeTableEditor.RemoveSelectedRow,
            () => SizeTableEditor.CanEdit && SizeTableEditor.SelectedRow is not null && SizeTableEditor.Rows.Count > 1);
        AddSizeTableColumnCommand = new RelayCommand(SizeTableEditor.AddColumn, () => SizeTableEditor.CanEdit);
        RemoveSizeTableColumnCommand = new RelayCommand(
            SizeTableEditor.RemoveSelectedColumn,
            () => SizeTableEditor.CanEdit && SizeTableEditor.SelectedColumn is not null && SizeTableEditor.Columns.Count > 1);
        SaveSizeTableCommand = new AsyncRelayCommand(
            SaveSizeTableAsync,
            () => !SizeTableEditor.IsSaving && (_pendingRepair is not null
                || SizeTableEditor.CanSave && (_repairBaseline is not null ? _repairs is not null : _sizeTableEditingWorkflow is not null)));
        SizeTableEditor.PropertyChanged += SizeTableEditor_PropertyChanged;
    }

    private async Task OpenSizeTableEditorAsync(ResultPageCardViewModel? page)
    {
        var batchId = _host.BatchId;
        if (page is null || !page.IsSizeTablePage || IsOpening || IsSizeTableEditorVisible || !_host.CanOpenEditor
            || _sizeTableEditingWorkflow is null && _repairs is null
            || string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        if (_pendingRepair is { } pending)
        {
            if (pending.Target.BatchId == batchId && pending.Target.StyleId == page.StyleId
                && pending.Target.PageId == page.PageId && pending.Target.OutputVariantId == page.OutputVariantId)
                IsSizeTableEditorVisible = true;
            else _host.ShowAnalysisStatus("上一尺码修复请求尚未确认，请先回到原页确认同一请求，或从任务记录恢复；不会创建另一份修复。");
            return;
        }
        IsOpening = true;
        try
        {
        var journal = _rendering is null ? null : await _rendering.LoadAsync(batchId, CancellationToken.None).ConfigureAwait(true);
        if (batchId != _host.BatchId) return;
        if (journal is not null)
        {
            if (!_host.CanEditRenderedBatch(batchId))
            {
                _host.ShowAnalysisStatus("按款输出暂不支持生成后修改尺码；已生成的页面和公开目录保持不变。");
                return;
            }
            if (_repairs is null) { _host.ShowAnalysisStatus(FrozenPlanMessage); return; }
            var baseline = await _repairs.LoadSizeTableRepairAsync(batchId, page.StyleId, page.PageId,
                page.OutputVariantId, CancellationToken.None).ConfigureAwait(true);
            if (batchId != _host.BatchId) return;
            _repairBaseline = baseline;
            SizeTableEditor.Load(baseline.Document, _host.GetSourcePreviewPath(page.StyleId, baseline.Document.SourceAssetId));
            SizeTableEditor.IsRepair = true;
            SizeTableEditor.StatusMessage = "修改当前页尺码并生成新结果；其他页、来源和旧输出保持不变。请只填写有依据的值。";
            IsSizeTableEditorVisible = true;
            return;
        }
        _repairBaseline = null;
        if (_sizeTableEditingWorkflow is null) return;
        var loaded = await _sizeTableEditingWorkflow.LoadAsync(
            batchId,
            page.StyleId,
            page.PageId,
            CancellationToken.None).ConfigureAwait(true);
        if (batchId != _host.BatchId) return;
        if (loaded.Document is null)
        {
            _host.ShowAnalysisStatus(loaded.Status == SizeTableEditorLoadStatus.RecoveryRequired
                ? "尺码数据记录损坏，需要重新识别；旧输出未被覆盖。"
                : "尚无可编辑的结构化尺码数据。");
            return;
        }

        var sourcePreviewPath = _host.GetSourcePreviewPath(page.StyleId, loaded.Document.SourceAssetId);
        SizeTableEditor.Load(loaded.Document, sourcePreviewPath);
        SizeTableEditor.IsRepair = false;
        IsSizeTableEditorVisible = true;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Data.Common.DbException)
        {
            if (batchId == _host.BatchId) _host.ShowAnalysisStatus("无法核对当前尺码页的安全修复基线。请先恢复任务；未知执行、缺失来源或不支持的旧校准不能直接修改。原数据和旧输出保持不变。");
        }
        finally { IsOpening = false; }
    }

    private void CloseSizeTableEditor()
    {
        if (!SizeTableEditor.IsSaving) IsSizeTableEditorVisible = false;
    }

    private async Task SaveSizeTableAsync()
    {
        if (_repairBaseline is not null)
        {
            await SaveRepairAsync().ConfigureAwait(true);
            return;
        }
        if (_sizeTableEditingWorkflow is null || !SizeTableEditor.TryBuild(out var draft))
        {
            return;
        }

        SizeTableEditor.IsSaving = true;
        try
        {
            var source = SizeTableEditor.SourceDocument!;
            // Recheck persistence, not only the page projection: a render plan may
            // have been frozen after this editor was opened.
            if (await EditingBlockReasonAsync(source.BatchId).ConfigureAwait(true) is { } blockReason)
            {
                SizeTableEditor.StatusMessage = blockReason;
                return;
            }
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
            await _host.RefreshSavedPageAsync(saved).ConfigureAwait(true);
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

    private async Task<string?> EditingBlockReasonAsync(string batchId)
    {
        try
        {
            return _rendering is not null
                && await _rendering.LoadAsync(batchId, CancellationToken.None).ConfigureAwait(true) is not null
                    ? FrozenPlanMessage : null;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Data.Common.DbException)
        {
            return "无法核对当前批次的渲染记录，暂不能保存尺码修改。请先恢复任务；尺码数据、原计划和旧输出保持不变。";
        }
    }

    public void Dispose()
    {
        SizeTableEditor.PropertyChanged -= SizeTableEditor_PropertyChanged;
        GC.SuppressFinalize(this);
    }

    private void SizeTableEditor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SizeTableEditorState.CanSave)
            or nameof(SizeTableEditorState.SelectedRow)
            or nameof(SizeTableEditorState.SelectedColumn)
            or nameof(SizeTableEditorState.CanEdit)
            or nameof(SizeTableEditorState.IsRepairPending)
            or nameof(SizeTableEditorState.IsSaving))
        {
            (SaveSizeTableCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RemoveSizeTableRowCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveSizeTableColumnCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AddSizeTableRowCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AddSizeTableColumnCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CloseSizeTableEditorCommand as RelayCommand)?.RaiseCanExecuteChanged();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }
}

/// <summary>Cross-page context supplied by the shell; editor commands and state remain local.</summary>
public interface ISizeTableEditorHost
{
    string? BatchId { get; }
    bool CanEditRenderedBatch(string batchId) => true;
    bool CanOpenEditor => true;
    string? GetSourcePreviewPath(string styleId, string sourceAssetId);
    void ShowAnalysisStatus(string message);
    Task RefreshSavedPageAsync(SizeTableEditorDocument saved);
    Task ContinueSizeTableRepairAsync(RenderBatchJournal journal, SizeTablePageRepairCommand command) =>
        throw new InvalidOperationException("SIZE_REPAIR_HOST_UNAVAILABLE");
}
