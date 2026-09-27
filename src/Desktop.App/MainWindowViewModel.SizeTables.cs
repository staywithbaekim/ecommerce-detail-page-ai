using Desktop.Application.SizeTables;
using Desktop.Application.Rendering;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : ISizeTableEditorHost
{
    public SizeTableEditorViewModel SizeTableEditorPage { get; private set; } = null!;

    private void InitializeSizeTableEditor(ISizeTableEditingWorkflow? workflow, IRenderBatchWorkflow? rendering)
    {
        SizeTableEditorPage = new SizeTableEditorViewModel(workflow, this, rendering);
    }

    string? ISizeTableEditorHost.BatchId => CurrentBatchId ?? _aiBatchProjection?.BatchId;
    bool ISizeTableEditorHost.CanOpenEditor => !BatchInput.BatchBusy && ((IBatchInputHost)this).CanSwitchBatch;
    bool ISizeTableEditorHost.CanEditRenderedBatch(string batchId) => CanEditRenderedBatch(batchId);

    string? ISizeTableEditorHost.GetSourcePreviewPath(string styleId, string sourceAssetId) =>
        _aiBatchProjection?.Styles
            .SingleOrDefault(style => string.Equals(style.StyleId, styleId, StringComparison.Ordinal))
            ?.Assets?.SingleOrDefault(asset => string.Equals(
                asset.AssetId, sourceAssetId, StringComparison.Ordinal))?.PreviewPath;

    void ISizeTableEditorHost.ShowAnalysisStatus(string message)
    {
        AiAnalysisPageViewModel.AiStatusMessage = message;
        ResultReviewPageViewModel.Coordinator.ReportEditorStatus(message);
    }

    async Task ISizeTableEditorHost.RefreshSavedPageAsync(SizeTableEditorDocument saved)
    {
        var preparation = await _aiAnalysisPage.PrepareSavedSizePageAsync(saved.BatchId).ConfigureAwait(true);
        if (preparation is not null)
        {
            ApplyRenderPreparation(preparation);
            ResultReviewPageViewModel.Coordinator.SelectedResultPage = ResultReviewPageViewModel.State.ResultStyleGroups.SelectMany(group => group.Pages)
                .FirstOrDefault(page => string.Equals(page.StyleId, saved.StyleId, StringComparison.Ordinal)
                    && string.Equals(page.PageId, saved.PageId, StringComparison.Ordinal));
        }
    }

    Task ISizeTableEditorHost.ContinueSizeTableRepairAsync(RenderBatchJournal journal, SizeTablePageRepairCommand command) =>
        ResultReview.ContinueSizeTableRepairAsync(journal, command);
}
