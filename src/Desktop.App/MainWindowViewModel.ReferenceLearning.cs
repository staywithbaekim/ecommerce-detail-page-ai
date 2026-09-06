using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Desktop.Application.Templates;

namespace Desktop.App;

public enum TemplateCalibrationMode
{
    DragAndDrop = 0,
    ReferenceLearning
}

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<string, ReferenceLearningDocument> _referenceLearningDocuments =
        new(StringComparer.Ordinal);
    private IReferenceLearningService? _referenceLearning;
    private IReferenceLearningCommitService? _referenceLearningCommit;
    private TemplateCalibrationMode _templateCalibrationMode;
    private string _referenceLearningPhotoDirectory = "";
    private string _referenceLearningStatusMessage = "请选择制作这些 PSD 时使用的同款源照片文件夹。";
    private bool _referenceLearningBusy;
    private ReferenceLearningFrameViewModel? _selectedReferenceLearningFrame;
    private ReferenceLearningPhotoViewModel? _selectedReferenceLearningPhoto;

    public ObservableCollection<ReferenceLearningFrameViewModel> ReferenceLearningFrames { get; } = [];

    public ObservableCollection<ReferenceLearningPhotoViewModel> ReferenceLearningPhotos { get; } = [];

    public ICommand ChooseReferenceLearningFolderCommand { get; private set; } = null!;

    public ICommand AcceptReferenceLearningFrameCommand { get; private set; } = null!;

    public ICommand ConfirmReferenceLearningPageCommand { get; private set; } = null!;

    public ICommand ConfirmAllQualifiedReferenceLearningCommand { get; private set; } = null!;

    public ICommand RejectReferenceLearningFrameCommand { get; private set; } = null!;

    public ICommand ReturnReferenceLearningToManualCommand { get; private set; } = null!;

    public ICommand ReanalyzeReferenceLearningCommand { get; private set; } = null!;

    public ICommand CancelReferenceLearningCommand { get; private set; } = null!;

    public bool IsDragCalibrationModeSelected
    {
        get => _templateCalibrationMode == TemplateCalibrationMode.DragAndDrop;
        set
        {
            if (value)
            {
                SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
            }
        }
    }

    public bool IsReferenceLearningModeSelected
    {
        get => _templateCalibrationMode == TemplateCalibrationMode.ReferenceLearning;
        set
        {
            if (value)
            {
                SetTemplateCalibrationMode(TemplateCalibrationMode.ReferenceLearning);
            }
        }
    }

    public bool IsReferenceLearningReviewMode => _templateScreen == TemplateScreen.ReferenceLearning;

    public bool IsManualCalibrationReviewMode => !IsReferenceLearningReviewMode;

    public bool HasReferenceLearningPhotoDirectory => !string.IsNullOrWhiteSpace(
        _referenceLearningPhotoDirectory);

    public string ReferenceLearningPhotoDirectoryDisplay => HasReferenceLearningPhotoDirectory
        ? new DirectoryInfo(_referenceLearningPhotoDirectory).Name
        : "尚未选择源照片文件夹";

    public string ReferenceLearningStatusMessage
    {
        get => _referenceLearningStatusMessage;
        private set => SetTemplateField(ref _referenceLearningStatusMessage, value);
    }

    public bool ReferenceLearningBusy
    {
        get => _referenceLearningBusy;
        private set
        {
            if (SetTemplateField(ref _referenceLearningBusy, value))
            {
                OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
                OnPropertyChanged(nameof(CanCommitReferenceLearning));
                RaiseReferenceLearningCommands();
                RaiseTemplateCommandCanExecuteChanged();
            }
        }
    }

    public IEnumerable CalibrationFrameItems => IsReferenceLearningReviewMode
        ? ReferenceLearningFrames
        : CalibrationDraft?.Frames ?? [];

    public string CalibrationWorkflowStatusMessage => IsReferenceLearningReviewMode
        ? ReferenceLearningStatusMessage
        : CalibrationDraft?.StatusMessage ?? TemplateStatusMessage;

    public string CalibrationCanvasInstruction => IsReferenceLearningReviewMode
        ? "选择右侧源照片，确认用途后可直接拖动与缩放"
        : "拖入代表款素材，或在右侧选择文件";

    public object? CalibrationPreviewImageSource => IsReferenceLearningReviewMode
        ? SelectedReferenceLearningPhoto?.PreviewSource
        : CalibrationPreviewImagePath;

    public string CalibrationFooterMetaText => IsReferenceLearningReviewMode
        ? ReferenceLearningPageProgress
        : CalibrationZoomText;

    public bool CanSaveCurrentCalibration => !IsReferenceLearningReviewMode
        || CanCommitReferenceLearning;

    public string CalibrationStartActionText => IsReferenceLearningModeSelected
        ? "开始学习并复核"
        : "开始校准";

    public string CalibrationModeHelpText => IsReferenceLearningModeSelected
        ? "PSD 中的参考款只用于学习；保存后的规则必须由新批次另选目标款素材。"
        : "在模板画布逐框放入正确素材，并由人工确认。";

    public string ReferenceLearningPageProgress
    {
        get
        {
            var pages = ReferenceLearningPages();
            var ready = pages.Count(page => _referenceLearningDocuments.TryGetValue(
                page.PageId,
                out var document) && document.State == ReferenceLearningState.ReadyToCommit);
            return $"{ready}/{pages.Length} 页已完成复核";
        }
    }

    public string ReferenceLearningFrameSummary =>
        SelectedReferenceLearningFrame is { } frame
            ? frame.StructuralSummary
            : "结构检测会折叠同一图片框的重叠替换层，只保留栈顶候选。";

    public ReferenceLearningFrameViewModel? SelectedReferenceLearningFrame
    {
        get => _selectedReferenceLearningFrame;
        set
        {
            if (ReferenceEquals(_selectedReferenceLearningFrame, value))
            {
                return;
            }

            _selectedReferenceLearningFrame?.SetSelected(false);
            _selectedReferenceLearningFrame = value;
            _selectedReferenceLearningFrame?.SetSelected(true);
            OnPropertyChanged();
            if (value is not null)
            {
                CalibrationFrameIdInput = value.FrameId;
                CalibrationRequirementInput = value.SemanticLabel;
                CalibrationScaleInput = value.Scale;
                CalibrationOffsetXInput = value.OffsetX;
                CalibrationOffsetYInput = value.OffsetY;
                SelectedReferenceLearningPhoto = ReferenceLearningPhotos.FirstOrDefault(photo =>
                    string.Equals(photo.AssetId, value.ReferenceAssetId, StringComparison.Ordinal));
            }
            else
            {
                SelectedReferenceLearningPhoto = null;
            }

            NotifyCalibrationFrameChanged();
            OnPropertyChanged(nameof(ReferenceLearningMatchText));
            OnPropertyChanged(nameof(ReferenceLearningConfidenceText));
            OnPropertyChanged(nameof(ReferenceLearningDecisionText));
            OnPropertyChanged(nameof(ReferenceLearningFrameSummary));
            OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
            RaiseReferenceLearningCommands();
        }
    }

    public ReferenceLearningPhotoViewModel? SelectedReferenceLearningPhoto
    {
        get => _selectedReferenceLearningPhoto;
        set
        {
            if (ReferenceEquals(_selectedReferenceLearningPhoto, value))
            {
                return;
            }

            _selectedReferenceLearningPhoto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationPreviewImagePath));
            OnPropertyChanged(nameof(CalibrationPreviewImageSource));
            OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
            RaiseReferenceLearningCommands();
        }
    }

    public string ReferenceLearningMatchText => SelectedReferenceLearningFrame?.MatchText
        ?? "尚未选择图片框";

    public string ReferenceLearningConfidenceText => SelectedReferenceLearningFrame?.ConfidenceText
        ?? "—";

    public string ReferenceLearningDecisionText => SelectedReferenceLearningFrame?.RegistrationLabel
        ?? "尚未选择图片框";

    public string ReferenceLearningQualifiedCandidateSummary
    {
        get
        {
            var document = CurrentReferenceLearningDocument;
            if (document is null)
            {
                return "";
            }

            var qualified = document.Candidates.Count(candidate => candidate.IsAutoConfirmable);
            var manual = document.Candidates.Count(candidate =>
                candidate.ReviewDecision == ReferenceReviewDecision.Pending
                && !candidate.IsAutoConfirmable);
            return manual > 0
                ? $"本页可直接确认 {qualified} 个高置信候选；另有 {manual} 个候选需逐项处理。"
                : qualified > 0
                    ? $"本页有 {qualified} 个高置信候选可直接确认；仍需显式确认后才能提交。"
                    : "本页没有可批量确认的候选；请逐项处理异常或低置信候选。";
        }
    }

    public bool CanConfirmReferenceLearningPage => !ReferenceLearningBusy
        && CurrentReferenceLearningDocument is { } document
        && document.Candidates.Any(candidate => candidate.IsAutoConfirmable);

    public bool CanConfirmAllQualifiedReferenceLearning => !ReferenceLearningBusy
        && _referenceLearningDocuments.Values.Any(document =>
            document.Candidates.Any(candidate => candidate.IsAutoConfirmable));

    public bool CanAcceptReferenceLearningFrame => !ReferenceLearningBusy
        && SelectedReferenceLearningFrame is not null
        && SelectedReferenceLearningFrame.IsProduct
        && SelectedReferenceLearningPhoto is not null
        && !string.IsNullOrWhiteSpace(CalibrationRequirementInput);

    public bool CanCommitReferenceLearning
    {
        get
        {
            if (ReferenceLearningBusy)
            {
                return false;
            }

            var pages = ReferenceLearningPages();
            return pages.Length > 0 && pages.All(page =>
                _referenceLearningDocuments.TryGetValue(page.PageId, out var document)
                && document.State == ReferenceLearningState.ReadyToCommit);
        }
    }

    private ReferenceLearningDocument? CurrentReferenceLearningDocument =>
        SelectedTemplatePage is not null
        && _referenceLearningDocuments.TryGetValue(SelectedTemplatePage.PageId, out var document)
            ? document
            : null;

    private void InitializeReferenceLearning(
        IReferenceLearningService? referenceLearning,
        IReferenceLearningCommitService? referenceLearningCommit)
    {
        _referenceLearning = referenceLearning;
        _referenceLearningCommit = referenceLearningCommit;
        ChooseReferenceLearningFolderCommand = new AsyncRelayCommand(ChooseReferenceLearningFolderAsync);
        AcceptReferenceLearningFrameCommand = new AsyncRelayCommand(
            () => ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.Accepted),
            () => CanAcceptReferenceLearningFrame);
        ConfirmReferenceLearningPageCommand = new AsyncRelayCommand(
            ConfirmReferenceLearningPageAsync,
            () => CanConfirmReferenceLearningPage);
        ConfirmAllQualifiedReferenceLearningCommand = new AsyncRelayCommand(
            ConfirmAllQualifiedReferenceLearningAsync,
            () => CanConfirmAllQualifiedReferenceLearning);
        RejectReferenceLearningFrameCommand = new AsyncRelayCommand(
            () => ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.Rejected),
            () => SelectedReferenceLearningFrame is not null && !ReferenceLearningBusy);
        ReturnReferenceLearningToManualCommand = new AsyncRelayCommand(
            ReturnReferenceLearningToManualAsync,
            () => SelectedReferenceLearningFrame is not null && !ReferenceLearningBusy);
        ReanalyzeReferenceLearningCommand = new AsyncRelayCommand(
            ReanalyzeCurrentReferenceLearningPageAsync,
            () => SelectedTemplatePage is not null && HasReferenceLearningPhotoDirectory && !ReferenceLearningBusy);
        CancelReferenceLearningCommand = new AsyncRelayCommand(CancelReferenceLearningAsync);

        ReferenceLearningFrames.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CalibrationFrameItems));
            OnPropertyChanged(nameof(CanCommitReferenceLearning));
            OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
            OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
            OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
        };
    }

    private void SetTemplateCalibrationMode(TemplateCalibrationMode mode)
    {
        if (_templateCalibrationMode == mode)
        {
            return;
        }

        _templateCalibrationMode = mode;
        OnPropertyChanged(nameof(IsDragCalibrationModeSelected));
        OnPropertyChanged(nameof(IsReferenceLearningModeSelected));
        OnPropertyChanged(nameof(CalibrationStartActionText));
        OnPropertyChanged(nameof(CalibrationModeHelpText));
        OnPropertyChanged(nameof(CanContinuePageSettings));
        RaiseTemplateCommandCanExecuteChanged();
    }

    private async Task ChooseReferenceLearningFolderAsync()
    {
        if (_folderPicker is null)
        {
            ReferenceLearningStatusMessage = "本机文件夹选择器不可用。";
            return;
        }

        var selected = await _folderPicker.PickFolderAsync(
            "选择制作参考 PSD 时使用的源照片文件夹",
            HasReferenceLearningPhotoDirectory ? _referenceLearningPhotoDirectory : null,
            CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        _referenceLearningPhotoDirectory = Path.GetFullPath(selected);
        ReferenceLearningStatusMessage = "已选择源照片文件夹；路径只在本次本地学习中使用。";
        OnPropertyChanged(nameof(HasReferenceLearningPhotoDirectory));
        OnPropertyChanged(nameof(ReferenceLearningPhotoDirectoryDisplay));
        OnPropertyChanged(nameof(CanContinuePageSettings));
        RaiseTemplateCommandCanExecuteChanged();
        RaiseReferenceLearningCommands();
    }

    private async Task StartReferenceLearningAsync()
    {
        if (_referenceLearning is null || _templateDraft is null || !HasReferenceLearningPhotoDirectory)
        {
            TemplateStatusMessage = "请选择参考款源照片文件夹，并确认本地学习服务可用。";
            return;
        }

        var pages = ReferenceLearningPages();
        if (pages.Length == 0)
        {
            TemplateStatusMessage = "当前模板没有需要学习的普通套图页。";
            return;
        }

        ReferenceLearningBusy = true;
        SetTemplateScreen(TemplateScreen.ReferenceLearning);
        try
        {
            _referenceLearningDocuments.Clear();
            foreach (var page in pages)
            {
                var request = CreateReferenceLearningRequest(page);
                var result = await _referenceLearning.ResumeAsync(request, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!result.IsSuccess && result.Error?.Code == "TPL_LEARN_SESSION_NOT_FOUND")
                {
                    result = await _referenceLearning.AnalyzeAsync(request, CancellationToken.None)
                        .ConfigureAwait(true);
                }

                if (!result.IsSuccess)
                {
                    SelectedTemplatePage = page;
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                    DisplayReferenceLearningPage(page);
                    return;
                }

                if (result.Document!.State is ReferenceLearningState.Cancelled
                    or ReferenceLearningState.Committed)
                {
                    result = await _referenceLearning.AnalyzeAsync(request, CancellationToken.None)
                        .ConfigureAwait(true);
                    if (!result.IsSuccess)
                    {
                        SelectedTemplatePage = page;
                        ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                        return;
                    }
                }

                _referenceLearningDocuments[page.PageId] = result.Document!;
            }

            SelectedTemplatePage = pages.FirstOrDefault(page =>
                _referenceLearningDocuments[page.PageId].State != ReferenceLearningState.ReadyToCommit)
                ?? pages[0];
            DisplayReferenceLearningPage(SelectedTemplatePage);
            var candidateCount = _referenceLearningDocuments.Values.Sum(document =>
                document.Candidates.Count(candidate => candidate.IsProduct));
            var fixedCount = _referenceLearningDocuments.Values.Sum(document =>
                document.Candidates.Count(candidate => !candidate.IsProduct));
            var supersededCount = _referenceLearningDocuments.Values.Sum(document =>
                document.Candidates.Sum(candidate => candidate.SupersededLayerEvidence.Count));
            ReferenceLearningStatusMessage =
                $"已识别 {candidateCount} 个商品图位；{fixedCount} 个固定内容保留；折叠 {supersededCount} 个旧替换层。高置信候选可按本页或全部合格候选确认，异常候选仍需逐项处理。";
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ReanalyzeCurrentReferenceLearningPageAsync()
    {
        if (_referenceLearning is null || SelectedTemplatePage is null)
        {
            return;
        }

        ReferenceLearningBusy = true;
        try
        {
            var result = await _referenceLearning.AnalyzeAsync(
                CreateReferenceLearningRequest(SelectedTemplatePage),
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            _referenceLearningDocuments[SelectedTemplatePage.PageId] = result.Document!;
            DisplayReferenceLearningPage(SelectedTemplatePage);
            ReferenceLearningStatusMessage = "当前页已重新分析，旧复核结论未沿用。";
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ApplyReferenceLearningReviewAsync(ReferenceReviewDecision decision)
    {
        if (_referenceLearning is null
            || CurrentReferenceLearningDocument is not { } document
            || SelectedReferenceLearningFrame is not { } frame)
        {
            return;
        }

        ReferenceLearningBusy = true;
        try
        {
            var review = new ReferenceFrameReview(
                frame.FrameId,
                decision,
                decision == ReferenceReviewDecision.Accepted
                    ? SelectedReferenceLearningPhoto?.AssetId
                    : null,
                CalibrationRequirementInput,
                CalibrationScaleInput,
                CalibrationOffsetXInput,
                CalibrationOffsetYInput,
                decision == ReferenceReviewDecision.Rejected
                    ? "用户确认该智能对象不是可替换商品图片框。"
                    : null);
            var result = await _referenceLearning.ApplyReviewAsync(
                document,
                [review],
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            var updatedDocument = result.Document!;
            _referenceLearningDocuments[document.PageId] = updatedDocument;
            var nextFrameId = updatedDocument.Candidates
                .FirstOrDefault(candidate => candidate.ReviewDecision == ReferenceReviewDecision.Pending)?.FrameId
                ?? frame.FrameId;
            DisplayReferenceLearningPage(SelectedTemplatePage, nextFrameId);
            ReferenceLearningStatusMessage = updatedDocument.State switch
            {
                ReferenceLearningState.ReadyToCommit => "当前页已全部复核；继续检查其他页面或保存模板组。",
                ReferenceLearningState.RequiresManualCalibration => "当前页已转为人工校准，学习规则不会提交。",
                _ => "当前图片框复核已保存。"
            };
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ConfirmReferenceLearningPageAsync()
    {
        if (_referenceLearning is null
            || SelectedTemplatePage is null
            || CurrentReferenceLearningDocument is not { } document)
        {
            return;
        }

        var reviews = BuildQualifiedReviews(document);
        if (reviews.Length == 0)
        {
            ReferenceLearningStatusMessage = "本页没有可直接确认的高置信候选；请逐项处理异常候选。";
            return;
        }

        await ApplyReferenceLearningReviewsAsync(
            document,
            reviews,
            $"本页已确认 {reviews.Length} 个高置信候选；异常候选仍需逐项处理。",
            SelectedTemplatePage).ConfigureAwait(true);
    }

    private async Task ConfirmAllQualifiedReferenceLearningAsync()
    {
        if (_referenceLearning is null)
        {
            return;
        }

        var pending = _referenceLearningDocuments.Values
            .Select(document => (Document: document, Reviews: BuildQualifiedReviews(document)))
            .Where(item => item.Reviews.Length > 0)
            .ToArray();
        if (pending.Length == 0)
        {
            ReferenceLearningStatusMessage = "当前没有可直接确认的高置信候选；请逐项处理异常候选。";
            return;
        }

        ReferenceLearningBusy = true;
        try
        {
            var confirmed = 0;
            foreach (var item in pending)
            {
                var result = await _referenceLearning.ApplyReviewAsync(
                    item.Document,
                    item.Reviews,
                    CancellationToken.None).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                    return;
                }

                _referenceLearningDocuments[item.Document.PageId] = result.Document!;
                confirmed += item.Reviews.Length;
            }

            if (SelectedTemplatePage is not null
                && _referenceLearningDocuments.TryGetValue(SelectedTemplatePage.PageId, out var current))
            {
                DisplayReferenceLearningPage(SelectedTemplatePage);
                var remaining = current.Candidates.Count(candidate =>
                    candidate.ReviewDecision == ReferenceReviewDecision.Pending);
                ReferenceLearningStatusMessage = remaining > 0
                    ? $"已批量确认 {confirmed} 个高置信候选；当前页仍有 {remaining} 个候选需逐项处理。"
                    : $"已批量确认 {confirmed} 个高置信候选；仍需显式检查后才能保存模板组。";
            }
            else
            {
                ReferenceLearningStatusMessage = $"已批量确认 {confirmed} 个高置信候选；异常候选仍需逐项处理。";
            }
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private async Task ApplyReferenceLearningReviewsAsync(
        ReferenceLearningDocument document,
        IReadOnlyList<ReferenceFrameReview> reviews,
        string successMessage,
        TemplatePageRowViewModel page)
    {
        ReferenceLearningBusy = true;
        try
        {
            var result = await _referenceLearning!.ApplyReviewAsync(
                document,
                reviews,
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            _referenceLearningDocuments[document.PageId] = result.Document!;
            DisplayReferenceLearningPage(page);
            ReferenceLearningStatusMessage = successMessage;
        }
        finally
        {
            ReferenceLearningBusy = false;
            NotifyReferenceLearningStateChanged();
        }
    }

    private static ReferenceFrameReview[] BuildQualifiedReviews(
        ReferenceLearningDocument document) => document.Candidates
        .Where(candidate => candidate.IsAutoConfirmable)
        .Select(candidate => new ReferenceFrameReview(
            candidate.FrameId,
            ReferenceReviewDecision.Accepted,
            candidate.ReferenceAssetId,
            candidate.SemanticLabel,
            Note: "系统根据闭合证据批量确认；保留 PSD 已提取的裁切、缩放和偏移。"))
        .ToArray();

    private async Task ReturnReferenceLearningToManualAsync()
    {
        await ApplyReferenceLearningReviewAsync(ReferenceReviewDecision.ManualCalibration)
            .ConfigureAwait(true);
        if (SelectedTemplatePage is null)
        {
            return;
        }

        SetTemplateCalibrationMode(TemplateCalibrationMode.DragAndDrop);
        SetTemplateScreen(TemplateScreen.Calibration);
        await LoadCalibrationPageAsync(SelectedTemplatePage).ConfigureAwait(true);
        TemplateStatusMessage = "已退回拖拽校准；学习候选不会提交为模板规则。";
    }

    private async Task CancelReferenceLearningAsync()
    {
        if (_referenceLearning is null)
        {
            SetTemplateScreen(TemplateScreen.PageSettings);
            return;
        }

        ReferenceLearningBusy = true;
        try
        {
            foreach (var document in _referenceLearningDocuments.Values.ToArray())
            {
                if (document.State is ReferenceLearningState.Cancelled
                    or ReferenceLearningState.Committed)
                {
                    continue;
                }

                var cancelled = await _referenceLearning.CancelAsync(document, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!cancelled.IsSuccess)
                {
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(cancelled.Error!);
                    return;
                }
                _referenceLearningDocuments[document.PageId] = cancelled.Document!;
            }

            TemplateStatusMessage = "本次学习已取消；模板草稿和用户源文件保持不变。";
            SetTemplateScreen(TemplateScreen.PageSettings);
        }
        finally
        {
            ReferenceLearningBusy = false;
        }
    }

    private async Task CommitReferenceLearningAsync()
    {
        if (_referenceLearning is null
            || _referenceLearningCommit is null
            || _templateDraft is null
            || !CanCommitReferenceLearning)
        {
            ReferenceLearningStatusMessage = "所有普通套图页逐框确认后才能保存模板组。";
            return;
        }

        ReferenceLearningBusy = true;
        TemplateBusy = true;
        try
        {
            var pages = ReferenceLearningPages();
            var verifiedDocuments = new List<ReferenceLearningDocument>(pages.Length);
            foreach (var page in pages)
            {
                var verification = await _referenceLearning.ResumeAsync(
                    CreateReferenceLearningRequest(page),
                    CancellationToken.None).ConfigureAwait(true);
                if (!verification.IsSuccess)
                {
                    SelectedTemplatePage = page;
                    ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(verification.Error!);
                    return;
                }
                if (verification.Document!.State != ReferenceLearningState.ReadyToCommit)
                {
                    SelectedTemplatePage = page;
                    ReferenceLearningStatusMessage = "学习复核状态已变化；请重新检查当前页面。";
                    _referenceLearningDocuments[page.PageId] = verification.Document;
                    DisplayReferenceLearningPage(page);
                    return;
                }

                _referenceLearningDocuments[page.PageId] = verification.Document;
                verifiedDocuments.Add(verification.Document);
            }

            var documents = verifiedDocuments.ToArray();
            var result = await _referenceLearningCommit.CommitAsync(
                documents,
                _templateDraft,
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            _templateDetails = result.Template;
            foreach (var document in documents)
            {
                _referenceLearningDocuments[document.PageId] = document with
                {
                    State = ReferenceLearningState.Committed,
                    UpdatedAt = result.Document!.UpdatedAt,
                    BlockReason = null
                };
            }
            TemplateStatusMessage = "人工结果学习规则已原子保存；参考款照片不会进入后续目标批次。";
            OnPropertyChanged(nameof(TemplateDetailsTitle));
            OnPropertyChanged(nameof(TemplateDetailsMeta));
            OnPropertyChanged(nameof(TemplateDetailPages));
            OnPropertyChanged(nameof(CanIncrementTemplate));
            SetTemplateScreen(TemplateScreen.Details);
        }
        finally
        {
            TemplateBusy = false;
            ReferenceLearningBusy = false;
        }
    }

    private void DisplayReferenceLearningPage(
        TemplatePageRowViewModel? page,
        string? preferredFrameId = null)
    {
        ReferenceLearningFrames.Clear();
        ReferenceLearningPhotos.Clear();
        SelectedReferenceLearningFrame = null;
        if (page is null || !_referenceLearningDocuments.TryGetValue(page.PageId, out var document))
        {
            NotifyReferenceLearningStateChanged();
            return;
        }

        foreach (var photo in document.ReferencePhotos)
        {
            ReferenceLearningPhotos.Add(new ReferenceLearningPhotoViewModel(
                photo,
                ResolveReferencePhotoPath(photo.RelativePath)));
        }
        foreach (var candidate in document.Candidates)
        {
            ReferenceLearningFrames.Add(new ReferenceLearningFrameViewModel(candidate));
        }

        SelectedReferenceLearningFrame = ReferenceLearningFrames.FirstOrDefault(frame =>
                string.Equals(frame.FrameId, preferredFrameId, StringComparison.Ordinal))
            ?? ReferenceLearningFrames.FirstOrDefault(frame =>
                frame.Decision == ReferenceReviewDecision.Pending)
            ?? ReferenceLearningFrames.FirstOrDefault();
        NotifyReferenceLearningStateChanged();
    }

    private void SelectReferenceLearningFrame(string frameId)
    {
        SelectedReferenceLearningFrame = ReferenceLearningFrames.FirstOrDefault(frame =>
            string.Equals(frame.FrameId, frameId, StringComparison.Ordinal));
    }

    private ReferenceLearningRequest CreateReferenceLearningRequest(TemplatePageRowViewModel page)
    {
        if (_templateDraft is null || string.IsNullOrWhiteSpace(page.InternalPsdRelativePath))
        {
            throw new InvalidOperationException("TPL_LEARN_REQUEST_INVALID");
        }

        return new ReferenceLearningRequest(
            StableLearningId(_templateDraft.DraftRevisionId, page.PageId),
            page.PageId,
            page.InternalPsdRelativePath,
            _referenceLearningPhotoDirectory,
            TemplatePageImportService.GetPreviewRelativePath(page.InternalPsdRelativePath));
    }

    private TemplatePageRowViewModel[] ReferenceLearningPages() => TemplatePages
        .Where(page => page.IsAffected
            && page.Enabled
            && page.ContentType == TemplatePageContentTypeOption.NormalComposition)
        .ToArray();

    private string? ResolveReferencePhotoPath(string relativePath)
    {
        if (!HasReferenceLearningPhotoDirectory)
        {
            return null;
        }

        var root = Path.GetFullPath(_referenceLearningPhotoDirectory);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && File.Exists(candidate)
                ? candidate
                : null;
    }

    private static string StableLearningId(string revisionId, string pageId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{revisionId}\n{pageId}"))).ToLowerInvariant();
        return $"learn_{digest[..32]}";
    }

    private static string ToReferenceLearningUserMessage(ReferenceLearningError error) =>
        $"{error.Message} {error.UserAction}".Trim();

    private void NotifyReferenceLearningStateChanged()
    {
        OnPropertyChanged(nameof(CalibrationFrameItems));
        OnPropertyChanged(nameof(CalibrationWorkflowStatusMessage));
        OnPropertyChanged(nameof(CalibrationPreviewImagePath));
        OnPropertyChanged(nameof(CalibrationPreviewImageSource));
        OnPropertyChanged(nameof(CalibrationTemplatePreviewImagePath));
        OnPropertyChanged(nameof(CalibrationCanvasWidth));
        OnPropertyChanged(nameof(CalibrationCanvasHeight));
        OnPropertyChanged(nameof(CalibrationHeaderTitle));
        OnPropertyChanged(nameof(CanEditCalibration));
        OnPropertyChanged(nameof(CanClearCalibrationAsset));
        OnPropertyChanged(nameof(CanSaveCurrentCalibration));
        OnPropertyChanged(nameof(ReferenceLearningPageProgress));
        OnPropertyChanged(nameof(ReferenceLearningFrameSummary));
        OnPropertyChanged(nameof(CalibrationFooterMetaText));
        OnPropertyChanged(nameof(CanCommitReferenceLearning));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
        NotifyCalibrationFrameChanged();
        RaiseReferenceLearningCommands();
    }

    private void ResetReferenceLearningDraft()
    {
        _referenceLearningDocuments.Clear();
        ReferenceLearningFrames.Clear();
        ReferenceLearningPhotos.Clear();
        SelectedReferenceLearningFrame = null;
        ReferenceLearningStatusMessage = "请选择制作这些 PSD 时使用的同款源照片文件夹。";
        OnPropertyChanged(nameof(CanCommitReferenceLearning));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
    }

    private void RaiseReferenceLearningCommands()
    {
        (AcceptReferenceLearningFrameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmReferenceLearningPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmAllQualifiedReferenceLearningCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RejectReferenceLearningFrameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReturnReferenceLearningToManualCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReanalyzeReferenceLearningCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed class ReferenceLearningFrameViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public ReferenceLearningFrameViewModel(ReferenceFrameCandidate candidate)
    {
        Candidate = candidate;
    }

    public ReferenceFrameCandidate Candidate { get; }

    public string FrameId => Candidate.FrameId;

    public string LayerName => string.IsNullOrWhiteSpace(Candidate.LayerName)
        ? $"图片框 {Candidate.LayerId}"
        : Candidate.LayerName;

    public ReferenceReviewDecision Decision => Candidate.ReviewDecision;

    public string? ReferenceAssetId => Candidate.ReferenceAssetId;

    public bool IsProduct => Candidate.IsProduct;

    public bool IsAutoConfirmable => Candidate.IsAutoConfirmable;

    public string StructuralSummary => Candidate.IsProduct
        ? Candidate.SupersededLayerEvidence.Count > 0
            ? $"商品图位 · 已折叠 {Candidate.SupersededLayerEvidence.Count} 个旧替换层"
            : "商品图位 · 独立可见智能对象"
        : $"固定内容 · {Candidate.StructuralReasonText}";

    public string SemanticLabel => Candidate.SemanticLabel;

    public double Scale => Candidate.LayoutScale;

    public double OffsetX => Candidate.LayoutOffsetX;

    public double OffsetY => Candidate.LayoutOffsetY;

    public double Left => Candidate.LayoutSafeLeft;

    public double Top => Candidate.LayoutSafeTop;

    public double Width => Candidate.LayoutSafeRight - Candidate.LayoutSafeLeft;

    public double Height => Candidate.LayoutSafeBottom - Candidate.LayoutSafeTop;

    public string MatchText => !Candidate.IsProduct
        ? Candidate.StructuralReasonText
        : Candidate.MatchState switch
        {
            ReferenceMatchState.Exact => "嵌入内容与源照片 SHA-256 精确匹配",
            ReferenceMatchState.ManuallyConfirmed => "已由用户明确选择源照片",
            ReferenceMatchState.Ambiguous => "多个源照片内容相同，需要人工选择",
            ReferenceMatchState.Modified => "文件名相似但内容不同，不能据此匹配",
            ReferenceMatchState.ExternalLink => "外链智能对象不可读取",
            ReferenceMatchState.Unsupported => "智能对象结构不支持自动学习",
            _ => "未找到内容完全一致的源照片"
        };

    public string ConfidenceText => $"{Math.Round(Candidate.Confidence * 100):0}%";

    public string RegistrationLabel => !Candidate.IsProduct
        ? "固定内容（不替换）"
        : Candidate.ReviewDecision switch
        {
            ReferenceReviewDecision.Accepted => "已确认",
            ReferenceReviewDecision.Rejected => "保留模板内容",
            ReferenceReviewDecision.ManualCalibration => "转人工校准",
            _ => Candidate.IsAutoConfirmable ? "高置信，可直接确认" : "待复核"
        };

    public string StatusColor => !Candidate.IsProduct
        ? "#8E8E93"
        : Candidate.ReviewDecision switch
        {
            ReferenceReviewDecision.Accepted => "#34C759",
            ReferenceReviewDecision.Rejected => "#8E8E93",
            ReferenceReviewDecision.ManualCalibration => "#FF3B30",
            _ => "#E7C46A"
        };

    public bool IsSelected => _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetSelected(bool selected)
    {
        if (_isSelected == selected)
        {
            return;
        }

        _isSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }
}

public sealed record ReferenceLearningPhotoViewModel(
    string AssetId,
    string DisplayName,
    string Meta,
    string? SourcePath,
    ImageSource? PreviewSource)
{
    public ReferenceLearningPhotoViewModel(ReferencePhotoFingerprint photo, string? sourcePath)
        : this(
            photo.AssetId,
            photo.FileName,
            $"{photo.Width} × {photo.Height} · {photo.MimeType[6..].ToUpperInvariant()}",
            sourcePath,
            LoadPreview(sourcePath))
    {
    }


    private static BitmapImage? LoadPreview(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return null;
        }
    }
}
