using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

/// <summary>Pure batch/style/page status decisions extracted from result projection.</summary>
public static class AiProjectionStatusPolicy
{
    public static AiStyleUiStatus DetermineStyleStatus(IReadOnlyList<AiPageAnalysisProjection> pages)
    {
        var active = pages.Where(page => page.Status != AiPageUiStatus.Cancelled).ToArray();
        if (pages.Count == 0) return AiStyleUiStatus.SystemError;
        if (active.Length == 0) return AiStyleUiStatus.Cancelled;
        if (active.Any(page => page.Status == AiPageUiStatus.SystemError)) return AiStyleUiStatus.SystemError;
        if (active.All(page => page.Status == AiPageUiStatus.Ready)) return AiStyleUiStatus.Ready;
        if (active.Any(page => page.Status == AiPageUiStatus.BusinessError))
            return active.Any(page => page.Status is AiPageUiStatus.Ready or AiPageUiStatus.ReviewRequired)
                ? AiStyleUiStatus.ReviewRequired : AiStyleUiStatus.BusinessError;
        if (active.Any(page => page.Status == AiPageUiStatus.ReviewRequired)) return AiStyleUiStatus.ReviewRequired;
        if (active.Any(page => page.Status == AiPageUiStatus.Analysing)) return AiStyleUiStatus.Analysing;
        return AiStyleUiStatus.Waiting;
    }

    public static string StyleStatusText(AiStyleUiStatus status, IReadOnlyList<AiPageAnalysisProjection> pages) => status switch
    {
        AiStyleUiStatus.Ready => "可生成",
        AiStyleUiStatus.ReviewRequired when pages.Any(page => page.Status == AiPageUiStatus.ReviewRequired && !page.RequiresManualConfiguration)
            && !pages.Any(page => page.RequiresManualConfiguration || page.Status is AiPageUiStatus.BusinessError or AiPageUiStatus.SystemError)
            => "可生成 · 建议检查",
        AiStyleUiStatus.ReviewRequired => "部分页面需处理",
        AiStyleUiStatus.BusinessError => "需要修复",
        AiStyleUiStatus.SystemError => "需要恢复",
        AiStyleUiStatus.Cancelled => "已取消",
        AiStyleUiStatus.Analysing => "分析中",
        _ => "待分析"
    };
}
