using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Ai;
using Desktop.Application.Templates;

namespace Desktop.App;

/// <summary>Owns AI page workflows, consent, selection and refresh; the host supplies cross-page navigation.</summary>
public sealed partial class AiAnalysisViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AiAnalysisPageState _aiPageState;
    private readonly IAiBatchAnalysisWorkflow? _aiBatchWorkflow;
    private readonly IAiAnalysisHost _host;
    private long _pageDialogVersion;

    public AiAnalysisViewModel(IAiBatchAnalysisWorkflow? workflow, IAiAnalysisHost host)
        : this(new AiAnalysisPageState(), workflow, host) { }

    internal AiAnalysisViewModel(AiAnalysisPageState state, IAiBatchAnalysisWorkflow? workflow, IAiAnalysisHost host)
    {
        _aiPageState = state;
        _aiBatchWorkflow = workflow;
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeCommands();
        ConfirmInstanceSelectionCommand = new AsyncRelayCommand(ConfirmInstanceSelectionAsync,
            () => SelectedInstanceTarget is not null && !_aiBusy);
        CancelInstanceSelectionCommand = new RelayCommand(CloseInstanceSelection);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand PrepareRenderingCommand { get; private set; } = null!;

    // Page operations share one state with the retained shell compatibility aliases.
    private AiBatchAnalysisProjection? _aiBatchProjection
    {
        get => _aiPageState.BatchProjection;
        set => _aiPageState.BatchProjection = value;
    }
    private AiUploadConsentRecord? _uploadConsent
    {
        get => _aiPageState.UploadConsent;
        set => _aiPageState.UploadConsent = value;
    }
    private AiConsentContinuation _pendingAiConsentContinuation
    {
        get => _aiPageState.PendingConsentContinuation;
        set
        {
            _aiPageState.PendingConsentContinuation = value;
            OnPropertyChanged(nameof(IsBatchScanConsentPending));
            OnPropertyChanged(nameof(IsReferenceLearningConsentPending));
        }
    }
    private bool _aiBusy
    {
        get => _aiPageState.Busy;
        set => _aiPageState.Busy = value;
    }
    private bool _isAiUploadConsentDialogVisible
    {
        get => _aiPageState.IsUploadConsentDialogVisible;
        set => _aiPageState.IsUploadConsentDialogVisible = value;
    }
    private bool _aiUploadConsentAcknowledged
    {
        get => _aiPageState.UploadConsentAcknowledged;
        set => _aiPageState.UploadConsentAcknowledged = value;
    }
    private bool _isAiCandidatePreviewVisible
    {
        get => _aiPageState.IsCandidatePreviewVisible;
        set => _aiPageState.IsCandidatePreviewVisible = value;
    }
    private AiCandidateViewModel? _aiCandidatePreview
    {
        get => _aiPageState.CandidatePreview;
        set => _aiPageState.CandidatePreview = value;
    }
    private AiRequirementUsageViewModel? _aiReplacementTarget
    {
        get => _aiPageState.ReplacementTarget;
        set => _aiPageState.ReplacementTarget = value;
    }
    private AiStyleAssetViewModel? _selectedAiReplacementAsset
    {
        get => _aiPageState.SelectedReplacementAsset;
        set => _aiPageState.SelectedReplacementAsset = value;
    }
    private AiStyleAnalysisViewModel? _selectedAiStyle
    {
        get => _aiPageState.SelectedStyle;
        set => _aiPageState.SelectedStyle = value;
    }
    private string _aiStatusMessage
    {
        get => _aiPageState.StatusMessage;
        set => _aiPageState.StatusMessage = value;
    }
    private HashSet<string> _expandedAiStyleIds => _aiPageState.ExpandedStyleIds;
    // The page owns the timer; the shell retains only its diagnostic mirror.
    private System.Windows.Threading.DispatcherTimer? _aiRefreshTimer
    {
        get => _aiPageState.RefreshTimer;
        set => _aiPageState.RefreshTimer = value;
    }

    internal System.Windows.Threading.DispatcherTimer? RefreshTimer => _aiPageState.RefreshTimer;

    public ObservableCollection<AiStyleAnalysisViewModel> AiStyles => _aiPageState.Styles;
    public ObservableCollection<AiStyleAnalysisViewModel> VisibleAiStyles => _aiPageState.VisibleStyles;
    public ObservableCollection<AiStatusFilterViewModel> AiStatusFilters => _aiPageState.StatusFilters;

    public ICommand PauseAiAnalysisCommand
    {
        get => _aiPageState.PauseCommand;
        internal set => _aiPageState.PauseCommand = value;
    }
    public ICommand ResumeAiAnalysisCommand
    {
        get => _aiPageState.ResumeCommand;
        internal set => _aiPageState.ResumeCommand = value;
    }
    public ICommand CancelAiAnalysisCommand
    {
        get => _aiPageState.CancelCommand;
        internal set => _aiPageState.CancelCommand = value;
    }
    public ICommand WaitForAiNetworkCommand
    {
        get => _aiPageState.WaitForNetworkCommand;
        internal set => _aiPageState.WaitForNetworkCommand = value;
    }
    public ICommand RefreshAiAnalysisCommand
    {
        get => _aiPageState.RefreshCommand;
        internal set => _aiPageState.RefreshCommand = value;
    }
    public ICommand RetryAiAnalysisCommand
    {
        get => _aiPageState.RetryCommand;
        internal set => _aiPageState.RetryCommand = value;
    }
    public ICommand LockAiSelectionCommand
    {
        get => _aiPageState.LockSelectionCommand;
        internal set => _aiPageState.LockSelectionCommand = value;
    }
    public ICommand AnalyzeAiAssetCommand
    {
        get => _aiPageState.AnalyzeAssetCommand;
        internal set => _aiPageState.AnalyzeAssetCommand = value;
    }
    public ICommand SelectAiSizeTableSourceCommand
    {
        get => _aiPageState.SelectSizeTableSourceCommand;
        internal set => _aiPageState.SelectSizeTableSourceCommand = value;
    }
    public ICommand SelectTopNCandidateCommand
    {
        get => _aiPageState.SelectTopNCandidateCommand;
        internal set => _aiPageState.SelectTopNCandidateCommand = value;
    }
    public ICommand SelectAiStatusFilterCommand
    {
        get => _aiPageState.SelectStatusFilterCommand;
        internal set => _aiPageState.SelectStatusFilterCommand = value;
    }
    public ICommand GrantAiUploadConsentCommand
    {
        get => _aiPageState.GrantUploadConsentCommand;
        internal set => _aiPageState.GrantUploadConsentCommand = value;
    }
    public ICommand RevokeAiUploadConsentCommand
    {
        get => _aiPageState.RevokeUploadConsentCommand;
        internal set => _aiPageState.RevokeUploadConsentCommand = value;
    }
    public ICommand RefreshAiConsentCommand
    {
        get => _aiPageState.RefreshConsentCommand;
        internal set => _aiPageState.RefreshConsentCommand = value;
    }
    public ICommand ConfirmAiUploadConsentCommand
    {
        get => _aiPageState.ConfirmUploadConsentCommand;
        internal set => _aiPageState.ConfirmUploadConsentCommand = value;
    }
    public ICommand CancelAiUploadConsentCommand
    {
        get => _aiPageState.CancelUploadConsentCommand;
        internal set => _aiPageState.CancelUploadConsentCommand = value;
    }
    public ICommand OpenAiCandidatePreviewCommand
    {
        get => _aiPageState.OpenCandidatePreviewCommand;
        internal set => _aiPageState.OpenCandidatePreviewCommand = value;
    }
    public ICommand CloseAiCandidatePreviewCommand
    {
        get => _aiPageState.CloseCandidatePreviewCommand;
        internal set => _aiPageState.CloseCandidatePreviewCommand = value;
    }
    public ICommand ConfirmAiCandidateSelectionCommand
    {
        get => _aiPageState.ConfirmCandidateSelectionCommand;
        internal set => _aiPageState.ConfirmCandidateSelectionCommand = value;
    }
    public ICommand AnalyzeAiPreviewAssetCommand
    {
        get => _aiPageState.AnalyzePreviewAssetCommand;
        internal set => _aiPageState.AnalyzePreviewAssetCommand = value;
    }
    public ICommand OpenAiRequirementPreviewCommand
    {
        get => _aiPageState.OpenRequirementPreviewCommand;
        internal set => _aiPageState.OpenRequirementPreviewCommand = value;
    }
    public ICommand OpenAiSizeTableSourcePreviewCommand
    {
        get => _aiPageState.OpenSizeTableSourcePreviewCommand;
        internal set => _aiPageState.OpenSizeTableSourcePreviewCommand = value;
    }
    public ICommand BeginAiReplacementCommand
    {
        get => _aiPageState.BeginReplacementCommand;
        internal set => _aiPageState.BeginReplacementCommand = value;
    }
    public ICommand SelectAiReplacementAssetCommand
    {
        get => _aiPageState.SelectReplacementAssetCommand;
        internal set => _aiPageState.SelectReplacementAssetCommand = value;
    }
    public ICommand BeginAiSizeTableSourceReplacementCommand
    {
        get => _aiPageState.BeginSizeTableSourceReplacementCommand;
        internal set => _aiPageState.BeginSizeTableSourceReplacementCommand = value;
    }
    public ICommand ConfirmAiReplacementCommand
    {
        get => _aiPageState.ConfirmReplacementCommand;
        internal set => _aiPageState.ConfirmReplacementCommand = value;
    }
    public ICommand CancelAiReplacementCommand
    {
        get => _aiPageState.CancelReplacementCommand;
        internal set => _aiPageState.CancelReplacementCommand = value;
    }
    public ICommand OpenAiStyleReviewCommand
    {
        get => _aiPageState.OpenStyleReviewCommand;
        internal set => _aiPageState.OpenStyleReviewCommand = value;
    }
    public ICommand ReturnFromAiStyleReviewCommand
    {
        get => _aiPageState.ReturnFromStyleReviewCommand;
        internal set => _aiPageState.ReturnFromStyleReviewCommand = value;
    }

    public bool IsAiAnalysisVisible => _host.IsAiAnalysisVisible;

    public bool IsAiStyleReviewVisible => _host.IsAiStyleReviewVisible;

    public AiStyleAnalysisViewModel? SelectedAiStyle
    {
        get => _selectedAiStyle;
        internal set
        {
            if (ReferenceEquals(_selectedAiStyle, value))
            {
                return;
            }

            _selectedAiStyle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAiStyleReviewVisible));
            _host.NotifyMainVisibilityChanged();
        }
    }

    public bool HasAiAnalysis => _aiBatchProjection is not null;

    internal bool AiWorkflowAvailable => _aiBatchWorkflow is not null;

    public AiBatchAnalysisProjection? AiProjection => _aiBatchProjection;

    public string AiBatchStatusText => _aiBatchProjection?.StatusText ?? "等待分析";

    public string AiStatusMessage
    {
        get => _aiStatusMessage;
        internal set
        {
            if (_aiStatusMessage == value)
            {
                return;
            }

            _aiStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public string AiBatchProgressText => _aiBatchProjection?.ProgressText ?? "尚未开始分析";

    public string AiBatchStatusState => _aiBatchProjection?.StatusState ?? nameof(AiBatchUiStatus.Waiting);

    public string AiPageTitle => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled
        ? "批次已取消"
        : "生成前检查";

    public string AiStyleReviewContextText => SelectedAiStyle is null
        ? $"模板组：{AiTemplateSummary}"
        : $"模板组：{AiTemplateSummary} · 当前款式：{SelectedAiStyle.StyleName}";

    public string AiPageSubtitle => _aiBatchProjection is null
        ? "扫描与分析尚未开始"
        : _aiBatchProjection.Status switch
        {
            AiBatchUiStatus.Cancelled => "0 个正式输出 · 不进入完成状态",
            AiBatchUiStatus.NotGeneratable => "0 个可执行页面 · 需要用户处理",
            AiBatchUiStatus.BudgetStopped => "本轮预算已停止 · 已保留已完成结果",
            AiBatchUiStatus.Analysing or AiBatchUiStatus.Waiting => "此阶段不占用 Photoshop",
            _ => $"{_aiBatchProjection.ExecutablePageCount} 个可执行页面 · 隔离 {_aiBatchProjection.IsolatedPageCount} 个红色页面"
        };

    public string AiTemplateSummary => NormalizeUserTemplateName(
        _aiBatchProjection?.TemplateSummary ?? "当前已校准模板");
    public string AiTemplateMeta => _aiBatchProjection?.TemplateMeta ?? "模板已准备好";
    public string AiBatchSummary => _aiBatchProjection?.BatchSummary ?? "当前输入批次";
    public string AiBatchMeta => _aiBatchProjection?.BatchMeta ?? "素材清单已登记";
    public string AiOutputSummary => _aiBatchProjection?.OutputSummary ?? "仅 JPG";
    public string AiOutputMeta => _aiBatchProjection?.OutputMeta ?? "正式输出目录已确认";
    public string AiPageSummary => _aiBatchProjection?.PageSummary ?? "0 个页面";
    public string AiPageMeta => _aiBatchProjection?.PageMeta ?? "等待分析";

    private static string NormalizeUserTemplateName(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("TPL", StringComparison.OrdinalIgnoreCase)
        || value.Contains("规则实机", StringComparison.Ordinal)
        || value.Contains('_')
        || value.Length > 28
            ? "已校准模板"
            : value;
    public GridLength AiSummaryHeight => _host.IsBatchNarrowLayout
        ? new GridLength(124)
        : new GridLength(104);
    public int AiSummaryColumnCount => _host.IsBatchNarrowLayout ? 2 : 4;
    public double AiStyleRowHeight => _host.IsBatchNarrowLayout ? 56 : 64;
    public double AiStyleReviewHeight => _host.IsBatchNarrowLayout ? 436 : 540;
    public double AiRequirementRowHeight => _host.IsBatchNarrowLayout ? 96 : 104;
    public double AiAssetTileHeight => _host.IsBatchNarrowLayout ? 116 : 144;
    public double AiZeroOutputPanelWidth => _host.IsBatchNarrowLayout ? 420 : 464;
    public string AiPrimaryActionText => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled
        ? "批次已取消"
        : _aiBatchProjection?.Status == AiBatchUiStatus.BudgetStopped
            ? "稍后继续"
        : "开始生成";

    public bool IsAiZeroOutput => _aiBatchProjection?.Status is AiBatchUiStatus.Cancelled or AiBatchUiStatus.NotGeneratable;
    public bool IsAiRegularState => !IsAiZeroOutput;
    public string AiZeroOutputTitle => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled
        ? "全部已选页面已取消"
        : "没有可生成页面，需要处理";
    public string AiZeroOutputDescription => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled
        ? "这是用户明确取消的结果，不是业务红页或系统失败。Photoshop 不会启动，也不会显示虚构结果图。"
        : "全部页面因业务问题阻断。请在应用内修正；不要把 Photoshop 当作默认补救动作。";
    public string AiZeroReasonOneLabel => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled ? "取消范围" : "缺少必要素材";
    public string AiZeroReasonOneText => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled
        ? "全部已选页面在开始生成前取消"
        : "选择已有源图或补充素材";
    public string AiZeroReasonTwoLabel => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled ? "保留内容" : "来源或变体冲突";
    public string AiZeroReasonTwoText => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled
        ? "源图片、原始 PSD 和已有正式输出不受影响"
        : "确认正确来源、变体和图片框选择";
    public string AiZeroReasonThreeLabel => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled ? "后续操作" : "图片框结构不可靠";
    public string AiZeroReasonThreeText => _aiBatchProjection?.Status == AiBatchUiStatus.Cancelled
        ? "可查看取消范围，或删除软件内的任务记录"
        : "返回模板重新绑定后再分析";

    public bool IsAiBusy => _aiBusy;

    public bool IsAiCandidatePreviewVisible
    {
        get => _isAiCandidatePreviewVisible;
        internal set
        {
            if (_isAiCandidatePreviewVisible == value)
            {
                return;
            }

            _isAiCandidatePreviewVisible = value;
            OnPropertyChanged();
        }
    }

    public AiCandidateViewModel? AiCandidatePreview
    {
        get => _aiCandidatePreview;
        internal set
        {
            if (ReferenceEquals(_aiCandidatePreview, value))
            {
                return;
            }

            _aiCandidatePreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmAiCandidateSelection));
            OnPropertyChanged(nameof(CanAnalyzeAiPreviewAsset));
        }
    }

    // AI's Top-1 is already the default selection.  Candidate inspection is a
    // read-only view; replacement is the single user action that changes a
    // frame.  Keep the legacy commands as non-visible compatibility members,
    // but never expose a second confirmation path in the UI.
    public bool CanConfirmAiCandidateSelection => !_aiBusy && false;

    public bool CanAnalyzeAiPreviewAsset => !_aiBusy && false;

    public AiRequirementUsageViewModel? AiReplacementTarget => _aiReplacementTarget;

    public AiStyleAssetViewModel? SelectedAiReplacementAsset => _selectedAiReplacementAsset;

    public bool IsAiReplacementActive => AiReplacementTarget is not null;

    public string AiReplacementInstructionText => AiReplacementTarget is null
        ? "点击左侧“替换用图”，再选择一张照片。"
        : _aiBusy
            ? "正在保存选图，请稍候…"
            : SelectedAiReplacementAsset is null
                ? $"当前用图：{AiReplacementTarget.CurrentAssetDisplayName}；请选择要替换的照片。"
                : string.Equals(
                    AiReplacementTarget.SelectedAssetId,
                    SelectedAiReplacementAsset.AssetId,
                    StringComparison.Ordinal)
                    ? AiReplacementTarget.IsManualLocked
                        ? $"当前用图：{AiReplacementTarget.CurrentAssetDisplayName}；当前照片已人工锁定，无需重复确认。"
                        : $"当前用图：{AiReplacementTarget.CurrentAssetDisplayName}；点击“确认”将人工确认并锁定当前照片，不更换素材。"
                    : $"当前用图：{AiReplacementTarget.CurrentAssetDisplayName}；已选择 {SelectedAiReplacementAsset.DisplayName}，点击“确认”完成替换。";

    public string ConfirmAiReplacementText => _aiBusy ? "保存中…" : "确认";

    public bool CanConfirmAiReplacement =>
        AiReplacementTarget is not null
        && SelectedAiReplacementAsset is not null
        && string.Equals(
            AiReplacementTarget.StyleId,
            SelectedAiReplacementAsset.StyleId,
            StringComparison.Ordinal)
        && (!AiReplacementTarget.IsManualLocked || !string.Equals(
            AiReplacementTarget.SelectedAssetId,
            SelectedAiReplacementAsset.AssetId,
            StringComparison.Ordinal))
        && !_aiBusy;

    public bool IsAiUploadConsentGranted => _uploadConsent?.IsGranted == true;

    public bool IsAiUploadConsentRevoked => _uploadConsent?.State == AiUploadConsentState.Revoked;

    public bool IsBatchScanConsentPending => _pendingAiConsentContinuation == AiConsentContinuation.BatchScan;
    public bool IsReferenceLearningConsentPending => _pendingAiConsentContinuation == AiConsentContinuation.ReferenceLearning;

    public bool IsAiUploadConsentDialogVisible
    {
        get => _isAiUploadConsentDialogVisible;
        internal set
        {
            if (_isAiUploadConsentDialogVisible == value)
            {
                return;
            }

            _isAiUploadConsentDialogVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmAiUploadConsent));
            (ConfirmAiUploadConsentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool AiUploadConsentAcknowledged
    {
        get => _aiUploadConsentAcknowledged;
        set
        {
            if (_aiUploadConsentAcknowledged == value)
            {
                return;
            }

            _aiUploadConsentAcknowledged = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmAiUploadConsent));
            (ConfirmAiUploadConsentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string AiUploadConsentStatusText => _uploadConsent switch
    {
        { State: AiUploadConsentState.Granted } => "已授权上传压缩分析图",
        { State: AiUploadConsentState.Revoked } => "已撤回上传授权",
        _ => "尚未授权上传压缩分析图"
    };

    public string AiUploadConsentDetailText => _uploadConsent switch
    {
        { State: AiUploadConsentState.Granted } => "仅上传应用生成的压缩分析副本；原始 PSD 和高清源文件不会上传。",
        { State: AiUploadConsentState.Revoked } => "新的 AI 分析请求已停止；已有本地分析结果不会删除。",
        _ => "首次使用 AI 前需要明确允许上传压缩分析副本；原始 PSD 和高清源文件不会上传。"
    };

    public bool CanGrantAiUploadConsent => _aiBatchWorkflow is not null && !IsAiUploadConsentGranted && !_aiBusy;

    public bool CanRevokeAiUploadConsent => _aiBatchWorkflow is not null && IsAiUploadConsentGranted && !_aiBusy;

    public bool CanConfirmAiUploadConsent =>
        IsAiUploadConsentDialogVisible
        && AiUploadConsentAcknowledged
        && _aiBatchWorkflow is not null
        && !_aiBusy;

    public bool CanPauseAi => _aiBatchProjection?.CanPause == true && !_aiBusy;

    public bool CanResumeAi => _aiBatchProjection?.CanResume == true && !_aiBusy;

    public bool CanCancelAi => _aiBatchProjection?.CanCancel == true && !_aiBusy;

    public bool IsAiReady => _aiBatchProjection?.CanEnterGeneration == true;

    /// <summary>
    /// A submission timeout is safe to retry only when the adapter proved that
    /// the request body was not fully written.  The workflow projection uses
    /// the dedicated action text for that case; response-unknown and ordinary
    /// terminal failures intentionally do not expose this command.
    /// </summary>
    public bool CanRetryAiAnalysis =>
        string.Equals(_aiBatchProjection?.ActionText, "重试分析", StringComparison.Ordinal)
        && !_aiBusy;

    public void Initialize()
    {
        _aiRefreshTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1),
            System.Windows.Threading.DispatcherPriority.Background,
            async (_, _) => await AutoRefreshAiAsync().ConfigureAwait(true),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _aiPageState.RefreshTimer = _aiRefreshTimer;
        _aiRefreshTimer.Stop();
        _ = RefreshUploadConsentAsync();
    }

    internal async Task AutoRefreshAiAsync()
    {
        if (_aiBusy
            || _aiBatchWorkflow is null
            || _host.CurrentBatchId is null
            || !ShouldAutoRefreshAi)
        {
            UpdateAiAutoRefresh();
            return;
        }

        await RefreshAiAnalysisAsync().ConfigureAwait(true);
    }

    private bool ShouldAutoRefreshAi
    {
        get
        {
            // The style-review screen is a detail view of the same live batch.
            // Keep polling while it is open so the overview and the detail view
            // cannot diverge while another page is still being analysed.
            if (!IsAiAnalysisVisible && !IsAiStyleReviewVisible)
            {
                return false;
            }

            if (_aiBatchProjection is null
                || _aiBatchProjection.ControlState != AiAnalysisSchedulingControlState.Running)
            {
                return false;
            }

            // A batch can be reported as ReviewRequired as soon as one page is
            // ready for review, even though sibling pages are still waiting or
            // analysing. Inspect page state instead of relying on the aggregate
            // batch status, otherwise progress such as 3/6 would remain stale.
            return _aiBatchProjection.Status is AiBatchUiStatus.Waiting or AiBatchUiStatus.Analysing
                || _aiBatchProjection.Styles
                    .SelectMany(style => style.Pages)
                    .Any(page => page.Status is AiPageUiStatus.Waiting or AiPageUiStatus.Analysing);
        }
    }

    internal void UpdateAiAutoRefresh()
    {
        if (_aiRefreshTimer is null)
        {
            return;
        }

        if (ShouldAutoRefreshAi)
        {
            if (!_aiRefreshTimer.IsEnabled)
            {
                _aiRefreshTimer.Start();
            }
        }
        else if (_aiRefreshTimer.IsEnabled)
        {
            _aiRefreshTimer.Stop();
        }
    }

    internal void DisposeAiAutoRefresh()
    {
        if (_aiRefreshTimer is null)
        {
            return;
        }

        _aiRefreshTimer.Stop();
        _aiRefreshTimer = null;
        _aiPageState.RefreshTimer = null;
    }

    internal void OpenAiCandidatePreview(AiCandidateViewModel? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        AiCandidatePreview = candidate;
        IsAiCandidatePreviewVisible = true;
        NotifyAiChanged();
    }

    internal void CloseAiCandidatePreview()
    {
        IsAiCandidatePreviewVisible = false;
        AiCandidatePreview = null;
        NotifyAiChanged();
    }

    internal void ClosePageDialogs()
    {
        // Invalidate late dialog-opening continuations without cancelling paid work.
        _pageDialogVersion++;
        _pendingAiConsentContinuation = AiConsentContinuation.None;
        IsAiUploadConsentDialogVisible = false;
        AiUploadConsentAcknowledged = false;
        CloseInstanceSelection();
        CloseAiCandidatePreview();
    }

    internal async Task ConfirmAiCandidateSelectionAsync()
    {
        var candidate = AiCandidatePreview;
        if (candidate is not null && await TryLockAiSelectionAsync(candidate).ConfigureAwait(true))
        {
            CloseAiCandidatePreview();
        }
    }

    internal async Task AnalyzeAiPreviewAssetAsync()
    {
        var candidate = AiCandidatePreview;
        if (candidate is null || !candidate.IsLocked)
        {
            return;
        }

        CloseAiCandidatePreview();
        await AnalyzeAiAssetAsync(candidate).ConfigureAwait(true);
    }

    internal void OpenAiRequirementPreview(AiRequirementUsageViewModel? requirement)
    {
        var candidate = requirement?.CreatePreviewCandidate();
        if (candidate is null)
        {
            return;
        }

        OpenAiCandidatePreview(candidate);
    }

    internal void OpenAiSizeTableSourcePreview(AiSizeTableSourceCandidateViewModel? candidate)
    {
        var preview = candidate?.CreatePreviewCandidate();
        if (preview is null)
        {
            return;
        }

        OpenAiCandidatePreview(preview);
    }

    internal void OpenAiStyleReview(AiStyleAnalysisViewModel? style)
    {
        if (style is null)
        {
            return;
        }

        SelectedAiStyle = style;
        _host.ShowAiStyleReview();
        NotifyAiChanged();
    }

    internal void ReturnFromAiStyleReview()
    {
        ClearAiReplacementState();
        _host.ShowAiAnalysis();
        NotifyAiChanged();
    }

    internal void BeginAiReplacement(AiRequirementUsageViewModel? requirement)
    {
        if (requirement is null || _aiBusy)
        {
            return;
        }

        if (requirement.IsSizeTableSource)
        {
            BeginAiSizeTableSourceReplacement();
            return;
        }

        // Explicitly selecting the current AI photo confirms it without
        // replacing the source. Nothing is saved merely by opening this UI.
        SetAiReplacementState(requirement, null);
        AiStatusMessage = $"请选择要替换的照片：{requirement.SelectionLabel}。选择后点击“确认”即可保存。";
    }

    internal bool IsAiReplacementAssetSelectable(AiStyleAssetViewModel? asset) =>
        asset is not null
        && AiReplacementTarget is not null
        && string.Equals(asset.StyleId, AiReplacementTarget.StyleId, StringComparison.Ordinal)
        && !_aiBusy;

    internal void SelectAiReplacementAsset(AiStyleAssetViewModel? asset)
    {
        var selectedStyle = SelectedAiStyle;
        var sizeTableSource = selectedStyle?.SizeTableSource;
        if (asset is not null
            && sizeTableSource is not null
            && _aiPageState.IsSizeTableSourceReplacementActive
            && string.Equals(asset.StyleId, selectedStyle!.StyleId, StringComparison.Ordinal))
        {
            _ = SelectAiSizeTableSourceFromLibraryAsync(sizeTableSource, asset);
            return;
        }

        if (!IsAiReplacementAssetSelectable(asset))
        {
            return;
        }

        foreach (var style in AiStyles)
        {
            foreach (var item in style.Assets)
            {
                item.IsSelected = ReferenceEquals(item, asset);
            }
        }

        _selectedAiReplacementAsset = asset;
        NotifyAiChanged();
    }

    private async Task SelectAiSizeTableSourceFromLibraryAsync(
        AiSizeTableSourceViewModel sizeTableSource,
        AiStyleAssetViewModel asset)
    {
        if (_aiBatchWorkflow is null || _aiBusy) return;
        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            var projection = await _aiBatchWorkflow.SelectSizeTableSourceAsync(
                asset.BatchId,
                asset.StyleId,
                asset.AssetId,
                CancellationToken.None).ConfigureAwait(true);
            ApplyAiProjection(projection);
            _aiPageState.IsSizeTableSourceReplacementActive = false;
            AiStatusMessage = $"已将{asset.DisplayName}设为尺码表来源；本款照片列表已同步更新。";
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        catch (Exception)
        {
            AiStatusMessage = "尺码表来源未能保存；当前选择没有更改，请刷新后重试。";
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal bool CanSelectAiAssetFromLibrary(AiStyleAssetViewModel? asset) =>
        asset is not null && !_aiBusy &&
        (AiReplacementTarget is not null
            && string.Equals(asset.StyleId, AiReplacementTarget.StyleId, StringComparison.Ordinal)
         || (SelectedAiStyle?.SizeTableSource is not null
             && string.Equals(asset.StyleId, SelectedAiStyle.StyleId, StringComparison.Ordinal)));

    internal void BeginAiSizeTableSourceReplacement()
    {
        _aiPageState.IsSizeTableSourceReplacementActive = true;
        AiStatusMessage = "请选择右侧照片作为尺码表来源。";
        NotifyAiChanged();
    }

    internal async Task ConfirmAiReplacementAsync()
    {
        var target = AiReplacementTarget;
        var asset = SelectedAiReplacementAsset;
        if (!CanConfirmAiReplacement || target is null || asset is null || _aiBatchWorkflow is null)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            if (await BeginInstanceSelectionAsync(target.BatchId, target.StyleId, target.RequirementId, asset.AssetId).ConfigureAwait(true))
                return;
            var projection = await _aiBatchWorkflow.LockSelectionAsync(
                target.BatchId,
                target.StyleId,
                target.RequirementId,
                asset.AssetId,
                CancellationToken.None).ConfigureAwait(true);
            ApplyAiProjection(projection);
            ClearAiReplacementState();
            AiStatusMessage = string.Equals(target.SelectedAssetId, asset.AssetId, StringComparison.Ordinal)
                ? $"已确认并锁定当前照片 {asset.DisplayName}（{target.SelectionLabel}）；素材未更换，刷新或重启后仍会保留。"
                : $"已替换为 {asset.DisplayName}（{target.SelectionLabel}）；刷新或重启后仍会保留。";
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        catch (Exception)
        {
            AiStatusMessage = "本地选图保存失败，当前用图没有更改；请刷新后重新选择。";
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal void SetAiReplacementState(
        AiRequirementUsageViewModel requirement,
        string? pendingAssetId)
    {
        _aiReplacementTarget = requirement;
        _selectedAiReplacementAsset = null;
        foreach (var style in AiStyles)
        {
            var active = string.Equals(style.StyleId, requirement.StyleId, StringComparison.Ordinal);
            style.SetReplacementActive(active, active ? requirement : null);
            foreach (var asset in style.Assets)
            {
                asset.IsSelected = active
                    && pendingAssetId is not null
                    && string.Equals(asset.AssetId, pendingAssetId, StringComparison.Ordinal);
                if (asset.IsSelected)
                {
                    _selectedAiReplacementAsset = asset;
                }
            }
        }

        NotifyAiChanged();
    }

    internal void ClearAiReplacementState()
    {
        _aiPageState.IsSizeTableSourceReplacementActive = false;
        _aiReplacementTarget = null;
        _selectedAiReplacementAsset = null;
        foreach (var style in AiStyles)
        {
            style.SetReplacementActive(false, null);
            foreach (var asset in style.Assets)
            {
                asset.IsSelected = false;
            }
        }

        NotifyAiChanged();
    }

    public async Task<bool> EnsureAiUploadConsentForScanAsync()
    {
        var dialogVersion = _pageDialogVersion;
        if (!await _host.EnsureCredentialConfiguredAsync().ConfigureAwait(true))
        {
            return false;
        }

        if (dialogVersion != _pageDialogVersion) return false;

        if (_aiBatchWorkflow is null)
        {
            return true;
        }

        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .GetUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            if (dialogVersion != _pageDialogVersion) return false;
            NotifyAiChanged();
            if (IsAiUploadConsentGranted)
            {
                _pendingAiConsentContinuation = AiConsentContinuation.None;
                return true;
            }

            _pendingAiConsentContinuation = AiConsentContinuation.BatchScan;
            AiUploadConsentAcknowledged = false;
            IsAiUploadConsentDialogVisible = true;
            _host.BatchStatusMessage = IsAiUploadConsentRevoked
                ? "上传授权已撤回；重新授权后才会扫描并发起新的 AI 请求。"
                : "请先确认 AI 分析与图片上传授权；未同意前不会扫描或发起 AI 请求。";
            return false;
        }
        catch (InvalidOperationException exception)
        {
            _host.SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToAiUserMessage(exception.Message));
            return false;
        }
    }

    internal async Task ConfirmAiUploadConsentAsync()
    {
        if (!CanConfirmAiUploadConsent || _aiBatchWorkflow is null)
        {
            return;
        }

        var dialogVersion = _pageDialogVersion;
        var granted = false;
        var continuation = AiConsentContinuation.None;
        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .GrantUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            granted = _uploadConsent.IsGranted;
            if (granted && dialogVersion == _pageDialogVersion)
            {
                continuation = _pendingAiConsentContinuation;
                _pendingAiConsentContinuation = AiConsentContinuation.None;
                IsAiUploadConsentDialogVisible = false;
                AiUploadConsentAcknowledged = false;
                AiStatusMessage = "上传授权已保存；现在可以开始 AI 分析。";
            }
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }

        if (granted && continuation == AiConsentContinuation.ReferenceLearning)
        {
            await _host.StartReferenceLearningAsync().ConfigureAwait(true);
        }
        else if (granted && continuation == AiConsentContinuation.BatchScan)
        {
            await _host.ScanBatchInputAsync().ConfigureAwait(true);
        }
    }

    internal void CancelAiUploadConsent()
    {
        _pageDialogVersion++;
        _pendingAiConsentContinuation = AiConsentContinuation.None;
        IsAiUploadConsentDialogVisible = false;
        AiUploadConsentAcknowledged = false;
        _host.BatchStatusMessage = "未授予上传权限；源文件未修改，扫描和 AI 分析均未启动。";
    }

    internal async Task RefreshUploadConsentAsync()
    {
        if (_aiBatchWorkflow is null)
        {
            return;
        }

        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .GetUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            NotifyAiChanged();
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
    }

    internal async Task GrantAiUploadConsentAsync()
    {
        if (_aiBatchWorkflow is null || _aiBusy)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .GrantUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            AiStatusMessage = "上传授权已保存；现在可以开始 AI 分析。";
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal async Task RevokeAiUploadConsentAsync()
    {
        if (_aiBatchWorkflow is null || _aiBusy)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .RevokeUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            AiStatusMessage = "上传授权已撤回；新的 AI 分析请求已停止。";
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    public async Task StartAiAnalysisAsync(string batchId)
    {
        if (_aiBatchWorkflow is null)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            var projection = await _aiBatchWorkflow.StartAsync(batchId, CancellationToken.None).ConfigureAwait(true);
            ApplyAiProjection(projection);
            _host.ShowAiAnalysis();
        }
        catch (InvalidOperationException exception)
        {
            _host.SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToAiUserMessage(exception.Message));
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal async Task RefreshAiAnalysisAsync()
    {
        if (_aiBatchWorkflow is null || _host.CurrentBatchId is null)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            var projection = await _aiBatchWorkflow.RefreshAsync(_host.CurrentBatchId, CancellationToken.None).ConfigureAwait(true);
            if (projection is not null)
            {
                ApplyAiProjection(projection);
            }
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal async Task RetryAiAnalysisAsync()
    {
        if (!CanRetryAiAnalysis)
        {
            return;
        }

        if (!_host.CanRestartBatchAnalysis)
        {
            AiStatusMessage = "当前批次配置已失效，请返回批次设置重新选择模板、商品目录和输出目录后重试。";
            return;
        }

        // A provider submission timeout is safe to retry, but the exhausted
        // logical request itself is terminal. Build a fresh input batch and
        // let the normal scan/admission path create a new request identity;
        // never mutate or requeue the old terminal graph in place.
        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            await _host.RestartBatchAnalysisAsync().ConfigureAwait(true);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal async Task RunAiControlAsync(
        Func<IAiBatchAnalysisWorkflow, string, CancellationToken, Task<AiBatchAnalysisProjection>> workflowAction)
    {
        if (_aiBatchWorkflow is null || _host.CurrentBatchId is null)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            ApplyAiProjection(await workflowAction(_aiBatchWorkflow, _host.CurrentBatchId, CancellationToken.None).ConfigureAwait(true));
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal async Task LockAiSelectionAsync(AiCandidateViewModel? candidate)
    {
        _ = await TryLockAiSelectionAsync(candidate).ConfigureAwait(true);
    }

    internal async Task<bool> TryLockAiSelectionAsync(AiCandidateViewModel? candidate)
    {
        if (candidate is null || _aiBatchWorkflow is null || _aiBusy)
        {
            return false;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            if (await BeginInstanceSelectionAsync(candidate.BatchId, candidate.StyleId, candidate.RequirementId, candidate.AssetId).ConfigureAwait(true))
                return false; // The explicit instance confirmation, not this preview, persists the choice.
            ApplyAiProjection(await _aiBatchWorkflow.LockSelectionAsync(
                candidate.BatchId,
                candidate.StyleId,
                candidate.RequirementId,
                candidate.AssetId,
                CancellationToken.None).ConfigureAwait(true));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.InvalidDataException)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
            return false;
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal async Task AnalyzeAiAssetAsync(AiCandidateViewModel? candidate)
    {
        if (candidate is null || _aiBatchWorkflow is null || _aiBusy)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            ApplyAiProjection(await _aiBatchWorkflow.AnalyzeSelectedAssetAsync(
                candidate.BatchId,
                candidate.StyleId,
                candidate.AssetId,
                includeSizeTableSource: false,
                CancellationToken.None).ConfigureAwait(true));
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal async Task SelectAiSizeTableSourceAsync(AiSizeTableSourceCandidateViewModel? candidate)
    {
        if (candidate is null || _aiBatchWorkflow is null || _aiBusy)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            ApplyAiProjection(await _aiBatchWorkflow.SelectSizeTableSourceAsync(
                candidate.BatchId,
                candidate.StyleId,
                candidate.AssetId,
                CancellationToken.None).ConfigureAwait(true));
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToAiUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    internal void ApplyAiProjection(AiBatchAnalysisProjection projection)
    {
        // Rebuilding the projection creates new style view-model instances. If
        // the user is currently inside the style review page, keep the logical
        // style selection attached to the new instance; otherwise the review
        // pane remains bound to a stale object and appears not to change after
        // a successful replacement.
        var selectedStyleId = SelectedAiStyle?.StyleId;
        (string StyleId, string RequirementId)? replacementKey = AiReplacementTarget is null
            ? null
            : (AiReplacementTarget.StyleId, AiReplacementTarget.RequirementId);
        var replacementAssetId = SelectedAiReplacementAsset?.AssetId;
        _aiBatchProjection = projection;
        AiStatusMessage = projection.StatusText;
        AiStyles.Clear();
        foreach (var style in projection.Styles)
        {
            AiStyles.Add(new AiStyleAnalysisViewModel(style, this));
        }
        if (selectedStyleId is not null)
        {
            var reboundStyle = AiStyles.FirstOrDefault(style =>
                string.Equals(style.StyleId, selectedStyleId, StringComparison.Ordinal));
            SelectedAiStyle = reboundStyle;
            if (reboundStyle is null && _host.IsAiStyleReviewScreen)
            {
                _host.ShowAiAnalysis();
            }
        }
        RebuildStatusFilters();
        ApplyStyleFilter();
        if (replacementKey is { } key)
        {
            var restoredTarget = AiStyles
                .Where(style => string.Equals(style.StyleId, key.StyleId, StringComparison.Ordinal))
                .SelectMany(style => style.Pages)
                .SelectMany(page => page.Requirements)
                .FirstOrDefault(requirement =>
                    string.Equals(requirement.RequirementId, key.RequirementId, StringComparison.Ordinal));
            if (restoredTarget is not null)
            {
                SetAiReplacementState(restoredTarget, replacementAssetId);
            }
            else
            {
                _aiReplacementTarget = null;
                _selectedAiReplacementAsset = null;
            }
        }
        OnPropertyChanged(nameof(AiProjection));
        NotifyAiChanged();
        UpdateAiAutoRefresh();
    }

    internal bool IsAiStyleExpanded(string styleId) => _expandedAiStyleIds.Contains(styleId);

    internal void SetAiStyleExpanded(string styleId, bool isExpanded)
    {
        if (isExpanded)
        {
            _expandedAiStyleIds.Add(styleId);
        }
        else
        {
            _expandedAiStyleIds.Remove(styleId);
        }
    }

    internal void NotifyAiChanged()
    {
        OnPropertyChanged(nameof(IsAiAnalysisVisible));
        OnPropertyChanged(nameof(IsAiStyleReviewVisible));
        OnPropertyChanged(nameof(SelectedAiStyle));
        OnPropertyChanged(nameof(HasAiAnalysis));
        OnPropertyChanged(nameof(AiBatchStatusText));
        OnPropertyChanged(nameof(AiStatusMessage));
        OnPropertyChanged(nameof(AiBatchProgressText));
        OnPropertyChanged(nameof(AiBatchStatusState));
        OnPropertyChanged(nameof(AiPageTitle));
        OnPropertyChanged(nameof(AiStyleReviewContextText));
        OnPropertyChanged(nameof(AiPageSubtitle));
        OnPropertyChanged(nameof(AiTemplateSummary));
        OnPropertyChanged(nameof(AiTemplateMeta));
        OnPropertyChanged(nameof(AiBatchSummary));
        OnPropertyChanged(nameof(AiBatchMeta));
        OnPropertyChanged(nameof(AiOutputSummary));
        OnPropertyChanged(nameof(AiOutputMeta));
        OnPropertyChanged(nameof(AiPageSummary));
        OnPropertyChanged(nameof(AiPageMeta));
        OnPropertyChanged(nameof(AiSummaryHeight));
        OnPropertyChanged(nameof(AiSummaryColumnCount));
        OnPropertyChanged(nameof(AiStyleRowHeight));
        OnPropertyChanged(nameof(AiStyleReviewHeight));
        OnPropertyChanged(nameof(AiRequirementRowHeight));
        OnPropertyChanged(nameof(AiAssetTileHeight));
        OnPropertyChanged(nameof(AiZeroOutputPanelWidth));
        OnPropertyChanged(nameof(AiPrimaryActionText));
        OnPropertyChanged(nameof(IsAiZeroOutput));
        OnPropertyChanged(nameof(IsAiRegularState));
        OnPropertyChanged(nameof(AiZeroOutputTitle));
        OnPropertyChanged(nameof(AiZeroOutputDescription));
        OnPropertyChanged(nameof(AiZeroReasonOneLabel));
        OnPropertyChanged(nameof(AiZeroReasonOneText));
        OnPropertyChanged(nameof(AiZeroReasonTwoLabel));
        OnPropertyChanged(nameof(AiZeroReasonTwoText));
        OnPropertyChanged(nameof(AiZeroReasonThreeLabel));
        OnPropertyChanged(nameof(AiZeroReasonThreeText));
        OnPropertyChanged(nameof(CanRetryAiAnalysis));
        OnPropertyChanged(nameof(IsAiBusy));
        OnPropertyChanged(nameof(IsAiCandidatePreviewVisible));
        OnPropertyChanged(nameof(AiCandidatePreview));
        OnPropertyChanged(nameof(CanConfirmAiCandidateSelection));
        OnPropertyChanged(nameof(CanAnalyzeAiPreviewAsset));
        OnPropertyChanged(nameof(AiReplacementTarget));
        OnPropertyChanged(nameof(SelectedAiReplacementAsset));
        OnPropertyChanged(nameof(IsAiReplacementActive));
        OnPropertyChanged(nameof(AiReplacementInstructionText));
        OnPropertyChanged(nameof(ConfirmAiReplacementText));
        OnPropertyChanged(nameof(CanConfirmAiReplacement));
        OnPropertyChanged(nameof(IsAiUploadConsentGranted));
        OnPropertyChanged(nameof(IsAiUploadConsentRevoked));
        OnPropertyChanged(nameof(IsAiUploadConsentDialogVisible));
        OnPropertyChanged(nameof(IsBatchScanConsentPending));
        OnPropertyChanged(nameof(IsReferenceLearningConsentPending));
        OnPropertyChanged(nameof(AiUploadConsentAcknowledged));
        OnPropertyChanged(nameof(AiUploadConsentStatusText));
        OnPropertyChanged(nameof(AiUploadConsentDetailText));
        OnPropertyChanged(nameof(CanGrantAiUploadConsent));
        OnPropertyChanged(nameof(CanRevokeAiUploadConsent));
        OnPropertyChanged(nameof(CanConfirmAiUploadConsent));
        OnPropertyChanged(nameof(CanPauseAi));
        OnPropertyChanged(nameof(CanResumeAi));
        OnPropertyChanged(nameof(CanCancelAi));
        OnPropertyChanged(nameof(IsAiReady));
        _host.RaiseResultCommandsChanged();
        (PauseAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResumeAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (WaitForAiNetworkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (GrantAiUploadConsentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RevokeAiUploadConsentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshAiConsentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmAiUploadConsentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmAiCandidateSelectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AnalyzeAiPreviewAssetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (BeginAiReplacementCommand as RelayCommand<AiRequirementUsageViewModel>)?.RaiseCanExecuteChanged();
        (SelectAiReplacementAssetCommand as RelayCommand<AiStyleAssetViewModel>)?.RaiseCanExecuteChanged();
        (ConfirmAiReplacementCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenAiStyleReviewCommand as RelayCommand<AiStyleAnalysisViewModel>)?.RaiseCanExecuteChanged();
    }

    internal static string ToAiUserMessage(string code)
    {
        if (code.StartsWith("AI_CHECKPOINT_", StringComparison.Ordinal)
            || code.StartsWith("AI_SCHEDULER_STATE_", StringComparison.Ordinal)
            || code is "AI_SCHEDULER_PENDING_LEDGER_MISSING"
                or "AI_SCHEDULER_PENDING_SESSION_MISSING")
        {
            return "本地分析状态未保存或不一致，未发送新的 AI 请求。请重新扫描并建立新批次。";
        }

        return code switch
        {
            "AI_INPUT_BATCH_NOT_READY" => "批次尚未完成扫描，请先完成输入检查。",
            "AI_BATCH_NOT_FOUND" => "找不到该批次，请返回工作台重新打开。",
            "INPUT_PERSISTENCE_FAILURE" or "AI_INPUT_PERSISTENCE_FAILURE" =>
                "本地状态未能安全保存，未发送 AI 请求。请重新扫描并建立新批次；源图片未修改。",
            "INPUT_MANIFEST_DATABASE_MISMATCH" =>
                "本地批次清单与保存记录不一致，已阻止 AI 请求。请重新扫描并建立新批次。",
            "AI_SESSION_NOT_FOUND" => "分析状态缺失，需要恢复；未启动生成。",
            "AI_SELECTION_NOT_AVAILABLE" => "当前页面还没有可供人工锁定的分析结果。",
            "AI_SELECTION_STATE_MISMATCH" => "本地分析状态不一致，当前用图没有更改；请刷新后重新选择。",
            "AI_SELECTION_REVISION_CONFLICT" => "分析结果已更新，请刷新后重新选择。",
            "TPL_INSTANCE_PRODUCTION_FACTS_FROZEN" => "该批次实例事实已冻结；修改用图或尺码请新建批次，原批次只用于恢复。",
            "TPL_INSTANCE_SELECTION_REQUIRED" => "请选择明确的输出实例后锁定用图。",
            "TPL_INSTANCE_SELECTION_FACTS_CHANGED" or "TPL_INSTANCE_SELECTION_REVISION_CONFLICT" => "实例选图事实或版本已变化；未更改原锁，请刷新核对，必要时新建批次。",
            "TPL_INSTANCE_COLOR_FACTS_UNRESOLVED" => "本款颜色事实尚未闭合；不会猜测主色或副色，请核对分析输入后新建批次。",
            "AI_REFINEMENT_SELECTION_INVALID" => "所选素材已失效，请刷新后重新选择。",
            "AI_REFINEMENT_SELECTION_NOT_LOCKED" => "请先锁定选中的候选图，再进行逐图主体分析。",
            "AI_UPLOAD_CONSENT_REQUIRED" => "首次使用 AI 前请先同意上传授权：仅上传压缩分析副本到火山方舟，不上传 PSD、原始高清图片、本地路径或正式输出；可在设置中确认。",
            "AI_UPLOAD_CONSENT_REVOKED" => "上传授权已撤回，请在设置中重新授权后再开始分析。",
            "AI_SCHEDULER_CANCELLED" => "该批次已取消，不能继续分析。",
            "AI_CHECKPOINT_BATCH_SCOPE_INVALID" => "批次正在恢复，请稍后刷新。",
            "AI_PROVIDER_REQUEST_TOO_LARGE" => "最终 JSON 请求体超过 16 MiB；未发送 AI 请求。请减少本款照片后重新建立批次。",
            _ => "分析未能安全完成，请按页面提示处理后重试。"
        };
    }    internal void InitializeCommands()
    {
        PrepareRenderingCommand = new AsyncRelayCommand(PrepareRenderingAsync, () => IsAiReady && !IsAiBusy);
        _aiPageState.PauseCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(static (service, batchId, token) => service.PauseAsync(batchId, token)),
            () => CanPauseAi);
        _aiPageState.ResumeCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(static (service, batchId, token) => service.ResumeAsync(batchId, token)),
            () => CanResumeAi);
        _aiPageState.CancelCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(static (service, batchId, token) => service.CancelAsync(batchId, token)),
            () => CanCancelAi);
        _aiPageState.WaitForNetworkCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(static (service, batchId, token) => service.WaitForNetworkAsync(batchId, token)),
            () => HasAiAnalysis && !IsAiBusy);
        _aiPageState.RefreshCommand = new AsyncRelayCommand(
            RefreshAiAnalysisAsync,
            () => HasAiAnalysis && !IsAiBusy);
        _aiPageState.RetryCommand = new AsyncRelayCommand(
            RetryAiAnalysisAsync,
            () => CanRetryAiAnalysis);
        _aiPageState.LockSelectionCommand = new AsyncRelayCommand<AiCandidateViewModel>(
            LockAiSelectionAsync);
        _aiPageState.AnalyzeAssetCommand = new AsyncRelayCommand<AiCandidateViewModel>(
            AnalyzeAiAssetAsync);
        _aiPageState.SelectSizeTableSourceCommand = new AsyncRelayCommand<AiSizeTableSourceCandidateViewModel>(
            SelectAiSizeTableSourceAsync);
        _aiPageState.SelectTopNCandidateCommand = new RelayCommand<AiCandidateViewModel>(SelectTopNCandidate);
        _aiPageState.SelectStatusFilterCommand = new RelayCommand<AiStatusFilterViewModel>(SelectStatusFilter);
        _aiPageState.GrantUploadConsentCommand = new AsyncRelayCommand(
            GrantAiUploadConsentAsync,
            () => CanGrantAiUploadConsent);
        _aiPageState.RevokeUploadConsentCommand = new AsyncRelayCommand(
            RevokeAiUploadConsentAsync,
            () => CanRevokeAiUploadConsent);
        _aiPageState.RefreshConsentCommand = new AsyncRelayCommand(
            RefreshUploadConsentAsync,
            () => AiWorkflowAvailable && !IsAiBusy);
        _aiPageState.ConfirmUploadConsentCommand = new AsyncRelayCommand(
            ConfirmAiUploadConsentAsync,
            () => CanConfirmAiUploadConsent);
        _aiPageState.CancelUploadConsentCommand = new RelayCommand(CancelAiUploadConsent);
        _aiPageState.OpenCandidatePreviewCommand = new RelayCommand<AiCandidateViewModel>(OpenAiCandidatePreview);
        _aiPageState.CloseCandidatePreviewCommand = new RelayCommand(CloseAiCandidatePreview);
        _aiPageState.ConfirmCandidateSelectionCommand = new AsyncRelayCommand(
            ConfirmAiCandidateSelectionAsync,
            () => CanConfirmAiCandidateSelection);
        _aiPageState.AnalyzePreviewAssetCommand = new AsyncRelayCommand(
            AnalyzeAiPreviewAssetAsync,
            () => CanAnalyzeAiPreviewAsset);
        _aiPageState.OpenRequirementPreviewCommand = new RelayCommand<AiRequirementUsageViewModel>(OpenAiRequirementPreview);
        _aiPageState.OpenSizeTableSourcePreviewCommand = new RelayCommand<AiSizeTableSourceCandidateViewModel>(OpenAiSizeTableSourcePreview);
        _aiPageState.BeginReplacementCommand = new RelayCommand<AiRequirementUsageViewModel>(
            BeginAiReplacement,
            _ => !IsAiBusy);
        _aiPageState.SelectReplacementAssetCommand = new RelayCommand<AiStyleAssetViewModel>(
            SelectAiReplacementAsset,
            CanSelectAiAssetFromLibrary);
        _aiPageState.BeginSizeTableSourceReplacementCommand = new RelayCommand(BeginAiSizeTableSourceReplacement);
        _aiPageState.ConfirmReplacementCommand = new AsyncRelayCommand(
            ConfirmAiReplacementAsync,
            () => CanConfirmAiReplacement);
        _aiPageState.CancelReplacementCommand = new RelayCommand(ClearAiReplacementState);
        _aiPageState.OpenStyleReviewCommand = new RelayCommand<AiStyleAnalysisViewModel>(OpenAiStyleReview);
        _aiPageState.ReturnFromStyleReviewCommand = new RelayCommand(ReturnFromAiStyleReview);
    }

    internal void SelectTopNCandidate(AiCandidateViewModel? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        foreach (var style in _aiPageState.Styles.Where(style => style.StyleId == candidate.StyleId))
        {
            foreach (var page in style.Pages)
            {
                foreach (var item in page.Candidates.Where(item =>
                             item.RequirementId == candidate.RequirementId))
                {
                    item.IsTopNSelected = item.AssetId == candidate.AssetId;
                }
            }
        }
    }

    internal void SelectStatusFilter(AiStatusFilterViewModel? filter)
    {
        if (filter is null)
        {
            return;
        }

        _aiPageState.SelectedFilter = filter.Filter;
        ApplyStyleFilter();
        foreach (var item in _aiPageState.StatusFilters)
        {
            item.IsSelected = item.Filter == _aiPageState.SelectedFilter;
        }
    }

    internal void RebuildStatusFilters()
    {
        _aiPageState.StatusFilters.Clear();
        AddFilter(AiStyleFilter.All, "全部", _aiPageState.Styles.Count, "Neutral");
        AddFilter(AiStyleFilter.Ready, "可生成", _aiPageState.Styles.Count(IsFullyGeneratableStyle), "Ready");
        AddFilter(AiStyleFilter.Partial, "部分可生成", _aiPageState.Styles.Count(IsPartiallyGeneratableStyle), "Partial");
        AddFilter(AiStyleFilter.Review, "待检查", _aiPageState.Styles.Count(IsReviewOnlyStyle), "Review");
        AddFilter(AiStyleFilter.Blocked, "不可生成", _aiPageState.Styles.Count(IsFullyBlockedStyle), "Blocked");
    }

    internal void ApplyStyleFilter()
    {
        _aiPageState.VisibleStyles.Clear();
        foreach (var style in _aiPageState.Styles.Where(MatchesFilter))
        {
            _aiPageState.VisibleStyles.Add(style);
        }
    }

    private void AddFilter(AiStyleFilter filter, string label, int count, string tone) =>
        _aiPageState.StatusFilters.Add(new AiStatusFilterViewModel(
            filter,
            label,
            count,
            tone,
            filter == _aiPageState.SelectedFilter));

    private bool MatchesFilter(AiStyleAnalysisViewModel style) => _aiPageState.SelectedFilter switch
    {
        AiStyleFilter.All => true,
        AiStyleFilter.Processing => style.Status is AiStyleUiStatus.Waiting or AiStyleUiStatus.Analysing,
        AiStyleFilter.Ready => IsFullyGeneratableStyle(style),
        AiStyleFilter.Partial => IsPartiallyGeneratableStyle(style),
        AiStyleFilter.Review => IsReviewOnlyStyle(style),
        AiStyleFilter.Blocked => IsFullyBlockedStyle(style),
        _ => true
    };

    private static bool IsHardBlocked(AiPageAnalysisViewModel page) =>
        page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.SystemError;

    private static bool NeedsReview(AiPageAnalysisViewModel page) =>
        page.Status is AiPageUiStatus.Waiting or AiPageUiStatus.Analysing
        || page.Status == AiPageUiStatus.ReviewRequired && page.RequiresManualConfiguration;

    private static bool IsFullyGeneratableStyle(AiStyleAnalysisViewModel style) =>
        style.CanGenerate && !style.Pages.Any(IsHardBlocked);

    private static bool IsPartiallyGeneratableStyle(AiStyleAnalysisViewModel style) =>
        style.CanGenerate && style.Pages.Any(IsHardBlocked);

    private static bool IsFullyBlockedStyle(AiStyleAnalysisViewModel style) =>
        !style.CanGenerate && style.Pages.Any(IsHardBlocked);

    private static bool IsReviewOnlyStyle(AiStyleAnalysisViewModel style) =>
        !style.CanGenerate && !style.Pages.Any(IsHardBlocked) && style.Pages.Any(NeedsReview);

    public async Task<bool> EnsureAiUploadConsentForReferenceLearningAsync()
    {
        var dialogVersion = _pageDialogVersion;
        if (!await _host.EnsureCredentialConfiguredAsync().ConfigureAwait(true))
        {
            _host.ReferenceLearningStatusMessage = _host.BatchStatusMessage;
            return false;
        }

        if (dialogVersion != _pageDialogVersion) return false;

        // Offline/unit-test compositions intentionally omit the batch
        // workflow. Preserve their local reference-learning behavior.
        if (_aiBatchWorkflow is null)
        {
            return true;
        }

        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .GetUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            if (dialogVersion != _pageDialogVersion) return false;
            NotifyAiChanged();
            if (IsAiUploadConsentGranted)
            {
                return true;
            }

            _pendingAiConsentContinuation = AiConsentContinuation.ReferenceLearning;
            AiUploadConsentAcknowledged = false;
            IsAiUploadConsentDialogVisible = true;
            _host.ReferenceLearningStatusMessage = IsAiUploadConsentRevoked
                ? "上传授权已撤回；重新授权后才会开始参考学习 AI 识别。"
                : "开始参考学习 AI 识别前，请先确认压缩分析图上传授权。未同意前不会上传或调用 AI。";
            return false;
        }
        catch (InvalidOperationException exception)
        {
            _host.ReferenceLearningStatusMessage = ToAiUserMessage(exception.Message);
            return false;
        }
    }

    internal async Task PrepareRenderingAsync()
    {
        var batchId = _host.CurrentBatchId ?? _aiBatchProjection?.BatchId;
        if (_aiBatchWorkflow is null || string.IsNullOrWhiteSpace(batchId) || _aiBusy)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            _host.ApplyRenderPreparation(await _aiBatchWorkflow
                .PrepareRenderingAsync(batchId, CancellationToken.None)
                .ConfigureAwait(true));
            _host.ShowResultOverview();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.InvalidDataException)
        {
            AiStatusMessage = ToRenderUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    private static string ToRenderUserMessage(string code) => code switch
    {
        "TPL_INSTANCE_PRODUCTION_FACTS_FROZEN" => "本批次实例事实已冻结；修改选图或尺码后请创建新批次，原批次只使用冻结事实恢复。",
        "AI_RENDER_ANALYSIS_NOT_CLOSED" => "分析尚未收口，不能生成页面计划。",
        "AI_RENDER_FACTORY_NOT_CONFIGURED" => "页面计划服务暂不可用；未启动 Photoshop。",
        "AI_RENDER_TEMPLATE_RESOURCE_NOT_FOUND" => "模板内部副本缺失，请返回模板检查。",
        "AI_RENDER_SNAPSHOT_MISMATCH" or "AI_RENDER_SNAPSHOT_PAGE_MISMATCH" =>
            "批次模板快照与当前资源不一致，已停止生成。",
        _ => "页面计划未能安全准备；未启动 Photoshop，请检查批次状态。"
    };
    public async Task RestoreAiAnalysisAsync(string batchId)
    {
        if (_aiBatchWorkflow is null) return;
        var projection = await _aiBatchWorkflow.RefreshAsync(batchId, CancellationToken.None).ConfigureAwait(true);
        if (projection is not null)
        {
            ApplyAiProjection(projection);
            _host.ShowAiAnalysis();
        }
    }

    internal async Task<AiRenderPlanPreparation?> PrepareSavedSizePageAsync(string batchId) =>
        _aiBatchWorkflow is null ? null
            : await _aiBatchWorkflow.PrepareRenderingAsync(batchId, CancellationToken.None).ConfigureAwait(true);

    internal void NotifyShellViewChanged()
    {
        OnPropertyChanged(nameof(IsAiAnalysisVisible));
        OnPropertyChanged(nameof(IsAiStyleReviewVisible));
        OnPropertyChanged(nameof(AiSummaryHeight));
        OnPropertyChanged(nameof(AiSummaryColumnCount));
        OnPropertyChanged(nameof(AiStyleRowHeight));
        OnPropertyChanged(nameof(AiStyleReviewHeight));
        OnPropertyChanged(nameof(AiRequirementRowHeight));
        OnPropertyChanged(nameof(AiAssetTileHeight));
        OnPropertyChanged(nameof(AiZeroOutputPanelWidth));
        UpdateAiAutoRefresh();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        DisposeAiAutoRefresh();
        GC.SuppressFinalize(this);
    }
}

public interface IAiAnalysisHost
{
    string? CurrentBatchId { get; }
    bool IsBatchNarrowLayout { get; }
    bool IsAiAnalysisVisible { get; }
    bool IsAiStyleReviewVisible { get; }
    bool IsAiStyleReviewScreen { get; }
    bool CanRestartBatchAnalysis { get; }
    string BatchStatusMessage { get; set; }
    string ReferenceLearningStatusMessage { get; set; }
    void NotifyMainVisibilityChanged();
    void ShowAiAnalysis();
    void ShowAiStyleReview();
    void ShowResultOverview();
    void RaiseResultCommandsChanged();
    void ApplyRenderPreparation(AiRenderPlanPreparation preparation);
    void SetBatchStatus(BatchInputUiStatus status, string message);
    Task<bool> EnsureCredentialConfiguredAsync();
    Task StartReferenceLearningAsync();
    Task ScanBatchInputAsync();
    Task RestartBatchAnalysisAsync();
}
