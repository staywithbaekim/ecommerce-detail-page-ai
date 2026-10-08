using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;

namespace Desktop.App;

/// <summary>Owns result filtering, page selection, paging commands and layout.</summary>
public sealed partial class ResultReviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IResultReviewHost _host;
    public ResultReviewPageState State { get; } = new();
    private ResultReviewPageState ResultState => State;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ResultStyleGroupViewModel> ResultStyleGroups =>
        ResultState.ResultStyleGroups;

    public ObservableCollection<ResultStyleGroupViewModel> VisibleResultStyleGroups =>
        ResultState.VisibleResultStyleGroups;

    public ObservableCollection<ResultFilterViewModel> ResultFilters =>
        ResultState.ResultFilters;

    public ICommand SelectResultFilterCommand { get; private set; } = null!;

    public ICommand OpenResultPageCommand { get; private set; } = null!;

    public ICommand ReturnToResultOverviewCommand { get; private set; } = null!;

    public ICommand ReturnToPreflightCommand { get; private set; } = null!;

    public ICommand PreviousResultPageCommand { get; private set; } = null!;

    public ICommand NextResultPageCommand { get; private set; } = null!;

    public bool IsResultDetailNarrowLayout => ResultState.IsResultDetailNarrowLayout;

    public GridLength ResultCandidateColumnWidth => ResultState.ResultCandidateColumnWidth;

    public GridLength ResultDetailGutterWidth => ResultState.ResultDetailGutterWidth;

    public GridLength ResultAdjustmentColumnWidth => ResultState.ResultAdjustmentColumnWidth;

    public ResultPageCardViewModel? SelectedResultPage
    {
        get => ResultState.SelectedResultPage;
        internal set
        {
            if (ReferenceEquals(ResultState.SelectedResultPage, value))
            {
                return;
            }

            ResultState.SelectedResultPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OutputDirectoryMessage));
            _host.NotifyDetailSelectionChanged();
            OnPropertyChanged(nameof(CanMoveToPreviousResultPage));
            OnPropertyChanged(nameof(CanMoveToNextResultPage));
            RaiseResultCommandsChanged();
            RaiseRenderingCommandsChanged();
            _ = RefreshRenderingAsync();
        }
    }

    public string ResultStatusText => ResultState.ResultStatusText;

    public string ResultTitleText => ResultState.ResultTitleText;

    public string ResultSummaryText => ResultState.ResultSummaryText;

    public string ResultFormalOutputText => ResultState.ResultFormalOutputText;

    public bool CanMoveToPreviousResultPage => ResultState.CanMoveToPreviousResultPage;

    public bool CanMoveToNextResultPage => ResultState.CanMoveToNextResultPage;

    private ResultPageCardViewModel[] AllResultPages => ResultState.AllResultPages;

    public ResultReviewViewModel(IResultReviewHost host, IRenderBatchWorkflow? rendering = null, IRenderBatchOutputReader? outputs = null)
        : this(host, rendering, outputs, new WindowsOutputDirectoryOpener())
    {
    }

    internal ResultReviewViewModel(IResultReviewHost host, IRenderBatchWorkflow? rendering,
        IRenderBatchOutputReader? outputs, IOutputDirectoryOpener directoryOpener)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _rendering = rendering; _outputs = outputs;
        _directoryOpener = directoryOpener ?? throw new ArgumentNullException(nameof(directoryOpener));
        InitializeRenderingCommands();
        State.PropertyChanged += State_PropertyChanged;
        SelectResultFilterCommand = new RelayCommand<ResultFilterViewModel>(SelectResultFilter);
        OpenResultPageCommand = new RelayCommand<ResultPageCardViewModel>(OpenResultPage);
        ReturnToResultOverviewCommand = new RelayCommand(ReturnToResultOverview);
        ReturnToPreflightCommand = new RelayCommand(ReturnToPreflight, () => !_renderRunning);
        PreviousResultPageCommand = new RelayCommand(
            () => MoveResultPage(-1),
            () => CanMoveToPreviousResultPage);
        NextResultPageCommand = new RelayCommand(
            () => MoveResultPage(1),
            () => CanMoveToNextResultPage);
    }

    public void SetResultWindowWidth(double width)
    {
        var before = ResultState.IsResultDetailNarrowLayout;
        ResultState.SetWindowWidth(width);
        if (before == ResultState.IsResultDetailNarrowLayout)
        {
            return;
        }

        OnPropertyChanged(nameof(IsResultDetailNarrowLayout));
        OnPropertyChanged(nameof(ResultCandidateColumnWidth));
        OnPropertyChanged(nameof(ResultDetailGutterWidth));
        OnPropertyChanged(nameof(ResultAdjustmentColumnWidth));
    }

    public void ApplyRenderPreparation(AiRenderPlanPreparation preparation, AiBatchAnalysisProjection? projection)
    {
        ResultState.ApplyRenderPreparation(preparation, projection);
        OnPropertyChanged(nameof(ResultStyleGroups));
        OnPropertyChanged(nameof(VisibleResultStyleGroups));
        OnPropertyChanged(nameof(ResultFilters));
        OnPropertyChanged(nameof(ResultStatusText));
        OnPropertyChanged(nameof(ResultSummaryText));
        OnPropertyChanged(nameof(ResultFormalOutputText));
        RaiseRenderingCommandsChanged();
        _ = RefreshRenderingAsync();
    }

    private void SelectResultFilter(ResultFilterViewModel? filter)
    {
        if (filter is null)
        {
            return;
        }

        ResultState.SelectResultFilter(filter.Filter);
        OnPropertyChanged(nameof(VisibleResultStyleGroups));
        OnPropertyChanged(nameof(ResultFilters));
    }

    private void OpenResultPage(ResultPageCardViewModel? page)
    {
        if (page is null)
        {
            return;
        }

        SelectedResultPage = ResultState.ResultStyleGroups.SelectMany(group => group.Pages)
            .FirstOrDefault(item => item.StyleId == page.StyleId && item.PageId == page.PageId && item.OutputVariantId == page.OutputVariantId && item.PageInstanceId == page.PageInstanceId)
            ?? page;
        _host.ShowDetails();
    }

    private void ReturnToResultOverview()
    {
        _host.ShowOverview();
    }

    private void ReturnToPreflight()
    {
        SelectedResultPage = null;
        _host.ShowPreflight();
    }

    private void MoveResultPage(int offset)
    {
        ResultState.MoveResultPage(offset);
        OnPropertyChanged(nameof(SelectedResultPage));
        _host.NotifyDetailSelectionChanged();
        OnPropertyChanged(nameof(CanMoveToPreviousResultPage));
        OnPropertyChanged(nameof(CanMoveToNextResultPage));
        RaiseResultCommandsChanged();
    }

    internal void RaiseResultCommandsChanged()
    {
        _host.RefreshGenerationCommand();
        (PreviousResultPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextResultPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
        if (e.PropertyName == nameof(ResultReviewPageState.RenderPreparation))
            OnPropertyChanged(nameof(OutputDirectoryMessage));
        if (e.PropertyName == nameof(ResultReviewPageState.RenderJournal))
            OnPropertyChanged(nameof(RenderedEditingMessage));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _renderLifetime.Cancel();
        State.PropertyChanged -= State_PropertyChanged;
        GC.SuppressFinalize(this);
    }
}

public interface IResultReviewHost
{
    bool CanEditRenderedBatch(string batchId) => true;
    bool CanRecoverFailedPage(string batchId, RenderBatchPage page) => false;
    void ShowDetails();
    void ShowOverview();
    void ShowPreflight();
    void NotifyDetailSelectionChanged();
    void RefreshGenerationCommand();
}
