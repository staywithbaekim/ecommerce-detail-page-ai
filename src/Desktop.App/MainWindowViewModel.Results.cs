using Desktop.Application.Ai;
using Desktop.Application.Rendering;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : IResultReviewPageHost
{
    private ResultReviewViewModel ResultReview => ResultReviewPageViewModel.Coordinator;

    public bool IsResultOverviewVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.ResultOverview;

    public bool IsResultDetailVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.ResultDetail
        && ResultReviewPageViewModel.Coordinator.SelectedResultPage is not null;

    private void ApplyRenderPreparation(AiRenderPlanPreparation preparation) =>
        ResultReview.ApplyRenderPreparation(preparation, _aiBatchProjection);
    private void RaiseResultCommandsChanged() => ResultReview.RaiseResultCommandsChanged();

    private bool CanEditRenderedBatch(string batchId) => CurrentBatchId == batchId && BatchInputState.OutputMode is null;
    bool IResultReviewHost.CanEditRenderedBatch(string batchId) => CanEditRenderedBatch(batchId);
    bool IResultReviewHost.CanRecoverFailedPage(string batchId, RenderBatchPage page) =>
        CurrentBatchId == batchId && BatchInputState.OutputMode is not null
        && page.Status == RenderBatchPageStatus.Failed;

    void IResultReviewHost.ShowDetails()
    {
        _batchScreen = BatchWorkspaceScreen.ResultDetail;
        NotifyBatchScreenChanged();
    }
    void IResultReviewHost.ShowOverview()
    {
        _batchScreen = BatchWorkspaceScreen.ResultOverview;
        NotifyBatchScreenChanged();
    }
    void IResultReviewHost.ShowPreflight()
    {
        _batchScreen = BatchWorkspaceScreen.AiAnalysis;
        NotifyBatchScreenChanged();
    }
    void IResultReviewHost.NotifyDetailSelectionChanged() => OnPropertyChanged(nameof(IsResultDetailVisible));
    void IResultReviewHost.RefreshGenerationCommand() =>
        (AiAnalysisPageViewModel.PrepareRenderingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
}
