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


}
