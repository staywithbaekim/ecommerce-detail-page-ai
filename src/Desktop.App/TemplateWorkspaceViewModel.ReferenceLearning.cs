using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

public sealed partial class TemplateWorkspaceViewModel
{
    private readonly Dictionary<string, ReferenceLearningDocument> _referenceLearningDocuments =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReferenceLearningInputDraft> _referenceLearningInputDrafts =
        new(StringComparer.Ordinal);
    private readonly IReferenceLearningService? _referenceLearning;
    private readonly IReferenceLearningCommitService? _referenceLearningCommit;
    private readonly IReferenceLearningAiService? _referenceLearningAi;
    private TemplateCalibrationMode _templateCalibrationMode;
    private string _referenceLearningPhotoDirectory = "";
    private string _referenceLearningStatusMessage = "请选择制作这些 PSD 时使用的同款源照片文件夹。";
    private bool _referenceLearningBusy;
    // The local PSD pass produces a structural placeholder before the
    // whole-group semantic request returns. Keep that placeholder visibly
    // provisional and do not capture it as a user edit while the frame list
    // is being rebuilt.
    private bool _referenceLearningSemanticPending;
    private bool _refreshingReferenceLearningPage;
    private ReferenceLearningFrameViewModel? _selectedReferenceLearningFrame;
    private ReferenceLearningPhotoViewModel? _selectedReferenceLearningPhoto;

    private sealed record ReferenceLearningInputDraft(
        string SemanticLabel,
        bool IsSemanticUserOverride,
        double Scale,
        double OffsetX,
        double OffsetY,
        string? ReferenceAssetId);

    public ObservableCollection<ReferenceLearningFrameViewModel> ReferenceLearningFrames { get; } = [];

    public ObservableCollection<ReferenceLearningPhotoViewModel> ReferenceLearningPhotos { get; } = [];

    public ICommand ChooseReferenceLearningFolderCommand { get; internal set; } = null!;

    public ICommand AcceptReferenceLearningFrameCommand { get; internal set; } = null!;

    public ICommand ConfirmReferenceLearningPageCommand { get; internal set; } = null!;

    public ICommand ConfirmAllQualifiedReferenceLearningCommand { get; internal set; } = null!;

    public ICommand RejectReferenceLearningFrameCommand { get; internal set; } = null!;

    public ICommand ReturnReferenceLearningToManualCommand { get; internal set; } = null!;

    public ICommand ReanalyzeReferenceLearningCommand { get; internal set; } = null!;

    public ICommand CancelReferenceLearningCommand { get; internal set; } = null!;

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
        internal set => SetTemplateField(ref _referenceLearningStatusMessage, value);
    }

    public bool ReferenceLearningBusy
    {
        get => _referenceLearningBusy;
        internal set
        {
            if (SetTemplateField(ref _referenceLearningBusy, value))
            {
                OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
                OnPropertyChanged(nameof(CanEditReferenceLearningRule));
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
        ? "左侧选择图片框；中间查看 PSD 位置；右侧确认或修改素材、用途和布局"
        : "左侧选择图片框；中间查看 PSD 位置；右侧确认或修改素材、用途和布局";

    public object? CalibrationPreviewImageSource => IsReferenceLearningReviewMode
        ? null
        : CalibrationPreviewImagePath;

    public string CalibrationFooterMetaText => IsReferenceLearningReviewMode
        ? ReferenceLearningPageProgress
        : CalibrationZoomText;

    public bool CanSaveCurrentCalibration => !IsReferenceLearningReviewMode
        || CanCommitReferenceLearning;

    public string CalibrationStartActionText => !HasCalibratableTemplatePages
        ? "保存模板组"
        : IsReferenceLearningModeSelected
            ? "开始学习并复核"
            : "开始校准";

    public string CalibrationModeHelpText => !HasCalibratableTemplatePages
        ? "当前模板只有动态尺码表页，无需选择商品图或进行图片框校准。"
        : _templateCalibrationMode == TemplateCalibrationMode.ReferenceLearning
            ? "人工学习结果会先自动填入；两种方式最终都保存为同一套模板规则。"
            : "拖拽校准从用户选择开始；两种方式最终都保存为同一套模板规则。";

    public string CalibrationFlowStepText => IsReferenceLearningReviewMode
        ? "统一画布  /  人工结果学习  /  复核"
        : "统一画布  /  导入模板  /  页面设置  /  模板校准";

    public string CalibrationPageListTitle => IsTemplateCalibrationVisible
        ? "校准页面"
        : "页面";

    public string CalibrationEditorHintText => IsReferenceLearningReviewMode
        ? "人工学习已根据参考 PSD 和源照片预填当前图片框的素材、用途和布局；请直接核对，发现错误时再修改。"
        : "拖拽校准与人工学习使用同一套规则；当前图片框的素材、用途和布局都可以在此确认或修改。";

    public string ReferenceLearningPageProgress
    {
        get
        {
            if (IsReferenceLearningSemanticPending)
            {
                return "AI 正在生成当前页动态规则";
            }

            var (reviewed, total, pending, manual) = GetReferenceLearningProgress(
                SelectedTemplatePage);
            if (total == 0)
            {
                return "当前页没有可学习的图片框";
            }

            var suffix = manual > 0 ? $" · 需人工校准 {manual} 个" : "";
            return pending == 0
                ? $"当前页已复核 {reviewed}/{total} 个图片框{suffix}"
                : $"当前页已复核 {reviewed}/{total} 个图片框 · 还剩 {pending} 个未复核{suffix}";
        }
    }

    public bool IsReferenceLearningSemanticPending => IsReferenceLearningReviewMode
        && _referenceLearningSemanticPending;

    public string ReferenceLearningOverallProgress
    {
        get
        {
            if (IsReferenceLearningSemanticPending)
            {
                return "AI 正在结合整组参考照片生成动态规则";
            }

            var (reviewed, total, pending, manual) = GetReferenceLearningProgress();
            if (total == 0)
            {
                return "尚未开始分析图片框";
            }

            var suffix = manual > 0 ? $" · 需人工校准 {manual} 个" : "";
            return pending == 0
                ? $"全部页面已复核 {reviewed}/{total} 个图片框{suffix}"
                : $"全部页面已复核 {reviewed}/{total} 个图片框 · 还剩 {pending} 个未复核{suffix}";
        }
    }

    public string ReferenceLearningCurrentPageProgress => ReferenceLearningPageProgress;

    public string ReferenceLearningReviewInstruction
    {
        get
        {
            if (IsReferenceLearningSemanticPending)
            {
                return "正在等待 AI 返回逐框动态规则；完成后再进行人工确认。";
            }

            var (_, _, pending, manual) = GetReferenceLearningProgress(SelectedTemplatePage);
            if (pending > 0 && manual > 0)
            {
                return "请点击左侧标记“未复核”的图片框，确认规则或转人工校准。";
            }

            if (pending > 0)
            {
                return "请点击左侧标记“未复核”的图片框，确认未来选图规则。";
            }

            if (manual > 0)
            {
                return "当前页已作出决定，但存在“需人工校准”项，不能保存学习规则。";
            }

            return "当前页所有图片框都已作出决定；全部页面完成后才能保存模板组。";
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

            CaptureReferenceLearningInputs();
            _selectedReferenceLearningFrame?.SetSelected(false);
            _selectedReferenceLearningFrame = value;
            _selectedReferenceLearningFrame?.SetSelected(true);
            OnPropertyChanged();
            if (value is not null)
            {
                CalibrationFrameIdInput = value.FrameId;
                if (_referenceLearningInputDrafts.TryGetValue(value.FrameId, out var draft))
                {
                    // Geometry/photo drafts are always preserved, but only a
                    // real user text edit may override a newly returned AiV2
                    // semantic. Automatically captured local placeholders are
                    // never allowed to mask a refreshed model result.
                    CalibrationRequirementInput = draft.IsSemanticUserOverride
                        && !string.IsNullOrWhiteSpace(draft.SemanticLabel)
                            ? draft.SemanticLabel
                            : value.SemanticLabel;
                    CalibrationScaleInput = draft.Scale;
                    CalibrationOffsetXInput = draft.OffsetX;
                    CalibrationOffsetYInput = draft.OffsetY;
                }
                else
                {
                    CalibrationRequirementInput = value.SemanticLabel;
                    CalibrationScaleInput = value.Scale;
                    CalibrationOffsetXInput = value.OffsetX;
                    CalibrationOffsetYInput = value.OffsetY;
                }

                var referenceAssetId = _referenceLearningInputDrafts.TryGetValue(value.FrameId, out draft)
                    ? draft.ReferenceAssetId
                    : value.ReferenceAssetId;
                SelectedReferenceLearningPhoto = ReferenceLearningPhotos.FirstOrDefault(photo =>
                    string.Equals(photo.AssetId, referenceAssetId, StringComparison.Ordinal));
            }
            else
            {
                SelectedReferenceLearningPhoto = null;
            }

            NotifyCalibrationFrameChanged();
            OnPropertyChanged(nameof(ReferenceLearningMatchText));
            OnPropertyChanged(nameof(ReferenceLearningConfidenceText));
            OnPropertyChanged(nameof(ReferenceLearningDecisionText));
            OnPropertyChanged(nameof(IsReferenceLearningAiUnavailable));
            OnPropertyChanged(nameof(IsReferenceLearningSemanticWarningVisible));
            OnPropertyChanged(nameof(ReferenceLearningSemanticWarningText));
            OnPropertyChanged(nameof(ReferenceLearningFrameSummary));
            OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
            OnPropertyChanged(nameof(CalibrationRuleStatusText));
            OnPropertyChanged(nameof(CalibrationEditorHintText));
            OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
            OnPropertyChanged(nameof(ReferenceLearningPageProgress));
            OnPropertyChanged(nameof(ReferenceLearningCurrentPageProgress));
            OnPropertyChanged(nameof(ReferenceLearningReviewInstruction));
            OnPropertyChanged(nameof(IsReferenceLearningManualFallbackVisible));
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
            OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
            OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
            OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
            RaiseReferenceLearningCommands();
        }
    }

    public string ReferenceLearningMatchText => SelectedReferenceLearningFrame?.MatchText
        ?? "尚未选择图片框";

    public string ReferenceLearningConfidenceText => SelectedReferenceLearningFrame?.ConfidenceText
        ?? "—";

    public string ReferenceLearningDecisionText => SelectedReferenceLearningFrame?.RegistrationLabel
        ?? "尚未选择图片框";

    /// <summary>
    /// AI semantic generation is deliberately fail-closed. A local hint can
    /// still be useful as an editing starting point, but it must never look
    /// like a successful model result when the provider timed out or was not
    /// available.
    /// </summary>
    public bool IsReferenceLearningAiUnavailable => IsReferenceLearningReviewMode
        && SelectedReferenceLearningFrame?.Candidate.SemanticMetadata?.Source
            == ReferenceSemanticSource.AiUnavailable;

    public bool IsReferenceLearningSemanticWarningVisible =>
        IsReferenceLearningSemanticPending || IsReferenceLearningAiUnavailable;

    public string ReferenceLearningSemanticWarningText
    {
        get
        {
            if (IsReferenceLearningSemanticPending)
            {
                return "AI 语义识别进行中：正在结合整组参考照片生成逐框动态规则。当前文字只是本地初步提示，不是最终规则，请等待分析完成。";
            }

            if (!IsReferenceLearningAiUnavailable)
            {
                return "";
            }

            var reason = CurrentReferenceLearningDocument?.BlockReason;
            return string.IsNullOrWhiteSpace(reason)
                ? "AI 语义识别未完成。下方文字只是本地初步提示，不是 AI 识别结果；请手动填写并应用确认。"
                : $"AI 语义识别未完成：{reason}\n下方文字只是本地初步提示，不是 AI 识别结果；请手动填写并应用确认。";
        }
    }

    public bool CanEditReferenceLearningRule => IsReferenceLearningReviewMode
        ? !ReferenceLearningBusy
            && !IsReferenceLearningSemanticPending
            && CurrentReferenceLearningDocument is
            {
                State: not (
                ReferenceLearningState.Cancelled or
                ReferenceLearningState.Committed or
                ReferenceLearningState.Blocked)
            }
        : CanEditCalibration;

    public bool IsReferenceLearningManualFallbackVisible => IsReferenceLearningReviewMode
        && SelectedReferenceLearningFrame is { } frame
        && frame.IsProduct
        && (!frame.Candidate.IsEvidenceClosed || IsReferenceLearningAiUnavailable);

    public string ReferenceLearningRuleSummary
    {
        get
        {
            if (SelectedReferenceLearningFrame is not { } frame)
            {
                return "请点击左侧任一图片框，查看系统理解的未来选图规则。";
            }

            var photo = frame.Decision == ReferenceReviewDecision.Rejected
                ? "不适用（保留 PSD 原有内容）"
                : SelectedReferenceLearningPhoto?.DisplayName;
            if (string.IsNullOrWhiteSpace(photo))
            {
                photo = string.IsNullOrWhiteSpace(frame.Candidate.ReferencePhotoRelativePath)
                    ? "尚未匹配源照片"
                    : Path.GetFileName(frame.Candidate.ReferencePhotoRelativePath);
            }

            var semantic = EffectiveReferenceLearningSemantic(frame);
            var decision = frame.Decision switch
            {
                ReferenceReviewDecision.Accepted => "已确认规则",
                ReferenceReviewDecision.Rejected => "保留模板内容",
                ReferenceReviewDecision.ManualCalibration => "需人工校准",
                _ => "未复核"
            };
            var layout = string.Join(
                "；",
                $"缩放 {FormatRuleNumber(frame.Scale)}",
                $"水平偏移 {FormatRuleNumber(frame.OffsetX)}",
                $"垂直偏移 {FormatRuleNumber(frame.OffsetY)}");
            return $"未来选图规则：未来新款应选择“{semantic}”的照片放入此图片框。\n"
                + $"参考款对应照片：{photo}\n"
                + $"布局证据：沿用参考 PSD 已体现的裁切、蒙版、缩放和位置（{layout}）。\n"
                + $"当前状态：{decision}";
        }
    }

    public string CalibrationRuleStatusText
    {
        get
        {
            if (SelectedCalibrationFrame is not { } frame)
            {
                return "请选择图片框查看规则状态。";
            }

            if (IsReferenceLearningSemanticPending)
            {
                return "AI 识别进行中；下方文字仅为本地初步提示，暂不能编辑或确认。";
            }

            var referenceFrame = SelectedReferenceLearningFrame;
            var requirement = referenceFrame is null
                ? CalibrationRequirementInput.Trim()
                : EffectiveReferenceLearningSemantic(referenceFrame);
            if (IsReferenceLearningAiUnavailable)
            {
                if (!string.Equals(requirement, referenceFrame?.SemanticLabel, StringComparison.Ordinal)
                    && !ReferenceSelectionRequirementPolicy.IsValid(requirement))
                {
                    return "请填写 2–40 字的可复用图片类别，例如“人物正面端坐图”；不要写颜色、背景、家具或单张照片故事。";
                }

                return "AI 识别未完成；下方文字仅为本地初步提示，不能直接作为学习结果。请手动修改后再点击“应用并确认当前规则”。";
            }

            if (!ReferenceSelectionRequirementPolicy.IsValid(requirement))
            {
                return "规则必须是 2–40 字的可复用图片类别，例如“面料纹理特写图”；判断证据不要写进规则。";
            }

            return frame.HasPersistedBinding
                ? "已有模板规则；可直接核对或修改后应用。"
                : IsReferenceLearningReviewMode
                    ? "系统已预填候选规则；确认后会写入模板规则。"
                    : "当前图片框尚未保存模板规则；请先选择素材，再应用并确认。";
        }
    }

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
                ? $"本页有 {qualified} 条规则可以直接采用；另有 {manual} 个图片框需要逐项判断。"
                : qualified > 0
                    ? $"本页有 {qualified} 条规则可以直接采用；确认后会标记为“已确认规则”。"
                    : "本页没有可批量确认的规则；请点击“未复核”的图片框逐项处理。";
        }
    }

    public bool CanConfirmReferenceLearningPage => !ReferenceLearningBusy
        && !IsReferenceLearningSemanticPending
        && CurrentReferenceLearningDocument is { } document
        && document.Candidates.Any(candidate => candidate.IsAutoConfirmable);

    public bool CanConfirmAllQualifiedReferenceLearning => !ReferenceLearningBusy
        && !IsReferenceLearningSemanticPending
        && _referenceLearningDocuments.Values.Any(document =>
            document.Candidates.Any(candidate => candidate.IsAutoConfirmable));

    public bool CanAcceptReferenceLearningFrame => !ReferenceLearningBusy
        && !IsReferenceLearningSemanticPending
        && SelectedReferenceLearningFrame is not null
        && SelectedReferenceLearningFrame.IsProduct
        && SelectedReferenceLearningPhoto is not null
        && ReferenceSelectionRequirementPolicy.IsValid(
            EffectiveReferenceLearningSemantic(SelectedReferenceLearningFrame))

        // When AI is unavailable, accepting the unchanged local hint would
        // silently promote a heuristic into a learned rule. Require the user
        // to edit the text first so ApplyUserOverrideAsync records a
        // UserOverride semantic source.

        && (!IsReferenceLearningAiUnavailable
            || !string.Equals(
                EffectiveReferenceLearningSemantic(SelectedReferenceLearningFrame),
                SelectedReferenceLearningFrame.SemanticLabel,
                StringComparison.Ordinal));

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

    private void InitializeReferenceLearning()
    {
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
        OnPropertyChanged(nameof(CalibrationEditorHintText));
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
        RaiseTemplateCommandCanExecuteChanged();
        RaiseReferenceLearningCommands();
    }

    public async Task StartReferenceLearningAsync()
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

        // Reference learning uploads the same bounded analysis copies as a
        // normal batch analysis. Keep the privacy gate in front of the whole
        // workflow so an unconsented run cannot quietly fall back to the
        // generic local placeholder and look like a successful AI result.
        if (!await EnsureAiUploadConsentForReferenceLearningAsync().ConfigureAwait(true))
        {
            return;
        }

        ReferenceLearningBusy = true;
        SetTemplateScreen(TemplateScreen.ReferenceLearning);
        SetReferenceLearningSemanticPending(true);
        ClearReferenceLearningPreview();
        ReferenceLearningStatusMessage = "正在读取参考 PSD 并准备整组 AI 语义分析；完成前不会把本地占位当作最终规则。";
        try
        {
            _referenceLearningDocuments.Clear();
            // A previous review can have captured the old local placeholder
            // in this view-model. It must never override a newly returned
            // AiV2 semantic result.
            _referenceLearningInputDrafts.Clear();
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

            ReferenceLearningAiFailure? semanticFailure = null;
            if (_referenceLearningAi is not null)
            {
                ReferenceLearningStatusMessage = "本地结构已完成，正在调用 AI 生成整组逐框动态规则（只发起一次整组请求）。";
                var semantic = await _referenceLearningAi.GenerateSelectionRequirementsAsync(
                    pages.Select(page => new ReferenceSemanticGenerationCommand(
                        _referenceLearningDocuments[page.PageId],
                        _referenceLearningPhotoDirectory,
                        page.Name)).ToArray(),
                    CancellationToken.None).ConfigureAwait(true);
                foreach (var document in semantic.Documents)
                {
                    _referenceLearningDocuments[document.PageId] = document;
                }
                semanticFailure = semantic.Failure;
            }

            SelectedTemplatePage = pages.FirstOrDefault(page =>
                _referenceLearningDocuments[page.PageId].State != ReferenceLearningState.ReadyToCommit)
                ?? pages[0];
            // Keep the pending gate active while rebuilding the observable
            // frame list. SelectedReferenceLearningFrame's teardown would
            // otherwise capture the old placeholder into the input-draft
            // dictionary and mask the returned AiV2 text.
            _referenceLearningInputDrafts.Clear();
            DisplayReferenceLearningPage(SelectedTemplatePage);
            SetReferenceLearningSemanticPending(false);
            var candidateCount = _referenceLearningDocuments.Values.Sum(document =>
                document.Candidates.Count(candidate => candidate.IsProduct));
            ReferenceLearningStatusMessage = semanticFailure is null
                ? $"已识别 {candidateCount} 个有效可替换素材框。系统只显示同时匹配参考源照片、并在最终页面实际显示的内容层。"
                : semanticFailure.UserAction;
        }
        finally
        {
            SetReferenceLearningSemanticPending(false);
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
        SetReferenceLearningSemanticPending(true);
        ClearReferenceLearningPreview();
        ReferenceLearningStatusMessage = "正在重新分析当前页；AI 动态规则返回前不会显示本地占位为最终结果。";
        try
        {
            if (_referenceLearningDocuments.TryGetValue(
                    SelectedTemplatePage.PageId,
                    out var previousDocument))
            {
                foreach (var frame in previousDocument.Candidates)
                {
                    _referenceLearningInputDrafts.Remove(frame.FrameId);
                }
            }

            var result = await _referenceLearning.AnalyzeAsync(
                CreateReferenceLearningRequest(SelectedTemplatePage),
                CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ReferenceLearningStatusMessage = ToReferenceLearningUserMessage(result.Error!);
                return;
            }

            var document = result.Document!;
            ReferenceLearningAiFailure? semanticFailure = null;
            if (_referenceLearningAi is not null)
            {
                ReferenceLearningStatusMessage = "本地结构已完成，正在调用 AI 生成当前页逐框动态规则。";
                var semantic = await _referenceLearningAi.GenerateSelectionRequirementsAsync(
                    new ReferenceSemanticGenerationCommand(
                        document,
                        _referenceLearningPhotoDirectory,
                        SelectedTemplatePage.Name),
                    CancellationToken.None).ConfigureAwait(true);
                document = semantic.Document;
                semanticFailure = semantic.Failure;
            }
            _referenceLearningDocuments[SelectedTemplatePage.PageId] = document;
            _referenceLearningInputDrafts.Clear();
            DisplayReferenceLearningPage(SelectedTemplatePage);
            SetReferenceLearningSemanticPending(false);
            ReferenceLearningStatusMessage = semanticFailure?.UserAction
                ?? "当前页已重新分析，旧复核结论未沿用。";
        }
        finally
        {
            SetReferenceLearningSemanticPending(false);
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
            var semanticLabel = EffectiveReferenceLearningSemantic(frame);
            var review = new ReferenceFrameReview(
                frame.FrameId,
                decision,
                decision == ReferenceReviewDecision.Accepted
                    ? SelectedReferenceLearningPhoto?.AssetId
                    : null,
                semanticLabel,
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
        catch (InvalidOperationException exception) when (
            string.Equals(exception.Message, "TPL_LEARN_SELECTION_REQUIREMENT_INVALID", StringComparison.Ordinal))
        {
            ReferenceLearningStatusMessage = "请把未来选图规则写成 2–40 字的图片类别短语，例如“人物正面端坐图”或“面料纹理特写图”。";
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
        SetTemplateScreen(TemplateScreen.Canvas);
        var pageLoadVersion = Interlocked.Increment(ref _calibrationPageLoadVersion);
        await LoadCalibrationPageAsync(SelectedTemplatePage, pageLoadVersion).ConfigureAwait(true);
        TemplateStatusMessage = "已退回拖拽校准；学习候选不会提交为模板规则。";
    }

    private async Task CancelReferenceLearningAsync()
    {
        if (_referenceLearning is null)
        {
            SetTemplateScreen(TemplateScreen.Canvas);
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
            SetTemplateScreen(TemplateScreen.Canvas);
        }
        catch (Exception)
        {
            ReferenceLearningStatusMessage = "取消学习未能完成，但主窗口仍保持打开；请重试或返回页面设置。";
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
        _refreshingReferenceLearningPage = true;
        try
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
        finally
        {
            _refreshingReferenceLearningPage = false;
        }
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
            TemplatePageImportService.GetPreviewRelativePath(page.InternalPsdRelativePath),
            page.Name);
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

    private string ToReferenceLearningUserMessage(
        ReferenceLearningError error,
        TemplatePageRowViewModel? page = null)
    {
        page ??= SelectedTemplatePage;
        var fileName = page?.InternalPsdRelativePath is { Length: > 0 } path
            ? Path.GetFileName(path)
            : null;
        var prefix = string.IsNullOrWhiteSpace(fileName)
            ? ""
            : $"PSD“{fileName}”：";
        var message = error.Code switch
        {
            "TPL_SOURCE_UNREADABLE" => "应用内部副本不可读取。",
            "TPL_LEARN_SOURCE_MISSING" => "当前学习会话引用的应用内部 PSD 副本已不存在。",
            "TPL_LEARN_SOURCE_CHANGED" => "参考 PSD 或源照片集合已发生变化。",
            "TPL_PSD_UNSUPPORTED_STRUCTURE" => "PSD 结构暂不支持自动学习，不能据此判断原始 PSD 损坏。",
            "TPL_PSD_RESOURCE_LIMIT" => "PSD 过大或结构复杂，检查器已安全停止。",
            "TPL_PSD_WORKER_UNAVAILABLE" => "PSD 检查组件不可用。",
            "TPL_PSD_WORKER_TIMEOUT" => "PSD 检查超时，源文件未被修改。",
            "TPL_PSD_WORKER_RESPONSE_TOO_LARGE" => "PSD 检查结果过大，已安全停止。",
            "TPL_PSD_WORKER_FAILED" => "PSD 检查组件异常退出。",
            "TPL_PSD_INVALID_RESPONSE" or "TPL_PSD_WORKER_INVALID_RESPONSE" => "PSD 检查组件返回了无效结果。",
            _ => error.Message
        };
        var action = error.Code switch
        {
            "TPL_SOURCE_UNREADABLE" or "TPL_LEARN_SOURCE_MISSING" => "请重新导入对应 PSD 后重新开始学习；不会修改用户原始 PSD。",
            "TPL_LEARN_SOURCE_CHANGED" => "请确认当前 PSD 和源照片文件夹后重新分析；不会沿用旧坐标或匹配结果。",
            "TPL_PSD_UNSUPPORTED_STRUCTURE" => "请确认 PSD 保留可读的智能对象；也可以返回页面设置改用拖拽校准。",
            "TPL_PSD_WORKER_UNAVAILABLE" => "请使用带 psd-inspection-worker 的完整运行目录重试。",
            _ => error.UserAction
        };
        return $"{prefix}{message} {action}".Trim();
    }

    private Task<bool> EnsureAiUploadConsentForReferenceLearningAsync() =>
        _host.EnsureUploadConsentAsync();

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
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        OnPropertyChanged(nameof(ReferenceLearningPageProgress));
        OnPropertyChanged(nameof(ReferenceLearningCurrentPageProgress));
        OnPropertyChanged(nameof(ReferenceLearningOverallProgress));
        OnPropertyChanged(nameof(ReferenceLearningReviewInstruction));
        OnPropertyChanged(nameof(ReferenceLearningFrameSummary));
        OnPropertyChanged(nameof(IsReferenceLearningAiUnavailable));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticPending));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticWarningVisible));
        OnPropertyChanged(nameof(ReferenceLearningSemanticWarningText));
        OnPropertyChanged(nameof(ReferenceLearningRuleSummary));
        OnPropertyChanged(nameof(CalibrationRuleStatusText));
        OnPropertyChanged(nameof(CalibrationEditorHintText));
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        OnPropertyChanged(nameof(IsReferenceLearningManualFallbackVisible));
        OnPropertyChanged(nameof(CalibrationFooterMetaText));
        OnPropertyChanged(nameof(CanCommitReferenceLearning));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
        NotifyCalibrationFrameChanged();
        RaiseReferenceLearningCommands();
    }

    private void SetReferenceLearningSemanticPending(bool pending)
    {
        if (_referenceLearningSemanticPending == pending)
        {
            return;
        }

        _referenceLearningSemanticPending = pending;
        OnPropertyChanged(nameof(IsReferenceLearningSemanticPending));
        OnPropertyChanged(nameof(IsReferenceLearningSemanticWarningVisible));
        OnPropertyChanged(nameof(ReferenceLearningSemanticWarningText));
        OnPropertyChanged(nameof(CalibrationRuleStatusText));
        OnPropertyChanged(nameof(CanEditReferenceLearningRule));
        OnPropertyChanged(nameof(CanAcceptReferenceLearningFrame));
        OnPropertyChanged(nameof(CanApplyAndConfirmCalibration));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        RaiseReferenceLearningCommands();
    }

    private void ClearReferenceLearningPreview()
    {
        _refreshingReferenceLearningPage = true;
        try
        {
            ReferenceLearningFrames.Clear();
            ReferenceLearningPhotos.Clear();
            SelectedReferenceLearningFrame = null;
            SelectedReferenceLearningPhoto = null;
        }
        finally
        {
            _refreshingReferenceLearningPage = false;
        }

        NotifyReferenceLearningStateChanged();
    }

    private void ResetReferenceLearningDraft()
    {
        _referenceLearningDocuments.Clear();
        _referenceLearningInputDrafts.Clear();
        ReferenceLearningFrames.Clear();
        ReferenceLearningPhotos.Clear();
        SelectedReferenceLearningFrame = null;
        ReferenceLearningStatusMessage = "请选择制作这些 PSD 时使用的同款源照片文件夹。";
        OnPropertyChanged(nameof(CanCommitReferenceLearning));
        OnPropertyChanged(nameof(CanSaveTemplateCanvas));
        OnPropertyChanged(nameof(CanConfirmReferenceLearningPage));
        OnPropertyChanged(nameof(CanConfirmAllQualifiedReferenceLearning));
        OnPropertyChanged(nameof(ReferenceLearningQualifiedCandidateSummary));
    }

    private void RaiseReferenceLearningCommands()
    {
        (AcceptReferenceLearningFrameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyAndConfirmCalibrationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmReferenceLearningPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmAllQualifiedReferenceLearningCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RejectReferenceLearningFrameCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReturnReferenceLearningToManualCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReanalyzeReferenceLearningCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void CaptureReferenceLearningInputs()
    {
        if (_refreshingReferenceLearningPage
            || IsReferenceLearningSemanticPending
            || !IsReferenceLearningReviewMode
            || _selectedReferenceLearningFrame is null)
        {
            return;
        }

        var semanticLabel = EffectiveReferenceLearningSemantic(_selectedReferenceLearningFrame);
        _referenceLearningInputDrafts[_selectedReferenceLearningFrame.FrameId] =
            new ReferenceLearningInputDraft(
                semanticLabel,
                !string.Equals(
                    semanticLabel,
                    _selectedReferenceLearningFrame.SemanticLabel?.Trim(),
                    StringComparison.Ordinal),
                CalibrationScaleInput,
                CalibrationOffsetXInput,
                CalibrationOffsetYInput,
                SelectedReferenceLearningPhoto?.AssetId);
    }

    private string EffectiveReferenceLearningSemantic(ReferenceLearningFrameViewModel frame)
    {
        if (!string.IsNullOrWhiteSpace(CalibrationRequirementInput))
        {
            return CalibrationRequirementInput.Trim();
        }

        if (_referenceLearningInputDrafts.TryGetValue(frame.FrameId, out var draft)
            && draft.IsSemanticUserOverride
            && !string.IsNullOrWhiteSpace(draft.SemanticLabel))
        {
            return draft.SemanticLabel.Trim();
        }

        return frame.SemanticLabel?.Trim() ?? "";
    }

    private static string FormatRuleNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private (int Reviewed, int Total, int Pending, int Manual) GetReferenceLearningProgress(
        TemplatePageRowViewModel? page = null)
    {
        IEnumerable<ReferenceLearningDocument> documents = page is not null
            ? _referenceLearningDocuments.TryGetValue(page.PageId, out var document)
                ? [document]
                : []
            : _referenceLearningDocuments.Values;
        var candidates = documents.SelectMany(document => document.Candidates).ToArray();
        var pending = candidates.Count(candidate =>
            candidate.ReviewDecision == ReferenceReviewDecision.Pending);
        var manual = candidates.Count(candidate =>
            candidate.ReviewDecision == ReferenceReviewDecision.ManualCalibration);
        return (candidates.Length - pending, candidates.Length, pending, manual);
    }
}
