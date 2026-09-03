using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Ai;

namespace Desktop.App;

public sealed partial class MainWindowViewModel
{
    private AiRenderPlanPreparation? _renderPreparation;
    private ResultPageCardViewModel? _selectedResultPage;
    private ResultPageFilter _selectedResultFilter = ResultPageFilter.All;
    private bool _isResultDetailNarrowLayout;

    public ObservableCollection<ResultStyleGroupViewModel> ResultStyleGroups { get; } = [];

    public ObservableCollection<ResultStyleGroupViewModel> VisibleResultStyleGroups { get; } = [];

    public ObservableCollection<ResultFilterViewModel> ResultFilters { get; } = [];

    public ICommand PrepareRenderingCommand { get; private set; } = null!;

    public ICommand SelectResultFilterCommand { get; private set; } = null!;

    public ICommand OpenResultPageCommand { get; private set; } = null!;

    public ICommand ReturnToResultOverviewCommand { get; private set; } = null!;

    public ICommand ReturnToPreflightCommand { get; private set; } = null!;

    public ICommand PreviousResultPageCommand { get; private set; } = null!;

    public ICommand NextResultPageCommand { get; private set; } = null!;

    public bool IsResultOverviewVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.ResultOverview;

    public bool IsResultDetailVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.ResultDetail
        && SelectedResultPage is not null;

    public bool IsResultDetailNarrowLayout => _isResultDetailNarrowLayout;

    public GridLength ResultCandidateColumnWidth => new(
        _isResultDetailNarrowLayout ? 172 : 220);

    public GridLength ResultDetailGutterWidth => new(
        _isResultDetailNarrowLayout ? 12 : 16);

    public GridLength ResultAdjustmentColumnWidth => new(
        _isResultDetailNarrowLayout ? 216 : 240);

    public ResultPageCardViewModel? SelectedResultPage
    {
        get => _selectedResultPage;
        private set
        {
            if (ReferenceEquals(_selectedResultPage, value))
            {
                return;
            }

            _selectedResultPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsResultDetailVisible));
            OnPropertyChanged(nameof(CanMoveToPreviousResultPage));
            OnPropertyChanged(nameof(CanMoveToNextResultPage));
            RaiseResultCommandsChanged();
        }
    }

    public string ResultStatusText => _renderPreparation?.Status switch
    {
        AiRenderPreparationStatus.WaitingForPhotoshop =>
            "RenderPlan 已通过结构、资源和摘要校验；Photoshop 暂不可用，批次已安全停在待渲染。",
        AiRenderPreparationStatus.ReadyToRender =>
            "RenderPlan 已通过结构、资源和摘要校验；等待正式渲染接管。",
        AiRenderPreparationStatus.UserActionRequired =>
            "没有可安全执行的页面；未启动 Photoshop，请先处理页面问题。",
        _ => "尚未准备页面级 RenderPlan。"
    };

    public string ResultSummaryText => _renderPreparation is null
        ? "0 个页面"
        : $"{ResultStyleGroups.Count} 款 · {_renderPreparation.Pages.Count} 个页面 · {_renderPreparation.ReadyPageCount} 个计划已闭合";

    public string ResultFormalOutputText => _renderPreparation?.HasFormalOutput == true
        ? "已有正式输出"
        : "尚无正式输出；当前只展示页面状态和已确认选图";

    public bool CanMoveToPreviousResultPage => ResultPageIndex > 0;

    public bool CanMoveToNextResultPage => ResultPageIndex >= 0
        && ResultPageIndex < AllResultPages.Length - 1;

    private ResultPageCardViewModel[] AllResultPages => ResultStyleGroups
        .SelectMany(group => group.Pages)
        .ToArray();

    private int ResultPageIndex => SelectedResultPage is null
        ? -1
        : Array.IndexOf(AllResultPages, SelectedResultPage);

    private void InitializeResultReview()
    {
        PrepareRenderingCommand = new AsyncRelayCommand(
            PrepareRenderingAsync,
            () => IsAiReady && !_aiBusy);
        SelectResultFilterCommand = new RelayCommand<ResultFilterViewModel>(SelectResultFilter);
        OpenResultPageCommand = new RelayCommand<ResultPageCardViewModel>(OpenResultPage);
        ReturnToResultOverviewCommand = new RelayCommand(ReturnToResultOverview);
        ReturnToPreflightCommand = new RelayCommand(ReturnToPreflight);
        PreviousResultPageCommand = new RelayCommand(
            () => MoveResultPage(-1),
            () => CanMoveToPreviousResultPage);
        NextResultPageCommand = new RelayCommand(
            () => MoveResultPage(1),
            () => CanMoveToNextResultPage);
    }

    private void SetResultWindowWidth(double width)
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

    private async Task PrepareRenderingAsync()
    {
        var batchId = _currentBatchId ?? _aiBatchProjection?.BatchId;
        if (_aiBatchWorkflow is null || string.IsNullOrWhiteSpace(batchId) || _aiBusy)
        {
            return;
        }

        _aiBusy = true;
        NotifyAiChanged();
        try
        {
            ApplyRenderPreparation(await _aiBatchWorkflow
                .PrepareRenderingAsync(batchId, CancellationToken.None)
                .ConfigureAwait(true));
            _batchScreen = BatchWorkspaceScreen.ResultOverview;
            NotifyBatchScreenChanged();
        }
        catch (InvalidOperationException exception)
        {
            AiStatusMessage = ToRenderUserMessage(exception.Message);
        }
        finally
        {
            _aiBusy = false;
            NotifyAiChanged();
        }
    }

    private void ApplyRenderPreparation(AiRenderPlanPreparation preparation)
    {
        _renderPreparation = preparation;
        ResultStyleGroups.Clear();

        var sourceStyles = (_aiBatchProjection?.Styles ?? [])
            .ToDictionary(style => style.StyleId, StringComparer.Ordinal);
        foreach (var stylePages in preparation.Pages.GroupBy(page => page.StyleId, StringComparer.Ordinal))
        {
            sourceStyles.TryGetValue(stylePages.Key, out var sourceStyle);
            var cards = stylePages.Select(page =>
            {
                var sourcePage = sourceStyle?.Pages.FirstOrDefault(item =>
                    string.Equals(item.PageId, page.PageId, StringComparison.Ordinal));
                return ResultPageCardViewModel.Create(
                    sourceStyle?.StyleName ?? "当前款式",
                    sourcePage,
                    page,
                    preparation.Status,
                    preparation.HasFormalOutput);
            }).ToArray();
            ResultStyleGroups.Add(new ResultStyleGroupViewModel(
                stylePages.Key,
                sourceStyle?.StyleName ?? "当前款式",
                cards));
        }

        RebuildResultFilters();
        ApplyResultFilter();
        OnPropertyChanged(nameof(ResultStatusText));
        OnPropertyChanged(nameof(ResultSummaryText));
        OnPropertyChanged(nameof(ResultFormalOutputText));
    }

    private void RebuildResultFilters()
    {
        ResultFilters.Clear();
        var pages = ResultStyleGroups.SelectMany(group => group.Pages).ToArray();
        AddResultFilter(ResultPageFilter.All, "全部", pages.Length);
        AddResultFilter(ResultPageFilter.NeedsReview, "需检查", pages.Count(page => page.Filter == ResultPageFilter.NeedsReview));
        AddResultFilter(ResultPageFilter.CompletedWithProblems, "完成但有问题", pages.Count(page => page.Filter == ResultPageFilter.CompletedWithProblems));
        AddResultFilter(ResultPageFilter.RuntimeFailed, "运行失败", pages.Count(page => page.Filter == ResultPageFilter.RuntimeFailed));
    }

    private void AddResultFilter(ResultPageFilter filter, string label, int count) =>
        ResultFilters.Add(new ResultFilterViewModel(
            filter,
            label,
            count,
            filter == _selectedResultFilter));

    private void SelectResultFilter(ResultFilterViewModel? filter)
    {
        if (filter is null)
        {
            return;
        }

        _selectedResultFilter = filter.Filter;
        foreach (var item in ResultFilters)
        {
            item.IsSelected = item.Filter == _selectedResultFilter;
        }

        ApplyResultFilter();
    }

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
    }

    private void OpenResultPage(ResultPageCardViewModel? page)
    {
        if (page is null)
        {
            return;
        }

        SelectedResultPage = ResultStyleGroups.SelectMany(group => group.Pages)
            .FirstOrDefault(item => item.StyleId == page.StyleId && item.PageId == page.PageId)
            ?? page;
        _batchScreen = BatchWorkspaceScreen.ResultDetail;
        NotifyBatchScreenChanged();
    }

    private void ReturnToResultOverview()
    {
        _batchScreen = BatchWorkspaceScreen.ResultOverview;
        NotifyBatchScreenChanged();
    }

    private void ReturnToPreflight()
    {
        SelectedResultPage = null;
        _batchScreen = BatchWorkspaceScreen.AiAnalysis;
        NotifyBatchScreenChanged();
    }

    private void MoveResultPage(int offset)
    {
        var pages = AllResultPages;
        var index = ResultPageIndex;
        var target = index + offset;
        if (target >= 0 && target < pages.Length)
        {
            SelectedResultPage = pages[target];
        }
    }

    private void RaiseResultCommandsChanged()
    {
        (PrepareRenderingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PreviousResultPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextResultPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string ToRenderUserMessage(string code) => code switch
    {
        "AI_RENDER_ANALYSIS_NOT_CLOSED" => "分析尚未收口，不能生成页面计划。",
        "AI_RENDER_FACTORY_NOT_CONFIGURED" => "页面计划服务暂不可用；未启动 Photoshop。",
        "AI_RENDER_TEMPLATE_RESOURCE_NOT_FOUND" => "模板内部副本缺失，请返回模板检查。",
        "AI_RENDER_SNAPSHOT_MISMATCH" or "AI_RENDER_SNAPSHOT_PAGE_MISMATCH" =>
            "批次模板快照与当前资源不一致，已停止生成。",
        _ => "页面计划未能安全准备；未启动 Photoshop，请检查批次状态。"
    };
}

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
    public ResultPageFilter Filter { get; }
    public string StatusText { get; }
    public string DetailText { get; }
    public string PreviewPath { get; }
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewPath);
    public bool HasNoPreview => !HasPreview;
    public bool HasFormalOutput { get; }
    public bool IsSizeTablePage { get; }
    public string OutputBoundaryText => HasFormalOutput
        ? "正式输出已登记"
        : "当前为已确认源图参考，不是生成结果";
    public IReadOnlyList<ResultCandidateViewModel> Candidates { get; }

    public static ResultPageCardViewModel Create(
        string styleName,
        AiPageAnalysisProjection? source,
        AiRenderPagePreparation page,
        AiRenderPreparationStatus overall,
        bool hasFormalOutput)
    {
        var status = MapStatus(page, overall, hasFormalOutput);
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
            source?.PageName ?? "页面",
            status.Filter,
            status.StatusText,
            status.DetailText,
            preview,
            hasFormalOutput,
            page.ReasonCode.Contains("SIZE_TABLE", StringComparison.Ordinal)
                || page.RenderPlanJson?.Contains("\"dynamic_size_table\"", StringComparison.Ordinal) == true,
            candidates);
    }

    private static (ResultPageFilter Filter, string StatusText, string DetailText) MapStatus(
        AiRenderPagePreparation page,
        AiRenderPreparationStatus overall,
        bool hasFormalOutput) =>
        page.Status switch
        {
            AiRenderPagePreparationStatus.ReadyToRender when hasFormalOutput =>
                (ResultPageFilter.All, "已完成", "正式输出已登记，可进入单页检查。"),
            AiRenderPagePreparationStatus.ReadyToRender when overall == AiRenderPreparationStatus.WaitingForPhotoshop =>
                (ResultPageFilter.NeedsReview, "待渲染", "页面计划已闭合；Photoshop 暂不可用，未生成正式图片。"),
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
