using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Templates;
using Microsoft.Win32;

namespace Desktop.App;

public sealed partial class TemplateWorkspaceViewModel : INotifyPropertyChanged, IDisposable
{

    private readonly ITemplateWorkspaceHost _host;
    private readonly IFolderPicker? _folderPicker;
    public TemplateWorkspacePageState State { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    public TemplateWorkspaceViewModel(
        ITemplateWorkspaceService? templateWorkspace,
        ITemplateCalibrationService? templateCalibration,
        ITemplatePackageService? templatePackages,
        IReferenceLearningService? referenceLearning,
        IReferenceLearningCommitService? referenceLearningCommit,
        IReferenceLearningAiService? referenceLearningAi,
        IFolderPicker? folderPicker,
        ITemplateWorkspaceHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _folderPicker = folderPicker;
        _templateWorkspace = templateWorkspace;
        _templateCalibration = templateCalibration;
        _templatePackages = templatePackages;
        _referenceLearning = referenceLearning;
        _referenceLearningCommit = referenceLearningCommit;
        _referenceLearningAi = referenceLearningAi;
        State.PropertyChanged += TemplateWorkspaceState_PropertyChanged;
        InitializeTemplates();
        InitializeReferenceLearning();
    }

    private readonly SemaphoreSlim _templateDraftWrite = new(1, 1);
    private readonly ITemplateWorkspaceService? _templateWorkspace;
    private readonly ITemplateCalibrationService? _templateCalibration;
    private readonly ITemplatePackageService? _templatePackages;
    private TemplateScreen _templateScreen = TemplateScreen.Library;
    private TemplateGroupDetails? _templateDetails;
    private TemplateDraftSession? _templateDraft;
    private PendingTemplateImport? _pendingTemplateImport;
    private TaskCompletionSource<TemplateDialogChoice>? _templateDialogCompletion;
    private long _calibrationPageLoadVersion;

    private TemplateWorkspacePageState TemplateWorkspaceState => State;

    public ObservableCollection<TemplateLibraryCardViewModel> TemplateGroups { get; } = [];

    public IReadOnlyList<TemplateLibraryCardViewModel> BatchTemplateGroups => TemplateGroups
        .Where(group => group.HasCommittedRevision)
        .ToArray();

    public ObservableCollection<ImportedPsdRowViewModel> ImportedTemplateFiles { get; } = [];

    public ObservableCollection<TemplatePageRowViewModel> TemplatePages { get; } = [];

    public ICommand RefreshTemplatesCommand { get; internal set; } = null!;

    public ICommand NewTemplateCommand { get; internal set; } = null!;

    public ICommand OpenTemplateCommand { get; internal set; } = null!;

    public ICommand RenameTemplateCommand { get; internal set; } = null!;

    public ICommand DeleteTemplateFromLibraryCommand { get; internal set; } = null!;

    public ICommand BackToTemplateLibraryCommand { get; internal set; } = null!;

    public ICommand AddTemplatePagesCommand { get; internal set; } = null!;

    public ICommand EditTemplatePageCommand { get; internal set; } = null!;

    public ICommand EditTemplateGroupCommand { get; internal set; } = null!;

    public ICommand RemoveTemplatePageCommand { get; internal set; } = null!;

    public ICommand RemoveImportedPsdCommand { get; internal set; } = null!;

    public ICommand AbandonTemplateDraftCommand { get; internal set; } = null!;

    public ICommand KeepExistingDuplicateCommand { get; internal set; } = null!;

    public ICommand KeepImportedDuplicateCommand { get; internal set; } = null!;

    public ICommand CancelDuplicateCommand { get; internal set; } = null!;

    public ICommand ConfirmTemplateDialogCommand { get; internal set; } = null!;

    public ICommand SecondaryTemplateDialogCommand { get; internal set; } = null!;

    public ICommand TertiaryTemplateDialogCommand { get; internal set; } = null!;

    public ICommand StartCalibrationCommand { get; internal set; } = null!;

    public ICommand SelectCalibrationAssetCommand { get; internal set; } = null!;

    public ICommand ClearCalibrationAssetCommand { get; internal set; } = null!;

    public ICommand UndoCalibrationCommand { get; internal set; } = null!;

    public ICommand RedoCalibrationCommand { get; internal set; } = null!;

    public ICommand ApplyCalibrationGeometryCommand { get; internal set; } = null!;

    public ICommand ApplyAndConfirmCalibrationCommand { get; internal set; } = null!;

    public ICommand SaveTemplateCommand { get; internal set; } = null!;

    public ICommand ExportTemplateCommand { get; internal set; } = null!;

    public ICommand ImportTemplatePackageCommand { get; internal set; } = null!;

    public ICommand SaveTemplateAsCommand { get; internal set; } = null!;

    public ICommand DeleteTemplateCommand { get; internal set; } = null!;

    public ICommand ShowCalibrationPagesCommand { get; internal set; } = null!;

    public ICommand ShowCalibrationCanvasCommand { get; internal set; } = null!;

    public ICommand ShowCalibrationInspectorCommand { get; internal set; } = null!;

    public bool IsTemplateLibraryVisible => _templateScreen == TemplateScreen.Library;

    public bool IsTemplateDetailsVisible => _templateScreen == TemplateScreen.Details;

    public bool IsTemplateCalibrationVisible => _templateScreen is TemplateScreen.Canvas
        or TemplateScreen.ReferenceLearning;

    public bool IsTemplateCanvasVisible => _templateScreen is TemplateScreen.Canvas or TemplateScreen.ReferenceLearning;

    public bool IsTemplateDialogVisible
    {
        get => TemplateWorkspaceState.IsTemplateDialogVisible;
        internal set => TemplateWorkspaceState.IsTemplateDialogVisible = value;
    }

    public string TemplateDialogTitle
    {
        get => TemplateWorkspaceState.TemplateDialogTitle;
        internal set => TemplateWorkspaceState.TemplateDialogTitle = value;
    }

    public string TemplateDialogBody
    {
        get => TemplateWorkspaceState.TemplateDialogBody;
        internal set => TemplateWorkspaceState.TemplateDialogBody = value;
    }

    public string TemplateDialogSafetyNote
    {
        get => TemplateWorkspaceState.TemplateDialogSafetyNote;
        internal set => TemplateWorkspaceState.TemplateDialogSafetyNote = value;
    }

    public string TemplateDialogPrimaryText
    {
        get => TemplateWorkspaceState.TemplateDialogPrimaryText;
        internal set => TemplateWorkspaceState.TemplateDialogPrimaryText = value;
    }

    public string TemplateDialogSecondaryText
    {
        get => TemplateWorkspaceState.TemplateDialogSecondaryText;
        internal set => TemplateWorkspaceState.TemplateDialogSecondaryText = value;
    }

    public string TemplateDialogTertiaryText
    {
        get => TemplateWorkspaceState.TemplateDialogTertiaryText;
        internal set => TemplateWorkspaceState.TemplateDialogTertiaryText = value;
    }

    public bool TemplateDialogHasSecondaryAction
    {
        get => TemplateWorkspaceState.TemplateDialogHasSecondaryAction;
        internal set => TemplateWorkspaceState.TemplateDialogHasSecondaryAction = value;
    }

    public bool TemplateDialogHasTertiaryAction
    {
        get => TemplateWorkspaceState.TemplateDialogHasTertiaryAction;
        internal set => TemplateWorkspaceState.TemplateDialogHasTertiaryAction = value;
    }

    public bool TemplateDialogIsDestructive
    {
        get => TemplateWorkspaceState.TemplateDialogIsDestructive;
        internal set => TemplateWorkspaceState.TemplateDialogIsDestructive = value;
    }

    public bool TemplateDialogHasTextInput
    {
        get => TemplateWorkspaceState.TemplateDialogHasTextInput;
        internal set
        {
            if (TemplateWorkspaceState.TemplateDialogHasTextInput != value)
            {
                TemplateWorkspaceState.TemplateDialogHasTextInput = value;
                (ConfirmTemplateDialogCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string TemplateDialogInputText
    {
        get => TemplateWorkspaceState.TemplateDialogInputText;
        set
        {
            if (!string.Equals(TemplateWorkspaceState.TemplateDialogInputText, value, StringComparison.Ordinal))
            {
                TemplateWorkspaceState.TemplateDialogInputText = value;
                (ConfirmTemplateDialogCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCalibrationNarrowLayout => TemplateWorkspaceState.IsCalibrationNarrowLayout;

    public bool IsCalibrationPagePanelVisible => TemplateWorkspaceState.IsCalibrationPagePanelVisible;

    public bool IsCalibrationCanvasPanelVisible => TemplateWorkspaceState.IsCalibrationCanvasPanelVisible;

    public bool IsCalibrationInspectorPanelVisible => TemplateWorkspaceState.IsCalibrationInspectorPanelVisible;

    public GridLength CalibrationPageColumnWidth => TemplateWorkspaceState.CalibrationPageColumnWidth;

    public GridLength CalibrationCanvasColumnWidth => TemplateWorkspaceState.CalibrationCanvasColumnWidth;

    public GridLength CalibrationInspectorColumnWidth => TemplateWorkspaceState.CalibrationInspectorColumnWidth;

    public GridLength CalibrationPanelGapWidth => TemplateWorkspaceState.CalibrationPanelGapWidth;

    public TemplateCalibrationDraftView? CalibrationDraft { get; internal set; }

    public string? CalibrationPreviewImagePath
    {
        get
        {
            if (IsReferenceLearningReviewMode
                || CalibrationDraft?.SelectedAssetId is not { } assetId)
            {
                return null;
            }

            var source = CalibrationDraft.Assets.FirstOrDefault(asset => string.Equals(
                asset.AssetId,
                assetId,
                StringComparison.Ordinal))?.SourceReference;
            return !string.IsNullOrWhiteSpace(source) && File.Exists(source)
                ? source
                : null;
        }
    }

    public string? CalibrationTemplatePreviewImagePath => IsReferenceLearningReviewMode
        ? SelectedTemplatePage?.PreviewSourceReference
        : CalibrationDraft?.TemplatePreviewSourceReference ?? SelectedTemplatePage?.PreviewSourceReference;

    public string CalibrationAssetActionText => SelectedCalibrationFrame?.IsRegistered == true
        ? "更换素材"
        : "选择素材";

    public string CalibrationHeaderTitle
    {
        get
        {
            var index = SelectedTemplatePage is null ? -1 : TemplatePages.IndexOf(SelectedTemplatePage);
            return index < 0 ? "模板校准" : $"模板校准 {index + 1}/{TemplatePages.Count}";
        }
    }

    public bool CanEditCalibration => IsReferenceLearningReviewMode
        ? CurrentReferenceLearningDocument is not null
            && CurrentReferenceLearningDocument.State is not (ReferenceLearningState.Cancelled
                or ReferenceLearningState.Committed
                or ReferenceLearningState.Blocked)
        : CalibrationDraft is { IsReadOnly: false, IsBlocked: false };

    public bool CanClearCalibrationAsset => !IsReferenceLearningReviewMode && CanEditCalibration
        && SelectedCalibrationFrame is { } frame
        && (frame.IsRegistered || frame.HasPersistedBinding);

    public bool CanApplyAndConfirmCalibration => IsReferenceLearningReviewMode
        ? CanAcceptReferenceLearningFrame
        : CanEditCalibration
            && SelectedCalibrationFrame is { IsPreviewSupported: true }
            && (SelectedCalibrationFrame.IsRegistered || SelectedCalibrationFrame.HasPersistedBinding)
            && !string.IsNullOrWhiteSpace(CalibrationRequirementInput);

    public double CalibrationCanvasWidth => IsReferenceLearningReviewMode
        ? CurrentReferenceLearningDocument?.Canvas.Width ?? 790
        : CalibrationDraft?.CanvasWidth ?? 790;

    public double CalibrationCanvasHeight => IsReferenceLearningReviewMode
        ? CurrentReferenceLearningDocument?.Canvas.Height ?? 1252
        : CalibrationDraft?.CanvasHeight ?? 1252;

    public double CalibrationFrameLeft => IsReferenceLearningReviewMode
        ? SelectedReferenceLearningFrame?.Left ?? 0
        : SelectedCalibrationFrame?.Left ?? 0;

    public double CalibrationFrameTop => IsReferenceLearningReviewMode
        ? SelectedReferenceLearningFrame?.Top ?? 0
        : SelectedCalibrationFrame?.Top ?? 0;

    public double CalibrationFrameWidth => IsReferenceLearningReviewMode
        ? SelectedReferenceLearningFrame?.Width ?? 1
        : SelectedCalibrationFrame?.Width ?? 1;

    public double CalibrationFrameHeight => IsReferenceLearningReviewMode
        ? SelectedReferenceLearningFrame?.Height ?? 1
        : SelectedCalibrationFrame?.Height ?? 1;

    public string CalibrationFrameDisplayName => IsReferenceLearningReviewMode
        ? SelectedReferenceLearningFrame?.LayerName ?? "未选择"
        : SelectedCalibrationFrame is { } frame
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
                if (IsReferenceLearningReviewMode
                    && SelectedReferenceLearningFrame?.FrameId != value)
                {
                    SelectReferenceLearningFrame(value);
                    return;
                }
                NotifyCalibrationFrameChanged();
            }
        }
    }

    public double CalibrationScaleInput
    {
        get => _calibrationScaleInput;
        set
        {
            if (SetTemplateField(ref _calibrationScaleInput, value))
            {
                OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
            }
        }
    }

    public double CalibrationOffsetXInput
    {
        get => _calibrationOffsetXInput;
        set
        {
            if (SetTemplateField(ref _calibrationOffsetXInput, value))
            {
                OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
            }
        }
    }

    public double CalibrationOffsetYInput
    {
        get => _calibrationOffsetYInput;
        set
        {
            if (SetTemplateField(ref _calibrationOffsetYInput, value))
            {
                OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
            }
        }
    }

    public string CalibrationRequirementInput
    {
        get => _calibrationRequirementInput;
        set
        {
            if (SetTemplateField(ref _calibrationRequirementInput, value))
            {
                OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
                OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
                OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
                RaiseReferenceLearningCommands();
            }
        }
    }

    public double CalibrationViewportZoom => TemplateWorkspaceState.CalibrationViewportZoom;

    public string CalibrationZoomText => TemplateWorkspaceState.CalibrationZoomText;

    public void SetCalibrationViewportZoom(double zoom) => TemplateWorkspaceState.SetCalibrationViewportZoom(zoom);

    public void AdjustCalibrationViewportZoom(int wheelDelta) =>
        TemplateWorkspaceState.AdjustCalibrationViewportZoom(wheelDelta);

    public double TemplateBoardZoom => TemplateWorkspaceState.TemplateBoardZoom;

    public double TemplateBoardPanX => TemplateWorkspaceState.TemplateBoardPanX;

    public double TemplateBoardPanY => TemplateWorkspaceState.TemplateBoardPanY;

    public string TemplateBoardZoomText => TemplateWorkspaceState.TemplateBoardZoomText;

    public void AdjustTemplateBoardZoom(int wheelDelta) =>
        TemplateWorkspaceState.AdjustTemplateBoardZoom(wheelDelta);

    public void SetTemplateBoardZoom(double zoom) => TemplateWorkspaceState.SetTemplateBoardZoom(zoom);

    public void PanTemplateBoard(double deltaX, double deltaY) =>
        TemplateWorkspaceState.PanTemplateBoard(deltaX, deltaY);

    public void ResetTemplateBoardView() => TemplateWorkspaceState.ResetTemplateBoardView();

    public bool HasTemplateGroups => TemplateGroups.Count > 0;

    public bool IsTemplateLibraryEmpty => !TemplateBusy && !HasTemplateGroups;

    public bool HasImportedTemplateFiles => ImportedTemplateFiles.Count > 0;

    /// <summary>
    /// Dynamic size table pages are data replacement pages. They do not have
    /// product image frames and must never enter the image calibration flow.
    /// </summary>
    public bool HasCalibratableTemplatePages => TemplatePages.Any(page =>
        page.IsAffected
        && page.Enabled
        && page.ContentType == TemplatePageContentTypeOption.NormalComposition);

    public bool IsSelectedNormalComposition => SelectedTemplatePage?.ContentType
        == TemplatePageContentTypeOption.NormalComposition;

    public bool IsSelectedDynamicSizeTable => SelectedTemplatePage?.ContentType
        == TemplatePageContentTypeOption.DynamicSizeTable;

    public IReadOnlyList<TemplatePageRowViewModel> CalibrationPages => _templateScreen == TemplateScreen.Canvas
        ? TemplatePages.ToArray()
        : TemplatePages
            .Where(page => page.ContentType == TemplatePageContentTypeOption.NormalComposition)
            .ToArray();

    public bool IsIncrementalTemplateDraft => _templateDraft?.IsIncremental == true;

    public bool IsDuplicateReviewVisible
    {
        get => TemplateWorkspaceState.IsDuplicateReviewVisible;
        internal set => TemplateWorkspaceState.IsDuplicateReviewVisible = value;
    }

    public bool TemplateBusy
    {
        get => TemplateWorkspaceState.TemplateBusy;
        internal set => TemplateWorkspaceState.TemplateBusy = value;
    }

    public string TemplateGroupName
    {
        get => TemplateWorkspaceState.TemplateGroupName;
        set => TemplateWorkspaceState.TemplateGroupName = value;
    }

    public string TemplateStatusMessage
    {
        get => TemplateWorkspaceState.TemplateStatusMessage;
        internal set => TemplateWorkspaceState.TemplateStatusMessage = value;
    }

    public bool HasTemplateStatusMessage => TemplateWorkspaceState.HasTemplateStatusMessage;

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

    public bool CanSaveTemplateCanvas => _templateDraft is not null
        && TemplatePages.Count > 0
        && !TemplateBusy
        && (!HasCalibratableTemplatePages || CanSaveCurrentCalibration);

    public string TemplateCanvasTitle => IsIncrementalTemplateDraft ? "添加模板页面" : "新建模板组";

    public string TemplateCanvasSubtitle => IsIncrementalTemplateDraft
        ? $"{TemplateGroupName} · 在画布中添加并编辑新增页面"
        : $"{TemplateGroupName} · 添加 PSD、设置页面并完成校准";

    public string TemplateCanvasStatusText => TemplatePages.Count == 0
        ? "画布为空，请添加一个或多个 PSD。"
        : $"{TemplatePages.Count} 个页面 · 页面顺序决定输出顺序";

    public bool IsTemplatePageEditDraft => _templateDraft?.IsPageEdit == true;

    public bool IsFullTemplateEdit => _templateDraft?.IsFullTemplateEdit == true;

    public bool CanEditTemplateGroup => _templateDetails?.IsCommitted == true && !TemplateBusy;

    public bool CanSaveAsTemplate => IsFullTemplateEdit
        && IsManualCalibrationReviewMode
        && _templateDraft is not null
        && !TemplateBusy;

    public IReadOnlyList<TemplatePageDetailsRowViewModel> TemplateDetailPages => _templateDetails?.Pages
        .OrderBy(page => page.Order)
        .Select(page => new TemplatePageDetailsRowViewModel(page))
        .ToArray() ?? [];

    public TemplatePageRowViewModel? SelectedTemplatePage
    {
        get => TemplateWorkspaceState.SelectedTemplatePage;
        set
        {
            if (!ReferenceEquals(TemplateWorkspaceState.SelectedTemplatePage, value))
            {
                CaptureReferenceLearningInputs();
            }

            if (!ReferenceEquals(TemplateWorkspaceState.SelectedTemplatePage, value))
            {
                TemplateWorkspaceState.SelectedTemplatePage = value;
                var pageLoadVersion = Interlocked.Increment(ref _calibrationPageLoadVersion);
                OnPropertyChanged(nameof(SelectedCalibrationPage));
                OnPropertyChanged(nameof(CalibrationHeaderTitle));
                OnPropertyChanged(nameof(CalibrationFrameDisplayName));
                OnPropertyChanged(nameof(IsSelectedNormalComposition));
                OnPropertyChanged(nameof(IsSelectedDynamicSizeTable));
                if (_templateScreen == TemplateScreen.Canvas
                    && value is not null)
                {
                    _ = value.ContentType == TemplatePageContentTypeOption.NormalComposition
                        ? LoadCalibrationPageAsync(value, pageLoadVersion)
                        : ClearCalibrationForDynamicPageAsync(pageLoadVersion);
                }
                else if (_templateScreen == TemplateScreen.ReferenceLearning && value is not null)
                {
                    DisplayReferenceLearningPage(value);
                }
            }
        }
    }

    public TemplatePageRowViewModel? SelectedCalibrationPage
    {
        get => IsTemplateCalibrationVisible ? SelectedTemplatePage : null;
        set
        {
            // The hidden, filtered calibration list must not clear the page-settings selection.
            if (IsTemplateCalibrationVisible)
            {
                SelectedTemplatePage = value;
            }
        }
    }

    public TemplateLibraryCardViewModel? SelectedTemplate
    {
        get => TemplateWorkspaceState.SelectedTemplate;
        set => TemplateWorkspaceState.SelectedTemplate = value;
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
            string? lastImportedPageId = null;
            foreach (var path in paths)
            {
                var pending = new PendingTemplateImport(
                    $"command_{Guid.NewGuid():N}",
                    $"resource_{Guid.NewGuid():N}",
                    path,
                    Path.GetFileNameWithoutExtension(path),
                    DuplicateResourceId: null);
                if (!await ImportPendingAsync(pending, TemplateDuplicateDecision.AutoReuseDraftDuplicates).ConfigureAwait(true))
                {
                    break;
                }

                lastImportedPageId = ImportedTemplateFiles.LastOrDefault()?.PageId;
            }

            await LoadDraftPagesAsync(populateImportedFiles: false).ConfigureAwait(true);
            SelectedTemplatePage = TemplatePages.FirstOrDefault(page =>
                string.Equals(page.PageId, lastImportedPageId, StringComparison.Ordinal))
                ?? TemplatePages.LastOrDefault(page => page.IsAffected)
                ?? TemplatePages.FirstOrDefault();
            SetTemplateScreen(TemplateScreen.Canvas);
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

    public async Task ChoosePsdFolderAsync()
    {
        if (_folderPicker is null)
        {
            TemplateStatusMessage = "本机文件夹选择器不可用。";
            return;
        }

        var selected = await _folderPicker.PickFolderAsync(
            "选择包含模板 PSD 的文件夹（只读取直属 PSD）",
            null,
            CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(
                    Path.GetFullPath(selected),
                    "*.psd",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TemplateStatusMessage = "无法读取所选文件夹，请检查权限后重试。";
            return;
        }

        if (paths.Length == 0)
        {
            TemplateStatusMessage = "所选文件夹中没有直属 PSD 文件；请确认 PSD 位于该文件夹内。";
            return;
        }

        await ImportPsdFilesAsync(paths).ConfigureAwait(true);
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

    public Task CommitCalibrationGeometryAsync() => IsReferenceLearningReviewMode
        ? Task.CompletedTask
        : ApplyCalibrationGeometryAsync();

    public void SelectCalibrationFrame(string frameId)
    {
        if (IsReferenceLearningReviewMode)
        {
            SelectReferenceLearningFrame(frameId);
            return;
        }

        if (CalibrationDraft?.Frames.Any(frame => string.Equals(
                frame.FrameId,
                frameId,
                StringComparison.Ordinal)) != true)
        {
            return;
        }

        var frame = CalibrationDraft.Frames.First(candidate => string.Equals(
            candidate.FrameId,
            frameId,
            StringComparison.Ordinal));
        CalibrationDraft = CalibrationDraft with
        {
            Frames = CalibrationDraft.Frames.Select(candidate => candidate with
            {
                IsSelected = string.Equals(candidate.FrameId, frameId, StringComparison.Ordinal)
            }).ToArray(),
            SelectedFrameId = frameId,
            SelectedAssetId = frame.BoundAssetId,
            SelectedScale = frame.Scale,
            SelectedOffsetX = frame.OffsetX,
            SelectedOffsetY = frame.OffsetY,
            SelectionRequirement = frame.SelectionRequirement
        };
        CalibrationAssetIdInput = frame.BoundAssetId ?? string.Empty;
        UpdateCalibrationInputs(CalibrationDraft);
    }

    public void SetWindowWidth(double width)
    {
        if (TemplateWorkspaceState.SetWindowWidth(width))
        {
            NotifyReferenceLearningStateChanged();
        }
    }

    private void InitializeTemplates()
    {
        RefreshTemplatesCommand = new AsyncRelayCommand(LoadTemplateLibraryAsync, () => !TemplateBusy);
        NewTemplateCommand = new RelayCommand(BeginNewTemplate);
        OpenTemplateCommand = new AsyncRelayCommand<TemplateLibraryCardViewModel>(OpenTemplateAsync);
        RenameTemplateCommand = new AsyncRelayCommand<TemplateLibraryCardViewModel>(
            RenameTemplateAsync,
            HandleTemplateCommandException);
        DeleteTemplateFromLibraryCommand = new AsyncRelayCommand<TemplateLibraryCardViewModel>(
            DeleteTemplateFromLibraryAsync,
            HandleTemplateCommandException);
        EditTemplateGroupCommand = new AsyncRelayCommand(
            BeginTemplateGroupEditAsync,
            onError: HandleTemplateCommandException);
        EditTemplatePageCommand = new AsyncRelayCommand<TemplatePageDetailsRowViewModel>(
            BeginTemplatePageEditAsync,
            HandleTemplateCommandException);
        BackToTemplateLibraryCommand = new AsyncRelayCommand(BackToLibraryAsync);
        AddTemplatePagesCommand = new AsyncRelayCommand(BeginIncrementAsync, () => _templateDetails is not null);
        RemoveTemplatePageCommand = new AsyncRelayCommand<TemplatePageRowViewModel>(RemoveTemplatePageAsync);
        RemoveImportedPsdCommand = new AsyncRelayCommand<ImportedPsdRowViewModel>(RemoveImportedPsdAsync);
        AbandonTemplateDraftCommand = new AsyncRelayCommand(
            AbandonTemplateDraftAsync,
            onError: HandleTemplateCommandException);
        KeepExistingDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.ReuseExisting));
        KeepImportedDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.ContinueAsSeparate));
        CancelDuplicateCommand = new AsyncRelayCommand(
            () => ResolveDuplicateAsync(TemplateDuplicateDecision.Cancel));
        ConfirmTemplateDialogCommand = new RelayCommand(
            () => CompleteTemplateDialog(TemplateDialogChoice.Primary),
            () => !TemplateDialogHasTextInput || !string.IsNullOrWhiteSpace(TemplateDialogInputText));
        SecondaryTemplateDialogCommand = new RelayCommand(
            () => CompleteTemplateDialog(TemplateDialogChoice.Secondary));
        TertiaryTemplateDialogCommand = new RelayCommand(
            () => CompleteTemplateDialog(TemplateDialogChoice.Tertiary));
        StartCalibrationCommand = new AsyncRelayCommand(StartCalibrationAsync, () => TemplatePages.Count > 0);
        SelectCalibrationAssetCommand = new AsyncRelayCommand(SelectCalibrationAssetAsync);
        ClearCalibrationAssetCommand = new AsyncRelayCommand(ClearCalibrationAssetAsync, () => CanClearCalibrationAsset);
        UndoCalibrationCommand = new AsyncRelayCommand(() => ApplyCalibrationHistoryAsync(CalibrationUndoAction.Undo));
        RedoCalibrationCommand = new AsyncRelayCommand(() => ApplyCalibrationHistoryAsync(CalibrationUndoAction.Redo));
        ApplyCalibrationGeometryCommand = new AsyncRelayCommand(ApplyCalibrationGeometryAsync);
        ApplyAndConfirmCalibrationCommand = new AsyncRelayCommand(
            ApplyAndConfirmCalibrationAsync,
            () => CanApplyAndConfirmCalibration);
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync, () => CanSaveTemplateCanvas);
        ExportTemplateCommand = new AsyncRelayCommand(ExportTemplateAsync);
        ImportTemplatePackageCommand = new AsyncRelayCommand(ImportTemplatePackageAsync);
        SaveTemplateAsCommand = new AsyncRelayCommand(SaveTemplateAsAsync);
        DeleteTemplateCommand = new AsyncRelayCommand(
            DeleteTemplateAsync,
            onError: HandleTemplateCommandException);
        ShowCalibrationPagesCommand = new RelayCommand(() => SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel.Pages));
        ShowCalibrationCanvasCommand = new RelayCommand(() => SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel.Canvas));
        ShowCalibrationInspectorCommand = new RelayCommand(() => SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel.Inspector));
        ImportedTemplateFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasImportedTemplateFiles));
            RaiseTemplateCommandCanExecuteChanged();
        };
        TemplatePages.CollectionChanged += (_, _) => RaiseTemplateCommandCanExecuteChanged();
        TemplatePages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCalibratableTemplatePages));
            OnPropertyChanged(nameof(CalibrationPages));
            OnPropertyChanged(nameof(CalibrationStartActionText));
            OnPropertyChanged(nameof(CalibrationModeHelpText));
            OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        };
        TemplateGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BatchTemplateGroups));
    }

    internal void OnNavigationChanged()
    {
        if (_host.IsTemplateSectionVisible)
        {
            _ = LoadTemplateLibraryAsync();
        }
    }

    public async Task LoadTemplateLibraryAsync()
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
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
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
        SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
        ResetReferenceLearningDraft();
        NotifyTemplateDraftChanged();
        SetTemplateScreen(TemplateScreen.Canvas);
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
                SetTemplateScreen(TemplateScreen.Canvas);
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

    private async Task RenameTemplateAsync(TemplateLibraryCardViewModel? item)
    {
        if (_templateWorkspace is null || item is null || TemplateBusy)
        {
            return;
        }

        var confirmation = await ShowTemplateRenameDialogAsync(item.Name).ConfigureAwait(true);
        if (confirmation != TemplateDialogChoice.Primary)
        {
            return;
        }

        var newName = TemplateDialogInputText.Trim();
        if (string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            TemplateStatusMessage = "模板组名称未改变。";
            return;
        }

        TemplateBusy = true;
        try
        {
            await _templateWorkspace.RenameTemplateGroupAsync(
                item.GroupId,
                newName,
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            TemplateBusy = false;
        }

        await LoadTemplateLibraryAsync().ConfigureAwait(true);
        TemplateStatusMessage = $"模板组已重命名为“{newName}”。";
    }

    private Task DeleteTemplateFromLibraryAsync(TemplateLibraryCardViewModel? item) =>
        item is null
            ? Task.CompletedTask
            : DeleteTemplateGroupAsync(item.GroupId, item.Name);

    private async Task BackToLibraryAsync()
    {
        SetTemplateScreen(TemplateScreen.Library);
        try
        {
            await LoadTemplateLibraryAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Navigation commands are async-void ICommand boundaries. A local
            // store/cleanup failure must not close the WPF main window.
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
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
            SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
            ResetReferenceLearningDraft();
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.Canvas);
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
                : ToUserMessage(result.Import.Error.Code);
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
    }

    private async Task RemoveImportedPsdAsync(ImportedPsdRowViewModel? item)
    {
        if (_templateWorkspace is null || _templateDraft is null || item is null)
        {
            return;
        }

        var confirmation = await ShowTemplateDialogAsync(
            "移除这个 PSD？",
            $"将从当前模板草稿中移除“{item.FileName}”，它不会再参与后续页面设置和学习。",
            "只移除应用内部草稿记录，不会删除用户源 PSD。",
            "移除 PSD",
            "取消",
            isDestructive: true).ConfigureAwait(true);
        if (confirmation != TemplateDialogChoice.Primary)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            await _templateWorkspace.RemovePageAsync(
                _templateDraft,
                item.PageId,
                CancellationToken.None).ConfigureAwait(true);

            ImportedTemplateFiles.Remove(item);
            var page = TemplatePages.FirstOrDefault(candidate => string.Equals(
                candidate.PageId,
                item.PageId,
                StringComparison.Ordinal));
            if (page is not null)
            {
                var index = TemplatePages.IndexOf(page);
                TemplatePages.Remove(page);
                UpdatePageOrderLabels();
                SelectedTemplatePage = TemplatePages.ElementAtOrDefault(Math.Max(0, index - 1))
                    ?? TemplatePages.FirstOrDefault();
            }

            OnPropertyChanged(nameof(HasImportedTemplateFiles));
            TemplateStatusMessage = "PSD 已从当前草稿移除；用户源 PSD 保持只读未改动。";
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
        }
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
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
            TemplatePages.Add(new TemplatePageRowViewModel(
                page,
                OnTemplatePageChanged,
                OnTemplatePagePresentationChanged));
        }

        SelectedTemplatePage = TemplatePages.FirstOrDefault(page => page.IsAffected)
            ?? TemplatePages.FirstOrDefault();
    }

    private void OnTemplatePageChanged(TemplatePageRowViewModel page)
    {
        OnPropertyChanged(nameof(HasCalibratableTemplatePages));
        OnPropertyChanged(nameof(CalibrationPages));
        OnPropertyChanged(nameof(CalibrationStartActionText));
        OnPropertyChanged(nameof(IsSelectedNormalComposition));
        OnPropertyChanged(nameof(IsSelectedDynamicSizeTable));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
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

        if (!HasCalibratableTemplatePages)
        {
            CalibrationDraft = null;
            TemplateStatusMessage = "请设置尺码表区域与样式；未设置的页面可以保存，但会阻止批次渲染。";
            SetTemplateScreen(TemplateScreen.Canvas);
            return;
        }

        if (SelectedTemplatePage is null
            || SelectedTemplatePage.ContentType != TemplatePageContentTypeOption.NormalComposition)
        {
            SelectedTemplatePage = CalibrationPages.FirstOrDefault(page => page.IsAffected)
                ?? (CalibrationPages.Count > 0 ? CalibrationPages[0] : null);
        }

        TemplateStatusMessage = IsFullTemplateEdit
            ? "模板组页面设置已保存；下一步进入统一校准编辑器。"
            : IsIncrementalTemplateDraft
            ? "新增页面设置已保存；下一步只校准新增页。"
            : "页面设置已保存；下一步进入统一校准编辑器。";
        if (IsReferenceLearningModeSelected)
        {
            await StartReferenceLearningAsync().ConfigureAwait(true);
            return;
        }
        if (_templateCalibration is null || SelectedTemplatePage is null)
        {
            SetTemplateScreen(TemplateScreen.Canvas);
            return;
        }

        try
        {
            SetTemplateScreen(TemplateScreen.Canvas);
            var pageLoadVersion = Interlocked.Increment(ref _calibrationPageLoadVersion);
            await LoadCalibrationPageAsync(SelectedTemplatePage, pageLoadVersion).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            TemplateStatusMessage = ToUserMessage(exception.Message);
            SetTemplateScreen(TemplateScreen.Canvas);
        }
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

        var confirmation = await ShowTemplateDialogAsync(
            "移除模板页面？",
            $"将从当前模板草稿中移除“{page.Name}”。保存模板组后，这一页不会再参与后续批次。",
            "只移除应用内部副本，不会删除用户源 PSD。",
            "移除页面",
            "取消",
            isDestructive: true).ConfigureAwait(true);
        if (confirmation != TemplateDialogChoice.Primary)
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

        try
        {
            var confirmation = await ShowTemplateDialogAsync(
                "放弃当前模板组？",
                "将删除当前模板草稿，以及软件内部为这个草稿保存的 PSD 副本和校准输入。此操作无法在应用内撤销。",
                "不会删除用户源 PSD、源照片、正式输出或其他模板组。",
                "放弃模板组",
                "取消",
                isDestructive: true).ConfigureAwait(true);
            if (confirmation != TemplateDialogChoice.Primary)
            {
                return;
            }

            TemplateBusy = true;
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
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task BeginTemplatePageEditAsync(TemplatePageDetailsRowViewModel? page)
    {
        if (_templateWorkspace is null || _templateDetails is null || page is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDraft = await _templateWorkspace.BeginPageEditAsync(
                _templateDetails.GroupId,
                page.PageId,
                CancellationToken.None).ConfigureAwait(true);
            TemplateGroupName = _templateDraft.GroupName;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
            ResetReferenceLearningDraft();
            await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
            TemplateStatusMessage = $"已进入“{page.Name}”编辑草稿；其他页面保持只读，保存后才生成新修订。";
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private async Task BeginTemplateGroupEditAsync()
    {
        if (_templateWorkspace is null || _templateDetails is null)
        {
            return;
        }

        TemplateBusy = true;
        try
        {
            _templateDraft = await _templateWorkspace.BeginFullEditAsync(
                _templateDetails.GroupId,
                CancellationToken.None).ConfigureAwait(true);
            TemplateGroupName = _templateDraft.GroupName;
            ImportedTemplateFiles.Clear();
            TemplatePages.Clear();
            SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
            ResetReferenceLearningDraft();
            await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
            TemplateStatusMessage = "已进入模板组全量编辑草稿；全部页面均可修改，原模板修订仍保持不变。";
            NotifyTemplateDraftChanged();
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception exception)
        {
            TemplateStatusMessage = ToTemplateOperationError(exception);
        }
        finally
        {
            TemplateBusy = false;
        }
    }

    private void HandleTemplateCommandException(Exception exception)
    {
        TemplateBusy = false;
        TemplateStatusMessage = ToTemplateOperationError(exception);
        if (IsReferenceLearningReviewMode)
        {
            ReferenceLearningStatusMessage = "模板操作未完成，但主窗口仍保持打开；请重试或返回页面设置。";
        }
    }

    private async Task LoadCalibrationPageAsync(
        TemplatePageRowViewModel page,
        long pageLoadVersion)
    {
        var calibration = _templateCalibration;
        var draftSession = _templateDraft;
        if (calibration is null || draftSession is null)
        {
            return;
        }

        var draft = await calibration.LoadAsync(
            draftSession,
            page.PageId,
            CancellationToken.None).ConfigureAwait(true);
        if (pageLoadVersion != Volatile.Read(ref _calibrationPageLoadVersion)
            || _templateScreen != TemplateScreen.Canvas
            || !ReferenceEquals(SelectedTemplatePage, page)
            || !ReferenceEquals(_templateDraft, draftSession))
        {
            return;
        }

        CalibrationDraft = draft;
        UpdateCalibrationInputs(draft);
    }

    private void OnTemplatePagePresentationChanged(TemplatePageRowViewModel page)
    {
        if (!page.IsReadOnly)
        {
            _ = SavePagePresentationAsync(page);
        }
    }

    private async Task SavePagePresentationAsync(TemplatePageRowViewModel page)
    {
        if (_templateWorkspace is not ITemplatePagePresentationService presentation
            || _templateDraft is null)
        {
            return;
        }

        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            await presentation.UpdatePagePresentationAsync(
                _templateDraft,
                new TemplatePagePresentationUpdate(page.PageId, page.Notes, page.CanvasX, page.CanvasY),
                CancellationToken.None).ConfigureAwait(true);
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

    private Task ClearCalibrationForDynamicPageAsync(long pageLoadVersion)
    {
        if (pageLoadVersion != Volatile.Read(ref _calibrationPageLoadVersion)
            || !IsSelectedDynamicSizeTable)
        {
            return Task.CompletedTask;
        }

        CalibrationDraft = null;
        CalibrationAssetIdInput = string.Empty;
        CalibrationFrameIdInput = string.Empty;
        OnPropertyChanged(nameof(CalibrationDraft));
        OnPropertyChanged(nameof(CalibrationFrameItems));
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationPreviewImageSource));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        return Task.CompletedTask;
    }

    public async Task ConfigureSizeTableAsync(Window owner)
    {
        if (_templateWorkspace is not ISizeTableTemplateCalibrationService calibrationService
            || _templateDraft is null || SelectedTemplatePage is not { IsReadOnly: false } selected
            || !IsSelectedDynamicSizeTable) return;
        await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            var session = _templateDraft;
            var draft = await _templateWorkspace.GetDraftAsync(session, CancellationToken.None).ConfigureAwait(true);
            var page = draft.Pages.Single(item => item.Page.PageId == selected.PageId);
            var dialog = new SizeTableCalibrationDialog(page) { Owner = owner };
            if (dialog.ShowDialog() != true || dialog.Calibration is null) return;
            await calibrationService.CalibrateSizeTableAsync(session, selected.PageId, dialog.Calibration, CancellationToken.None).ConfigureAwait(true);
            await LoadDraftPagesAsync(populateImportedFiles: false).ConfigureAwait(true);
            TemplateStatusMessage = "尺码表区域与样式已保存到草稿；保存模板组后用于新批次。";
            SelectedTemplatePage = TemplatePages.Single(item => item.PageId == selected.PageId);
        }
        catch (InvalidOperationException error) { TemplateStatusMessage = ToUserMessage(error.Message); }
        finally { _templateDraftWrite.Release(); }
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

    private Task ApplyAndConfirmCalibrationAsync() => IsReferenceLearningReviewMode
        ? ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.Accepted)
        : ApplyCalibrationGeometryAsync();

    private async Task SaveTemplateAsync()
    {
        if (IsReferenceLearningReviewMode)
        {
            await CommitReferenceLearningAsync().ConfigureAwait(true);
            return;
        }

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
        if (_templateWorkspace is not null
            && _templateDraft is not null
            && _templateDetails is not null
            && IsFullTemplateEdit
            && IsManualCalibrationReviewMode)
        {
            TemplateBusy = true;
            try
            {
                _templateDetails = await _templateWorkspace.CommitDraftAsNewGroupAsync(
                    _templateDraft,
                    _templateDetails.Name + " 副本",
                    CancellationToken.None).ConfigureAwait(true);
                _templateDraft = null;
                TemplatePages.Clear();
                ImportedTemplateFiles.Clear();
                TemplateStatusMessage = "已将本次全量规则修改另存为独立模板组；原模板组保持不变。";
                OnPropertyChanged(nameof(TemplateDetailsTitle));
                OnPropertyChanged(nameof(TemplateDetailsMeta));
                OnPropertyChanged(nameof(CanIncrementTemplate));
                NotifyTemplateDraftChanged();
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

            return;
        }

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

    private Task DeleteTemplateAsync() =>
        _templateDetails is null
            ? Task.CompletedTask
            : DeleteTemplateGroupAsync(_templateDetails.GroupId, _templateDetails.Name);

    private async Task DeleteTemplateGroupAsync(string groupId, string groupName)
    {
        if (_templateWorkspace is null || TemplateBusy)
        {
            return;
        }

        var confirmation = await ShowTemplateDialogAsync(
            "删除模板组？",
            $"将删除“{groupName}”在软件内部保存的 PSD 副本、页面规则和校准结果。此操作无法在应用内撤销。",
            "不会删除最初导入的 PSD、已生成结果或其他模板组。",
            "删除模板组",
            "取消",
            isDestructive: true).ConfigureAwait(true);
        if (confirmation != TemplateDialogChoice.Primary)
        {
            return;
        }

        TemplateBusy = true;
        IReadOnlyList<string> blockers;
        try
        {
            blockers = await _templateWorkspace.DeleteTemplateGroupAsync(
                groupId,
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            TemplateBusy = false;
        }

        if (blockers.Count > 0)
        {
            TemplateStatusMessage = "存在未完成引用任务，不能删除：" + string.Join("、", blockers);
            await ShowTemplateDialogAsync(
                "模板组正在使用，无法删除",
                "以下未完成任务仍引用此模板组：\n" + string.Join("\n", blockers.Select(blocker => $"• {blocker}")),
                "请先完成或取消这些任务。用户源文件和模板副本均未改变。",
                "知道了",
                secondaryText: null).ConfigureAwait(true);
            return;
        }

        _templateDetails = null;
        SetTemplateScreen(TemplateScreen.Library);
        await LoadTemplateLibraryAsync().ConfigureAwait(true);
        TemplateStatusMessage = "模板组已删除；用户源 PSD 和正式输出未受影响。";
    }

    private Task<TemplateDialogChoice> ShowTemplateDialogAsync(
        string title,
        string body,
        string safetyNote,
        string primaryText,
        string? secondaryText,
        string? tertiaryText = null,
        bool isDestructive = false) =>
        ShowTemplateDialogCoreAsync(
            title,
            body,
            safetyNote,
            primaryText,
            secondaryText,
            tertiaryText,
            isDestructive,
            hasTextInput: false,
            inputText: "");

    private Task<TemplateDialogChoice> ShowTemplateRenameDialogAsync(string currentName) =>
        ShowTemplateDialogCoreAsync(
            "重命名模板组",
            $"为“{currentName}”输入新的模板组名称。",
            "只修改模板库中的显示名称；页面规则、内部 PSD 副本和既有批次快照保持不变。",
            "保存名称",
            "取消",
            tertiaryText: null,
            isDestructive: false,
            hasTextInput: true,
            inputText: currentName);

    private Task<TemplateDialogChoice> ShowTemplateDialogCoreAsync(
        string title,
        string body,
        string safetyNote,
        string primaryText,
        string? secondaryText,
        string? tertiaryText,
        bool isDestructive,
        bool hasTextInput,
        string inputText)
    {
        if (_templateDialogCompletion is not null)
        {
            throw new InvalidOperationException("A template dialog is already open.");
        }

        TemplateDialogTitle = title;
        TemplateDialogBody = body;
        TemplateDialogSafetyNote = safetyNote;
        TemplateDialogPrimaryText = primaryText;
        TemplateDialogSecondaryText = secondaryText ?? "";
        TemplateDialogTertiaryText = tertiaryText ?? "";
        TemplateDialogHasSecondaryAction = !string.IsNullOrWhiteSpace(secondaryText);
        TemplateDialogHasTertiaryAction = !string.IsNullOrWhiteSpace(tertiaryText);
        TemplateDialogIsDestructive = isDestructive;
        TemplateDialogInputText = inputText;
        TemplateDialogHasTextInput = hasTextInput;
        IsTemplateDialogVisible = true;

        _templateDialogCompletion = new TaskCompletionSource<TemplateDialogChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _templateDialogCompletion.Task;
    }

    private void CompleteTemplateDialog(TemplateDialogChoice choice)
    {
        var completion = _templateDialogCompletion;
        if (completion is null)
        {
            return;
        }

        _templateDialogCompletion = null;
        IsTemplateDialogVisible = false;
        completion.TrySetResult(choice);
    }

    private enum TemplateDialogChoice
    {
        Primary,
        Secondary,
        Tertiary
    }

    private void UpdateCalibrationInputs(TemplateCalibrationDraftView draft)
    {
        OnPropertyChanged(nameof(CalibrationDraft));
        OnPropertyChanged(nameof(CalibrationFrameItems));
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
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
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
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private void SetCalibrationNarrowPanel(TemplateWorkspaceNarrowPanel panel) =>
        TemplateWorkspaceState.SetNarrowPanel(panel);

    private void UpdatePageOrderLabels()
    {
        for (var index = 0; index < TemplatePages.Count; index++)
        {
            TemplatePages[index].SetDisplayOrder(index + 1);
        }
    }

    private void SetTemplateScreen(TemplateScreen screen)
    {
        if (screen != TemplateScreen.ReferenceLearning)
        {
            CaptureReferenceLearningInputs();
        }

        _templateScreen = screen;
        if (screen is not (TemplateScreen.Canvas or TemplateScreen.ReferenceLearning))
        {
            Interlocked.Increment(ref _calibrationPageLoadVersion);
        }
        OnPropertyChanged(nameof(IsTemplateLibraryVisible));
        OnPropertyChanged(nameof(IsTemplateDetailsVisible));
        OnPropertyChanged(nameof(IsTemplateCalibrationVisible));
        OnPropertyChanged(nameof(IsTemplateCanvasVisible));
        OnPropertyChanged(nameof(SelectedCalibrationPage));
        OnPropertyChanged(nameof(IsReferenceLearningReviewMode));
        OnPropertyChanged(nameof(IsManualCalibrationReviewMode));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticPending));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticWarningVisible));
        OnPropertyChanged(nameof(CalibrationFrameItems));
        OnPropertyChanged(nameof(CalibrationWorkflowStatusMessage));
        OnPropertyChanged(nameof(CalibrationFlowStepText));
        OnPropertyChanged(nameof(CanSaveCurrentCalibration));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationPreviewImageSource));
        OnPropertyChanged(nameof(CalibrationEditorHintText));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        OnPropertyChanged(nameof(IsSelectedNormalComposition));
        OnPropertyChanged(nameof(IsSelectedDynamicSizeTable));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
    }

    private void NotifyTemplateDraftChanged()
    {
        OnPropertyChanged(nameof(IsIncrementalTemplateDraft));
        OnPropertyChanged(nameof(IsTemplatePageEditDraft));
        OnPropertyChanged(nameof(IsFullTemplateEdit));
        OnPropertyChanged(nameof(CanEditTemplateGroup));
        OnPropertyChanged(nameof(CanSaveAsTemplate));
        OnPropertyChanged(nameof(TemplateCanvasTitle));
        OnPropertyChanged(nameof(TemplateCanvasSubtitle));
        OnPropertyChanged(nameof(TemplateCanvasStatusText));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private void RaiseTemplateCommandCanExecuteChanged()
    {
        (RefreshTemplatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddTemplatePagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartCalibrationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearCalibrationAssetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveTemplateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EditTemplateGroupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveTemplateAsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
        "SIZE_TABLE_CALIBRATION_INVALID" => "尺码表区域、图层或样式与模板不匹配，请重新设置。",
        "TPL_PAGE_NAME_INVALID" => "页面名称不能为空，且不能超过 256 个字符。",
        "TPL_PAGE_VARIANT_CAPACITY_INVALID" => "按变体数量组合时必须填写大于 0 的容量。",
        "TPL_REVISION_PAGE_SET_EMPTY" => "模板组至少保留一个页面。",
        "TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY" => "增量流程默认不修改原页面，请只设置新增页面。",
        "TPL_CALIBRATION_ASSET_REF_INVALID" => "请选择可读取的 JPG、PNG、WebP、TIFF 或 BMP 代表款素材。",
        "TPL_CALIBRATION_ASSET_SOURCE_UNAVAILABLE" => "本机代表款素材只读检查服务不可用。",
        "TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE" => "PSD 图片框的结构证据不完整，已阻止注册；请重新检查模板。",
        "TPL_SOURCE_UNREADABLE" => "应用内部 PSD 副本不可读取；请重新导入对应 PSD。",
        "TPL_PSD_UNSUPPORTED_STRUCTURE" => "这个 PSD 的结构暂不支持自动学习；请确认保留了可读的智能对象，或改用拖拽校准。",
        "TPL_PSD_RESOURCE_LIMIT" => "这个 PSD 过大或结构复杂，检查器已安全停止；请改用较小的副本或 Photoshop 兼容性检查。",
        "TPL_PSD_WORKER_UNAVAILABLE" => "PSD 检查组件不可用；请使用带 psd-inspection-worker 的完整运行目录重试。",
        "TPL_PSD_WORKER_TIMEOUT" => "PSD 检查超时但源文件未被修改；请重试或改用 Photoshop 兼容性检查。",
        "TPL_PSD_WORKER_RESPONSE_TOO_LARGE" => "PSD 检查结果过大，已安全停止；请改用 Photoshop 兼容性检查。",
        "TPL_PSD_WORKER_FAILED" => "PSD 检查组件异常退出；请确认应用目录中的检查组件完整后重试。",
        "TPL_PSD_WORKER_INVALID_RESPONSE" or "TPL_PSD_INVALID_RESPONSE" => "PSD 检查组件返回了无效结果；请重启完整运行目录后重试。",
        "TPL_LEARN_SOURCE_MISSING" => "当前学习会话引用的应用内部 PSD 副本已不存在；请重新导入对应 PSD 后重新开始学习。",
        "TPL_DRAFT_IN_PROGRESS" => "已有未完成的模板编辑草稿；请先打开模板组恢复草稿，或放弃该草稿后再编辑。",
        "TPL_DELETE_FILES_FAILED" => "模板文件删除失败，文件可能被其他程序占用；请关闭相关程序后重试。",
        "TPL_DELETE_ACCESS_DENIED" => "没有权限删除模板文件；请检查文件权限或以管理员身份运行。",
        "TPL_DELETE_FILES_LOCKED" => "模板文件被占用无法删除；请关闭正在访问这些文件的程序后重试。",
        _ => $"操作未完成（{code}），请检查后重试。"
    };

    private static string ToTemplateOperationError(Exception exception)
    {
        var root = UnwrapTemplateOperationException(exception);
        return root switch
        {
            IOException or UnauthorizedAccessException =>
                "模板库或内部副本暂时不可访问；请关闭占用文件的程序后重试。",
            InvalidOperationException invalidOperation => ToUserMessage(invalidOperation.Message),
            JsonException or InvalidDataException =>
                "模板内部资源记录无法读取；模板组未被删除，请重新导入对应 PSD 后再试。",
            FormatException =>
                "模板修订记录格式异常；模板组未被删除，请重新打开模板组后再试。",
            ArgumentException or NullReferenceException =>
                "模板页面的内部资源信息不完整；模板组未被删除，请重新导入对应 PSD 后再试。",
            _ =>
                $"模板编辑草稿加载失败（程序内部：{root.GetType().Name}）；模板组未被删除，请将此提示截图交给技术处理。"
        };
    }

    private static Exception UnwrapTemplateOperationException(Exception exception)
    {
        while (exception is AggregateException aggregate
               && aggregate.InnerExceptions.Count == 1)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }

    private enum TemplateScreen
    {
        Library,
        Details,
        Canvas,
        ReferenceLearning
    }

    private sealed record PendingTemplateImport(
        string CommandId,
        string ResourceId,
        string SourcePath,
        string PageName,
        string? DuplicateResourceId);
    private void TemplateWorkspaceState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        switch (e.PropertyName)
        {
            case nameof(TemplateWorkspacePageState.CalibrationViewportZoom):
                OnPropertyChanged(nameof(CalibrationFooterMetaText));
                break;
            case nameof(TemplateWorkspacePageState.TemplateStatusMessage):
                OnPropertyChanged(nameof(HasTemplateStatusMessage));
                break;
            case nameof(TemplateWorkspacePageState.TemplateBusy):
                OnPropertyChanged(nameof(IsTemplateLibraryEmpty));
                OnPropertyChanged(nameof(CanEditTemplateGroup));
                OnPropertyChanged(nameof(CanSaveAsTemplate));
                RaiseTemplateCommandCanExecuteChanged();
                break;
        }
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        State.PropertyChanged -= TemplateWorkspaceState_PropertyChanged;
        _templateDraftWrite.Dispose();
        GC.SuppressFinalize(this);
    }
}

public interface ITemplateWorkspaceHost
{
    bool IsTemplateSectionVisible { get; }
    Task<bool> EnsureUploadConsentAsync();
}
