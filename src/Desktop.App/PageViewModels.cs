using System.Collections;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Ai;
using Desktop.Application.Templates;
using Desktop.Application.Rendering;
using Desktop.Application.Diagnostics;

namespace Desktop.App;

public interface IBatchInputPageHost : IBatchInputHost, INotifyPropertyChanged
{
    bool IsBatchInputVisible { get; }
    IReadOnlyList<TemplateLibraryCardViewModel> BatchTemplateGroups { get; }
    Task ImportPsdFilesAsync(IReadOnlyList<string> paths);
    Task ChoosePsdFolderAsync();
    bool HasImportedTemplateFiles { get; }
}

public interface IResultReviewPageHost : IResultReviewHost, INotifyPropertyChanged
{
    bool IsResultOverviewVisible { get; }
    bool IsResultDetailVisible { get; }
    SizeTableEditorViewModel SizeTableEditorPage { get; }
}

public interface ITemplateWorkspacePageHost : INotifyPropertyChanged
{
    bool IsTemplateSectionVisible { get; }
}

public interface ISettingsPageHost : INotifyPropertyChanged
{
    bool IsSettingsVisible { get; }
    IReadOnlyList<string> AdditionalSettingsRows { get; }
    string AiUploadConsentStatusText { get; }
    string AiUploadConsentDetailText { get; }
    bool CanGrantAiUploadConsent { get; }
    bool CanRevokeAiUploadConsent { get; }
    ICommand RefreshAiConsentCommand { get; }
    ICommand GrantAiUploadConsentCommand { get; }
    ICommand RevokeAiUploadConsentCommand { get; }
}

/// <summary>
/// Forwards local coordinator changes and the explicitly selected host notifications.
/// Bindings resolve only against the page's declared properties.
/// </summary>
public abstract class PageViewModel : INotifyPropertyChanged, IDisposable
{
    private bool _disposed;
    private readonly INotifyPropertyChanged _host;
    private readonly HashSet<string> _hostPropertyNames;
    private readonly List<INotifyPropertyChanged> _sources = [];

    protected PageViewModel(INotifyPropertyChanged host, params string[] hostPropertyNames)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _hostPropertyNames = new HashSet<string>(hostPropertyNames, StringComparer.Ordinal);
        _host.PropertyChanged += Host_PropertyChanged;
    }

    protected void Observe(INotifyPropertyChanged source)
    {
        _sources.Add(source);
        source.PropertyChanged += Source_PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.PropertyChanged -= Host_PropertyChanged;
        foreach (var source in _sources)
            source.PropertyChanged -= Source_PropertyChanged;
        _sources.Clear();
        GC.SuppressFinalize(this);
    }

    private void Host_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || _hostPropertyNames.Contains(e.PropertyName))
            ForwardPropertyChanged(e);
    }

    private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        ForwardPropertyChanged(e);

    protected void ForwardPropertyChanged(PropertyChangedEventArgs e) =>
        PropertyChanged?.Invoke(this, e);
}

public sealed class BatchInputPageViewModel : PageViewModel
{
    private readonly IBatchInputPageHost _host;

    public BatchInputPageViewModel(IBatchInputPageHost host,
        IFolderPicker? folderPicker = null, IBatchInputWorkflow? workflow = null,
        ITemplateWorkspaceService? templateWorkspace = null)
        : base(host, nameof(IsBatchInputVisible), nameof(BatchTemplateGroups), nameof(HasImportedTemplateFiles))
    {
        _host = host;
        Coordinator = new BatchInputViewModel(folderPicker, workflow, templateWorkspace, host);
        Observe(Coordinator);
    }

    public BatchInputViewModel Coordinator { get; }
    public BatchInputPageState State => Coordinator.State;

    public bool IsBatchInputVisible => _host.IsBatchInputVisible;
    public IReadOnlyList<TemplateLibraryCardViewModel> BatchTemplateGroups => _host.BatchTemplateGroups;
    public TemplateLibraryCardViewModel? SelectedBatchTemplate
    {
        get => State.SelectedTemplate;
        set => Coordinator.SelectedBatchTemplate = value;
    }
    public ObservableCollection<BatchPageOptionViewModel> BatchPages => State.BatchPages;
    public bool IsBatchConfigurationEditable => State.IsBatchConfigurationEditable;
    public bool IsBatchStyleSourceOutput
    {
        get => Coordinator.IsBatchStyleSourceOutput;
        set => Coordinator.IsBatchStyleSourceOutput = value;
    }
    public bool IsBatchUnifiedOutput
    {
        get => Coordinator.IsBatchUnifiedOutput;
        set => Coordinator.IsBatchUnifiedOutput = value;
    }
    public bool CanChooseBatchOutputDirectory => Coordinator.CanChooseBatchOutputDirectory;
    public bool IsBatchAutomaticSelection
    {
        get => State.AutomaticSelection;
        set => State.AutomaticSelection = value;
    }
    public bool IsBatchManualSelection
    {
        get => State.ManualSelection;
        set => State.ManualSelection = value;
    }
    public bool BatchIncludeEditablePsd
    {
        get => State.IncludeEditablePsd;
        set => State.IncludeEditablePsd = value;
    }
    public string BatchTemplateHelperText => State.BatchTemplateHelperText;
    public string BatchInputDirectoryDisplay => State.BatchInputDirectoryDisplay;
    public string BatchOutputDirectoryDisplay => State.BatchOutputDirectoryDisplay;
    public string BatchRecognitionText => State.RecognitionText;
    public string BatchOutputConfirmationText => State.OutputConfirmationText;
    public string BatchPageSelectionSummary => State.BatchPageSelectionSummary;
    public IReadOnlyList<string> BatchOutputPreviewFiles => State.BatchOutputPreviewFiles;
    public string BatchOutputPreviewSummary => State.BatchOutputPreviewSummary;
    public string BatchDraftSaveText => State.BatchDraftSaveText;
    public string BatchStatusMessage => State.StatusMessage;
    public string BatchStatusState => State.BatchStatusState;
    public bool IsBatchScanning => State.IsBatchScanning;
    public bool HasBatchRetry => State.HasBatchRetry;
    public string BatchPrimaryActionText => State.BatchPrimaryActionText;
    public bool CanScanBatch => State.CanScanBatch;
    public ICommand CancelBatchInputCommand => Coordinator.CancelBatchInputCommand;
    public ICommand ChooseBatchInputDirectoryCommand => Coordinator.ChooseBatchInputDirectoryCommand;
    public ICommand ChooseBatchOutputDirectoryCommand => Coordinator.ChooseBatchOutputDirectoryCommand;
    public ICommand OpenBatchPageSelectionCommand => Coordinator.OpenBatchPageSelectionCommand;
    public ICommand ApplyBatchPageSelectionCommand => Coordinator.ApplyBatchPageSelectionCommand;
    public ICommand CancelBatchPageSelectionCommand => Coordinator.CancelBatchPageSelectionCommand;
    public ICommand ScanBatchInputCommand => Coordinator.ScanBatchInputCommand;
    public ICommand CancelBatchScanCommand => Coordinator.CancelBatchScanCommand;
    public ICommand RetryBatchScanCommand => Coordinator.RetryBatchScanCommand;
    public ICommand CreateTemplateFromBatchCommand => Coordinator.CreateTemplateFromBatchCommand;
    public ICommand ImportTemplateFromBatchCommand => Coordinator.ImportTemplateFromBatchCommand;
    public Task ImportPsdFilesAsync(IReadOnlyList<string> paths) => _host.ImportPsdFilesAsync(paths);
    public Task ChoosePsdFolderAsync() => _host.ChoosePsdFolderAsync();
    public bool HasImportedTemplateFiles => _host.HasImportedTemplateFiles;
    public bool IsBatchPageSelectionVisible => State.IsPageSelectionVisible;
    public bool? AreAllBatchPagesSelected
    {
        get => State.AreAllBatchPagesSelected;
        set => State.AreAllBatchPagesSelected = value;
    }
    public override void Dispose()
    {
        Coordinator.Dispose();
        base.Dispose();
    }
}

public sealed class ResultReviewPageViewModel : PageViewModel
{
    private readonly IResultReviewPageHost _host;

    public ResultReviewPageViewModel(IResultReviewPageHost host, IRenderBatchWorkflow? rendering = null, IRenderBatchOutputReader? outputs = null)
        : base(host, nameof(IsResultOverviewVisible), nameof(IsResultDetailVisible))
    {
        _host = host;
        Coordinator = new ResultReviewViewModel(host, rendering, outputs);
        Observe(Coordinator);
        Observe(host.SizeTableEditorPage);
    }

    public ResultReviewViewModel Coordinator { get; }
    public ResultReviewPageState State => Coordinator.State;

    public bool IsResultOverviewVisible => _host.IsResultOverviewVisible;
    public bool IsResultDetailVisible => _host.IsResultDetailVisible;
    public GridLength ResultCandidateColumnWidth => State.ResultCandidateColumnWidth;
    public GridLength ResultDetailGutterWidth => State.ResultDetailGutterWidth;
    public GridLength ResultAdjustmentColumnWidth => State.ResultAdjustmentColumnWidth;
    public string ResultStatusText => State.ResultStatusText;
    public string ResultSummaryText => State.ResultSummaryText;
    public string ResultFormalOutputText => State.ResultFormalOutputText;
    public ObservableCollection<ResultFilterViewModel> ResultFilters => State.ResultFilters;
    public ObservableCollection<ResultStyleGroupViewModel> VisibleResultStyleGroups => State.VisibleResultStyleGroups;
    public ObservableCollection<ResultStyleGroupViewModel> ResultStyleGroups => State.ResultStyleGroups;
    public ResultPageCardViewModel? SelectedResultPage => State.SelectedResultPage;
    public SizeTableEditorViewModel SizeTableEditorPage => _host.SizeTableEditorPage;
    public bool IsSizeTableEditorVisible => SizeTableEditorPage.IsSizeTableEditorVisible;
    public SizeTableEditorState SizeTableEditor => SizeTableEditorPage.SizeTableEditor;
    public ICommand OpenSizeTableEditorCommand => SizeTableEditorPage.OpenSizeTableEditorCommand;
    public ICommand CloseSizeTableEditorCommand => SizeTableEditorPage.CloseSizeTableEditorCommand;
    public ICommand AddSizeTableRowCommand => SizeTableEditorPage.AddSizeTableRowCommand;
    public ICommand RemoveSizeTableRowCommand => SizeTableEditorPage.RemoveSizeTableRowCommand;
    public ICommand AddSizeTableColumnCommand => SizeTableEditorPage.AddSizeTableColumnCommand;
    public ICommand RemoveSizeTableColumnCommand => SizeTableEditorPage.RemoveSizeTableColumnCommand;
    public ICommand SaveSizeTableCommand => SizeTableEditorPage.SaveSizeTableCommand;
    public ICommand SelectResultFilterCommand => Coordinator.SelectResultFilterCommand;
    public ICommand OpenResultPageCommand => Coordinator.OpenResultPageCommand;
    public ICommand ReturnToResultOverviewCommand => Coordinator.ReturnToResultOverviewCommand;
    public ICommand ReturnToPreflightCommand => Coordinator.ReturnToPreflightCommand;
    public ICommand PreviousResultPageCommand => Coordinator.PreviousResultPageCommand;
    public ICommand NextResultPageCommand => Coordinator.NextResultPageCommand;
    public ICommand StartRenderingCommand => Coordinator.StartRenderingCommand;
    public ICommand PauseRenderingCommand => Coordinator.PauseRenderingCommand;
    public ICommand CancelRenderingCommand => Coordinator.CancelRenderingCommand;
    public ICommand RefreshRenderingCommand => Coordinator.RefreshRenderingCommand;
    public ICommand RegenerateResultPageCommand => Coordinator.RegenerateResultPageCommand;
    public string RenderActionText => Coordinator.RenderActionText;
    public string RenderControlMessage => Coordinator.RenderControlMessage;

    public override void Dispose()
    {
        Coordinator.Dispose();
        base.Dispose();
    }
}

public sealed class TemplateWorkspacePageViewModel : PageViewModel
{
    private readonly ITemplateWorkspacePageHost _host;

    public TemplateWorkspacePageViewModel(ITemplateWorkspacePageHost host, TemplateWorkspaceViewModel coordinator)
        : base(host, nameof(IsTemplateSectionVisible))
    {
        _host = host;
        Coordinator = coordinator;
        Observe(Coordinator);
    }

    public TemplateWorkspaceViewModel Coordinator { get; }
    public TemplateWorkspacePageState State => Coordinator.State;

    public bool IsTemplateSectionVisible => _host.IsTemplateSectionVisible;
    public bool IsTemplateLibraryVisible => Coordinator.IsTemplateLibraryVisible;
    public bool IsTemplateLibraryEmpty => Coordinator.IsTemplateLibraryEmpty;
    public bool IsTemplateDetailsVisible => Coordinator.IsTemplateDetailsVisible;
    public bool IsTemplateCalibrationVisible => Coordinator.IsTemplateCalibrationVisible;
    public bool IsTemplateCanvasVisible => Coordinator.IsTemplateCanvasVisible;
    public bool HasTemplateGroups => Coordinator.HasTemplateGroups;
    public bool HasCalibratableTemplatePages => Coordinator.HasCalibratableTemplatePages;
    public bool IsSelectedNormalComposition => Coordinator.IsSelectedNormalComposition;
    public bool IsSelectedDynamicSizeTable => Coordinator.IsSelectedDynamicSizeTable;
    public bool HasTemplateStatusMessage => State.HasTemplateStatusMessage;
    public string TemplateStatusMessage => State.TemplateStatusMessage;
    public ObservableCollection<TemplateLibraryCardViewModel> TemplateGroups => Coordinator.TemplateGroups;
    public IReadOnlyList<TemplatePageDetailsRowViewModel> TemplateDetailPages => Coordinator.TemplateDetailPages;
    public string TemplateDetailsTitle => Coordinator.TemplateDetailsTitle;
    public string TemplateDetailsMeta => Coordinator.TemplateDetailsMeta;
    public bool IsTemplateDialogVisible => State.IsTemplateDialogVisible;
    public string TemplateDialogTitle => State.TemplateDialogTitle;
    public string TemplateDialogBody => State.TemplateDialogBody;
    public string TemplateDialogSafetyNote => State.TemplateDialogSafetyNote;
    public string TemplateDialogPrimaryText => State.TemplateDialogPrimaryText;
    public string TemplateDialogSecondaryText => State.TemplateDialogSecondaryText;
    public string TemplateDialogTertiaryText => State.TemplateDialogTertiaryText;
    public bool TemplateDialogHasSecondaryAction => State.TemplateDialogHasSecondaryAction;
    public bool TemplateDialogHasTertiaryAction => State.TemplateDialogHasTertiaryAction;
    public bool TemplateDialogIsDestructive => State.TemplateDialogIsDestructive;
    public bool TemplateDialogHasTextInput => State.TemplateDialogHasTextInput;
    public string TemplateDialogInputText
    {
        get => State.TemplateDialogInputText;
        set => Coordinator.TemplateDialogInputText = value;
    }
    public bool IsDuplicateReviewVisible => State.IsDuplicateReviewVisible;
    public string DuplicateExistingLabel => Coordinator.DuplicateExistingLabel;
    public string DuplicateImportedLabel => Coordinator.DuplicateImportedLabel;

    public bool IsCalibrationNarrowLayout => State.IsCalibrationNarrowLayout;
    public bool IsCalibrationPagePanelVisible => State.IsCalibrationPagePanelVisible;
    public bool IsCalibrationCanvasPanelVisible => State.IsCalibrationCanvasPanelVisible;
    public bool IsCalibrationInspectorPanelVisible => State.IsCalibrationInspectorPanelVisible;
    public GridLength CalibrationPageColumnWidth => State.CalibrationPageColumnWidth;
    public GridLength CalibrationCanvasColumnWidth => State.CalibrationCanvasColumnWidth;
    public GridLength CalibrationInspectorColumnWidth => State.CalibrationInspectorColumnWidth;
    public GridLength CalibrationPanelGapWidth => State.CalibrationPanelGapWidth;
    public IReadOnlyList<TemplatePageRowViewModel> CalibrationPages => Coordinator.CalibrationPages;
    public ObservableCollection<TemplatePageRowViewModel> TemplatePages => Coordinator.TemplatePages;
    public TemplatePageRowViewModel? SelectedTemplatePage
    {
        get => State.SelectedTemplatePage;
        set => Coordinator.SelectedTemplatePage = value;
    }
    public TemplatePageRowViewModel? SelectedCalibrationPage
    {
        get => Coordinator.SelectedCalibrationPage;
        set => Coordinator.SelectedCalibrationPage = value;
    }
    public IEnumerable CalibrationFrameItems => Coordinator.CalibrationFrameItems;
    public TemplateCalibrationDraftView? CalibrationDraft => Coordinator.CalibrationDraft;
    public ObservableCollection<ReferenceLearningPhotoViewModel> ReferenceLearningPhotos => Coordinator.ReferenceLearningPhotos;
    public ReferenceLearningPhotoViewModel? SelectedReferenceLearningPhoto
    {
        get => Coordinator.SelectedReferenceLearningPhoto;
        set => Coordinator.SelectedReferenceLearningPhoto = value;
    }

    public bool IsReferenceLearningReviewMode => Coordinator.IsReferenceLearningReviewMode;
    public bool IsManualCalibrationReviewMode => Coordinator.IsManualCalibrationReviewMode;
    public bool IsReferenceLearningManualFallbackVisible => Coordinator.IsReferenceLearningManualFallbackVisible;
    public bool IsReferenceLearningSemanticWarningVisible => Coordinator.IsReferenceLearningSemanticWarningVisible;
    public bool CanEditCalibration => Coordinator.CanEditCalibration;
    public bool CanClearCalibrationAsset => Coordinator.CanClearCalibrationAsset;
    public bool CanEditReferenceLearningRule => Coordinator.CanEditReferenceLearningRule;
    public bool CanApplyAndConfirmCalibration => Coordinator.CanApplyAndConfirmCalibration;
    public bool CanConfirmReferenceLearningPage => Coordinator.CanConfirmReferenceLearningPage;
    public bool CanConfirmAllQualifiedReferenceLearning => Coordinator.CanConfirmAllQualifiedReferenceLearning;
    public bool CanSaveTemplateCanvas => Coordinator.CanSaveTemplateCanvas;
    public bool CanSaveAsTemplate => Coordinator.CanSaveAsTemplate;
    public bool CanIncrementTemplate => Coordinator.CanIncrementTemplate;
    public string CalibrationAssetActionText => Coordinator.CalibrationAssetActionText;
    public string CalibrationCanvasInstruction => Coordinator.CalibrationCanvasInstruction;
    public string CalibrationEditorHintText => Coordinator.CalibrationEditorHintText;
    public string CalibrationFlowStepText => Coordinator.CalibrationFlowStepText;
    public string CalibrationFooterMetaText => Coordinator.CalibrationFooterMetaText;
    public string CalibrationFrameDisplayName => Coordinator.CalibrationFrameDisplayName;
    public string CalibrationFrameIdInput
    {
        get => Coordinator.CalibrationFrameIdInput;
        set => Coordinator.CalibrationFrameIdInput = value;
    }
    public string CalibrationRuleStatusText => Coordinator.CalibrationRuleStatusText;
    public string CalibrationWorkflowStatusMessage => Coordinator.CalibrationWorkflowStatusMessage;
    public string CalibrationPageListTitle => Coordinator.CalibrationPageListTitle;
    public string? CalibrationPreviewImagePath => Coordinator.CalibrationPreviewImagePath;
    public object? CalibrationPreviewImageSource => Coordinator.CalibrationPreviewImageSource;
    public string? CalibrationTemplatePreviewImagePath => Coordinator.CalibrationTemplatePreviewImagePath;
    public string ReferenceLearningConfidenceText => Coordinator.ReferenceLearningConfidenceText;
    public string ReferenceLearningCurrentPageProgress => Coordinator.ReferenceLearningCurrentPageProgress;
    public string ReferenceLearningDecisionText => Coordinator.ReferenceLearningDecisionText;
    public string ReferenceLearningFrameSummary => Coordinator.ReferenceLearningFrameSummary;
    public string ReferenceLearningMatchText => Coordinator.ReferenceLearningMatchText;
    public string ReferenceLearningOverallProgress => Coordinator.ReferenceLearningOverallProgress;
    public string ReferenceLearningQualifiedCandidateSummary => Coordinator.ReferenceLearningQualifiedCandidateSummary;
    public string ReferenceLearningReviewInstruction => Coordinator.ReferenceLearningReviewInstruction;
    public string ReferenceLearningSemanticWarningText => Coordinator.ReferenceLearningSemanticWarningText;

    public double CalibrationCanvasWidth => Coordinator.CalibrationCanvasWidth;
    public double CalibrationCanvasHeight => Coordinator.CalibrationCanvasHeight;
    public double CalibrationFrameLeft => Coordinator.CalibrationFrameLeft;
    public double CalibrationFrameTop => Coordinator.CalibrationFrameTop;
    public double CalibrationFrameWidth => Coordinator.CalibrationFrameWidth;
    public double CalibrationFrameHeight => Coordinator.CalibrationFrameHeight;
    public double CalibrationOffsetXInput
    {
        get => Coordinator.CalibrationOffsetXInput;
        set => Coordinator.CalibrationOffsetXInput = value;
    }
    public double CalibrationOffsetYInput
    {
        get => Coordinator.CalibrationOffsetYInput;
        set => Coordinator.CalibrationOffsetYInput = value;
    }
    public string CalibrationRequirementInput
    {
        get => Coordinator.CalibrationRequirementInput;
        set => Coordinator.CalibrationRequirementInput = value;
    }
    public double CalibrationScaleInput
    {
        get => Coordinator.CalibrationScaleInput;
        set => Coordinator.CalibrationScaleInput = value;
    }
    public double CalibrationViewportZoom => State.CalibrationViewportZoom;
    public double TemplateBoardPanX => State.TemplateBoardPanX;
    public double TemplateBoardPanY => State.TemplateBoardPanY;
    public double TemplateBoardZoom => State.TemplateBoardZoom;
    public string TemplateBoardZoomText => State.TemplateBoardZoomText;
    public string TemplateCanvasTitle => Coordinator.TemplateCanvasTitle;
    public string TemplateCanvasSubtitle => Coordinator.TemplateCanvasSubtitle;

    public ICommand AddTemplatePagesCommand => Coordinator.AddTemplatePagesCommand;
    public ICommand ApplyAndConfirmCalibrationCommand => Coordinator.ApplyAndConfirmCalibrationCommand;
    public ICommand BackToTemplateLibraryCommand => Coordinator.BackToTemplateLibraryCommand;
    public ICommand CancelReferenceLearningCommand => Coordinator.CancelReferenceLearningCommand;
    public ICommand CancelDuplicateCommand => Coordinator.CancelDuplicateCommand;
    public ICommand ClearCalibrationAssetCommand => Coordinator.ClearCalibrationAssetCommand;
    public ICommand ConfirmAllQualifiedReferenceLearningCommand => Coordinator.ConfirmAllQualifiedReferenceLearningCommand;
    public ICommand ConfirmReferenceLearningPageCommand => Coordinator.ConfirmReferenceLearningPageCommand;
    public ICommand DeleteTemplateCommand => Coordinator.DeleteTemplateCommand;
    public ICommand EditTemplateGroupCommand => Coordinator.EditTemplateGroupCommand;
    public ICommand ExportTemplateCommand => Coordinator.ExportTemplateCommand;
    public ICommand ImportTemplatePackageCommand => Coordinator.ImportTemplatePackageCommand;
    public ICommand KeepExistingDuplicateCommand => Coordinator.KeepExistingDuplicateCommand;
    public ICommand KeepImportedDuplicateCommand => Coordinator.KeepImportedDuplicateCommand;
    public ICommand NewTemplateCommand => Coordinator.NewTemplateCommand;
    public ICommand OpenTemplateCommand => Coordinator.OpenTemplateCommand;
    public ICommand RenameTemplateCommand => Coordinator.RenameTemplateCommand;
    public ICommand DeleteTemplateFromLibraryCommand => Coordinator.DeleteTemplateFromLibraryCommand;
    public ICommand EditTemplatePageCommand => Coordinator.EditTemplatePageCommand;
    public ICommand ReanalyzeReferenceLearningCommand => Coordinator.ReanalyzeReferenceLearningCommand;
    public ICommand RedoCalibrationCommand => Coordinator.RedoCalibrationCommand;
    public ICommand RejectReferenceLearningFrameCommand => Coordinator.RejectReferenceLearningFrameCommand;
    public ICommand RemoveTemplatePageCommand => Coordinator.RemoveTemplatePageCommand;
    public ICommand ReturnReferenceLearningToManualCommand => Coordinator.ReturnReferenceLearningToManualCommand;
    public ICommand SaveTemplateAsCommand => Coordinator.SaveTemplateAsCommand;
    public ICommand SaveTemplateCommand => Coordinator.SaveTemplateCommand;
    public ICommand ShowCalibrationCanvasCommand => Coordinator.ShowCalibrationCanvasCommand;
    public ICommand ShowCalibrationInspectorCommand => Coordinator.ShowCalibrationInspectorCommand;
    public ICommand ShowCalibrationPagesCommand => Coordinator.ShowCalibrationPagesCommand;
    public ICommand UndoCalibrationCommand => Coordinator.UndoCalibrationCommand;
    public ICommand ConfirmTemplateDialogCommand => Coordinator.ConfirmTemplateDialogCommand;
    public ICommand SecondaryTemplateDialogCommand => Coordinator.SecondaryTemplateDialogCommand;
    public ICommand TertiaryTemplateDialogCommand => Coordinator.TertiaryTemplateDialogCommand;

    public Task ImportPsdFilesAsync(IReadOnlyList<string> paths) => Coordinator.ImportPsdFilesAsync(paths);
    public Task ChoosePsdFolderAsync() => Coordinator.ChoosePsdFolderAsync();
    public Task BindCalibrationAssetFileAsync(string path) => Coordinator.BindCalibrationAssetFileAsync(path);
    public Task BindCalibrationAssetIdAsync(string assetId) => Coordinator.BindCalibrationAssetIdAsync(assetId);
    public void SelectCalibrationFrame(string frameId) => Coordinator.SelectCalibrationFrame(frameId);
    public Task CommitCalibrationGeometryAsync() => Coordinator.CommitCalibrationGeometryAsync();
    public Task ClearCalibrationAssetAsync() => Coordinator.ClearCalibrationAssetAsync();
    public void AdjustCalibrationViewportZoom(int delta) => State.AdjustCalibrationViewportZoom(delta);
    public void SetCalibrationViewportZoom(double zoom) => State.SetCalibrationViewportZoom(zoom);
    public Task MoveTemplatePageAsync(TemplatePageRowViewModel source, TemplatePageRowViewModel target) =>
        Coordinator.MoveTemplatePageAsync(source, target);
    public void AdjustTemplateBoardZoom(int delta) => State.AdjustTemplateBoardZoom(delta);
    public void PanTemplateBoard(double deltaX, double deltaY) => State.PanTemplateBoard(deltaX, deltaY);
    public void ResetTemplateBoardView() => State.ResetTemplateBoardView();
    public override void Dispose()
    {
        Coordinator.Dispose();
        base.Dispose();
    }
}

public sealed class SettingsPageViewModel : PageViewModel
{
    private readonly ISettingsPageHost _host;
    private string _selectedSection = "AI 服务";
    private bool _settingsVisitActive;

    public SettingsPageViewModel(ISettingsPageHost host, IAiCredentialManagementPort? management = null,
        IPhotoshopPairingPort? photoshopPairing = null, IDiagnosticPackageExporter? diagnosticExporter = null,
        IPhotoshopEnvironmentProbe? photoshopEnvironment = null,
        IPhotoshopConnectionCheckPort? photoshopConnectionCheck = null,
        IPhotoshopHostSelectionPort? photoshopHostSelection = null)
        : base(host, nameof(IsSettingsVisible), nameof(AdditionalSettingsRows),
            nameof(AiUploadConsentStatusText), nameof(AiUploadConsentDetailText),
            nameof(CanGrantAiUploadConsent), nameof(CanRevokeAiUploadConsent))
    {
        _host = host;
        Credentials = new AiCredentialViewModel(management);
        Photoshop = new PhotoshopPairingViewModel(photoshopPairing, photoshopConnectionCheck);
        PhotoshopEnvironment = new PhotoshopEnvironmentViewModel(photoshopEnvironment, photoshopHostSelection);
        Diagnostics = new DiagnosticExportViewModel(diagnosticExporter);
        Observe(Credentials);
        PropertyChanged += SettingsPageViewModel_PropertyChanged;
        if (IsSettingsVisible) EnterSettingsVisit();
    }

    public AiCredentialViewModel Credentials { get; }
    public PhotoshopPairingViewModel Photoshop { get; }
    public PhotoshopEnvironmentViewModel PhotoshopEnvironment { get; }
    public DiagnosticExportViewModel Diagnostics { get; }
    public IReadOnlyList<string> Sections { get; } = ["AI 服务", "Photoshop", "存储与清理", "隐私与诊断", "软件信息"];
    public string SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value == _selectedSection || !Sections.Contains(value)) return;
            if (_selectedSection == "Photoshop") CancelPhotoshopOperations();
            if (_selectedSection == "AI 服务") Credentials.ClearAiCredentialInput();
            _selectedSection = value;
            ForwardPropertyChanged(new(nameof(SelectedSection)));
            ForwardPropertyChanged(new(nameof(IsAiSection)));
            ForwardPropertyChanged(new(nameof(IsPhotoshopSection)));
            ForwardPropertyChanged(new(nameof(IsStorageSection)));
            ForwardPropertyChanged(new(nameof(IsPrivacySection)));
            ForwardPropertyChanged(new(nameof(IsSoftwareSection)));
        }
    }
    public bool IsAiSection => SelectedSection == "AI 服务";
    public bool IsPhotoshopSection => SelectedSection == "Photoshop";
    public bool IsStorageSection => SelectedSection == "存储与清理";
    public bool IsPrivacySection => SelectedSection == "隐私与诊断";
    public bool IsSoftwareSection => SelectedSection == "软件信息";
    public string SoftwareVersion { get; } = typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString() ?? "未知";
#if DEBUG
    public string SoftwareBuildDescription { get; } = "Debug 开发构建 · 非交付安装包";
#else
    public string SoftwareBuildDescription { get; } = "Release 构建 · 安装交付状态另行验收";
#endif
    public SettingsPageState State => Credentials.State;

    public bool IsSettingsVisible => _host.IsSettingsVisible;
    public IReadOnlyList<string> AdditionalSettingsRows => _host.AdditionalSettingsRows;
    public AiCredentialState AiCredentialState => State.AiCredentialState;
    public string AiCredentialStatusState => State.AiCredentialStatusState;
    public string AiCredentialStatusText => State.AiCredentialStatusText;
    public string AiCredentialMaskedSuffix => State.AiCredentialMaskedSuffix;
    public string AiCredentialStatusDetailText => State.AiCredentialStatusDetailText;
    public string AiCredentialSaveButtonText => State.AiCredentialSaveButtonText;
    public string AiCredentialActionMessage => State.AiCredentialActionMessage;
    public string AiUploadConsentStatusText => _host.AiUploadConsentStatusText;
    public string AiUploadConsentDetailText => _host.AiUploadConsentDetailText;
    public bool IsAiCredentialBusy => State.IsAiCredentialBusy;
    public bool CanSaveAiCredential => State.CanSaveAiCredential;
    public bool CanRefreshAiCredential => State.CanRefreshAiCredential;
    public bool CanRemoveAiCredential => State.CanRemoveAiCredential;
    public bool CanGrantAiUploadConsent => _host.CanGrantAiUploadConsent;
    public bool CanRevokeAiUploadConsent => _host.CanRevokeAiUploadConsent;
    public ICommand SaveAiCredentialCommand => Credentials.SaveAiCredentialCommand;
    public ICommand RefreshAiCredentialCommand => Credentials.RefreshAiCredentialCommand;
    public ICommand RemoveAiCredentialCommand => Credentials.RemoveAiCredentialCommand;
    public ICommand RefreshAiConsentCommand => _host.RefreshAiConsentCommand;
    public ICommand GrantAiUploadConsentCommand => _host.GrantAiUploadConsentCommand;
    public ICommand RevokeAiUploadConsentCommand => _host.RevokeAiUploadConsentCommand;

    public void SetAiCredentialInput(string? value) => Credentials.SetAiCredentialInput(value);

    public void CancelPhotoshopOperations()
    {
        Photoshop.Cancel();
        PhotoshopEnvironment.Cancel();
    }

    private void SettingsPageViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(IsSettingsVisible)) return;
        if (IsSettingsVisible) EnterSettingsVisit();
        else LeaveSettingsVisit();
    }

    private void EnterSettingsVisit()
    {
        if (_settingsVisitActive) return;
        _settingsVisitActive = true;
        PhotoshopEnvironment.RefreshHostSelectionCommand.Execute(null);
    }

    private void LeaveSettingsVisit()
    {
        _settingsVisitActive = false;
        CancelPhotoshopOperations();
    }

    public override void Dispose()
    {
        _settingsVisitActive = false;
        PropertyChanged -= SettingsPageViewModel_PropertyChanged;
        Credentials.Dispose();
        Photoshop.Dispose();
        PhotoshopEnvironment.Dispose();
        Diagnostics.Dispose();
        base.Dispose();
    }
}
