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
    internal event Action? ScreenChanging;

    public TemplateWorkspaceViewModel(
        ITemplateWorkspaceService? templateWorkspace,
        ITemplateCalibrationService? templateCalibration,
        ITemplatePackageService? templatePackages,
        IReferenceLearningService? referenceLearning,
        IReferenceLearningCommitService? referenceLearningCommit,
        IReferenceLearningAiService? referenceLearningAi,
        IFolderPicker? folderPicker,
        ITemplateWorkspaceHost host,
        ITemplateRuleInterpreter? ruleInterpreter = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ruleInterpreter = ruleInterpreter;
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
    private long _pageDialogVersion;

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
        : $"{_templateDetails.Pages.Count} 个页面 · "
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


}

public interface ITemplateWorkspaceHost
{
    bool IsTemplateSectionVisible { get; }
    Task<bool> EnsureUploadConsentAsync();
}
