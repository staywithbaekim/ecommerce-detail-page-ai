using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Ai;

namespace Desktop.App;

public sealed partial class MainWindowViewModel
{
    private IAiBatchAnalysisWorkflow? _aiBatchWorkflow;
    private AiBatchAnalysisProjection? _aiBatchProjection;
    private AiUploadConsentRecord? _uploadConsent;
    private bool _aiBusy;
    private bool _isAiUploadConsentDialogVisible;
    private bool _aiUploadConsentAcknowledged;
    private bool _isAiCandidatePreviewVisible;
    private AiCandidateViewModel? _aiCandidatePreview;
    private AiRequirementUsageViewModel? _aiReplacementTarget;
    private AiStyleAssetViewModel? _selectedAiReplacementAsset;
    private AiStyleAnalysisViewModel? _selectedAiStyle;
    private string _aiStatusMessage = "等待分析";
    private AiStyleFilter _selectedAiFilter = AiStyleFilter.All;
    private readonly HashSet<string> _expandedAiStyleIds = new(StringComparer.Ordinal);
    private System.Windows.Threading.DispatcherTimer? _aiRefreshTimer;

    public ObservableCollection<AiStyleAnalysisViewModel> AiStyles { get; } = [];
    public ObservableCollection<AiStyleAnalysisViewModel> VisibleAiStyles { get; } = [];
    public ObservableCollection<AiStatusFilterViewModel> AiStatusFilters { get; } = [];

    public ICommand PauseAiAnalysisCommand { get; private set; } = null!;
    public ICommand ResumeAiAnalysisCommand { get; private set; } = null!;
    public ICommand CancelAiAnalysisCommand { get; private set; } = null!;
    public ICommand WaitForAiNetworkCommand { get; private set; } = null!;
    public ICommand RefreshAiAnalysisCommand { get; private set; } = null!;
    public ICommand LockAiSelectionCommand { get; private set; } = null!;
    public ICommand AnalyzeAiAssetCommand { get; private set; } = null!;
    public ICommand SelectTopNCandidateCommand { get; private set; } = null!;
    public ICommand SelectAiStatusFilterCommand { get; private set; } = null!;
    public ICommand GrantAiUploadConsentCommand { get; private set; } = null!;
    public ICommand RevokeAiUploadConsentCommand { get; private set; } = null!;
    public ICommand RefreshAiConsentCommand { get; private set; } = null!;
    public ICommand ConfirmAiUploadConsentCommand { get; private set; } = null!;
    public ICommand CancelAiUploadConsentCommand { get; private set; } = null!;
    public ICommand OpenAiCandidatePreviewCommand { get; private set; } = null!;
    public ICommand CloseAiCandidatePreviewCommand { get; private set; } = null!;
    public ICommand ConfirmAiCandidateSelectionCommand { get; private set; } = null!;
    public ICommand AnalyzeAiPreviewAssetCommand { get; private set; } = null!;
    public ICommand OpenAiRequirementPreviewCommand { get; private set; } = null!;
    public ICommand BeginAiReplacementCommand { get; private set; } = null!;
    public ICommand SelectAiReplacementAssetCommand { get; private set; } = null!;
    public ICommand ConfirmAiReplacementCommand { get; private set; } = null!;
    public ICommand CancelAiReplacementCommand { get; private set; } = null!;
    public ICommand OpenAiStyleReviewCommand { get; private set; } = null!;
    public ICommand ReturnFromAiStyleReviewCommand { get; private set; } = null!;

    public bool IsAiAnalysisVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.AiAnalysis;

    public bool IsAiStyleReviewVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.AiStyleReview
        && SelectedAiStyle is not null;

    public AiStyleAnalysisViewModel? SelectedAiStyle
    {
        get => _selectedAiStyle;
        private set
        {
            if (ReferenceEquals(_selectedAiStyle, value))
            {
                return;
            }

            _selectedAiStyle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAiStyleReviewVisible));
            OnPropertyChanged(nameof(IsWorkbenchVisible));
            OnPropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool HasAiAnalysis => _aiBatchProjection is not null;

    public AiBatchAnalysisProjection? AiProjection => _aiBatchProjection;

    public string AiBatchStatusText => _aiBatchProjection?.StatusText ?? "等待分析";

    public string AiStatusMessage
    {
        get => _aiStatusMessage;
        private set
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

    public string AiTemplateSummary => _aiBatchProjection?.TemplateSummary ?? "当前已校准模板";
    public string AiTemplateMeta => _aiBatchProjection?.TemplateMeta ?? "模板快照已冻结";
    public string AiBatchSummary => _aiBatchProjection?.BatchSummary ?? "当前输入批次";
    public string AiBatchMeta => _aiBatchProjection?.BatchMeta ?? "素材清单已登记";
    public string AiOutputSummary => _aiBatchProjection?.OutputSummary ?? "仅 JPG";
    public string AiOutputMeta => _aiBatchProjection?.OutputMeta ?? "正式输出目录已确认";
    public string AiPageSummary => _aiBatchProjection?.PageSummary ?? "0 个页面";
    public string AiPageMeta => _aiBatchProjection?.PageMeta ?? "等待分析";
    public GridLength AiSummaryHeight => IsBatchNarrowLayout
        ? new GridLength(124)
        : new GridLength(104);
    public int AiSummaryColumnCount => IsBatchNarrowLayout ? 2 : 4;
    public double AiStyleRowHeight => IsBatchNarrowLayout ? 56 : 64;
    public double AiStyleReviewHeight => IsBatchNarrowLayout ? 436 : 540;
    public double AiRequirementRowHeight => IsBatchNarrowLayout ? 96 : 104;
    public double AiAssetTileHeight => IsBatchNarrowLayout ? 116 : 144;
    public double AiZeroOutputPanelWidth => IsBatchNarrowLayout ? 420 : 464;
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
        private set
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
        private set
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

    public bool CanConfirmAiCandidateSelection =>
        IsAiCandidatePreviewVisible && AiCandidatePreview?.IsLockEnabled == true && !_aiBusy;

    public bool CanAnalyzeAiPreviewAsset =>
        IsAiCandidatePreviewVisible && AiCandidatePreview?.IsLocked == true && !_aiBusy;

    public AiRequirementUsageViewModel? AiReplacementTarget => _aiReplacementTarget;

    public AiStyleAssetViewModel? SelectedAiReplacementAsset => _selectedAiReplacementAsset;

    public bool IsAiReplacementActive => AiReplacementTarget is not null;

    public string AiReplacementInstructionText => AiReplacementTarget is null
        ? "点击左侧“替换”，再从这里选择一张照片。"
        : $"正在替换：{AiReplacementTarget.PageName} · {AiReplacementTarget.FrameDisplayName}";

    public bool CanConfirmAiReplacement =>
        AiReplacementTarget is not null
        && SelectedAiReplacementAsset is not null
        && string.Equals(
            AiReplacementTarget.StyleId,
            SelectedAiReplacementAsset.StyleId,
            StringComparison.Ordinal)
        && !string.Equals(
            AiReplacementTarget.SelectedAssetId,
            SelectedAiReplacementAsset.AssetId,
            StringComparison.Ordinal)
        && !_aiBusy;

    public bool IsAiUploadConsentGranted => _uploadConsent?.IsGranted == true;

    public bool IsAiUploadConsentRevoked => _uploadConsent?.State == AiUploadConsentState.Revoked;

    public bool IsAiUploadConsentDialogVisible
    {
        get => _isAiUploadConsentDialogVisible;
        private set
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

    private void InitializeAiBatchAnalysis(IAiBatchAnalysisWorkflow? workflow)
    {
        _aiBatchWorkflow = workflow;
        PauseAiAnalysisCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(workflowAction: static (service, batchId, token) => service.PauseAsync(batchId, token)),
            () => CanPauseAi);
        ResumeAiAnalysisCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(static (service, batchId, token) => service.ResumeAsync(batchId, token)),
            () => CanResumeAi);
        CancelAiAnalysisCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(static (service, batchId, token) => service.CancelAsync(batchId, token)),
            () => CanCancelAi);
        WaitForAiNetworkCommand = new AsyncRelayCommand(
            () => RunAiControlAsync(static (service, batchId, token) => service.WaitForNetworkAsync(batchId, token)),
            () => _aiBatchProjection is not null && !_aiBusy);
        RefreshAiAnalysisCommand = new AsyncRelayCommand(
            RefreshAiAnalysisAsync,
            () => _aiBatchProjection is not null && !_aiBusy);
        LockAiSelectionCommand = new AsyncRelayCommand<AiCandidateViewModel>(
            LockAiSelectionAsync);
        AnalyzeAiAssetCommand = new AsyncRelayCommand<AiCandidateViewModel>(
            AnalyzeAiAssetAsync);
        SelectTopNCandidateCommand = new RelayCommand<AiCandidateViewModel>(SelectTopNCandidate);
        SelectAiStatusFilterCommand = new RelayCommand<AiStatusFilterViewModel>(SelectAiStatusFilter);
        GrantAiUploadConsentCommand = new AsyncRelayCommand(
            GrantAiUploadConsentAsync,
            () => CanGrantAiUploadConsent);
        RevokeAiUploadConsentCommand = new AsyncRelayCommand(
            RevokeAiUploadConsentAsync,
            () => CanRevokeAiUploadConsent);
        RefreshAiConsentCommand = new AsyncRelayCommand(
            RefreshUploadConsentAsync,
            () => _aiBatchWorkflow is not null && !_aiBusy);
        ConfirmAiUploadConsentCommand = new AsyncRelayCommand(
            ConfirmAiUploadConsentAsync,
            () => CanConfirmAiUploadConsent);
        CancelAiUploadConsentCommand = new RelayCommand(CancelAiUploadConsent);
        OpenAiCandidatePreviewCommand = new RelayCommand<AiCandidateViewModel>(OpenAiCandidatePreview);
        CloseAiCandidatePreviewCommand = new RelayCommand(CloseAiCandidatePreview);
        ConfirmAiCandidateSelectionCommand = new AsyncRelayCommand(
            ConfirmAiCandidateSelectionAsync,
            () => CanConfirmAiCandidateSelection);
        AnalyzeAiPreviewAssetCommand = new AsyncRelayCommand(
            AnalyzeAiPreviewAssetAsync,
            () => CanAnalyzeAiPreviewAsset);
        OpenAiRequirementPreviewCommand = new RelayCommand<AiRequirementUsageViewModel>(OpenAiRequirementPreview);
        BeginAiReplacementCommand = new RelayCommand<AiRequirementUsageViewModel>(
            BeginAiReplacement,
            _ => !_aiBusy);
        SelectAiReplacementAssetCommand = new RelayCommand<AiStyleAssetViewModel>(
            SelectAiReplacementAsset,
            IsAiReplacementAssetSelectable);
        ConfirmAiReplacementCommand = new AsyncRelayCommand(
            ConfirmAiReplacementAsync,
            () => CanConfirmAiReplacement);
        CancelAiReplacementCommand = new RelayCommand(ClearAiReplacementState);
        OpenAiStyleReviewCommand = new RelayCommand<AiStyleAnalysisViewModel>(OpenAiStyleReview);
        ReturnFromAiStyleReviewCommand = new RelayCommand(ReturnFromAiStyleReview);
        _aiRefreshTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1),
            System.Windows.Threading.DispatcherPriority.Background,
            async (_, _) => await AutoRefreshAiAsync().ConfigureAwait(true),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _aiRefreshTimer.Stop();
        _ = RefreshUploadConsentAsync();
    }

    private async Task AutoRefreshAiAsync()
    {
        if (_aiBusy
            || _aiBatchWorkflow is null
            || _currentBatchId is null
            || !ShouldAutoRefreshAi)
        {
            UpdateAiAutoRefresh();
            return;
        }

        await RefreshAiAnalysisAsync().ConfigureAwait(true);
    }

    private bool ShouldAutoRefreshAi =>
        IsAiAnalysisVisible
        && _aiBatchProjection?.Status is AiBatchUiStatus.Waiting or AiBatchUiStatus.Analysing;

    private void UpdateAiAutoRefresh()
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

    private void DisposeAiAutoRefresh()
    {
        if (_aiRefreshTimer is null)
        {
            return;
        }

        _aiRefreshTimer.Stop();
        _aiRefreshTimer = null;
    }

    private void OpenAiCandidatePreview(AiCandidateViewModel? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        AiCandidatePreview = candidate;
        IsAiCandidatePreviewVisible = true;
        NotifyAiChanged();
    }

    private void CloseAiCandidatePreview()
    {
        IsAiCandidatePreviewVisible = false;
        AiCandidatePreview = null;
        NotifyAiChanged();
    }

    private async Task ConfirmAiCandidateSelectionAsync()
    {
        var candidate = AiCandidatePreview;
        if (candidate is not null && await TryLockAiSelectionAsync(candidate).ConfigureAwait(true))
        {
            CloseAiCandidatePreview();
        }
    }

    private async Task AnalyzeAiPreviewAssetAsync()
    {
        var candidate = AiCandidatePreview;
        if (candidate is null || !candidate.IsLocked)
        {
            return;
        }

        CloseAiCandidatePreview();
        await AnalyzeAiAssetAsync(candidate).ConfigureAwait(true);
    }

    private void OpenAiRequirementPreview(AiRequirementUsageViewModel? requirement)
    {
        var candidate = requirement?.CreatePreviewCandidate();
        if (candidate is null)
        {
            return;
        }

        OpenAiCandidatePreview(candidate);
    }

    internal void OpenAiStyleReview(AiStyleAnalysisViewModel? style)
    {
        if (style is null)
        {
            return;
        }

        SelectedAiStyle = style;
        _batchScreen = BatchWorkspaceScreen.AiStyleReview;
        NotifyBatchScreenChanged();
        NotifyAiChanged();
    }

    private void ReturnFromAiStyleReview()
    {
        ClearAiReplacementState();
        _batchScreen = BatchWorkspaceScreen.AiAnalysis;
        NotifyBatchScreenChanged();
        NotifyAiChanged();
    }

    private void BeginAiReplacement(AiRequirementUsageViewModel? requirement)
    {
        if (requirement is null || _aiBusy)
        {
            return;
        }

        SetAiReplacementState(requirement, requirement.SelectedAssetId);
        AiStatusMessage = $"请选择用于“{requirement.PageName} · {requirement.FrameDisplayName}”的照片；确认前不会更改当前用图。";
    }

    private bool IsAiReplacementAssetSelectable(AiStyleAssetViewModel? asset) =>
        asset is not null
        && AiReplacementTarget is not null
        && string.Equals(asset.StyleId, AiReplacementTarget.StyleId, StringComparison.Ordinal)
        && !_aiBusy;

    private void SelectAiReplacementAsset(AiStyleAssetViewModel? asset)
    {
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

    private async Task ConfirmAiReplacementAsync()
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
            var projection = await _aiBatchWorkflow.LockSelectionAsync(
                target.BatchId,
                target.StyleId,
                target.RequirementId,
                asset.AssetId,
                CancellationToken.None).ConfigureAwait(true);
            ApplyAiProjection(projection);
            ClearAiReplacementState();
            AiStatusMessage = $"已改用所选照片，并锁定“{target.PageName} · {target.FrameDisplayName}”；刷新或重启后仍会保留。";
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

    private void SetAiReplacementState(
        AiRequirementUsageViewModel requirement,
        string? selectedAssetId)
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
                    && string.Equals(asset.AssetId, selectedAssetId, StringComparison.Ordinal);
                if (asset.IsSelected)
                {
                    _selectedAiReplacementAsset = asset;
                }
            }
        }

        NotifyAiChanged();
    }

    private void ClearAiReplacementState()
    {
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

    private async Task<bool> EnsureAiUploadConsentForScanAsync()
    {
        if (_aiBatchWorkflow is null)
        {
            return true;
        }

        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .GetUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            NotifyAiChanged();
            if (IsAiUploadConsentGranted)
            {
                return true;
            }

            AiUploadConsentAcknowledged = false;
            IsAiUploadConsentDialogVisible = true;
            BatchStatusMessage = IsAiUploadConsentRevoked
                ? "上传授权已撤回；重新授权后才会扫描并发起新的 AI 请求。"
                : "请先确认 AI 分析与图片上传授权；未同意前不会扫描或发起 AI 请求。";
            return false;
        }
        catch (InvalidOperationException exception)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToAiUserMessage(exception.Message));
            return false;
        }
    }

    private async Task ConfirmAiUploadConsentAsync()
    {
        if (!CanConfirmAiUploadConsent || _aiBatchWorkflow is null)
        {
            return;
        }

        var granted = false;
        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            _uploadConsent = await _aiBatchWorkflow
                .GrantUploadConsentAsync(CancellationToken.None)
                .ConfigureAwait(true);
            granted = _uploadConsent.IsGranted;
            if (granted)
            {
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

        if (granted)
        {
            await ScanBatchInputAsync().ConfigureAwait(true);
        }
    }

    private void CancelAiUploadConsent()
    {
        IsAiUploadConsentDialogVisible = false;
        AiUploadConsentAcknowledged = false;
        BatchStatusMessage = "未授予上传权限；源文件未修改，扫描和 AI 分析均未启动。";
    }

    private async Task RefreshUploadConsentAsync()
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

    private async Task GrantAiUploadConsentAsync()
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

    private async Task RevokeAiUploadConsentAsync()
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

    private async Task StartAiAnalysisAsync(string batchId)
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
            _batchScreen = BatchWorkspaceScreen.AiAnalysis;
            NotifyBatchScreenChanged();
        }
        catch (InvalidOperationException exception)
        {
            SetBatchStatus(BatchInputUiStatus.ScanBlocked, ToAiUserMessage(exception.Message));
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    private async Task RefreshAiAnalysisAsync()
    {
        if (_aiBatchWorkflow is null || _currentBatchId is null)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            var projection = await _aiBatchWorkflow.RefreshAsync(_currentBatchId, CancellationToken.None).ConfigureAwait(true);
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

    private async Task RunAiControlAsync(
        Func<IAiBatchAnalysisWorkflow, string, CancellationToken, Task<AiBatchAnalysisProjection>> workflowAction)
    {
        if (_aiBatchWorkflow is null || _currentBatchId is null)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            ApplyAiProjection(await workflowAction(_aiBatchWorkflow, _currentBatchId, CancellationToken.None).ConfigureAwait(true));
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

    private async Task LockAiSelectionAsync(AiCandidateViewModel? candidate)
    {
        _ = await TryLockAiSelectionAsync(candidate).ConfigureAwait(true);
    }

    private async Task<bool> TryLockAiSelectionAsync(AiCandidateViewModel? candidate)
    {
        if (candidate is null || _aiBatchWorkflow is null || _aiBusy)
        {
            return false;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            ApplyAiProjection(await _aiBatchWorkflow.LockSelectionAsync(
                candidate.BatchId,
                candidate.StyleId,
                candidate.RequirementId,
                candidate.AssetId,
                CancellationToken.None).ConfigureAwait(true));
            return true;
        }
        catch (InvalidOperationException exception)
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

    private async Task AnalyzeAiAssetAsync(AiCandidateViewModel? candidate)
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

    private void SelectTopNCandidate(AiCandidateViewModel? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        foreach (var style in AiStyles.Where(style => style.StyleId == candidate.StyleId))
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

    private void SelectAiStatusFilter(AiStatusFilterViewModel? filter)
    {
        if (filter is null)
        {
            return;
        }

        _selectedAiFilter = filter.Filter;
        ApplyAiStyleFilter();
        foreach (var item in AiStatusFilters)
        {
            item.IsSelected = item.Filter == _selectedAiFilter;
        }
    }

    private void ApplyAiProjection(AiBatchAnalysisProjection projection)
    {
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
        RebuildAiStatusFilters();
        ApplyAiStyleFilter();
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

    private void RebuildAiStatusFilters()
    {
        AiStatusFilters.Clear();
        AddFilter(AiStyleFilter.All, "全部", AiStyles.Count, "Neutral");
        AddFilter(AiStyleFilter.Ready, "可生成", AiStyles.Count(style => style.Status == AiStyleUiStatus.Ready), "Ready");
        AddFilter(AiStyleFilter.Review, "待检查", AiStyles.Count(style =>
            style.Status is AiStyleUiStatus.Waiting or AiStyleUiStatus.Analysing or AiStyleUiStatus.ReviewRequired), "Review");
        AddFilter(AiStyleFilter.Blocked, "不可生成", AiStyles.Count(style => style.Status is AiStyleUiStatus.BusinessError or AiStyleUiStatus.SystemError), "Blocked");
    }

    private void AddFilter(AiStyleFilter filter, string label, int count, string tone) =>
        AiStatusFilters.Add(new AiStatusFilterViewModel(filter, label, count, tone, filter == _selectedAiFilter));

    private void ApplyAiStyleFilter()
    {
        VisibleAiStyles.Clear();
        foreach (var style in AiStyles.Where(MatchesAiFilter))
        {
            VisibleAiStyles.Add(style);
        }
    }

    private bool MatchesAiFilter(AiStyleAnalysisViewModel style) => _selectedAiFilter switch
    {
        AiStyleFilter.All => true,
        AiStyleFilter.Processing => style.Status is AiStyleUiStatus.Waiting or AiStyleUiStatus.Analysing,
        AiStyleFilter.Ready => style.Status == AiStyleUiStatus.Ready,
        AiStyleFilter.Review => style.Status is AiStyleUiStatus.Waiting or AiStyleUiStatus.Analysing or AiStyleUiStatus.ReviewRequired,
        AiStyleFilter.Blocked => style.Status is AiStyleUiStatus.BusinessError or AiStyleUiStatus.SystemError,
        _ => true
    };

    private void NotifyAiChanged()
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
        OnPropertyChanged(nameof(IsAiBusy));
        OnPropertyChanged(nameof(IsAiCandidatePreviewVisible));
        OnPropertyChanged(nameof(AiCandidatePreview));
        OnPropertyChanged(nameof(CanConfirmAiCandidateSelection));
        OnPropertyChanged(nameof(CanAnalyzeAiPreviewAsset));
        OnPropertyChanged(nameof(AiReplacementTarget));
        OnPropertyChanged(nameof(SelectedAiReplacementAsset));
        OnPropertyChanged(nameof(IsAiReplacementActive));
        OnPropertyChanged(nameof(AiReplacementInstructionText));
        OnPropertyChanged(nameof(CanConfirmAiReplacement));
        OnPropertyChanged(nameof(IsAiUploadConsentGranted));
        OnPropertyChanged(nameof(IsAiUploadConsentRevoked));
        OnPropertyChanged(nameof(IsAiUploadConsentDialogVisible));
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
        (PauseAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResumeAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (WaitForAiNetworkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshAiAnalysisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

    private static string ToAiUserMessage(string code) => code switch
    {
        "AI_INPUT_BATCH_NOT_READY" => "批次尚未完成扫描，请先完成输入检查。",
        "AI_BATCH_NOT_FOUND" => "找不到该批次，请返回工作台重新打开。",
        "AI_SESSION_NOT_FOUND" => "分析状态缺失，需要恢复；未启动生成。",
        "AI_SELECTION_NOT_AVAILABLE" => "当前页面还没有可供人工锁定的分析结果。",
        "AI_SELECTION_REVISION_CONFLICT" => "分析结果已更新，请刷新后重新选择。",
        "AI_REFINEMENT_SELECTION_INVALID" => "所选素材已失效，请刷新后重新选择。",
        "AI_REFINEMENT_SELECTION_NOT_LOCKED" => "请先锁定选中的候选图，再进行逐图主体分析。",
        "AI_UPLOAD_CONSENT_REQUIRED" => "首次使用 AI 前请先同意上传授权：仅上传压缩分析副本到火山方舟，不上传 PSD、原始高清图片、本地路径或正式输出；可在设置中确认。",
        "AI_UPLOAD_CONSENT_REVOKED" => "上传授权已撤回，请在设置中重新授权后再开始分析。",
        "AI_SCHEDULER_CANCELLED" => "该批次已取消，不能继续分析。",
        "AI_CHECKPOINT_BATCH_SCOPE_INVALID" => "批次正在恢复，请稍后刷新。",
        _ => "分析未能安全完成，请按页面提示处理后重试。"
    };
}

public sealed class AiStyleAnalysisViewModel
{
    private readonly MainWindowViewModel _owner;
    private readonly AiStyleAnalysisProjection _projection;
    private bool _isExpanded;
    private bool _isReplacementActive;
    private string _replacementInstructionText = "点击左侧“替换”，再从这里选择一张照片。";

    public AiStyleAnalysisViewModel(AiStyleAnalysisProjection projection, MainWindowViewModel owner)
    {
        _projection = projection;
        _owner = owner;
        _isExpanded = owner.IsAiStyleExpanded(projection.StyleId);
        Pages = new ObservableCollection<AiPageAnalysisViewModel>(projection.Pages.Select(page =>
            new AiPageAnalysisViewModel(page, owner)));
        AllRequirements = new ObservableCollection<AiRequirementUsageViewModel>(
            Pages.SelectMany(page => page.Requirements));
        Assets = new ObservableCollection<AiStyleAssetViewModel>((projection.Assets ?? [])
            .Select(asset => new AiStyleAssetViewModel(asset)));
        var useCounts = Pages
            .SelectMany(page => page.Requirements)
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.SelectedAssetId))
            .GroupBy(requirement => requirement.SelectedAssetId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            asset.SetUsageCount(useCounts.GetValueOrDefault(asset.AssetId));
        }
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string StyleName => _projection.StyleName;
    public string StatusText => _projection.StatusText;
    public string StatusState => _projection.StatusState;
    public AiStyleUiStatus Status => _projection.Status;
    public int AssetCount => _projection.AssetCount;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            _owner.SetAiStyleExpanded(StyleId, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
    public string MetaText => $"{AssetCount} 张素材 · {Pages.Count(page => page.Status is AiPageUiStatus.Ready or AiPageUiStatus.ReviewRequired)} / {Pages.Count} 页面已分析";
    public ObservableCollection<AiPageAnalysisViewModel> Pages { get; }
    public ObservableCollection<AiRequirementUsageViewModel> AllRequirements { get; }
    public ObservableCollection<AiStyleAssetViewModel> Assets { get; }
    public int RequirementCount => Pages.Sum(page => page.Requirements.Count);
    public string UsageSummaryText => $"{Pages.Count} 个页面 · {RequirementCount} 个图片框 · 向下滚动查看全部";
    public string AssetSummaryText => $"本款已登记 {Assets.Count} 张照片 · 不显示文件名";
    public bool IsReplacementActive
    {
        get => _isReplacementActive;
        private set
        {
            if (_isReplacementActive == value)
            {
                return;
            }

            _isReplacementActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacementActive)));
        }
    }
    public string ReplacementInstructionText
    {
        get => _replacementInstructionText;
        private set
        {
            if (_replacementInstructionText == value)
            {
                return;
            }

            _replacementInstructionText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReplacementInstructionText)));
        }
    }

    internal void SetReplacementActive(bool active, AiRequirementUsageViewModel? requirement)
    {
        IsReplacementActive = active;
        ReplacementInstructionText = active && requirement is not null
            ? $"正在替换：{requirement.PageName} · {requirement.FrameDisplayName}"
            : "点击左侧“替换”，再从这里选择一张照片。";
        foreach (var page in Pages)
        {
            foreach (var item in page.Requirements)
            {
                item.IsReplacementTarget = active && ReferenceEquals(item, requirement);
            }
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiPageAnalysisViewModel
{
    private readonly AiPageAnalysisProjection _projection;

    public AiPageAnalysisViewModel(AiPageAnalysisProjection projection, MainWindowViewModel owner)
    {
        _projection = projection;
        Candidates = new ObservableCollection<AiCandidateViewModel>(projection.Candidates.Select(candidate =>
            new AiCandidateViewModel(candidate)));
        var requirements = projection.Requirements ?? projection.Candidates
            .GroupBy(candidate => candidate.RequirementId, StringComparer.Ordinal)
            .Select(group =>
            {
                var selected = group.FirstOrDefault(candidate => candidate.IsLocked)
                    ?? group.OrderBy(candidate => candidate.Rank).First();
                return new AiRequirementSelectionProjection(
                    selected.BatchId,
                    selected.StyleId,
                    projection.PageId,
                    projection.PageName,
                    selected.RequirementId,
                    selected.FrameDisplayName,
                    selected.SelectionRequirement,
                    selected.IsLocked
                        ? AiRequirementSelectionUiStatus.ManualLocked
                        : AiRequirementSelectionUiStatus.AiSelected,
                    selected.IsLocked ? "人工选用 · 已锁定" : "AI 推荐 · 已选用",
                    selected.AssetId,
                    selected.PreviewPath);
            })
            .ToArray();
        Requirements = new ObservableCollection<AiRequirementUsageViewModel>(requirements.Select(requirement =>
            new AiRequirementUsageViewModel(requirement)));
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string PageId => _projection.PageId;
    public string PageName => _projection.PageName;
    public string StatusText => _projection.StatusText;
    public string StatusState => _projection.StatusState;
    public AiPageUiStatus Status => _projection.Status;
    public string ActionText => _projection.ActionText;
    public bool HasWarnings => _projection.HasWarnings;
    public IReadOnlyList<string> WarningTexts => _projection.WarningTexts;
    public ObservableCollection<AiCandidateViewModel> Candidates { get; }
    public ObservableCollection<AiRequirementUsageViewModel> Requirements { get; }
}

public sealed class AiRequirementUsageViewModel : INotifyPropertyChanged
{
    private readonly AiRequirementSelectionProjection _projection;
    private bool _isReplacementTarget;

    public AiRequirementUsageViewModel(AiRequirementSelectionProjection projection)
    {
        _projection = projection;
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string PageId => _projection.PageId;
    public string PageName => _projection.PageName;
    public string RequirementId => _projection.RequirementId;
    public string FrameDisplayName => _projection.FrameDisplayName;
    public string SelectionRequirement => _projection.SelectionRequirement;
    public string StatusText => _projection.StatusText;
    public string StatusState => _projection.StatusState;
    public string? SelectedAssetId => _projection.SelectedAssetId;
    public string PreviewPath => _projection.SelectedPreviewPath ?? "";
    public bool HasSelection => _projection.HasSelection;
    public string PreviewAutomationName => HasSelection
        ? $"查看{PageName}的{FrameDisplayName}当前用图"
        : $"{PageName}的{FrameDisplayName}尚未选图";
    public bool IsReplacementTarget
    {
        get => _isReplacementTarget;
        set
        {
            if (_isReplacementTarget == value)
            {
                return;
            }

            _isReplacementTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacementTarget)));
        }
    }

    public AiCandidateViewModel? CreatePreviewCandidate()
    {
        if (!HasSelection || SelectedAssetId is null)
        {
            return null;
        }

        return new AiCandidateViewModel(new AiCandidateProjection(
            BatchId,
            StyleId,
            RequirementId,
            SelectedAssetId,
            "已登记素材",
            1,
            1m,
            [],
            _projection.IsManualLocked,
            FrameDisplayName,
            PreviewPath,
            SelectionRequirement,
            PageName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiStyleAssetViewModel : INotifyPropertyChanged
{
    private readonly AiStyleAssetProjection _projection;
    private bool _isSelected;
    private int _usageCount;

    public AiStyleAssetViewModel(AiStyleAssetProjection projection)
    {
        _projection = projection;
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string AssetId => _projection.AssetId;
    public int DisplayOrder => _projection.DisplayOrder;
    public string DisplayName => $"照片 {DisplayOrder:D2}";
    public string PreviewPath => _projection.PreviewPath ?? "";
    public bool HasPreview => !string.IsNullOrWhiteSpace(_projection.PreviewPath);
    public bool IsCurrentlyUsed => _usageCount > 0;
    public string UsageText => _usageCount switch
    {
        <= 0 => "可选照片",
        1 => "1 个图片框正在使用",
        _ => $"{_usageCount} 个图片框正在使用"
    };
    public string SelectionStateText => IsSelected ? "已选择" : IsCurrentlyUsed ? "当前使用" : "可选照片";
    public string AutomationName => $"{DisplayName}，{SelectionStateText}";
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
        }
    }

    internal void SetUsageCount(int count)
    {
        _usageCount = Math.Max(0, count);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrentlyUsed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiCandidateViewModel : INotifyPropertyChanged
{
    private readonly AiCandidateProjection _projection;

    private bool _isTopNSelected;

    public AiCandidateViewModel(AiCandidateProjection projection)
    {
        _projection = projection;
        _isTopNSelected = projection.IsLocked || projection.Rank == 1;
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string RequirementId => _projection.RequirementId;
    public string AssetId => _projection.AssetId;
    public string AssetName => _projection.AssetName;
    public string RankText => _projection.RankText;
    public string ConfidenceText => _projection.ConfidenceText;
    public string EvidenceText => _projection.EvidenceText;
    public string FrameDisplayName => _projection.FrameDisplayName;
    public string PageName => _projection.PageName;
    public string PreviewPath => _projection.PreviewPath ?? "";
    public bool HasPreview => !string.IsNullOrWhiteSpace(_projection.PreviewPath);
    public string SelectionRequirement => _projection.SelectionRequirement;
    public string UsageText => $"用于：{PageName} · {FrameDisplayName}";
    public string RecommendationText => _projection.Rank == 1 ? "AI 推荐" : _projection.RankText;
    public string AnalysisHint => IsLocked
        ? "如需进一步确认商品边界，可选择“检查商品范围”；这会发起一次新的 AI 分析。"
        : "先确认选用这张图片，之后才能按需检查商品范围。";
    public bool IsLocked => _projection.IsLocked;
    public bool IsLockEnabled => IsTopNSelected && !_projection.IsLocked;
    public bool IsTopNSelected
    {
        get => _isTopNSelected;
        set
        {
            if (_isTopNSelected == value)
            {
                return;
            }

            _isTopNSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTopNSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLockEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionText)));
        }
    }
    public string SelectionText => IsTopNSelected ? "当前首选" : "选为首选";
    public string LockText => _projection.LockText;
    public string ChooseText => _projection.IsLocked ? "已选用" : "选用这张";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum AiStyleFilter
{
    All = 0,
    Processing,
    Review,
    Ready,
    Blocked
}

public sealed class AiStatusFilterViewModel(
    AiStyleFilter filter,
    string label,
    int count,
    string tone,
    bool isSelected) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;

    public AiStyleFilter Filter { get; } = filter;
    public string Label { get; } = label;
    public int Count { get; } = count;
    public string Tone { get; } = tone;
    public string DisplayText => Filter == AiStyleFilter.All ? Label : $"{Label} {Count}";
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
