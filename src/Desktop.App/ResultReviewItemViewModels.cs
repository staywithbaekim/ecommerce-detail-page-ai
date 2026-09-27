using System.ComponentModel;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using System.Windows.Media;

namespace Desktop.App;

public enum ResultPageFilter
{
    All = 0,
    NeedsReview,
    CompletedWithProblems,
    RuntimeFailed
}

public sealed class ResultFilterViewModel(
    ResultPageFilter filter,
    string label,
    int count,
    bool isSelected) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;

    public ResultPageFilter Filter { get; } = filter;

    public string Label { get; } = label;

    public int Count { get; } = count;

    public string DisplayText => Filter == ResultPageFilter.All ? $"{Label} {Count}" : $"{Label} {Count}";

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

public sealed class ResultStyleGroupViewModel(
    string styleId,
    string styleName,
    IReadOnlyList<ResultPageCardViewModel> pages)
{
    public string StyleId { get; } = styleId;

    public string StyleName { get; } = styleName;

    public IReadOnlyList<ResultPageCardViewModel> Pages { get; } = pages;

    public string SummaryText => $"{Pages.Count} 个页面";

    public ResultStyleGroupViewModel WithPages(IReadOnlyList<ResultPageCardViewModel> visiblePages) =>
        new(StyleId, StyleName, visiblePages);
}

public sealed class ResultPageCardViewModel
{
    private ResultPageCardViewModel(
        string styleId,
        string styleName,
        string pageId,
        string pageName,
        ResultPageFilter filter,
        string statusText,
        string detailText,
        string previewPath,
        bool hasFormalOutput,
        bool isSizeTablePage,
        IReadOnlyList<ResultCandidateViewModel> candidates)
    {
        StyleId = styleId;
        StyleName = styleName;
        PageId = pageId;
        PageName = pageName;
        Filter = filter;
        StatusText = statusText;
        DetailText = detailText;
        PreviewPath = previewPath;
        HasFormalOutput = hasFormalOutput;
        IsSizeTablePage = isSizeTablePage;
        Candidates = candidates;
    }

    public string StyleId { get; }
    public string StyleName { get; }
    public string PageId { get; }
    public string PageName { get; }
    public string? OutputVariantId { get; private init; }
    public int Attempt { get; private init; } = 1;
    public ImageSource? OutputPreview { get; private init; }
    public bool HasSourcePreview => !HasFormalOutput && !string.IsNullOrWhiteSpace(PreviewPath);
    public ResultPageFilter Filter { get; }
    public string StatusText { get; }
    public string DetailText { get; }
    public string PreviewPath { get; }
    public bool HasPreview => OutputPreview is not null || HasSourcePreview;
    public bool HasNoPreview => !HasPreview;
    public bool HasFormalOutput { get; }
    public bool IsSizeTablePage { get; }
    public bool ShowsPreviousOutput { get; init; }
    public string OutputBoundaryText => ShowsPreviousOutput ? "保留上次正式输出；本次执行状态见页面说明" : HasFormalOutput
        ? "正式输出已登记"
        : HasSourcePreview ? "当前为已确认源图参考，不是生成结果" : "本页尚无正式输出";
    public IReadOnlyList<ResultCandidateViewModel> Candidates { get; }

    public static ResultPageCardViewModel Create(
        string styleName,
        AiPageAnalysisProjection? source,
        AiRenderPagePreparation page,
        AiRenderPreparationStatus overall,
        RenderBatchPage? rendered = null,
        ImageSource? outputPreview = null,
        RenderBatchPage? retainedOutput = null)
    {
        var previousOutput = retainedOutput?.Commit is not null && retainedOutput.Attempt < rendered?.Attempt;
        var hasFormalOutput = rendered?.Status == RenderBatchPageStatus.Committed && rendered.Commit is not null || previousOutput;
        var status = MapStatus(page, overall, hasFormalOutput);
        status = rendered?.Status switch
        {
            RenderBatchPageStatus.Planned when rendered.SizeTableRepair is not null =>
                (ResultPageFilter.NeedsReview, "尺码修复待生成", "尺码修订已登记；等待生成当前页，旧正式输出保持不变。"),
            RenderBatchPageStatus.Committed when rendered.SizeTableRepair is not null =>
                (ResultPageFilter.All, "尺码修复已生成", $"新尺码结果已登记（attempt {rendered.Attempt}），请逐格检查。旧输出保持不变。"),
            RenderBatchPageStatus.Planned when rendered.Attempt > 1 => (ResultPageFilter.NeedsReview, "等待重新生成", "已保留旧输出，等待本次生成。"),
            RenderBatchPageStatus.Submitting => (ResultPageFilter.NeedsReview, "准备执行", "正在校验并准备本页资源。"),
            RenderBatchPageStatus.Running => (ResultPageFilter.NeedsReview, "正在生成", "Photoshop 正在处理当前页。"),
            RenderBatchPageStatus.Failed => (ResultPageFilter.RuntimeFailed, "运行失败", previousOutput
                ? "本次生成失败，上次正式输出已保留。" : "当前页未生成正式输出，已保留其他成功页。"),
            RenderBatchPageStatus.Cancelled => (ResultPageFilter.NeedsReview, "已取消", "本页已在安全边界取消。"),
            RenderBatchPageStatus.Unknown => (ResultPageFilter.RuntimeFailed, "需要恢复", "执行结果尚未确认；请先查询恢复，避免重复生成。"),
            _ => status
        };
        var selectedIds = (source?.Requirements ?? [])
            .Where(requirement => requirement.SelectedAssetId is not null)
            .Select(requirement => requirement.SelectedAssetId!)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = (source?.Candidates ?? [])
            .OrderBy(candidate => candidate.Rank)
            .Select(candidate => new ResultCandidateViewModel(
                candidate.RankText,
                candidate.PreviewPath ?? "",
                selectedIds.Contains(candidate.AssetId) || candidate.IsLocked,
                candidate.EvidenceText))
            .ToArray();
        var preview = (source?.Requirements ?? [])
            .Select(requirement => requirement.SelectedPreviewPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? candidates.FirstOrDefault(candidate => candidate.IsCurrent)?.PreviewPath
            ?? candidates.FirstOrDefault()?.PreviewPath
            ?? "";

        return new ResultPageCardViewModel(
            page.StyleId,
            styleName,
            page.PageId,
            (source?.PageName ?? "页面") + (page.OutputVariantName is { Length: > 0 } name ? " · " + name : ""),
            status.Filter,
            status.StatusText,
            status.DetailText,
            hasFormalOutput ? "" : preview,
            hasFormalOutput,
            page.ReasonCode.Contains("SIZE_TABLE", StringComparison.Ordinal)
                || page.RenderPlanJson?.Contains("\"dynamic_size_table\"", StringComparison.Ordinal) == true,
            candidates) { OutputVariantId = page.OutputVariantId, Attempt = rendered?.Attempt ?? 1,
                OutputPreview = hasFormalOutput ? outputPreview : null, ShowsPreviousOutput = previousOutput };
    }

    private static (ResultPageFilter Filter, string StatusText, string DetailText) MapStatus(
        AiRenderPagePreparation page,
        AiRenderPreparationStatus overall,
        bool hasFormalOutput) =>
        page.Status switch
        {
            AiRenderPagePreparationStatus.ReadyToRender when page.ReasonCode == "RENDER_PREPARATION_REVIEW_REQUIRED" =>
                (ResultPageFilter.NeedsReview, "待检查", "原准备记录尚未核对，不能确认构图检查已通过。"),
            AiRenderPagePreparationStatus.BusinessBlocked when page.ReasonCode == "TPL_RENDER_LAYOUT_REVIEW_REQUIRED" =>
                (ResultPageFilter.NeedsReview, "待检查构图", "当前图片无法安全满足图片框布局；请检查主体裁切或重新校准后再生成。"),
            AiRenderPagePreparationStatus.ReadyToRender when page.ReasonCode == "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK" =>
                (ResultPageFilter.NeedsReview, hasFormalOutput ? "待检查构图" : "普通填充待渲染", "当前使用按图片尺寸计算的填充；尚无主体定位，请检查构图。"),
            AiRenderPagePreparationStatus.BusinessBlocked when page.ReasonCode == "SIZE_TABLE_RENDER_TARGET_REQUIRED" =>
                (ResultPageFilter.CompletedWithProblems, "待校准尺码区域", "尺码内容已准备好；需先在模板中确认尺码区域和旧表替换范围。"),
            AiRenderPagePreparationStatus.ReadyToRender when hasFormalOutput =>
                (ResultPageFilter.All, "已完成", "正式输出已登记，可进入单页检查。"),
            AiRenderPagePreparationStatus.ReadyToRender when overall == AiRenderPreparationStatus.WaitingForPhotoshop =>
                (ResultPageFilter.NeedsReview, "待渲染", "页面计划已闭合；尚未连接 Photoshop 开始渲染，没有正式图片。"),
            AiRenderPagePreparationStatus.ReadyToRender =>
                (ResultPageFilter.NeedsReview, "等待渲染", "页面计划已闭合；尚未执行正式渲染。"),
            AiRenderPagePreparationStatus.AwaitingSubjectAnalysis =>
                (ResultPageFilter.CompletedWithProblems, "待检查商品范围", "已确认选图，但缺少主体几何；该页没有 RenderPlan。"),
            AiRenderPagePreparationStatus.AwaitingSizeTableRasterization =>
                (ResultPageFilter.CompletedWithProblems, "等待尺码页能力", "已保留尺码候选；需由 SIZE-01 确定性生成后才能渲染。"),
            _ =>
                (ResultPageFilter.CompletedWithProblems, "不可生成", "页面缺少必需选择或来源；未生成虚构预览。")
        };
}

public sealed record ResultCandidateViewModel(
    string RankText,
    string PreviewPath,
    bool IsCurrent,
    string EvidenceText)
{
    public string StateText => IsCurrent ? "当前选用" : "候选";

    // UI Automation must never fall back to the record's generated ToString(),
    // because that representation includes PreviewPath.  Keep the accessible
    // name useful while excluding source file names and local paths.
    public string AutomationName => string.IsNullOrWhiteSpace(EvidenceText)
        ? $"{RankText}，{StateText}"
        : $"{RankText}，{StateText}，{EvidenceText}";
}
