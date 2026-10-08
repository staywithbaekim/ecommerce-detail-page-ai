using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using System.Windows.Media;

namespace Desktop.App;

/// <summary>
/// Presentation state owned by the result review page.  It contains only
/// render-preparation facts and UI selection/filter state; workflow
/// execution belongs to the AI coordinator; result commands belong to the
/// result coordinator and navigation is supplied by its host.
/// </summary>
public sealed class ResultReviewPageState : INotifyPropertyChanged
{
    private AiRenderPlanPreparation? _renderPreparation;
    private ResultPageCardViewModel? _selectedResultPage;
    private ResultPageFilter _selectedResultFilter = ResultPageFilter.All;
    private bool _isResultDetailNarrowLayout;
    private AiBatchAnalysisProjection? _projection;
    public RenderBatchJournal? RenderJournal { get; private set; }
    private IReadOnlyDictionary<string, ImageSource> _outputPreviews = new Dictionary<string, ImageSource>();

    public ObservableCollection<ResultStyleGroupViewModel> ResultStyleGroups { get; } = [];

    public ObservableCollection<ResultStyleGroupViewModel> VisibleResultStyleGroups { get; } = [];

    public ObservableCollection<ResultFilterViewModel> ResultFilters { get; } = [];

    public AiRenderPlanPreparation? RenderPreparation => _renderPreparation;

    private bool HasRegisteredOutput => RenderJournal?.Pages.Any(page =>
        page.Status == RenderBatchPageStatus.Committed && page.Commit is not null) == true;

    public string ResultTitleText => HasRegisteredOutput ? "结果检查" : "生成准备";

    public ResultPageCardViewModel? SelectedResultPage
    {
        get => _selectedResultPage;
        set
        {
            if (ReferenceEquals(_selectedResultPage, value))
            {
                return;
            }

            _selectedResultPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanMoveToPreviousResultPage));
            OnPropertyChanged(nameof(CanMoveToNextResultPage));
        }
    }

    public ResultPageFilter SelectedResultFilter => _selectedResultFilter;

    public bool IsResultDetailNarrowLayout => _isResultDetailNarrowLayout;

    public GridLength ResultCandidateColumnWidth => new(
        _isResultDetailNarrowLayout ? 172 : 220);

    public GridLength ResultDetailGutterWidth => new(
        _isResultDetailNarrowLayout ? 12 : 16);

    public GridLength ResultAdjustmentColumnWidth => new(
        _isResultDetailNarrowLayout ? 216 : 240);

    internal bool IsWaitingForRenderHost => RenderJournal is { Status: RenderBatchStatus.Running } journal
        && !journal.PauseRequested && !journal.CancelRequested
        && journal.Pages.Where((_, index) => journal.RegenerationPageIndex is null || journal.RegenerationPageIndex == index)
            .Any(page => page.Status == RenderBatchPageStatus.Planned)
        && journal.Pages.All(page => page.Status is not (RenderBatchPageStatus.Submitting or RenderBatchPageStatus.Running or RenderBatchPageStatus.Unknown));

    public string ResultStatusText => RenderJournal is { } journal ? journal.Status switch
    {
        RenderBatchStatus.Running when journal.CancelRequested => "正在安全取消；当前页将在安全边界停止。",
        RenderBatchStatus.Running when journal.PauseRequested => "完成当前款式后暂停。",
        RenderBatchStatus.Running when IsWaitingForRenderHost => "等待 Photoshop 连接或就绪；尚未提交下一页。",
        RenderBatchStatus.Running => "正在生成；请保持 Photoshop 和渲染插件连接。",
        RenderBatchStatus.Paused => "已暂停，可继续尚未生成的页面。",
        RenderBatchStatus.RecoveryRequired => "需要查询恢复；未确认的页面不会重新提交。",
        RenderBatchStatus.Completed => "本批次页面已生成，可检查正式结果。",
        RenderBatchStatus.CompletedWithIssues => "可生成页面已完成，请检查失败或待处理页面。",
        RenderBatchStatus.Cancelled => "批次已取消；此前成功输出已保留。",
        RenderBatchStatus.Empty => "规则合法展开为 0 个实例；未启动 Photoshop，没有正式输出。",
        RenderBatchStatus.UserActionRequired => "所有实例均有业务问题；请查看各实例原因，修正输入后创建新批次。",
        RenderBatchStatus.Failed => "实例执行失败，没有成功输出；仅明确失败的实例可单独重试。",
        _ => "页面已准备，可开始渲染。"
    } : _renderPreparation?.Status switch
    {
        AiRenderPreparationStatus.WaitingForPhotoshop =>
            "页面计划已校验；尚未建立可用的 Photoshop 渲染连接。点击“开始渲染”后尝试自动连接。",
        AiRenderPreparationStatus.ReadyToRender =>
            "RenderPlan 已通过结构、资源和摘要校验；等待正式渲染接管。",
        AiRenderPreparationStatus.UserActionRequired =>
            "没有可安全执行的页面；未启动 Photoshop，请先处理页面问题。",
        _ => "尚未准备页面级 RenderPlan。"
    };

    public string ResultSummaryText => _renderPreparation is null
        ? "0 个页面"
        : $"{ResultStyleGroups.Count} 款 · {_renderPreparation.Pages.Count} 个页面 · {_renderPreparation.ReadyPageCount} 个计划已闭合";

    public string ResultFormalOutputText => RenderJournal?.Pages.Where(page => page.Status == RenderBatchPageStatus.Committed)
        .Select(page => (page.StyleId, page.PageId, page.OutputVariantId, page.Instance?.PageInstanceId)).Distinct().Count() is > 0 and var count
        ? $"已登记 {count} 页正式输出；预览只读取校验通过的 JPG"
        : "尚未生成正式图片；下方图片仅为选图参考，不是套版效果。";

    public bool CanMoveToPreviousResultPage => ResultPageIndex > 0;

    public bool CanMoveToNextResultPage => ResultPageIndex >= 0
        && ResultPageIndex < AllResultPages.Length - 1;

    public ResultPageCardViewModel[] AllResultPages => ResultStyleGroups
        .SelectMany(group => group.Pages)
        .ToArray();

    public void SetWindowWidth(double width)
    {
        var narrow = width <= 1180;
        if (_isResultDetailNarrowLayout == narrow)
        {
            return;
        }

        _isResultDetailNarrowLayout = narrow;
        OnPropertyChanged(nameof(IsResultDetailNarrowLayout));
        OnPropertyChanged(nameof(ResultCandidateColumnWidth));
        OnPropertyChanged(nameof(ResultDetailGutterWidth));
        OnPropertyChanged(nameof(ResultAdjustmentColumnWidth));
    }

    public void ApplyRenderPreparation(
        AiRenderPlanPreparation preparation,
        AiBatchAnalysisProjection? projection)
    {
        if (_renderPreparation?.BatchId != preparation.BatchId) { RenderJournal = null; _outputPreviews = new Dictionary<string, ImageSource>(); }
        _renderPreparation = preparation;
        _projection = projection;
        RebuildCards();
    }

    public void ApplyRenderJournal(RenderBatchJournal journal, IReadOnlyDictionary<string, ImageSource>? previews = null)
    {
        if (journal.BatchId != _renderPreparation?.BatchId) return;
        RenderJournal = journal;
        if (previews is not null) _outputPreviews = previews;
        RebuildCards();
        OnPropertyChanged(nameof(RenderJournal));
    }

    private void RebuildCards()
    {
        var preparation = _renderPreparation!;
        var selected = SelectedResultPage;
        ResultStyleGroups.Clear();

        var sourceStyles = (_projection?.Styles ?? [])
            .ToDictionary(style => style.StyleId, StringComparer.Ordinal);
        foreach (var stylePages in preparation.Pages.GroupBy(page => page.StyleId, StringComparer.Ordinal))
        {
            sourceStyles.TryGetValue(stylePages.Key, out var sourceStyle);
            var variantOrdinals = stylePages.GroupBy(page => page.PageId).SelectMany(group =>
                group.Where(page => page.OutputVariantId is not null).Select((page, index) =>
                    (Key: (page.PageId, page.OutputVariantId), Number: index + 1))).ToDictionary(item => item.Key, item => item.Number);
            var cards = stylePages.Select(page =>
            {
                var sourcePage = sourceStyle?.Pages.FirstOrDefault(item =>
                    string.Equals(item.PageId, page.PageId, StringComparison.Ordinal));
                var rendered = RenderJournal?.Pages.Where(item => item.StyleId == page.StyleId && item.PageId == page.PageId
                    && item.OutputVariantId == page.OutputVariantId && item.Instance == page.Instance).OrderByDescending(item => item.Attempt).FirstOrDefault();
                var output = RenderJournal?.Pages.Where(item => item.StyleId == page.StyleId && item.PageId == page.PageId
                    && item.OutputVariantId == page.OutputVariantId && item.Instance == page.Instance && item.Status == RenderBatchPageStatus.Committed)
                    .MaxBy(item => item.Attempt);
                var preview = output?.Commit is { } commit && _outputPreviews.TryGetValue(commit.ManifestSha256, out var image) ? image : null;
                return ResultPageCardViewModel.Create(
                    sourceStyle?.StyleName ?? "当前款式",
                    sourcePage,
                    page.OutputVariantName is null && page.OutputVariantId is not null
                        ? page with { OutputVariantName = $"输出 {variantOrdinals[(page.PageId, page.OutputVariantId)]}" } : page,
                    preparation.Status,
                    rendered, preview, output);
            }).ToArray();
            ResultStyleGroups.Add(new ResultStyleGroupViewModel(
                stylePages.Key,
                sourceStyle?.StyleName ?? "当前款式",
                cards));
        }

        RebuildResultFilters();
        ApplyResultFilter();
        if (selected is not null) SelectedResultPage = AllResultPages.FirstOrDefault(item => item.StyleId == selected.StyleId
            && item.PageId == selected.PageId && item.OutputVariantId == selected.OutputVariantId && item.PageInstanceId == selected.PageInstanceId);
        OnPropertyChanged(nameof(RenderPreparation));
        OnPropertyChanged(nameof(ResultTitleText));
        OnPropertyChanged(nameof(ResultStatusText));
        OnPropertyChanged(nameof(ResultSummaryText));
        OnPropertyChanged(nameof(ResultFormalOutputText));
    }

    public void SelectResultFilter(ResultPageFilter filter)
    {
        _selectedResultFilter = filter;
        foreach (var item in ResultFilters)
        {
            item.IsSelected = item.Filter == _selectedResultFilter;
        }

        ApplyResultFilter();
        OnPropertyChanged(nameof(SelectedResultFilter));
    }

    public void MoveResultPage(int offset)
    {
        var index = ResultPageIndex;
        var target = index + offset;
        if (target >= 0 && target < AllResultPages.Length)
        {
            SelectedResultPage = AllResultPages[target];
        }
    }

    private int ResultPageIndex => SelectedResultPage is null
        ? -1
        : Array.IndexOf(AllResultPages, SelectedResultPage);

    private void RebuildResultFilters()
    {
        ResultFilters.Clear();
        var pages = ResultStyleGroups.SelectMany(group => group.Pages).ToArray();
        AddResultFilter(ResultPageFilter.All, "全部", pages.Length);
        AddResultFilter(ResultPageFilter.NeedsReview, "需检查", pages.Count(page => page.Filter == ResultPageFilter.NeedsReview));
        AddResultFilter(ResultPageFilter.CompletedWithProblems, HasRegisteredOutput ? "完成但有问题" : "待处理",
            pages.Count(page => page.Filter == ResultPageFilter.CompletedWithProblems));
        AddResultFilter(ResultPageFilter.RuntimeFailed, "运行失败", pages.Count(page => page.Filter == ResultPageFilter.RuntimeFailed));
    }

    private void AddResultFilter(ResultPageFilter filter, string label, int count) =>
        ResultFilters.Add(new ResultFilterViewModel(
            filter,
            label,
            count,
            filter == _selectedResultFilter));

    private void ApplyResultFilter()
    {
        VisibleResultStyleGroups.Clear();
        foreach (var group in ResultStyleGroups)
        {
            var visiblePages = _selectedResultFilter == ResultPageFilter.All
                ? group.Pages
                : group.Pages.Where(page => page.Filter == _selectedResultFilter).ToArray();
            if (visiblePages.Count > 0)
            {
                VisibleResultStyleGroups.Add(group.WithPages(visiblePages));
            }
        }

        OnPropertyChanged(nameof(VisibleResultStyleGroups));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
