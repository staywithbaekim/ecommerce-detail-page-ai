using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Templates;
using Microsoft.Win32;

namespace Desktop.App;

public sealed partial class MainWindowViewModel
{
    private readonly SemaphoreSlim _templateDraftWrite = new(1, 1);
    private ITemplateWorkspaceService? _templateWorkspace;
    private ITemplateCalibrationService? _templateCalibration;
    private ITemplatePackageService? _templatePackages;
    private TemplateScreen _templateScreen = TemplateScreen.Library;
    private TemplateLibraryCardViewModel? _selectedTemplate;
    private TemplateGroupDetails? _templateDetails;
    private TemplateDraftSession? _templateDraft;
    private TemplatePageRowViewModel? _selectedTemplatePage;
    private PendingTemplateImport? _pendingTemplateImport;
    private string _templateGroupName = "新模板组";
    private string _templateStatusMessage = "";
    private bool _templateBusy;
    private bool _duplicateReviewVisible;
    private bool _isCalibrationNarrowLayout;
    private CalibrationNarrowPanel _calibrationNarrowPanel = CalibrationNarrowPanel.Canvas;

    public ObservableCollection<TemplateLibraryCardViewModel> TemplateGroups { get; } = [];

    public ObservableCollection<ImportedPsdRowViewModel> ImportedTemplateFiles { get; } = [];

    public ObservableCollection<TemplatePageRowViewModel> TemplatePages { get; } = [];

    public ICommand RefreshTemplatesCommand { get; private set; } = null!;

    public ICommand NewTemplateCommand { get; private set; } = null!;

    public ICommand OpenTemplateCommand { get; private set; } = null!;

    public ICommand BackToTemplateLibraryCommand { get; private set; } = null!;

    public ICommand AddTemplatePagesCommand { get; private set; } = null!;

    public ICommand ContinuePageSettingsCommand { get; private set; } = null!;

    public ICommand ReturnToTemplateImportCommand { get; private set; } = null!;

    public ICommand ReturnToPageSettingsCommand { get; private set; } = null!;

    public ICommand ExitCalibrationCommand { get; private set; } = null!;

    public ICommand RemoveTemplatePageCommand { get; private set; } = null!;

    public ICommand AbandonTemplateDraftCommand { get; private set; } = null!;

    public ICommand KeepExistingDuplicateCommand { get; private set; } = null!;

    public ICommand KeepImportedDuplicateCommand { get; private set; } = null!;

    public ICommand CancelDuplicateCommand { get; private set; } = null!;

    public ICommand StartCalibrationCommand { get; private set; } = null!;

    public ICommand SelectCalibrationAssetCommand { get; private set; } = null!;

    public ICommand ClearCalibrationAssetCommand { get; private set; } = null!;

    public ICommand UndoCalibrationCommand { get; private set; } = null!;

    public ICommand RedoCalibrationCommand { get; private set; } = null!;

    public ICommand ApplyCalibrationGeometryCommand { get; private set; } = null!;

    public ICommand SaveTemplateCommand { get; private set; } = null!;

    public ICommand ExportTemplateCommand { get; private set; } = null!;

    public ICommand ImportTemplatePackageCommand { get; private set; } = null!;

    public ICommand SaveTemplateAsCommand { get; private set; } = null!;

    public ICommand DeleteTemplateCommand { get; private set; } = null!;

    public ICommand ShowCalibrationPagesCommand { get; private set; } = null!;

    public ICommand ShowCalibrationCanvasCommand { get; private set; } = null!;

    public ICommand ShowCalibrationInspectorCommand { get; private set; } = null!;

    public bool IsTemplateLibraryVisible => _templateScreen == TemplateScreen.Library;

    public bool IsTemplateDetailsVisible => _templateScreen == TemplateScreen.Details;

    public bool IsTemplateImportVisible => _templateScreen == TemplateScreen.Import;

    public bool IsTemplatePageSettingsVisible => _templateScreen == TemplateScreen.PageSettings;

    public bool IsTemplateCalibrationPendingVisible => _templateScreen == TemplateScreen.CalibrationPending;

    public bool IsTemplateCalibrationVisible => _templateScreen == TemplateScreen.Calibration;

    public bool IsCalibrationNarrowLayout => _isCalibrationNarrowLayout;

    public bool IsCalibrationPagePanelVisible => !_isCalibrationNarrowLayout
        || _calibrationNarrowPanel == CalibrationNarrowPanel.Pages;

    public bool IsCalibrationCanvasPanelVisible => !_isCalibrationNarrowLayout
        || _calibrationNarrowPanel == CalibrationNarrowPanel.Canvas;

    public bool IsCalibrationInspectorPanelVisible => !_isCalibrationNarrowLayout
        || _calibrationNarrowPanel == CalibrationNarrowPanel.Inspector;

    public GridLength CalibrationPageColumnWidth => IsCalibrationPagePanelVisible
        ? _isCalibrationNarrowLayout ? new GridLength(1, GridUnitType.Star) : new GridLength(224)
        : new GridLength(0);

    public GridLength CalibrationCanvasColumnWidth => IsCalibrationCanvasPanelVisible
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(0);

    public GridLength CalibrationInspectorColumnWidth => IsCalibrationInspectorPanelVisible
        ? _isCalibrationNarrowLayout ? new GridLength(1, GridUnitType.Star) : new GridLength(260)
        : new GridLength(0);

    public GridLength CalibrationPanelGapWidth => _isCalibrationNarrowLayout
        ? new GridLength(0)
        : new GridLength(16);

    public TemplateCalibrationDraftView? CalibrationDraft { get; private set; }

    public string? CalibrationPreviewImagePath => CalibrationDraft?.SelectedAssetId is { } assetId
        ? CalibrationDraft.Assets.FirstOrDefault(asset => string.Equals(
            asset.AssetId,
            assetId,
            StringComparison.Ordinal))?.SourceReference
        : null;

    public string? CalibrationTemplatePreviewImagePath => CalibrationDraft?.TemplatePreviewSourceReference;

    public string CalibrationAssetActionText => SelectedCalibrationFrame?.IsRegistered == true
        ? "更换素材"
        : "选择素材";

    public string CalibrationHeaderTitle
    {
        get
        {
            var index = SelectedTemplatePage is null ? -1 : TemplatePages.IndexOf(SelectedTemplatePage);
            return index < 0 ? "校准" : $"校准 {index + 1}/{TemplatePages.Count}";
        }
    }

    public bool CanEditCalibration => CalibrationDraft is { IsReadOnly: false, IsBlocked: false };

    public bool CanClearCalibrationAsset => CanEditCalibration
        && SelectedCalibrationFrame is { } frame
        && (frame.IsRegistered || frame.HasPersistedBinding);

    public double CalibrationCanvasWidth => CalibrationDraft?.CanvasWidth ?? 790;

    public double CalibrationCanvasHeight => CalibrationDraft?.CanvasHeight ?? 1252;

    public double CalibrationFrameLeft => SelectedCalibrationFrame?.Left ?? 0;

    public double CalibrationFrameTop => SelectedCalibrationFrame?.Top ?? 0;

    public double CalibrationFrameWidth => SelectedCalibrationFrame?.Width ?? 1;

    public double CalibrationFrameHeight => SelectedCalibrationFrame?.Height ?? 1;

    public string CalibrationFrameDisplayName => SelectedCalibrationFrame is { } frame
        ? string.IsNullOrWhiteSpace(frame.LayerName) ? "当前图片框" : frame.LayerName
        : "未选择";

    private TemplateFrameProjection? SelectedCalibrationFrame => CalibrationDraft?.Frames.FirstOrDefault(frame =>
        string.Equals(frame.FrameId, CalibrationFrameIdInput, StringComparison.Ordinal));

    private string _calibrationAssetIdInput = "";
    private string _calibrationFrameIdInput = "";
    private double _calibrationScaleInput = 1;
    private double _calibrationOffsetXInput;
    private double _calibrationOffsetYInput;
    private string _calibrationRequirementInput = "";
    private double _calibrationViewportZoom = 1;

    public string CalibrationAssetIdInput
    {
        get => _calibrationAssetIdInput;
        set => SetTemplateField(ref _calibrationAssetIdInput, value);
    }

    public string CalibrationFrameIdInput
    {
        get => _calibrationFrameIdInput;
        set
        {
            if (SetTemplateField(ref _calibrationFrameIdInput, value))
            {
                NotifyCalibrationFrameChanged();
            }
        }
    }

    public double CalibrationScaleInput
    {
        get => _calibrationScaleInput;
        set => SetTemplateField(ref _calibrationScaleInput, value);
    }

    public double CalibrationOffsetXInput
    {
        get => _calibrationOffsetXInput;
        set => SetTemplateField(ref _calibrationOffsetXInput, value);
    }

    public double CalibrationOffsetYInput
    {
        get => _calibrationOffsetYInput;
        set => SetTemplateField(ref _calibrationOffsetYInput, value);
    }

    public string CalibrationRequirementInput
    {
        get => _calibrationRequirementInput;
        set => SetTemplateField(ref _calibrationRequirementInput, value);
    }

    public double CalibrationViewportZoom
    {
        get => _calibrationViewportZoom;
        private set
        {
            if (SetTemplateField(ref _calibrationViewportZoom, Math.Clamp(value, 0.5, 2.5)))
            {
                OnPropertyChanged(nameof(CalibrationZoomText));
            }
        }
    }

    public string CalibrationZoomText => $"画布 {Math.Round(CalibrationViewportZoom * 100):0}%";

    public void AdjustCalibrationViewportZoom(int wheelDelta)
    {
        if (wheelDelta == 0)
        {
            return;
        }

        CalibrationViewportZoom *= wheelDelta > 0 ? 1.1 : 0.9;
    }

    public bool HasTemplateGroups => TemplateGroups.Count > 0;

    public bool IsTemplateLibraryEmpty => !TemplateBusy && !HasTemplateGroups;

    public bool HasImportedTemplateFiles => ImportedTemplateFiles.Count > 0;

    public bool CanContinuePageSettings => HasImportedTemplateFiles && !TemplateBusy;

    public bool IsIncrementalTemplateDraft => _templateDraft?.IsIncremental == true;

    public bool IsDuplicateReviewVisible
    {
        get => _duplicateReviewVisible;
        private set => SetTemplateField(ref _duplicateReviewVisible, value);
    }

    public bool TemplateBusy
    {
        get => _templateBusy;
        private set
        {
            if (SetTemplateField(ref _templateBusy, value))
            {
                OnPropertyChanged(nameof(IsTemplateLibraryEmpty));
                OnPropertyChanged(nameof(CanContinuePageSettings));
                RaiseTemplateCommandCanExecuteChanged();
            }
        }
    }

    public string TemplateGroupName
    {
        get => _templateGroupName;
        set => SetTemplateField(ref _templateGroupName, value);
    }

    public string TemplateStatusMessage
    {
        get => _templateStatusMessage;
        private set => SetTemplateField(ref _templateStatusMessage, value);
    }

    public string TemplateFlowTitle => IsIncrementalTemplateDraft ? "添加模板页面" : "新建模板组";

    public string TemplateFlowSubtitle => IsIncrementalTemplateDraft
        ? $"{TemplateGroupName} · 只设置和校准新增页面"
        : "导入一个或多个 PSD，并选择首次校准方式";

    public string TemplateSettingsSubtitle => IsIncrementalTemplateDraft
        ? $"{TemplateGroupName} · 原页面规则保持"
        : $"{TemplateGroupName} · 新建模板组";

    public string TemplateSettingsScope => IsIncrementalTemplateDraft ? "仅设置新增页面" : "当前选中页面";

    public string DuplicateExistingLabel => _pendingTemplateImport?.DuplicateResourceId is null
        ? "保留已有页面"
        : $"保留已有页面 · {_pendingTemplateImport.DuplicateResourceId}";

    public string DuplicateImportedLabel => _pendingTemplateImport?.PageName is null
        ? "保留本次导入"
        : $"保留本次导入 · {_pendingTemplateImport.PageName}.psd";

    public string TemplateDetailsTitle => _templateDetails?.Name ?? "模板详情";

    public string TemplateDetailsMeta => _templateDetails is null
        ? ""
        : $"{_templateDetails.Pages.Count} 个页面 · 修订 {_templateDetails.RevisionNo} · "
        + $"{(_templateDetails.IsCommitted ? "可用" : "草稿")} · {_templateDetails.UpdatedAt:MM-dd HH:mm} 更新";

    public bool CanIncrementTemplate => _templateDetails?.IsCommitted == true;

    public IReadOnlyList<TemplatePageDetailsRowViewModel> TemplateDetailPages => _templateDetails?.Pages
        .OrderBy(page => page.Order)
        .Select(page => new TemplatePageDetailsRowViewModel(page))
        .ToArray() ?? [];

    public TemplatePageRowViewModel? SelectedTemplatePage
    {
        get => _selectedTemplatePage;
        set
        {
            if (SetTemplateField(ref _selectedTemplatePage, value))
            {
                OnPropertyChanged(nameof(CalibrationHeaderTitle));
                OnPropertyChanged(nameof(CalibrationFrameDisplayName));
                if (_templateScreen == TemplateScreen.Calibration && value is not null)
                {
                    _ = LoadCalibrationPageAsync(value);
                }
            }
        }
    }

    public TemplateLibraryCardViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set => SetTemplateField(ref _selectedTemplate, value);
    }

    public async Task ImportPsdFilesAsync(IReadOnlyList<string> paths)
    {
        if (_templateWorkspace is null || paths.Count == 0 || TemplateBusy)
        {
            return;
        }

        TemplateBusy = true;
        TemplateStatusMessage = "";
        try
        {
            _templateDraft ??= await _templateWorkspace.BeginCreateAsync(
                TemplateGroupName,
                CancellationToken.None).ConfigureAwait(true);
            NotifyTemplateDraftChanged();
            foreach (var path in paths)
            {
                var pending = new PendingTemplateImport(
                    $"command_{Guid.NewGuid():N}",
                    $"resource_{Guid.NewGuid():N}",
                    path,
                    Path.GetFileNameWithoutExtension(path),
                    DuplicateResourceId: null);
                if (!await ImportPendingAsync(pending, TemplateDuplicateDecision.NotSpecified).ConfigureAwait(true))
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    public async Task MoveTemplatePageAsync(
        TemplatePageRowViewModel source,
        TemplatePageRowViewModel target)
    {
        if (_templateWorkspace is null || _templateDraft is null || ReferenceEquals(source, target))
        {
            return;
        }

        var sourceIndex = TemplatePages.IndexOf(source);
        var targetIndex = TemplatePages.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        TemplatePages.Move(sourceIndex, targetIndex);
        UpdatePageOrderLabels();
        await SavePageOrderAsync().ConfigureAwait(true);
    }

    public async Task BindCalibrationAssetFileAsync(string sourcePath)
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            CalibrationDraft = await _templateCalibration.BindReadOnlyAssetAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                sourcePath,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            TemplateStatusMessage = "代表款素材已只读登记并绑定到当前图片框。";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    public async Task BindCalibrationAssetIdAsync(string assetId)
    {
        CalibrationAssetIdInput = assetId;
        await SelectCalibrationAssetAsync().ConfigureAwait(true);
    }

    public Task CommitCalibrationGeometryAsync() => ApplyCalibrationGeometryAsync();

    public void SelectCalibrationFrame(string frameId)
    {
        if (CalibrationDraft?.Frames.Any(frame => string.Equals(
                frame.FrameId,
                frameId,
                StringComparison.Ordinal)) != true)
        {
            return;
        }

        CalibrationFrameIdInput = frameId;
        var frame = CalibrationDraft.Frames.First(candidate => string.Equals(
            candidate.FrameId,
            frameId,
            StringComparison.Ordinal));
        CalibrationAssetIdInput = frame.IsRegistered
            ? CalibrationDraft.Assets.FirstOrDefault(asset => string.Equals(
                asset.AssetId,
                CalibrationDraft.SelectedAssetId,
                StringComparison.Ordinal))?.AssetId ?? CalibrationAssetIdInput
            : CalibrationAssetIdInput;
        UpdateCalibrationInputs(CalibrationDraft with { SelectedFrameId = frameId });
    }

    public void SetWindowWidth(double width)
    {
        var narrow = width <= 1180;
        if (_isCalibrationNarrowLayout == narrow)
        {
            return;
        }

        _isCalibrationNarrowLayout = narrow;
        NotifyCalibrationLayoutChanged();
    }

    private void InitializeTemplates(
        ITemplateWorkspaceService? templateWorkspace,
        ITemplateCalibrationService? templateCalibration,
        ITemplatePackageService? templatePackages)
    {
        _templateWorkspace = templateWorkspace;
        _templateCalibration = templateCalibration;
        _templatePackages = templatePackages;
        RefreshTemplatesCommand = new AsyncRelayCommand(LoadTemplateLibraryAsync, () => !TemplateBusy);
        NewTemplateCommand = new RelayCommand(BeginNewTemplate);
        OpenTemplateCommand = new AsyncRelayCommand<TemplateLibraryCardViewModel>(OpenTemplateAsync);
        BackToTemplateLibraryCommand = new AsyncRelayCommand(BackToLibraryAsync);
        AddTemplatePagesCommand = new AsyncRelayCommand(BeginIncrementAsync, () => _templateDetails is not null);
        ContinuePageSettingsCommand = new AsyncRelayCommand(ContinuePageSettingsAsync, () => CanContinuePageSettings);
        ReturnToTemplateImportCommand = new RelayCommand(() => SetTemplateScreen(TemplateScreen.Import));
        ReturnToPageSettingsCommand = new RelayCommand(() => SetTemplateScreen(TemplateScreen.PageSettings));
        ExitCalibrationCommand = new AsyncRelayCommand(ExitCalibrationAsync);
        RemoveTemplatePageCommand = new AsyncRelayCommand<TemplatePageRowViewModel>(RemoveTemplatePageAsync);
        AbandonTemplateDraftCommand = new AsyncRelayCommand(AbandonTemplateDraftAsync);
        KeepExistingDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.ReuseExisting));
        KeepImportedDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.ContinueAsSeparate));
        CancelDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.Cancel));
        StartCalibrationCommand = new AsyncRelayCommand(StartCalibrationAsync, () => TemplatePages.Count > 0);
        SelectCalibrationAssetCommand = new AsyncRelayCommand(SelectCalibrationAssetAsync);
        ClearCalibrationAssetCommand = new AsyncRelayCommand(ClearCalibrationAssetAsync, () => CanClearCalibrationAsset);
        UndoCalibrationCommand = new AsyncRelayCommand(() => ApplyCalibrationHistoryAsync(CalibrationUndoAction.Undo));
        RedoCalibrationCommand = new AsyncRelayCommand(() => ApplyCalibrationHistoryAsync(CalibrationUndoAction.Redo));
        ApplyCalibrationGeometryCommand = new AsyncRelayCommand(ApplyCalibrationGeometryAsync);
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync);
        ExportTemplateCommand = new AsyncRelayCommand(ExportTemplateAsync);
        ImportTemplatePackageCommand = new AsyncRelayCommand(ImportTemplatePackageAsync);
        SaveTemplateAsCommand = new AsyncRelayCommand(SaveTemplateAsAsync);
        DeleteTemplateCommand = new AsyncRelayCommand(DeleteTemplateAsync);
        ShowCalibrationPagesCommand = new RelayCommand(() => SetCalibrationNarrowPanel(CalibrationNarrowPanel.Pages));
        ShowCalibrationCanvasCommand = new RelayCommand(() => SetCalibrationNarrowPanel(CalibrationNarrowPanel.Canvas));
        ShowCalibrationInspectorCommand = new RelayCommand(() => SetCalibrationNarrowPanel(CalibrationNarrowPanel.Inspector));
        ImportedTemplateFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasImportedTemplateFiles));
            OnPropertyChanged(nameof(CanContinuePageSettings));
            RaiseTemplateCommandCanExecuteChanged();
        };
        TemplatePages.CollectionChanged += (_, _) => RaiseTemplateCommandCanExecuteChanged();
    }

    private void OnNavigationChanged()
    {
        if (IsTemplateSectionVisible)
        {
            _ = LoadTemplateLibraryAsync();
        }
    }

    private async Task LoadTemplateLibraryAsync()
    {
        if (_templateWorkspace is null || TemplateBusy)
        {
            return;
        }

        TemplateBusy = true;
        TemplateStatusMessage = "";
        try
        {
            var groups = await _templateWorkspace.ListAsync(CancellationToken.None).ConfigureAwait(true);
            TemplateGroups.Clear();
            foreach (var group in groups)
            {
                TemplateGroups.Add(new TemplateLibraryCardViewModel(group));
            }

            OnPropertyChanged(nameof(HasTemplateGroups));
            OnPropertyChanged(nameof(IsTemplateLibraryEmpty));
        }
        catch (IOException)
        {
            TemplateStatusMessage = "模板库暂时不可访问，请检查本地存储后重试。";
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private void BeginNewTemplate()
    {
        _templateDraft = null;
        _templateDetails = null;
        TemplateGroupName = "新模板组";
        ImportedTemplateFiles.Clear();
        TemplatePages.Clear();
        TemplateStatusMessage = "";
        NotifyTemplateDraftChanged();
        SetTemplateScreen(TemplateScreen.Import);
    }

    private async Task OpenTemplateAsync(TemplateLibraryCardViewModel? item)
    {
        if (_templateWorkspace is null || item is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templateWorkspace.GetDetailsAsync(item.GroupId, CancellationToken.None)
                .ConfigureAwait(true);
            if (_templateDetails is null)
            {
                TemplateStatusMessage = "此模板组尚未提交可用修订，可从草稿继续。";
                return;
            }

            if (!_templateDetails.IsCommitted)
            {
                _templateDraft = await _templateWorkspace.ResumeDraftAsync(
                    item.GroupId,
                    CancellationToken.None).ConfigureAwait(true);
                TemplateGroupName = _templateDraft.GroupName;
                await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
                TemplateStatusMessage = "本地草稿已恢复，可继续页面设置和校准。";
                NotifyTemplateDraftChanged();
                SetTemplateScreen(TemplateScreen.PageSettings);
                return;
            }

            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task BackToLibraryAsync()
    {
        SetTemplateScreen(TemplateScreen.Library);
        await LoadTemplateLibraryAsync().ConfigureAwait(true);
    }

    private async Task BeginIncrementAsync()
    {
        if (_templateWorkspace is null || _templateDetails is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDraft = await _templateWorkspace.BeginIncrementAsync(
                _templateDetails.GroupId,
                CancellationToken.None).ConfigureAwait(true);
            TemplateGroupName = _templateDraft.GroupName;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            TemplateStatusMessage = "原页面保持只读；请选择一个或多个新增 PSD。";
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.Import);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task<bool> ImportPendingAsync(
        PendingTemplateImport pending,
        TemplateDuplicateDecision decision)
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return false;
        }

        var result = await _templateWorkspace.ImportPageAsync(
            _templateDraft,
            pending.CommandId,
            pending.ResourceId,
            pending.SourcePath,
            pending.PageName,
            decision,
            decision == TemplateDuplicateDecision.ReuseExisting ? pending.DuplicateResourceId : null,
            CancellationToken.None).ConfigureAwait(true);
        if (result.Import.Status == TemplatePageImportStatus.DuplicateReviewRequired)
        {
            _pendingTemplateImport = pending with
            {
                DuplicateResourceId = result.Import.DuplicateCandidates[0].ResourceId
            };
            IsDuplicateReviewVisible = true;
            OnPropertyChanged(nameof(DuplicateExistingLabel));
            OnPropertyChanged(nameof(DuplicateImportedLabel));
            return false;
        }

        if (result.Import.Status == TemplatePageImportStatus.Cancelled)
        {
            TemplateStatusMessage = "已取消重复 PSD，本次只清理应用内部副本。";
            return true;
        }

        if (!result.Import.IsSuccess || result.AddedPage is null)
        {
            TemplateStatusMessage = result.Import.Error is null
                ? "PSD 导入未完成，请重试。"
                : $"{result.Import.Error.Code}：{result.Import.Error.UserAction}";
            return false;
        }

        if (result.AddedPage.IsAffected)
        {
            AddImportedFile(result.AddedPage);
        }

        TemplateStatusMessage = result.Import.Status == TemplatePageImportStatus.ReusedExisting
            ? "已保留模板库中的现有内部副本。"
            : "PSD 已复制到模板库，用户源文件保持只读。";
        return true;
    }

    private async Task ResolveDuplicateAsync(TemplateDuplicateDecision decision)
    {
        if (_pendingTemplateImport is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            var pending = _pendingTemplateImport;
            IsDuplicateReviewVisible = false;
            _pendingTemplateImport = null;
            await ImportPendingAsync(pending, decision).ConfigureAwait(true);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private void AddImportedFile(TemplateDraftPage imported)
    {
        if (ImportedTemplateFiles.Any(item => string.Equals(
            item.ResourceId,
            imported.Resource.ResourceId,
            StringComparison.Ordinal)))
        {
            return;
        }

        ImportedTemplateFiles.Add(new ImportedPsdRowViewModel(imported));
        OnPropertyChanged(nameof(HasImportedTemplateFiles));
        OnPropertyChanged(nameof(CanContinuePageSettings));
    }

    private async Task ContinuePageSettingsAsync()
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            await LoadDraftPagesAsync(populateImportedFiles: false).ConfigureAwait(true);
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.PageSettings);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task LoadDraftPagesAsync(bool populateImportedFiles)
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        var draft = await _templateWorkspace.GetDraftAsync(_templateDraft, CancellationToken.None)
            .ConfigureAwait(true);
        if (populateImportedFiles)
        {
            ImportedTemplateFiles.Clear();
            foreach (var page in draft.Pages.Where(page => page.IsAffected))
            {
                AddImportedFile(page);
            }
        }

        TemplatePages.Clear();
        foreach (var page in draft.Pages.OrderBy(item => item.Page.Order))
        {
            TemplatePages.Add(new TemplatePageRowViewModel(page, OnTemplatePageChanged));
        }

        SelectedTemplatePage = TemplatePages.FirstOrDefault(page => page.IsAffected)
            ?? TemplatePages.FirstOrDefault();
    }

    private void OnTemplatePageChanged(TemplatePageRowViewModel page)
    {
        if (!page.IsReadOnly)
        {
            _ = SavePageAsync(page);
        }
    }

    private async Task SavePageAsync(TemplatePageRowViewModel page)
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            await _templateWorkspace.ConfigurePageAsync(
                _templateDraft,
                page.PageId,
                page.Name,
                page.ContentType,
                page.GenerationMode,
                page.VariantCapacity,
                page.Enabled,
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "草稿已自动保存";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            _templateDraftWrite.Release();
        }
    }

    private async Task SavePageOrderAsync()
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            await _templateWorkspace.ReorderPagesAsync(
                _templateDraft,
                TemplatePages.Select(page => page.PageId).ToArray(),
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "输出页码已更新，草稿已自动保存";
        }
        finally
        {
            _templateDraftWrite.Release();
        }
    }

    private async Task StartCalibrationAsync()
    {
        foreach (var page in TemplatePages.Where(page => page.IsAffected))
        {
            await SavePageAsync(page).ConfigureAwait(true);
        }

        TemplateStatusMessage = IsIncrementalTemplateDraft
            ? "新增页面设置已保存；下一步只校准新增页。"
            : "页面设置已保存；下一步进入统一校准编辑器。";
        if (_templateCalibration is null || SelectedTemplatePage is null)
        {
            SetTemplateScreen(TemplateScreen.CalibrationPending);
            return;
        }

        try
        {
            SetTemplateScreen(TemplateScreen.Calibration);
            await LoadCalibrationPageAsync(SelectedTemplatePage).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
            SetTemplateScreen(TemplateScreen.CalibrationPending);
        }
    }

    private async Task ExitCalibrationAsync()
    {
        if (_templateDraft is null || !IsTemplateCalibrationVisible)
        {
            SetTemplateScreen(TemplateScreen.PageSettings);
            return;
        }

        var choice = MessageBox.Show(
            "校准操作会自动保存为本地草稿。\n\n是：保存当前输入并返回页面设置\n否：不提交模板组，返回页面设置（草稿仍可恢复）\n取消：留在校准页",
            "退出校准？",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
        {
            return;
        }

        if (choice == MessageBoxResult.Yes)
        {
            await CommitCalibrationGeometryAsync().ConfigureAwait(true);
            foreach (var page in TemplatePages.Where(page => page.IsAffected))
            {
                await SavePageAsync(page).ConfigureAwait(true);
            }

            TemplateStatusMessage = "校准草稿已保存，尚未提交模板组。";
        }
        else
        {
            TemplateStatusMessage = "已退出校准；模板组未提交，本地草稿仍可恢复。";
        }

        SetTemplateScreen(TemplateScreen.PageSettings);
    }

    private async Task RemoveTemplatePageAsync(TemplatePageRowViewModel? page)
    {
        if (_templateWorkspace is null || _templateDraft is null || page is null)
        {
            return;
        }

        if (page.IsReadOnly)
        {
            TemplateStatusMessage = "增量流程的原页面保持只读，不能移除。";
            return;
        }

        if (TemplatePages.Count <= 1)
        {
            TemplateStatusMessage = "模板组至少保留一个页面。";
            return;
        }

        var confirmation = MessageBox.Show(
            $"确定移除页面“{page.Name}”吗？\n\n只会移除当前模板草稿中的页面，不会删除用户源 PSD。",
            "移除模板页面？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            await _templateWorkspace.RemovePageAsync(
                _templateDraft,
                page.PageId,
                CancellationToken.None).ConfigureAwait(true);
            var index = TemplatePages.IndexOf(page);
            TemplatePages.Remove(page);
            var imported = ImportedTemplateFiles.FirstOrDefault(item => string.Equals(
                item.ResourceId,
                page.ResourceId,
                StringComparison.Ordinal));
            if (imported is not null)
            {
                ImportedTemplateFiles.Remove(imported);
            }
            UpdatePageOrderLabels();
            SelectedTemplatePage = TemplatePages.ElementAtOrDefault(Math.Max(0, index - 1))
                ?? TemplatePages.FirstOrDefault();
            TemplateStatusMessage = "页面已移除；用户源 PSD 保持只读未改动。";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task AbandonTemplateDraftAsync()
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            "会删除当前模板草稿及软件内部保存的模板副本。\n\n不会删除用户源 PSD、源照片或其他模板组。",
            "放弃当前模板组？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            var blockers = await _templateWorkspace.DeleteTemplateGroupAsync(
                _templateDraft.GroupId,
                CancellationToken.None).ConfigureAwait(true);
            if (blockers.Count > 0)
            {
                TemplateStatusMessage = "存在未完成引用任务，不能放弃：" + string.Join("、", blockers);
                return;
            }

            _templateDraft = null;
            _templateDetails = null;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            TemplateStatusMessage = "当前模板草稿已放弃；用户源文件未受影响。";
            SetTemplateScreen(TemplateScreen.Library);
            await LoadTemplateLibraryAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task LoadCalibrationPageAsync(TemplatePageRowViewModel page)
    {
        if (_templateCalibration is null || _templateDraft is null)
        {
            return;
        }

        CalibrationDraft = await _templateCalibration.LoadAsync(
            _templateDraft,
            page.PageId,
            CancellationToken.None).ConfigureAwait(true);
        UpdateCalibrationInputs(CalibrationDraft);
    }

    private async Task SelectCalibrationAssetAsync()
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            var frameId = CalibrationFrameIdInput;
            CalibrationDraft = await _templateCalibration.BindAssetAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                frameId,
                CalibrationAssetIdInput,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            CalibrationAssetIdInput = "";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    public async Task ClearCalibrationAssetAsync()
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            CalibrationDraft = await _templateCalibration.ClearAssetAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            TemplateStatusMessage = "已移除当前图片框中的素材；源文件未删除，可用撤销恢复。";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    private async Task ApplyCalibrationHistoryAsync(CalibrationUndoAction action)
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null)
        {
            return;
        }

        CalibrationDraft = await _templateCalibration.ApplyHistoryAsync(
            _templateDraft,
            SelectedTemplatePage.PageId,
            action,
            CancellationToken.None).ConfigureAwait(true);
        UpdateCalibrationInputs(CalibrationDraft);
        TemplateStatusMessage = action == CalibrationUndoAction.Undo
            ? "已撤销最近一次校准操作。"
            : "已重做最近一次校准操作。";
    }

    private async Task ApplyCalibrationGeometryAsync()
    {
        if (_templateCalibration is null || _templateDraft is null || SelectedTemplatePage is null
            || string.IsNullOrWhiteSpace(CalibrationFrameIdInput))
        {
            return;
        }

        try
        {
            CalibrationDraft = await _templateCalibration.SetGeometryAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                CalibrationScaleInput,
                CalibrationOffsetXInput,
                CalibrationOffsetYInput,
                CancellationToken.None).ConfigureAwait(true);
            CalibrationDraft = await _templateCalibration.SetSelectionRequirementAsync(
                _templateDraft,
                SelectedTemplatePage.PageId,
                CalibrationFrameIdInput,
                CalibrationRequirementInput,
                CancellationToken.None).ConfigureAwait(true);
            UpdateCalibrationInputs(CalibrationDraft);
            TemplateStatusMessage = "布局与选图规则已保存。";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
    }

    private async Task SaveTemplateAsync()
    {
        if (_templateWorkspace is null || _templateDraft is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templateWorkspace.CommitDraftAsync(
                _templateDraft,
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "模板组已原子保存；Photoshop 示例效果未运行。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task ExportTemplateAsync()
    {
        if (_templatePackages is null || _templateDetails is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出模板包",
            Filter = "模板包 (*.tplpkg)|*.tplpkg",
            FileName = _templateDetails.Name + ".tplpkg",
            AddExtension = true,
            DefaultExt = ".tplpkg"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _templatePackages.ExportCurrentAsync(
            _templateDetails.GroupId,
            dialog.FileName,
            CancellationToken.None).ConfigureAwait(true);
        TemplateStatusMessage = "模板包已导出；不包含源照片、任务历史、Key 或绝对路径。";
    }

    private async Task ImportTemplatePackageAsync()
    {
        if (_templatePackages is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入模板包",
            Filter = "模板包 (*.tplpkg)|*.tplpkg",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templatePackages.ImportAsNewGroupAsync(
                dialog.FileName,
                groupName: null,
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "模板包已作为新的本地模板组导入。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task SaveTemplateAsAsync()
    {
        if (_templatePackages is null || _templateDetails is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDetails = await _templatePackages.SaveAsNewGroupAsync(
                _templateDetails.GroupId,
                _templateDetails.Name + " 副本",
                CancellationToken.None).ConfigureAwait(true);
            TemplateStatusMessage = "已另存为独立模板组。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task DeleteTemplateAsync()
    {
        if (_templateWorkspace is null || _templateDetails is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            "会删除软件内部保存的模板副本和校准规则。\n\n不会删除最初导入的 PSD、已生成结果和其他模板组。",
            "删除模板组？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var blockers = await _templateWorkspace.DeleteTemplateGroupAsync(
            _templateDetails.GroupId,
            CancellationToken.None).ConfigureAwait(true);
        if (blockers.Count > 0)
        {
            TemplateStatusMessage = "存在未完成引用任务，不能删除：" + string.Join("、", blockers);
            return;
        }

        _templateDetails = null;
        TemplateStatusMessage = "模板组已删除；用户源 PSD 和正式输出未受影响。";
        SetTemplateScreen(TemplateScreen.Library);
        await LoadTemplateLibraryAsync().ConfigureAwait(true);
    }

    private void UpdateCalibrationInputs(TemplateCalibrationDraftView draft)
    {
        OnPropertyChanged(nameof(CalibrationDraft));
        var selectedFrameId = draft.SelectedFrameId;
        if (string.IsNullOrWhiteSpace(selectedFrameId)
            || !draft.Frames.Any(frame => string.Equals(
                frame.FrameId,
                selectedFrameId,
                StringComparison.Ordinal)))
        {
            selectedFrameId = draft.Frames.Any(frame => string.Equals(
                frame.FrameId,
                CalibrationFrameIdInput,
                StringComparison.Ordinal))
                ? CalibrationFrameIdInput
                : draft.Frames.Count > 0 ? draft.Frames[0].FrameId : null;
        }

        // Re-apply the selection after Frames changes. WPF temporarily writes an empty
        // SelectedValue back to this two-way property while replacing ItemsSource.
        CalibrationFrameIdInput = selectedFrameId ?? "";
        CalibrationScaleInput = draft.SelectedScale;
        CalibrationOffsetXInput = draft.SelectedOffsetX;
        CalibrationOffsetYInput = draft.SelectedOffsetY;
        CalibrationRequirementInput = draft.SelectionRequirement;
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationTemplatePreviewImagePath));
        OnPropertyChanged(nameof(CalibrationCanvasWidth));
        OnPropertyChanged(nameof(CalibrationCanvasHeight));
        OnPropertyChanged(nameof(CalibrationHeaderTitle));
        OnPropertyChanged(nameof(CanEditCalibration));
        NotifyCalibrationFrameChanged();
    }

    private void NotifyCalibrationFrameChanged()
    {
        OnPropertyChanged(nameof(CalibrationFrameLeft));
        OnPropertyChanged(nameof(CalibrationFrameTop));
        OnPropertyChanged(nameof(CalibrationFrameWidth));
        OnPropertyChanged(nameof(CalibrationFrameHeight));
        OnPropertyChanged(nameof(CalibrationFrameDisplayName));
        OnPropertyChanged(nameof(CalibrationAssetActionText));
        OnPropertyChanged(nameof(CanClearCalibrationAsset));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private void SetCalibrationNarrowPanel(CalibrationNarrowPanel panel)
    {
        if (_calibrationNarrowPanel == panel)
        {
            return;
        }
        _calibrationNarrowPanel = panel;
        NotifyCalibrationLayoutChanged();
    }

    private void NotifyCalibrationLayoutChanged()
    {
        OnPropertyChanged(nameof(IsCalibrationNarrowLayout));
        OnPropertyChanged(nameof(IsCalibrationPagePanelVisible));
        OnPropertyChanged(nameof(IsCalibrationCanvasPanelVisible));
        OnPropertyChanged(nameof(IsCalibrationInspectorPanelVisible));
        OnPropertyChanged(nameof(CalibrationPageColumnWidth));
        OnPropertyChanged(nameof(CalibrationCanvasColumnWidth));
        OnPropertyChanged(nameof(CalibrationInspectorColumnWidth));
        OnPropertyChanged(nameof(CalibrationPanelGapWidth));
    }

    private void UpdatePageOrderLabels()
    {
        for (var index = 0; index < TemplatePages.Count; index++)
        {
            TemplatePages[index].SetDisplayOrder(index + 1);
        }
    }

    private void SetTemplateScreen(TemplateScreen screen)
    {
        _templateScreen = screen;
        OnPropertyChanged(nameof(IsTemplateLibraryVisible));
        OnPropertyChanged(nameof(IsTemplateDetailsVisible));
        OnPropertyChanged(nameof(IsTemplateImportVisible));
        OnPropertyChanged(nameof(IsTemplatePageSettingsVisible));
        OnPropertyChanged(nameof(IsTemplateCalibrationPendingVisible));
        OnPropertyChanged(nameof(IsTemplateCalibrationVisible));
    }

    private void NotifyTemplateDraftChanged()
    {
        OnPropertyChanged(nameof(IsIncrementalTemplateDraft));
        OnPropertyChanged(nameof(TemplateFlowTitle));
        OnPropertyChanged(nameof(TemplateFlowSubtitle));
        OnPropertyChanged(nameof(TemplateSettingsSubtitle));
        OnPropertyChanged(nameof(TemplateSettingsScope));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private void RaiseTemplateCommandCanExecuteChanged()
    {
        (RefreshTemplatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddTemplatePagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ContinuePageSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartCalibrationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearCalibrationAssetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool SetTemplateField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private static string ToUserMessage(string code) => code switch
    {
        "TPL_GROUP_NAME_INVALID" => "请输入 1–256 个字符的模板组名称。",
        "TPL_PAGE_NAME_INVALID" => "页面名称不能为空，且不能超过 256 个字符。",
        "TPL_PAGE_VARIANT_CAPACITY_INVALID" => "按变体数量组合时必须填写大于 0 的容量。",
        "TPL_REVISION_PAGE_SET_EMPTY" => "模板组至少保留一个页面。",
        "TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY" => "增量流程默认不修改原页面，请只设置新增页面。",
        "TPL_CALIBRATION_ASSET_REF_INVALID" => "请选择可读取的 JPG、PNG、WebP、TIFF 或 BMP 代表款素材。",
        "TPL_CALIBRATION_ASSET_SOURCE_UNAVAILABLE" => "本机代表款素材只读检查服务不可用。",
        "TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE" => "PSD 图片框的结构证据不完整，已阻止注册；请重新检查模板。",
        _ => $"操作未完成（{code}），请检查后重试。"
    };

    private enum TemplateScreen
    {
        Library,
        Details,
        Import,
        PageSettings,
        CalibrationPending,
        Calibration
    }

    private enum CalibrationNarrowPanel
    {
        Pages,
        Canvas,
        Inspector
    }

    private sealed record PendingTemplateImport(
        string CommandId,
        string ResourceId,
        string SourcePath,
        string PageName,
        string? DuplicateResourceId);
}

public sealed record TemplateLibraryCardViewModel(
    string GroupId,
    string Name,
    string Meta,
    string UpdatedText,
    string Status)
{
    public TemplateLibraryCardViewModel(TemplateLibraryItem item)
        : this(
            item.GroupId,
            item.Name,
            $"{item.PageCount} 个页面 · {item.Status}",
            item.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            item.Status)
    {
    }
}

public sealed record TemplatePageDetailsRowViewModel(
    string OrderText,
    string Name,
    string Meta,
    string Status,
    string Action)
{
    public TemplatePageDetailsRowViewModel(TemplatePageProjection page)
        : this(
            (page.Order + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
            page.Name,
            $"{TemplatePageRowViewModel.ContentTypeLabel(page.ContentType)} · {TemplatePageRowViewModel.GenerationModeLabel(page.GenerationMode)}",
            "已校准",
            "编辑")
    {
    }
}

public sealed record ImportedPsdRowViewModel(
    string ResourceId,
    string FileName,
    string InspectionSummary,
    string Status)
{
    public ImportedPsdRowViewModel(TemplateDraftPage page)
        : this(
            page.Resource.ResourceId,
            $"{page.Page.Name}.psd",
            $"{page.Resource.Inspection.Canvas.Width} × {page.Resource.Inspection.Canvas.Height} · "
            + $"{page.Resource.Inspection.Canvas.Depth} 位 {page.Resource.Inspection.Canvas.ColorMode}",
            "可用")
    {
    }
}

public sealed class TemplatePageRowViewModel : INotifyPropertyChanged
{
    private readonly Action<TemplatePageRowViewModel> _changed;
    private string _name;
    private TemplatePageContentTypeOption _contentType;
    private TemplateGenerationModeOption _generationMode;
    private int? _variantCapacity;
    private bool _enabled;
    private int _displayOrder;

    public TemplatePageRowViewModel(TemplateDraftPage page, Action<TemplatePageRowViewModel> changed)
    {
        _changed = changed;
        PageId = page.Page.PageId;
        ResourceId = page.Resource.ResourceId;
        _displayOrder = page.Page.Order + 1;
        _name = page.Page.Name;
        _contentType = page.Page.ContentType;
        _generationMode = page.Page.GenerationMode;
        _variantCapacity = page.Page.VariantCapacity;
        _enabled = page.Page.Enabled;
        IsAffected = page.IsAffected;
        CanvasSummary = $"{page.Resource.Inspection.Canvas.Width} × {page.Resource.Inspection.Canvas.Height}";
        PreviewSourceReference = page.PreviewSourceReference;
    }

    public static IReadOnlyList<string> ContentTypeOptions { get; } = ["普通套图页", "动态尺码表页"];

    public static IReadOnlyList<string> GenerationModeOptions { get; } =
        ["每款生成一次", "每变体生成一次", "按变体数量组合"];

    public string PageId { get; }

    public string ResourceId { get; }

    public bool IsAffected { get; }

    public bool IsReadOnly => !IsAffected;

    public bool CanRemove => IsAffected;

    public string Status => IsAffected ? "新增" : "已校准";

    public string RuleSummary => IsAffected
        ? $"{ContentTypeLabel(ContentType)} · {GenerationModeLabel(GenerationMode)}"
        : "原规则保持";

    public string CanvasSummary { get; }

    public string? PreviewSourceReference { get; }

    public string DisplayOrder => _displayOrder.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string ContentTypeText
    {
        get => ContentTypeLabel(ContentType);
        set => ContentType = value == "动态尺码表页"
            ? TemplatePageContentTypeOption.DynamicSizeTable
            : TemplatePageContentTypeOption.NormalComposition;
    }

    public string GenerationModeText
    {
        get => GenerationModeLabel(GenerationMode);
        set => GenerationMode = value switch
        {
            "每变体生成一次" => TemplateGenerationModeOption.OncePerVariant,
            "按变体数量组合" => TemplateGenerationModeOption.VariantCapacityCombination,
            _ => TemplateGenerationModeOption.OncePerProduct
        };
    }

    public TemplatePageContentTypeOption ContentType
    {
        get => _contentType;
        private set
        {
            if (Set(ref _contentType, value))
            {
                OnPropertyChanged(nameof(ContentTypeText));
            }
        }
    }

    public TemplateGenerationModeOption GenerationMode
    {
        get => _generationMode;
        private set
        {
            if (Set(ref _generationMode, value))
            {
                if (value != TemplateGenerationModeOption.VariantCapacityCombination)
                {
                    _variantCapacity = null;
                    OnPropertyChanged(nameof(VariantCapacity));
                }

                OnPropertyChanged(nameof(GenerationModeText));
                OnPropertyChanged(nameof(IsVariantCapacityVisible));
            }
        }
    }

    public int? VariantCapacity
    {
        get => _variantCapacity;
        set => Set(ref _variantCapacity, value);
    }

    public bool IsVariantCapacityVisible => GenerationMode == TemplateGenerationModeOption.VariantCapacityCombination;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string ContentTypeLabel(TemplatePageContentTypeOption contentType) => contentType switch
    {
        TemplatePageContentTypeOption.DynamicSizeTable => "动态尺码表页",
        _ => "普通套图页"
    };

    public static string GenerationModeLabel(TemplateGenerationModeOption generationMode) => generationMode switch
    {
        TemplateGenerationModeOption.OncePerVariant => "每变体生成一次",
        TemplateGenerationModeOption.VariantCapacityCombination => "按变体数量组合",
        _ => "每款生成一次"
    };

    public void SetDisplayOrder(int displayOrder)
    {
        _displayOrder = displayOrder;
        OnPropertyChanged(nameof(DisplayOrder));
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(RuleSummary));
        _changed(this);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

internal sealed class AsyncRelayCommand<T>(Func<T?, Task> execute) : ICommand
    where T : class
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && parameter is T;

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute((T)parameter!).ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
