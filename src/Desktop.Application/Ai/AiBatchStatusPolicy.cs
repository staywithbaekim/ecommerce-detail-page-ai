using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

/// <summary>Pure aggregate batch-status decision rules extracted from projection orchestration.</summary>
public static class AiBatchStatusPolicy
{
    public static AiBatchUiStatus Determine(
        IReadOnlyList<AiStyleAnalysisProjection> styles,
        AiAnalysisSchedulingControlState controlState,
        bool manualSelection,
        bool budgetStopped)
    {
        var pages = styles.SelectMany(style => style.Pages).ToArray();
        if (controlState == AiAnalysisSchedulingControlState.Cancelled
            || pages.Length > 0 && pages.All(page => page.Status == AiPageUiStatus.Cancelled))
            return AiBatchUiStatus.Cancelled;
        if (styles.Any(style => style.Status == AiStyleUiStatus.SystemError)) return AiBatchUiStatus.RecoveryRequired;
        var allSettled = pages.Length > 0 && pages.All(page => page.Status is AiPageUiStatus.Ready
            or AiPageUiStatus.ReviewRequired or AiPageUiStatus.BusinessError or AiPageUiStatus.Cancelled);
        var hasExecutablePage = pages.Any(page => page.CanGenerate);
        var hasBlockingConfiguration = pages.Any(page => page.Status == AiPageUiStatus.ReviewRequired
            && page.RequiresManualConfiguration);
        if (budgetStopped && !allSettled) return AiBatchUiStatus.BudgetStopped;
        if (allSettled && !hasExecutablePage && !hasBlockingConfiguration) return AiBatchUiStatus.NotGeneratable;
        if (styles.Count > 0 && styles.All(style => style.Status == AiStyleUiStatus.Ready)) return AiBatchUiStatus.Ready;
        if (manualSelection && hasBlockingConfiguration) return AiBatchUiStatus.ManualConfiguration;
        if (styles.Any(style => style.Status is AiStyleUiStatus.ReviewRequired or AiStyleUiStatus.BusinessError))
            return AiBatchUiStatus.ReviewRequired;
        if (styles.Any(style => style.Status is AiStyleUiStatus.Analysing or AiStyleUiStatus.Waiting))
            return styles.Any(style => style.Status == AiStyleUiStatus.Analysing)
                ? AiBatchUiStatus.Analysing : AiBatchUiStatus.Waiting;
        return AiBatchUiStatus.Failed;
    }
}
