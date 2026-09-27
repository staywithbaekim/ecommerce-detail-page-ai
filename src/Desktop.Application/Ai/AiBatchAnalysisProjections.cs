using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

public enum AiBatchUiStatus
{
    Waiting = 0,
    Analysing,
    ManualConfiguration,
    Ready,
    ReviewRequired,
    BudgetStopped,
    NotGeneratable,
    Cancelled,
    RecoveryRequired,
    Failed
}

public enum AiStyleUiStatus
{
    Waiting = 0,
    Analysing,
    Ready,
    ReviewRequired,
    BusinessError,
    Cancelled,
    SystemError
}

public enum AiPageUiStatus
{
    Waiting = 0,
    Analysing,
    Ready,
    ReviewRequired,
    BusinessError,
    Cancelled,
    SystemError
}

public enum AiRequirementSelectionUiStatus
{
    Waiting = 0,
    Analysing,
    AiSelected,
    ManualLocked,
    Missing,
    Unavailable,
    Cancelled,
    /// <summary>AI selected a usable candidate but recommends a quick check.</summary>
    ReviewRequired,
    /// <summary>The current AI candidate cannot be used safely without a user fix.</summary>
    Unsafe
}

/// <summary>
/// Provider-neutral projection of the photo currently used by one template
/// frame.  Asset identifiers and preview paths are command/data bindings only;
/// presentation never has to expose source file names or local paths as text.
/// </summary>
public sealed record AiRequirementSelectionProjection(
    string BatchId,
    string StyleId,
    string PageId,
    string PageName,
    string RequirementId,
    string FrameDisplayName,
    string SelectionRequirement,
    AiRequirementSelectionUiStatus Status,
    string StatusText,
    string? SelectedAssetId,
    string? SelectedPreviewPath,
    string? SelectedAssetDisplayName = null)
{
    public string StatusState => Status.ToString();

    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedAssetId)
        && !string.IsNullOrWhiteSpace(SelectedPreviewPath);

    public bool IsManualLocked => Status == AiRequirementSelectionUiStatus.ManualLocked;
}

/// <summary>
/// One source photo already registered by INPUT-01 for a style.  This is the
/// complete local replacement library, not a provider candidate DTO.
/// </summary>
public sealed record AiStyleAssetProjection(
    string BatchId,
    string StyleId,
    string AssetId,
    string? PreviewPath,
    int DisplayOrder);

public sealed record AiCandidateProjection(
    string BatchId,
    string StyleId,
    string RequirementId,
    string AssetId,
    string AssetName,
    int Rank,
    decimal Confidence,
    IReadOnlyList<string> EvidenceCodes,
    bool IsLocked,
    string FrameDisplayName = "图片框",
    string? PreviewPath = null,
    string SelectionRequirement = "",
    string PageName = "")
{
    public string RankText => $"Top-{Rank}";

    public string ConfidenceText => $"匹配度 {Confidence:P0}";

    public string EvidenceText => EvidenceCodes.Count == 0
        ? "暂无补充证据"
        : string.Join(" · ", EvidenceCodes.Select(ToEvidenceText).Distinct(StringComparer.Ordinal));

    public string LockText => IsLocked ? "已确认" : "确认";

    public bool CanLock => !IsLocked;

    private static string ToEvidenceText(string code) => code switch
    {
        "front_view" => "正面",
        "back_view" => "背面",
        "detail_view" => "细节",
        AiTargetMatchEvidence.Exact => "精确符合要求",
        AiTargetMatchEvidence.SafeSimilar => "安全的相近替代",
        AiTargetMatchEvidence.MaterialDifference => "与要求存在实质差异",
        AiTargetMatchEvidence.Unsafe => "不安全候选",
        "clear_subject" => "主体清晰",
        "variant_match" => "变体一致",
        "SUBJECT_COMPLETE" => "主体完整",
        "NO_EXTRA_DISTRACTIONS" => "无多余干扰",
        "SHOWS_SHOULDER_STRAP" => "肩带完整",
        "SHOWS_RING_DETAIL" => "圆环细节清晰",
        "PURE_WHITE_BACKGROUND" => "纯白背景",
        "SUBJECT_CLEAR" => "主体清晰",
        "VARIANT_MATCH" => "款式一致",
        "FRONT_VIEW" => "正面视角",
        "BACK_VIEW" => "背面视角",
        "DETAIL_VIEW" => "细节视角",
        "color_mismatch_black_not_gray" => "颜色与模板要求不符",
        "missing_shoulder_strap" => "缺少模板要求的肩带",
        "subject_partial_no_other_distractors" => "主体可见，但仍需核对完整性",
        _ => "需要人工核对"
    };
}

public sealed record AiPageAnalysisProjection(
    string BatchId,
    string StyleId,
    string PageId,
    string PageName,
    AiPageUiStatus Status,
    string StatusText,
    string ActionText,
    IReadOnlyList<AiCandidateProjection> Candidates,
    IReadOnlyList<string> WarningTexts,
    bool RequiresManualConfiguration = false,
    IReadOnlyList<AiRequirementSelectionProjection>? Requirements = null,
    AiSizeTableSourceProjection? SizeTableSource = null)
{
    public string StatusState => Status.ToString();

    public bool HasWarnings => WarningTexts.Count > 0;

    /// <summary>
    /// Yellow is an executable state.  Only a page which explicitly carries a
    /// manual-configuration gate is withheld from generation.
    /// </summary>
    public bool CanGenerate => Status == AiPageUiStatus.Ready
        || (Status == AiPageUiStatus.ReviewRequired && !RequiresManualConfiguration);
}

public enum AiSizeTableSourceStatus
{
    Waiting = 0,
    Analysing,
    AutoSelected,
    NeedsSelection,
    Missing,
    ReviewRequired
}

public sealed record AiSizeTableSourceCandidateProjection(
    string BatchId,
    string StyleId,
    string AssetId,
    string DisplayName,
    string? PreviewPath,
    bool IsDetected,
    decimal? Confidence,
    bool IsSelected)
{
    public string ConfidenceText => Confidence is null ? "尚未识别" : $"尺码图匹配度 {Confidence:P0}";
}

public sealed record AiSizeTableSourceProjection(
    AiSizeTableSourceStatus Status,
    string StatusText,
    string? SelectedAssetId,
    string? SelectedPreviewPath,
    IReadOnlyList<AiSizeTableSourceCandidateProjection> Candidates)
{
    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedAssetId)
        && !string.IsNullOrWhiteSpace(SelectedPreviewPath);
}

public sealed record AiStyleAnalysisProjection(
    string BatchId,
    string StyleId,
    string StyleName,
    AiStyleUiStatus Status,
    string StatusText,
    IReadOnlyList<AiPageAnalysisProjection> Pages,
    int AssetCount,
    IReadOnlyList<AiStyleAssetProjection>? Assets = null)
{
    public string StatusState => Status.ToString();

    public bool HasReadyPage => Pages.Any(page => page.CanGenerate);

    /// <summary>
    /// A style can be shown in the executable queue when it has at least one
    /// usable page and every remaining page is either intentionally isolated
    /// or cancelled.  Advisory-yellow pages are deliberately included: they
    /// are already filled by AI and do not require a per-frame confirmation.
    /// </summary>
    public bool CanGenerate => Pages.Any(page => page.CanGenerate)
        && Pages.All(page => page.CanGenerate
            || page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.Cancelled);

    public bool HasBusinessError => Pages.Any(page => page.Status == AiPageUiStatus.BusinessError);
}

public sealed record AiBatchAnalysisProjection(
    string BatchId,
    AiBatchUiStatus Status,
    string StatusText,
    string ActionText,
    int StyleCount,
    int CompletedStyleCount,
    int ReadyPageCount,
    int TotalPageCount,
    IReadOnlyList<AiStyleAnalysisProjection> Styles,
    AiAnalysisSchedulingControlState ControlState,
    string? ControlReason,
    bool IsBusy = false,
    string TemplateSummary = "当前已校准模板",
    string TemplateMeta = "模板快照已冻结",
    string BatchSummary = "当前输入批次",
    string BatchMeta = "素材清单已登记",
    string OutputSummary = "仅 JPG",
    string OutputMeta = "正式输出目录已确认",
    string PageSummary = "0 个页面",
    string PageMeta = "等待分析")
{
    public string StatusState => Status.ToString();

    /// <summary>
    /// A terminal page can still require business or system review. Keep the
    /// processed count separate from the executable count so the UI never
    /// makes a completed analysis look like an unfinished upload.
    /// </summary>
    public int ProcessedPageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status is AiPageUiStatus.Ready
            or AiPageUiStatus.ReviewRequired
            or AiPageUiStatus.BusinessError
            or AiPageUiStatus.SystemError
            or AiPageUiStatus.Cancelled);

    public string ProgressText =>
        $"已完成 {CompletedStyleCount}/{StyleCount} 款 · 已处理 {ProcessedPageCount}/{TotalPageCount} 页 · 可生成 {ExecutablePageCount}/{TotalPageCount} 页";

    public int ExecutablePageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status == AiPageUiStatus.Ready
            || page.Status == AiPageUiStatus.ReviewRequired
                && !page.RequiresManualConfiguration);

    public int IsolatedPageCount => Styles.SelectMany(style => style.Pages)
        .Count(page => page.Status == AiPageUiStatus.BusinessError);

    public bool HasUnsettledPages => Styles.SelectMany(style => style.Pages)
        .Any(page => page.Status is AiPageUiStatus.Waiting or AiPageUiStatus.Analysing);

    public bool CanEnterGeneration => (Status is AiBatchUiStatus.Ready or AiBatchUiStatus.ReviewRequired)
        && ExecutablePageCount > 0
        && Styles.SelectMany(style => style.Pages).All(page =>
            page.CanGenerate
            || page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.Cancelled);

    public bool CanPause => IsBusy is false
        && Status is not AiBatchUiStatus.BudgetStopped
        && !IsTerminal
        && ControlState is AiAnalysisSchedulingControlState.Running;

    public bool CanResume => IsBusy is false
        && (ControlState is AiAnalysisSchedulingControlState.Paused
            or AiAnalysisSchedulingControlState.WaitingForNetwork);

    public bool CanCancel => IsBusy is false
        && !IsTerminal
        && ControlState is not AiAnalysisSchedulingControlState.Cancelled;

    public bool IsTerminal => Status is AiBatchUiStatus.Ready
        or AiBatchUiStatus.NotGeneratable
        or AiBatchUiStatus.Cancelled
        or AiBatchUiStatus.RecoveryRequired
        or AiBatchUiStatus.Failed
        || Status == AiBatchUiStatus.ReviewRequired && !HasUnsettledPages;
}
