using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Desktop.App;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BatchPageOptionViewModel> _batchPages = [];
    private readonly Dictionary<string, bool> _batchPageSelectionBackup = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _batchDraftWrite = new(1, 1);
    private IFolderPicker? _folderPicker;
    private IBatchInputWorkflow? _batchInputWorkflow;
    private BatchWorkspaceScreen _batchScreen;
    private TemplateLibraryCardViewModel? _selectedBatchTemplate;
    private string _batchInputDirectory = "";
    private string _batchOutputDirectory = "";
    private string _batchRecognitionText = "请选择直接包含商品图片的目录，或包含款式子文件夹的批次目录。";
    private string _batchOutputConfirmationText = "请选择本批次的正式输出目录。";
    private string _batchStatusMessage = "请确认模板、商品目录、输出目录和至少一个页面。";
    private BatchInputUiStatus _batchStatus = BatchInputUiStatus.Draft;
    private bool _isBatchAutomaticSelection = true;
    private bool _batchIncludeEditablePsd;
    private bool _isBatchPageSelectionVisible;
    private bool _batchBusy;
    private bool _isBatchNarrowLayout;
    private string? _currentBatchId;
    private CancellationTokenSource? _batchScanCancellation;
    private bool _applyingBatchProjection;
    private Task _batchTemplateLoadTask = Task.CompletedTask;
    private int _batchDraftGeneration;

    public ObservableCollection<BatchPageOptionViewModel> BatchPages => _batchPages;

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

    public bool IsBatchInputVisible =>
        SelectedNavigationItem.Key == "workbench" && _batchScreen == BatchWorkspaceScreen.BatchInput;

    public TemplateLibraryCardViewModel? SelectedBatchTemplate
    {
        get => _selectedBatchTemplate;
        set
        {
            if (ReferenceEquals(_selectedBatchTemplate, value))
            {
                return;
            }

            _selectedBatchTemplate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BatchTemplateHelperText));
            _batchTemplateLoadTask = LoadBatchTemplatePagesAsync(value);
            MarkBatchDraftChanged();
        }
    }

    public string BatchTemplateHelperText => SelectedBatchTemplate is null
        ? "请先选择一个已校准模板组。"
        : $"已校准 · {BatchPages.Count} 个可用页面";

    public string BatchInputDirectory
    {
        get => _batchInputDirectory;
        private set => SetBatchField(ref _batchInputDirectory, value);
    }

    public string BatchOutputDirectory
    {
        get => _batchOutputDirectory;
        private set => SetBatchField(ref _batchOutputDirectory, value);
    }

    public string BatchInputDirectoryDisplay => string.IsNullOrWhiteSpace(BatchInputDirectory)
        ? "尚未选择商品目录"
        : $"已选择：{DirectoryDisplayName(BatchInputDirectory)}";

    public string BatchOutputDirectoryDisplay => string.IsNullOrWhiteSpace(BatchOutputDirectory)
        ? "尚未选择正式输出目录"
        : $"已选择：{DirectoryDisplayName(BatchOutputDirectory)}";

    public string BatchRecognitionText
    {
        get => _batchRecognitionText;
        private set => SetBatchField(ref _batchRecognitionText, value);
    }

    public string BatchOutputConfirmationText
    {
        get => _batchOutputConfirmationText;
        private set => SetBatchField(ref _batchOutputConfirmationText, value);
    }

    public string BatchStatusMessage
    {
        get => _batchStatusMessage;
        private set => SetBatchField(ref _batchStatusMessage, value);
    }

    public string BatchStatusState => _batchStatus.ToString();

    public bool IsBatchScanning => _batchStatus == BatchInputUiStatus.Scanning;

    public bool IsBatchScanReady => _batchStatus == BatchInputUiStatus.ScanReady;

    public bool IsBatchScanBlocked => _batchStatus == BatchInputUiStatus.ScanBlocked;

    public bool HasBatchRetry => IsBatchScanBlocked && !BatchBusy;

    public string BatchPrimaryActionText => IsBatchScanReady ? "扫描完成" : "扫描并分析";

    public bool IsBatchConfigurationEditable => !BatchBusy && !IsBatchScanReady;

    public bool IsBatchAutomaticSelection
    {
        get => _isBatchAutomaticSelection;
        set
        {
            if (!SetBatchField(ref _isBatchAutomaticSelection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsBatchManualSelection));
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
        get => _batchIncludeEditablePsd;
        set
        {
            if (SetBatchField(ref _batchIncludeEditablePsd, value))
            {
                OnPropertyChanged(nameof(BatchOutputPreviewSummary));
                MarkBatchDraftChanged();
            }
        }
    }

    public bool IsBatchPageSelectionVisible
    {
        get => _isBatchPageSelectionVisible;
        private set => SetBatchField(ref _isBatchPageSelectionVisible, value);
    }

    public bool BatchBusy
    {
        get => _batchBusy;
        private set
        {
            if (SetBatchField(ref _batchBusy, value))
            {
                OnPropertyChanged(nameof(HasBatchRetry));
                OnPropertyChanged(nameof(CanScanBatch));
                OnPropertyChanged(nameof(IsBatchConfigurationEditable));
                RaiseBatchCommandCanExecuteChanged();
            }
        }
    }

    public bool IsBatchNarrowLayout => _isBatchNarrowLayout;

    public int SelectedBatchPageCount => BatchPages.Count(page => page.IsSelected);

    public bool? AreAllBatchPagesSelected
    {
        get
        {
            if (BatchPages.Count == 0 || SelectedBatchPageCount == 0)
            {
                return false;
            }

            return SelectedBatchPageCount == BatchPages.Count ? true : null;
        }
        set
        {
            if (!value.HasValue)
            {
                return;
            }

            foreach (var page in BatchPages)
            {
                page.IsSelected = value.Value;
            }

            NotifyBatchPageSelectionChanged();
        }
    }

    public string BatchPageSelectionSummary => $"已选择 {SelectedBatchPageCount}/{BatchPages.Count} 个页面";

    public IReadOnlyList<string> BatchOutputPreviewFiles => BatchPages
        .Where(page => page.IsSelected)
        .Select((page, index) => $"{index + 1:D2}-{page.Name}.jpg")
        .ToArray();

    public string BatchOutputPreviewSummary => BatchIncludeEditablePsd
        ? $"每款独立目录 · {SelectedBatchPageCount} 个 JPG + 同名可编辑 PSD"
        : $"每款独立目录 · {SelectedBatchPageCount} 个 JPG";

    public string BatchDraftSaveText => _currentBatchId is null ? "新批次草稿" : "草稿已自动保存";

    public bool CanScanBatch => IsBatchConfigurationEditable
        && SelectedBatchTemplate is not null
        && !string.IsNullOrWhiteSpace(BatchInputDirectory)
        && !string.IsNullOrWhiteSpace(BatchOutputDirectory)
        && SelectedBatchPageCount > 0;

    private void InitializeBatchInput(IFolderPicker? folderPicker, IBatchInputWorkflow? batchInputWorkflow)
    {
        _folderPicker = folderPicker;
        _batchInputWorkflow = batchInputWorkflow;
        OpenBatchInputCommand = new AsyncRelayCommand(OpenBatchInputAsync);
        RestoreBatchInputCommand = new AsyncRelayCommand(RestoreBatchInputAsync);
        CancelBatchInputCommand = new RelayCommand(CloseBatchInput, () => !IsBatchScanning);
        ChooseBatchInputDirectoryCommand = new AsyncRelayCommand(
            ChooseBatchInputDirectoryAsync,
            () => IsBatchConfigurationEditable);
        ChooseBatchOutputDirectoryCommand = new AsyncRelayCommand(
            ChooseBatchOutputDirectoryAsync,
            () => IsBatchConfigurationEditable);
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
        BatchPages.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
            {
                foreach (BatchPageOptionViewModel page in args.NewItems)
                {
                    page.PropertyChanged += BatchPage_PropertyChanged;
                }
            }

            if (args.OldItems is not null)
            {
                foreach (BatchPageOptionViewModel page in args.OldItems)
                {
                    page.PropertyChanged -= BatchPage_PropertyChanged;
                }
            }

            NotifyBatchPageSelectionChanged();
        };
    }

    private Task OpenBatchInputAsync() => OpenBatchInputAsync(restoreLatest: false);

    private Task RestoreBatchInputAsync() => OpenBatchInputAsync(restoreLatest: true);

    private async Task OpenBatchInputAsync(bool restoreLatest)
    {
        _batchScreen = BatchWorkspaceScreen.BatchInput;
        NotifyBatchScreenChanged();
        try
        {
            await LoadTemplateLibraryAsync().ConfigureAwait(true);
            var restored = !restoreLatest || _batchInputWorkflow is null
                ? null
                : await _batchInputWorkflow.RestoreLatestDraftAsync(CancellationToken.None).ConfigureAwait(true);
            _applyingBatchProjection = true;
            if (!restoreLatest || restored is null)
            {
                ResetBatchDraftForNewInput();
            }

            var targetTemplate = TemplateGroups.FirstOrDefault(group =>
                group.HasCommittedRevision
                && string.Equals(
                    group.GroupId,
                    restored?.TemplateGroupId ?? SelectedBatchTemplate?.GroupId,
                    StringComparison.Ordinal));
            if (restored is null)
            {
                targetTemplate ??= TemplateGroups.FirstOrDefault(group => group.HasCommittedRevision);
            }

            if (!ReferenceEquals(SelectedBatchTemplate, targetTemplate))
            {
                SelectedBatchTemplate = targetTemplate;
            }

            await _batchTemplateLoadTask.ConfigureAwait(true);
            if (restored is not null)
            {
                ApplyBatchProjection(restored);
                if (restored.Status == BatchInputUiStatus.ScanReady && _aiBatchWorkflow is not null)
                {
                    var recoveredAnalysis = await _aiBatchWorkflow.RefreshAsync(
                        restored.BatchId,
                        CancellationToken.None).ConfigureAwait(true);
                    if (recoveredAnalysis is not null)
                    {
                        ApplyAiProjection(recoveredAnalysis);
                        _batchScreen = BatchWorkspaceScreen.AiAnalysis;
                        NotifyBatchScreenChanged();
                    }
                }
            }
        }
        catch (IOException)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, "批次草稿暂时无法恢复，请检查本地存储后重试。");
        }
        catch (InvalidOperationException exception)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToBatchUserMessage(exception.Message));
        }
        finally
        {
            _applyingBatchProjection = false;
        }
    }

    private void CloseBatchInput()
    {
        IsBatchPageSelectionVisible = false;
        BeginFreshBatchDraft();
        _batchScreen = BatchWorkspaceScreen.Workbench;
        NotifyBatchScreenChanged();
    }

    private void ResetBatchDraftForNewInput()
    {
        BeginFreshBatchDraft();
        BatchInputDirectory = "";
        BatchOutputDirectory = "";
        BatchRecognitionText = "请选择直接包含商品图片的目录，或包含款式子文件夹的批次目录。";
        BatchOutputConfirmationText = "请选择本批次的正式输出目录。";
        IsBatchAutomaticSelection = true;
        BatchIncludeEditablePsd = false;
        OnPropertyChanged(nameof(BatchDraftSaveText));
        OnPropertyChanged(nameof(BatchInputDirectoryDisplay));
        OnPropertyChanged(nameof(BatchOutputDirectoryDisplay));
        SetBatchStatus(BatchInputUiStatus.Draft, "请确认模板、商品目录、输出目录和至少一个页面。");
    }

    private void BeginFreshBatchDraft()
    {
        // A retry/new-batch action must never reuse a batch whose manifest or
        // SQLite graph was already classified as inconsistent.  Incrementing
        // the generation also makes an older fire-and-forget draft save a
        // no-op when it completes after the UI has moved on.
        _batchDraftGeneration++;
        _currentBatchId = null;
        OnPropertyChanged(nameof(BatchDraftSaveText));
    }

    private void CreateTemplateFromBatch()
    {
        SelectedNavigationItem = NavigationItems.First(item => item.Key == "templates");
        NewTemplateCommand.Execute(null);
    }

    private void ImportTemplateFromBatch()
    {
        SelectedNavigationItem = NavigationItems.First(item => item.Key == "templates");
        ImportTemplatePackageCommand.Execute(null);
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

            OnPropertyChanged(nameof(BatchTemplateHelperText));
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
        OnPropertyChanged(nameof(BatchInputDirectoryDisplay));
        BatchRecognitionText = "目录已选择；扫描时只读取该目录及其一级子目录。";
        MarkBatchDraftChanged();
    }

    private async Task ChooseBatchOutputDirectoryAsync()
    {
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
        OnPropertyChanged(nameof(BatchOutputDirectoryDisplay));
        BatchOutputConfirmationText = "已确认为本次正式输出目录；软件不会清理目录内其他文件。";
        MarkBatchDraftChanged();
    }

    private void OpenBatchPageSelection()
    {
        _batchPageSelectionBackup.Clear();
        foreach (var page in BatchPages)
        {
            _batchPageSelectionBackup[page.PageId] = page.IsSelected;
        }

        IsBatchPageSelectionVisible = true;
    }

    private void ApplyBatchPageSelection()
    {
        IsBatchPageSelectionVisible = false;
        _batchPageSelectionBackup.Clear();
        MarkBatchDraftChanged();
    }

    private void CancelBatchPageSelection()
    {
        foreach (var page in BatchPages)
        {
            if (_batchPageSelectionBackup.TryGetValue(page.PageId, out var selected))
            {
                page.IsSelected = selected;
            }
        }

        IsBatchPageSelectionVisible = false;
        _batchPageSelectionBackup.Clear();
    }

    private async Task ScanBatchInputAsync()
    {
        if (!CanScanBatch)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, SelectedBatchPageCount == 0
                ? "至少选择 1 个页面才能扫描并分析。"
                : "请先确认模板、商品目录和正式输出目录。");
            return;
        }

        if (!await EnsureAiUploadConsentForScanAsync().ConfigureAwait(true))
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
            if (result.Status == BatchInputUiStatus.ScanReady && _aiBatchWorkflow is not null)
            {
                await StartAiAnalysisAsync(result.BatchId).ConfigureAwait(true);
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
            _currentBatchId = projection.BatchId;
            OnPropertyChanged(nameof(BatchDraftSaveText));
            BatchInputDirectory = projection.InputDirectory;
            BatchOutputDirectory = projection.OutputDirectory;
            OnPropertyChanged(nameof(BatchInputDirectoryDisplay));
            OnPropertyChanged(nameof(BatchOutputDirectoryDisplay));
            IsBatchAutomaticSelection = projection.SelectionMode == BatchSelectionMode.Automatic;
            BatchIncludeEditablePsd = projection.IncludeEditablePsd;
            BatchRecognitionText = projection.RecognitionText;
            BatchOutputConfirmationText = string.IsNullOrWhiteSpace(projection.OutputDirectory)
                ? "请选择本批次的正式输出目录。"
                : "已确认为本次正式输出目录；软件不会清理目录内其他文件。";

            if (projection.SelectedPageIds.Count > 0)
            {
                foreach (var page in BatchPages)
                {
                    page.IsSelected = projection.SelectedPageIds.Contains(page.PageId, StringComparer.Ordinal);
                }
            }

            SetBatchStatus(projection.Status, projection.StatusMessage);
        }
        finally
        {
            _applyingBatchProjection = wasApplyingProjection;
        }
    }

    private void OnBatchNavigationChanged()
    {
        // Navigation is a shell concern, not a batch-workflow reset.  Keep the
        // active screen, batch id, selected style and replacement state intact
        // while the user visits Settings/Templates/Tasks; returning to the
        // workbench must show the exact page that was left.
        UpdateAiAutoRefresh();
        OnPropertyChanged(nameof(IsBatchInputVisible));
        OnPropertyChanged(nameof(IsAiAnalysisVisible));
        OnPropertyChanged(nameof(IsAiStyleReviewVisible));
        OnPropertyChanged(nameof(IsResultOverviewVisible));
        OnPropertyChanged(nameof(IsResultDetailVisible));
    }

    private void SetBatchWindowWidth(double width)
    {
        var narrow = width <= 1180;
        if (_isBatchNarrowLayout == narrow)
        {
            return;
        }

        _isBatchNarrowLayout = narrow;
        OnPropertyChanged(nameof(IsBatchNarrowLayout));
        OnPropertyChanged(nameof(AiSummaryHeight));
        OnPropertyChanged(nameof(AiSummaryColumnCount));
        OnPropertyChanged(nameof(AiStyleRowHeight));
        OnPropertyChanged(nameof(AiStyleReviewHeight));
        OnPropertyChanged(nameof(AiRequirementRowHeight));
        OnPropertyChanged(nameof(AiAssetTileHeight));
        OnPropertyChanged(nameof(AiZeroOutputPanelWidth));
    }

    private void MarkBatchDraftChanged()
    {
        if (_batchStatus != BatchInputUiStatus.Scanning)
        {
            SetBatchStatus(BatchInputUiStatus.Draft, CanScanBatch
                ? "输入、输出和页面已确认，可以开始只读扫描。"
                : "请确认模板、商品目录、输出目录和至少一个页面。");
        }

        RaiseBatchCommandCanExecuteChanged();
        if (!_applyingBatchProjection
            && !IsBatchPageSelectionVisible
            && CanScanBatch
            && _batchInputWorkflow is not null)
        {
            _ = PersistBatchDraftAsync(_batchDraftGeneration);
        }
    }

    private BatchInputSubmission CreateBatchSubmission() => new(
        _currentBatchId,
        SelectedBatchTemplate!.GroupId,
        BatchInputDirectory,
        BatchOutputDirectory,
        BatchPages.Where(page => page.IsSelected).Select(page => page.PageId).ToArray(),
        IsBatchAutomaticSelection ? BatchSelectionMode.Automatic : BatchSelectionMode.Manual,
        BatchIncludeEditablePsd);

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

            _currentBatchId = projection.BatchId;
            OnPropertyChanged(nameof(BatchDraftSaveText));
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

    private void BatchPage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchPageOptionViewModel.IsSelected))
        {
            NotifyBatchPageSelectionChanged();
        }
    }

    private void NotifyBatchPageSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBatchPageCount));
        OnPropertyChanged(nameof(BatchPageSelectionSummary));
        OnPropertyChanged(nameof(AreAllBatchPagesSelected));
        OnPropertyChanged(nameof(CanScanBatch));
        OnPropertyChanged(nameof(BatchTemplateHelperText));
        OnPropertyChanged(nameof(BatchOutputPreviewFiles));
        OnPropertyChanged(nameof(BatchOutputPreviewSummary));
        RaiseBatchCommandCanExecuteChanged();
    }

    private void NotifyBatchScreenChanged()
    {
        OnPropertyChanged(nameof(IsWorkbenchVisible));
        OnPropertyChanged(nameof(IsBatchInputVisible));
        OnPropertyChanged(nameof(IsAiAnalysisVisible));
        OnPropertyChanged(nameof(IsAiStyleReviewVisible));
        OnPropertyChanged(nameof(IsResultOverviewVisible));
        OnPropertyChanged(nameof(IsResultDetailVisible));
        OnPropertyChanged(nameof(IsPlaceholderVisible));
        UpdateAiAutoRefresh();
    }

    private void SetBatchStatus(BatchInputUiStatus status, string message)
    {
        _batchStatus = status;
        BatchStatusMessage = message;
        OnPropertyChanged(nameof(BatchStatusState));
        OnPropertyChanged(nameof(IsBatchScanning));
        OnPropertyChanged(nameof(IsBatchScanReady));
        OnPropertyChanged(nameof(IsBatchScanBlocked));
        OnPropertyChanged(nameof(HasBatchRetry));
        OnPropertyChanged(nameof(BatchPrimaryActionText));
        OnPropertyChanged(nameof(CanScanBatch));
        OnPropertyChanged(nameof(IsBatchConfigurationEditable));
        RaiseBatchCommandCanExecuteChanged();
    }

    private void RaiseBatchCommandCanExecuteChanged()
    {
        (CancelBatchInputCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChooseBatchInputDirectoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ChooseBatchOutputDirectoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenBatchPageSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ScanBatchInputCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelBatchScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RetryBatchScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool SetBatchField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private static string ToBatchUserMessage(string code) => code switch
    {
        "INPUT_PERSISTENCE_FAILURE" =>
            "本地状态未能安全保存，未发送 AI 请求。请重新扫描并建立新批次；源图片未修改。",
        "INPUT_MANIFEST_DATABASE_MISMATCH" =>
            "本地批次清单与数据库记录不一致，已阻止 AI 请求。请点击“重试扫描”；系统会使用同一文件夹建立新批次，源图片和 PSD 不会修改。",
        _ => "批次未能安全恢复，请重新选择模板和目录后重试。"
    };

    private static string DirectoryDisplayName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "本地目录" : name;
    }

    private enum BatchWorkspaceScreen
    {
        Workbench,
        BatchInput,
        AiAnalysis,
        AiStyleReview,
        ResultOverview,
        ResultDetail
    }
}

public enum BatchInputUiStatus
{
    Draft,
    Scanning,
    ScanReady,
    ScanBlocked
}

public enum BatchSelectionMode
{
    Automatic,
    Manual
}

public sealed record BatchInputSubmission(
    string? BatchId,
    string TemplateGroupId,
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<string> SelectedPageIds,
    BatchSelectionMode SelectionMode,
    bool IncludeEditablePsd);

public sealed record BatchInputProjection(
    string BatchId,
    string? TemplateGroupId,
    BatchInputUiStatus Status,
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<string> SelectedPageIds,
    BatchSelectionMode SelectionMode,
    bool IncludeEditablePsd,
    string RecognitionText,
    string StatusMessage);

public interface IBatchInputWorkflow
{
    Task<BatchInputProjection?> RestoreLatestDraftAsync(CancellationToken cancellationToken);

    Task<BatchInputProjection> ScanAsync(
        BatchInputSubmission submission,
        CancellationToken cancellationToken);

    Task<BatchInputProjection> SaveDraftAsync(
        BatchInputSubmission submission,
        CancellationToken cancellationToken);
}

public sealed class BatchPageOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public BatchPageOptionViewModel(string pageId, string name, string meta, bool isSelected)
    {
        PageId = pageId;
        Name = name;
        Meta = meta;
        _isSelected = isSelected;
    }

    public string PageId { get; }

    public string Name { get; }

    public string Meta { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
