using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Desktop.Application.Templates;

namespace Desktop.App;

/// <summary>Owns batch input commands, draft writes and cancellable scanning.</summary>
public sealed class BatchInputViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SemaphoreSlim _batchDraftWrite = new(1, 1);
    private readonly IFolderPicker? _folderPicker;
    private readonly IBatchInputWorkflow? _batchInputWorkflow;
    private readonly ITemplateWorkspaceService? _templateWorkspace;
    private readonly IBatchInputHost _host;
    private CancellationTokenSource? _batchScanCancellation;
    private bool _applyingBatchProjection;
    private Task _batchTemplateLoadTask = Task.CompletedTask;
    private int _batchDraftGeneration;
    private bool _disposed;
    public string BatchSwitchStatusText { get; private set; } = "";

    public BatchInputPageState State { get; } = new();
    private BatchInputPageState BatchInputState => State;
    public bool IsWorkflowAvailable => _batchInputWorkflow is not null;
    public string? CurrentBatchId
    {
        get => State.CurrentBatchId;
        set => State.CurrentBatchId = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BatchPageOptionViewModel> BatchPages => BatchInputState.BatchPages;

    public ICommand OpenBatchInputCommand { get; private set; } = null!;

    public ICommand RestoreBatchInputCommand { get; private set; } = null!;

    public ICommand CancelBatchInputCommand { get; private set; } = null!;

    public ICommand ChooseBatchInputDirectoryCommand { get; private set; } = null!;

    public ICommand ChooseBatchOutputDirectoryCommand { get; private set; } = null!;

    public ICommand OpenBatchPageSelectionCommand { get; private set; } = null!;

    public ICommand ApplyBatchPageSelectionCommand { get; private set; } = null!;

    public ICommand CancelBatchPageSelectionCommand { get; private set; } = null!;

    public ICommand ScanBatchInputCommand { get; private set; } = null!;

    public ICommand CancelBatchScanCommand { get; private set; } = null!;

    public ICommand RetryBatchScanCommand { get; private set; } = null!;

    public ICommand CreateTemplateFromBatchCommand { get; private set; } = null!;

    public ICommand ImportTemplateFromBatchCommand { get; private set; } = null!;

    public TemplateLibraryCardViewModel? SelectedBatchTemplate
    {
        get => BatchInputState.SelectedTemplate;
        set
        {
            if (ReferenceEquals(BatchInputState.SelectedTemplate, value))
            {
                return;
            }

            BatchInputState.SelectedTemplate = value;
            _batchTemplateLoadTask = LoadBatchTemplatePagesAsync(value);
            MarkBatchDraftChanged();
        }
    }

    public string BatchTemplateHelperText => BatchInputState.BatchTemplateHelperText;

    public string BatchInputDirectory
    {
        get => BatchInputState.InputDirectory;
        private set => BatchInputState.InputDirectory = value;
    }

    public string BatchOutputDirectory
    {
        get => BatchInputState.OutputDirectory;
        private set => BatchInputState.OutputDirectory = value;
    }

    public string BatchInputDirectoryDisplay => BatchInputState.BatchInputDirectoryDisplay;

    public string BatchOutputDirectoryDisplay => BatchInputState.BatchOutputDirectoryDisplay;

    public bool IsBatchStyleSourceOutput
    {
        get => State.IsStyleSourceOutput;
        set { if (value && !State.IsStyleSourceOutput) SetOutputMode(BatchOutputMode.StyleSourceDirectories); }
    }
    public bool IsBatchUnifiedOutput
    {
        get => State.IsUnifiedOutput;
        set { if (value && State.IsStyleSourceOutput) SetOutputMode(BatchOutputMode.UnifiedDirectory); }
    }
    public bool CanChooseBatchOutputDirectory => State.CanChooseOutputDirectory;
    private void SetOutputMode(BatchOutputMode mode)
    {
        if (!IsBatchConfigurationEditable) return;
        State.OutputMode = mode;
        BatchOutputConfirmationText = State.IsStyleSourceOutput
            ? "按扫描确认的每款实际目录写入“生成详情页”，不猜父款号、不覆盖已有文件。"
            : "请选择统一输出目录；每款独立一个子目录，已有同名目标会阻止生成。";
        MarkBatchDraftChanged();
    }

    public string BatchRecognitionText
    {
        get => BatchInputState.RecognitionText;
        private set => BatchInputState.RecognitionText = value;
    }

    public string BatchOutputConfirmationText
    {
        get => BatchInputState.OutputConfirmationText;
        private set => BatchInputState.OutputConfirmationText = value;
    }

    public string BatchStatusMessage
    {
        get => BatchInputState.StatusMessage;
        private set => BatchInputState.SetStatus(BatchInputState.Status, value);
    }

    public string BatchStatusState => BatchInputState.BatchStatusState;

    public bool IsBatchScanning => BatchInputState.IsBatchScanning;

    public bool IsBatchScanReady => BatchInputState.IsBatchScanReady;

    public bool IsBatchScanBlocked => BatchInputState.IsBatchScanBlocked;

    public bool HasBatchRetry => BatchInputState.HasBatchRetry;

    public string BatchPrimaryActionText => BatchInputState.BatchPrimaryActionText;

    public bool IsBatchConfigurationEditable => BatchInputState.IsBatchConfigurationEditable;

    public bool IsBatchAutomaticSelection
    {
        get => BatchInputState.AutomaticSelection;
        set
        {
            if (BatchInputState.AutomaticSelection == value)
            {
                return;
            }

            BatchInputState.AutomaticSelection = value;
            MarkBatchDraftChanged();
        }
    }

    public bool IsBatchManualSelection
    {
        get => !IsBatchAutomaticSelection;
        set
        {
            if (value)
            {
                IsBatchAutomaticSelection = false;
            }
        }
    }

    public bool BatchIncludeEditablePsd
    {
        get => BatchInputState.IncludeEditablePsd;
        set
        {
            if (BatchInputState.IncludeEditablePsd != value)
            {
                BatchInputState.IncludeEditablePsd = value;
                MarkBatchDraftChanged();
            }
        }
    }

    public bool IsBatchPageSelectionVisible
    {
        get => BatchInputState.IsPageSelectionVisible;
    }

    public bool BatchBusy
    {
        get => BatchInputState.Busy;
        private set
        {
            if (BatchInputState.Busy != value)
            {
                BatchInputState.Busy = value;
                OnPropertyChanged(nameof(CanScanBatch));
                RaiseBatchCommandCanExecuteChanged();
            }
        }
    }

    public int SelectedBatchPageCount => BatchPages.Count(page => page.IsSelected);

    public bool? AreAllBatchPagesSelected
    {
        get => BatchInputState.AreAllBatchPagesSelected;
        set => BatchInputState.AreAllBatchPagesSelected = value;
    }

    public string BatchPageSelectionSummary => BatchInputState.BatchPageSelectionSummary;

    public IReadOnlyList<string> BatchOutputPreviewFiles => BatchInputState.BatchOutputPreviewFiles;

    public string BatchOutputPreviewSummary => BatchInputState.BatchOutputPreviewSummary;

    public string BatchDraftSaveText => BatchInputState.BatchDraftSaveText;

    public bool CanScanBatch => BatchInputState.CanScanBatch;

    public BatchInputViewModel(
        IFolderPicker? folderPicker,
        IBatchInputWorkflow? batchInputWorkflow,
        ITemplateWorkspaceService? templateWorkspace,
        IBatchInputHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _templateWorkspace = templateWorkspace;
        State.PropertyChanged += State_PropertyChanged;
        _folderPicker = folderPicker;
        _batchInputWorkflow = batchInputWorkflow;
        OpenBatchInputCommand = new AsyncRelayCommand(OpenBatchInputAsync,
            () => !BatchBusy && _host.CanSwitchBatch, ReportBatchSwitchFailure);
        RestoreBatchInputCommand = new AsyncRelayCommand(RestoreBatchInputAsync,
            () => !BatchBusy && _host.CanSwitchBatch, ReportBatchSwitchFailure);
        CancelBatchInputCommand = new RelayCommand(CloseBatchInput, () => !BatchBusy && !IsBatchScanning);
        ChooseBatchInputDirectoryCommand = new AsyncRelayCommand(
            ChooseBatchInputDirectoryAsync,
            () => IsBatchConfigurationEditable);
        ChooseBatchOutputDirectoryCommand = new AsyncRelayCommand(
            ChooseBatchOutputDirectoryAsync,
            () => CanChooseBatchOutputDirectory);
        OpenBatchPageSelectionCommand = new RelayCommand(
            OpenBatchPageSelection,
            () => BatchPages.Count > 0 && IsBatchConfigurationEditable);
        ApplyBatchPageSelectionCommand = new RelayCommand(ApplyBatchPageSelection);
        CancelBatchPageSelectionCommand = new RelayCommand(CancelBatchPageSelection);
        ScanBatchInputCommand = new AsyncRelayCommand(ScanBatchInputAsync, () => CanScanBatch);
        CancelBatchScanCommand = new RelayCommand(CancelBatchScan, () => IsBatchScanning);
        RetryBatchScanCommand = new AsyncRelayCommand(
            RetryBatchScanAsync,
            () => CanScanBatch && IsBatchScanBlocked);
        CreateTemplateFromBatchCommand = new RelayCommand(CreateTemplateFromBatch);
        ImportTemplateFromBatchCommand = new RelayCommand(ImportTemplateFromBatch);
    }

    private Task OpenBatchInputAsync() => OpenBatchInputAsync(restoreLatest: false);

    private Task RestoreBatchInputAsync() => OpenBatchInputAsync(restoreLatest: true);

    public Task<IReadOnlyList<BatchRecoverySummary>> ListRecoverableAsync(CancellationToken cancellationToken) =>
        _batchInputWorkflow?.ListRecoverableAsync(cancellationToken)
        ?? throw new InvalidOperationException("INPUT_BATCH_LIST_UNAVAILABLE");

    public Task RestoreBatchAsync(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        return OpenBatchInputAsync(restoreLatest: false, batchId);
    }

    private async Task OpenBatchInputAsync(bool restoreLatest, string? batchId = null)
    {
        if (BatchBusy || !_host.CanSwitchBatch)
        {
            throw new InvalidOperationException("INPUT_BATCH_BUSY");
        }

        BatchBusy = true;
        BatchSwitchStatusText = "";
        OnPropertyChanged(nameof(BatchSwitchStatusText));
        _applyingBatchProjection = true;
        // Invalidate pending autosaves before any await. An old save may finish
        // for its own batch, but must never replace the selected batch identity.
        _batchDraftGeneration++;
        var draftWriteHeld = false;
        try
        {
            await _batchDraftWrite.WaitAsync().ConfigureAwait(true);
            draftWriteHeld = true;
            var restored = batchId is not null
                ? await (_batchInputWorkflow?.RestoreBatchAsync(batchId, CancellationToken.None)
                    ?? throw new InvalidOperationException("INPUT_BATCH_SELECTION_UNAVAILABLE")).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("INPUT_BATCH_NOT_FOUND")
                : !restoreLatest || _batchInputWorkflow is null
                    ? null
                    : await _batchInputWorkflow.RestoreLatestDraftAsync(CancellationToken.None).ConfigureAwait(true);
            if (batchId is not null && !string.Equals(batchId, restored!.BatchId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("INPUT_BATCH_ID_MISMATCH");
            }

            await _host.LoadTemplateLibraryAsync().ConfigureAwait(true);
            if (!_host.CanSwitchBatch)
            {
                throw new InvalidOperationException("INPUT_BATCH_BUSY");
            }
            _host.ShowBatchInput();
            BatchInputState.ClosePageSelection();
            if (restored is null)
            {
                ResetBatchDraftForNewInput();
            }

            var targetTemplate = _host.TemplateGroups.FirstOrDefault(group =>
                group.HasCommittedRevision
                && string.Equals(
                    group.GroupId,
                    restored?.TemplateGroupId ?? SelectedBatchTemplate?.GroupId,
                    StringComparison.Ordinal));
            if (restored is null)
            {
                targetTemplate ??= _host.TemplateGroups.FirstOrDefault(group => group.HasCommittedRevision);
            }

            if (!ReferenceEquals(SelectedBatchTemplate, targetTemplate))
            {
                SelectedBatchTemplate = targetTemplate;
            }

            await _batchTemplateLoadTask.ConfigureAwait(true);
            if (restored is not null)
            {
                ApplyBatchProjection(restored);
                if (restored.Status == BatchInputUiStatus.ScanReady)
                {
                    await _host.RestoreAiAnalysisAsync(restored.BatchId).ConfigureAwait(true);
                }
            }
        }
        catch (IOException exception) when (batchId is null)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "批次草稿暂时无法恢复，请检查本地存储后重试。");
            ReportBatchSwitchFailure(exception);
        }
        catch (InvalidOperationException exception) when (batchId is null)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToBatchUserMessage(exception.Message));
            ReportBatchSwitchFailure(exception);
        }
        finally
        {
            if (draftWriteHeld)
            {
                _batchDraftWrite.Release();
            }
            _applyingBatchProjection = false;
            BatchBusy = false;
        }
    }

    private void CloseBatchInput()
    {
        BatchInputState.ClosePageSelection();
        BeginFreshBatchDraft();
        _host.ShowWorkbench();
    }

    private void ResetBatchDraftForNewInput()
    {
        BeginFreshBatchDraft();
        BatchInputState.ResetForNewInput();
    }

    internal bool CanRestartAnalysis => IsWorkflowAvailable
        && SelectedBatchTemplate is not null
        && !string.IsNullOrWhiteSpace(BatchInputDirectory)
        && (IsBatchStyleSourceOutput || !string.IsNullOrWhiteSpace(BatchOutputDirectory))
        && SelectedBatchPageCount > 0;

    internal async Task RestartAnalysisAsync()
    {
        BeginFreshBatchDraft();
        SetBatchStatus(BatchInputUiStatus.Draft,
            "已保留当前模板、目录和输出设置，正在重新建立批次并重试 AI 分析。旧失败记录不会重复发送。");
        _host.ShowBatchInput();
        await ScanBatchInputAsync().ConfigureAwait(true);
    }

    internal void BeginFreshBatchDraft()
    {
        // A retry/new-batch action must never reuse a batch whose manifest or
        // SQLite graph was already classified as inconsistent.  Incrementing
        // the generation also makes an older fire-and-forget draft save a
        // no-op when it completes after the UI has moved on.
        _batchDraftGeneration++;
        CurrentBatchId = null;
    }

    private void CreateTemplateFromBatch()
    {
        _host.CreateTemplate();
    }

    private void ImportTemplateFromBatch()
    {
        _host.ImportTemplate();
    }

    private async Task LoadBatchTemplatePagesAsync(TemplateLibraryCardViewModel? template)
    {
        BatchPages.Clear();
        if (_templateWorkspace is null || template is null)
        {
            BatchStatusMessage = "请先选择一个已校准模板组。";
            return;
        }

        try
        {
            var details = await _templateWorkspace.GetCommittedDetailsAsync(
                template.GroupId,
                CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(SelectedBatchTemplate, template))
            {
                return;
            }

            if (details is null || !details.IsCommitted)
            {
                BatchStatusMessage = "所选模板尚未完成校准，请返回模板页完成后重试。";
                return;
            }

            foreach (var page in details.Pages.Where(page => page.Enabled).OrderBy(page => page.Order))
            {
                BatchPages.Add(new BatchPageOptionViewModel(
                    page.PageId,
                    page.Name,
                    $"{TemplatePageRowViewModel.ContentTypeLabel(page.ContentType)} · {TemplatePageRowViewModel.GenerationModeLabel(page.GenerationMode)}",
                    true));
            }

            MarkBatchDraftChanged();
        }
        catch (IOException)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "模板库暂时不可访问，请检查本地存储后重试。");
        }
        catch (InvalidOperationException)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "模板信息未能安全载入，请返回模板页检查后重试。");
        }
    }

    private async Task ChooseBatchInputDirectoryAsync()
    {
        if (_folderPicker is null)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "系统文件夹选择器暂时不可用，请重启应用后重试。");
            return;
        }

        var selected = await _folderPicker.PickFolderAsync(
            "选择商品目录",
            BatchInputDirectory,
            CancellationToken.None).ConfigureAwait(true);
        if (selected is null)
        {
            return;
        }

        BatchInputDirectory = selected;
        BatchRecognitionText = "目录已选择；扫描时只读取该目录及其一级子目录。";
        MarkBatchDraftChanged();
    }

    private async Task ChooseBatchOutputDirectoryAsync()
    {
        if (!CanChooseBatchOutputDirectory) return;
        if (_folderPicker is null)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "系统文件夹选择器暂时不可用，请重启应用后重试。");
            return;
        }

        var selected = await _folderPicker.PickFolderAsync(
            "选择正式输出目录",
            BatchOutputDirectory,
            CancellationToken.None).ConfigureAwait(true);
        if (selected is null)
        {
            return;
        }

        BatchOutputDirectory = selected;
        BatchOutputConfirmationText = State.OutputMode is null
            ? "已确认为本次正式输出目录；软件不会清理目录内其他文件。"
            : "已选择统一根目录；每款独立子目录，已有同名目标不会覆盖。";
        MarkBatchDraftChanged();
    }

    private void OpenBatchPageSelection()
    {
        BatchInputState.OpenPageSelection();
    }

    private void ApplyBatchPageSelection()
    {
        BatchInputState.ApplyPageSelection();
        MarkBatchDraftChanged();
    }

    private void CancelBatchPageSelection()
    {
        BatchInputState.CancelPageSelection();
    }

    internal async Task ScanBatchInputAsync()
    {
        if (!CanScanBatch)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, SelectedBatchPageCount == 0
                ? "至少选择 1 个页面才能扫描并分析。"
                : "请先确认模板、商品目录和正式输出目录。");
            return;
        }

        if (!await _host.EnsureUploadConsentAsync().ConfigureAwait(true))
        {
            return;
        }

        if (_batchInputWorkflow is null)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "批次扫描服务暂时不可用，请重启应用后重试。");
            return;
        }

        _batchScanCancellation?.Dispose();
        _batchScanCancellation = new CancellationTokenSource();
        BatchBusy = true;
        SetBatchStatus(BatchInputUiStatus.Scanning, "正在只读扫描商品目录；此阶段不会修改源图片，也不占用 Photoshop。");
        var draftWriteHeld = false;
        try
        {
            await _batchDraftWrite.WaitAsync(_batchScanCancellation.Token).ConfigureAwait(true);
            draftWriteHeld = true;
            var request = CreateBatchSubmission();
            var result = await _batchInputWorkflow.ScanAsync(request, _batchScanCancellation.Token).ConfigureAwait(true);
            ApplyBatchProjection(result);
            if (result.Status == BatchInputUiStatus.ScanReady)
            {
                await _host.StartAiAnalysisAsync(result.BatchId).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "扫描已取消。源图片保持不变；可以重新扫描或返回工作台。");
        }
        catch (IOException)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "目录暂时不可访问，请检查磁盘、权限或目录是否仍存在后重试。");
        }
        catch (InvalidOperationException exception)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToBatchUserMessage(exception.Message));
        }
        finally
        {
            if (draftWriteHeld)
            {
                _batchDraftWrite.Release();
            }
            BatchBusy = false;
        }
    }

    private async Task RetryBatchScanAsync()
    {
        if (!CanScanBatch || !IsBatchScanBlocked)
        {
            return;
        }

        BeginFreshBatchDraft();
        await ScanBatchInputAsync().ConfigureAwait(true);
    }

    private void CancelBatchScan() => _batchScanCancellation?.Cancel();

    private void ApplyBatchProjection(BatchInputProjection projection)
    {
        var wasApplyingProjection = _applyingBatchProjection;
        _applyingBatchProjection = true;
        try
        {
            BatchInputState.ApplyProjection(projection);
        }
        finally
        {
            _applyingBatchProjection = wasApplyingProjection;
        }
    }

    private void MarkBatchDraftChanged()
    {
        if (BatchInputState.Status != BatchInputUiStatus.Scanning)
        {
            SetBatchStatus(BatchInputUiStatus.Draft, CanScanBatch
                ? "输入、输出和页面已确认，可以开始只读扫描。"
                : "请确认模板、商品目录、输出目录和至少一个页面。");
        }

        RaiseBatchCommandCanExecuteChanged();
        if (!_applyingBatchProjection
            && !BatchInputState.IsPageSelectionVisible
            && CanScanBatch
            && _batchInputWorkflow is not null)
        {
            _ = PersistBatchDraftAsync(_batchDraftGeneration);
        }
    }

    private BatchInputSubmission CreateBatchSubmission() => new(
        CurrentBatchId,
        SelectedBatchTemplate!.GroupId,
        BatchInputDirectory,
        BatchOutputDirectory,
        BatchPages.Where(page => page.IsSelected).Select(page => page.PageId).ToArray(),
        IsBatchAutomaticSelection ? BatchSelectionMode.Automatic : BatchSelectionMode.Manual,
        BatchIncludeEditablePsd,
        State.OutputMode);

    private async Task PersistBatchDraftAsync(int generation)
    {
        await _batchDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            if (generation != _batchDraftGeneration
                || _batchInputWorkflow is null
                || !CanScanBatch
                || IsBatchScanning)
            {
                return;
            }

            var projection = await _batchInputWorkflow.SaveDraftAsync(
                CreateBatchSubmission(),
                CancellationToken.None).ConfigureAwait(true);
            if (generation != _batchDraftGeneration)
            {
                return;
            }

            CurrentBatchId = projection.BatchId;
        }
        catch (IOException)
        {
            if (generation == _batchDraftGeneration)
            {
                SetBatchStatus(BatchInputUiStatus.ScanBlocked, "批次草稿暂时无法保存，请检查本地存储后重试。");
            }
        }
        catch (InvalidOperationException exception)
        {
            if (generation == _batchDraftGeneration)
            {
                SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToBatchUserMessage(exception.Message));
            }
        }
        finally
        {
            _batchDraftWrite.Release();
        }
    }

    internal void SetBatchStatus(BatchInputUiStatus status, string message)
    {
        BatchInputState.SetStatus(status, message);
        OnPropertyChanged(nameof(CanScanBatch));
        RaiseBatchCommandCanExecuteChanged();
    }

    private void ReportBatchSwitchFailure(Exception exception)
    {
        BatchSwitchStatusText = exception is InvalidOperationException { Message: "INPUT_BATCH_BUSY" }
            ? "当前批次操作尚未收口，请等待或在当前任务安全暂停后再切换。"
            : "批次页面暂时无法打开，请检查本地模板和存储后重试；未自动扫描或生成。";
        OnPropertyChanged(nameof(BatchSwitchStatusText));
    }

    internal void RefreshBatchSwitchCommands()
    {
        (OpenBatchInputCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestoreBatchInputCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseBatchCommandCanExecuteChanged()
    {
        RefreshBatchSwitchCommands();
        (CancelBatchInputCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChooseBatchInputDirectoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ChooseBatchOutputDirectoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenBatchPageSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ScanBatchInputCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelBatchScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RetryBatchScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string ToBatchUserMessage(string code) => code switch
    {
        "INPUT_PERSISTENCE_FAILURE" =>
            "本地状态未能安全保存，未发送 AI 请求。请重新扫描并建立新批次；源图片未修改。",
        "INPUT_MANIFEST_DATABASE_MISMATCH" =>
            "本地批次清单与保存记录不一致，已阻止 AI 请求。请点击“重试扫描”；系统会使用同一文件夹建立新批次，源图片和 PSD 不会修改。",
        _ => "批次未能安全恢复，请重新选择模板和目录后重试。"
    };

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        switch (e.PropertyName)
        {
            case nameof(BatchInputPageState.SelectedTemplate):
                OnPropertyChanged(nameof(SelectedBatchTemplate));
                break;
            case nameof(BatchInputPageState.InputDirectory):
                OnPropertyChanged(nameof(BatchInputDirectory));
                break;
            case nameof(BatchInputPageState.OutputDirectory):
                OnPropertyChanged(nameof(BatchOutputDirectory));
                break;
            case nameof(BatchInputPageState.OutputMode):
                OnPropertyChanged(nameof(IsBatchStyleSourceOutput));
                OnPropertyChanged(nameof(IsBatchUnifiedOutput));
                OnPropertyChanged(nameof(CanChooseBatchOutputDirectory));
                break;
            case nameof(BatchInputPageState.CanChooseOutputDirectory):
                OnPropertyChanged(nameof(CanChooseBatchOutputDirectory));
                RaiseBatchCommandCanExecuteChanged();
                break;
            case nameof(BatchInputPageState.RecognitionText):
                OnPropertyChanged(nameof(BatchRecognitionText));
                break;
            case nameof(BatchInputPageState.OutputConfirmationText):
                OnPropertyChanged(nameof(BatchOutputConfirmationText));
                break;
            case nameof(BatchInputPageState.StatusMessage):
                OnPropertyChanged(nameof(BatchStatusMessage));
                break;
            case nameof(BatchInputPageState.AutomaticSelection):
                OnPropertyChanged(nameof(IsBatchAutomaticSelection));
                break;
            case nameof(BatchInputPageState.ManualSelection):
                OnPropertyChanged(nameof(IsBatchManualSelection));
                break;
            case nameof(BatchInputPageState.IncludeEditablePsd):
                OnPropertyChanged(nameof(BatchIncludeEditablePsd));
                break;
            case nameof(BatchInputPageState.IsPageSelectionVisible):
                OnPropertyChanged(nameof(IsBatchPageSelectionVisible));
                break;
            case nameof(BatchInputPageState.Busy):
                OnPropertyChanged(nameof(BatchBusy));
                RefreshBatchSwitchCommands();
                break;
            case nameof(BatchInputPageState.CurrentBatchId):
                OnPropertyChanged(nameof(BatchDraftSaveText));
                break;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        State.PropertyChanged -= State_PropertyChanged;
        _batchDraftWrite.Dispose();
        _batchScanCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Cross-page navigation, template library and AI handoff supplied by the shell.</summary>
public interface IBatchInputHost
{
    bool CanSwitchBatch => true;
    IReadOnlyList<TemplateLibraryCardViewModel> TemplateGroups { get; }
    Task LoadTemplateLibraryAsync();
    void ShowBatchInput();
    void ShowWorkbench();
    void CreateTemplate();
    void ImportTemplate();
    Task<bool> EnsureUploadConsentAsync();
    Task StartAiAnalysisAsync(string batchId);
    Task RestoreAiAnalysisAsync(string batchId);
}
