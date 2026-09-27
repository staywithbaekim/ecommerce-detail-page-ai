using System.ComponentModel;
using System.Windows.Input;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : IBatchInputPageHost
{
    private BatchWorkspaceScreen _batchScreen;
    private bool _isBatchNarrowLayout;
    private BatchInputViewModel BatchInput => BatchInputPageViewModel.Coordinator;
    private BatchInputPageState BatchInputState => BatchInput.State;

    private string? CurrentBatchId
    {
        get => BatchInput.CurrentBatchId;
        set => BatchInput.CurrentBatchId = value;
    }

    public ICommand OpenBatchInputCommand => BatchInput.OpenBatchInputCommand;

    public ICommand RestoreBatchInputCommand => BatchInput.RestoreBatchInputCommand;

    public bool IsBatchInputVisible =>
        SelectedNavigationItem.Key == "workbench" && _batchScreen == BatchWorkspaceScreen.BatchInput;

    public string BatchStatusMessage
    {
        get => BatchInputState.StatusMessage;
        private set => BatchInputState.SetStatus(BatchInputState.Status, value);
    }

    public bool IsBatchNarrowLayout => _isBatchNarrowLayout;

    private Task ScanBatchInputAsync() => BatchInput.ScanBatchInputAsync();
    private void SetBatchStatus(BatchInputUiStatus status, string message) => BatchInput.SetBatchStatus(status, message);

    IReadOnlyList<TemplateLibraryCardViewModel> IBatchInputHost.TemplateGroups => TemplateGroups;
    Task IBatchInputHost.LoadTemplateLibraryAsync() => LoadTemplateLibraryAsync();
    void IBatchInputHost.ShowBatchInput()
    {
        _batchScreen = BatchWorkspaceScreen.BatchInput;
        SelectedNavigationItem = NavigationItems.First(item => item.Key == "workbench");
        NotifyBatchScreenChanged();
    }

    void IBatchInputHost.ShowWorkbench()
    {
        _batchScreen = BatchWorkspaceScreen.Workbench;
        NotifyBatchScreenChanged();
    }

    void IBatchInputHost.CreateTemplate()
    {
        SelectedNavigationItem = NavigationItems.First(item => item.Key == "templates");
        TemplateWorkspacePageViewModel.Coordinator.NewTemplateCommand.Execute(null);
    }

    void IBatchInputHost.ImportTemplate()
    {
        SelectedNavigationItem = NavigationItems.First(item => item.Key == "templates");
        TemplateWorkspacePageViewModel.Coordinator.ImportTemplatePackageCommand.Execute(null);
    }

    Task<bool> IBatchInputHost.EnsureUploadConsentAsync() => AiAnalysisPageViewModel.EnsureAiUploadConsentForScanAsync();
    Task IBatchInputHost.StartAiAnalysisAsync(string batchId) => _aiAnalysisPage.StartAiAnalysisAsync(batchId);
    async Task IBatchInputHost.RestoreAiAnalysisAsync(string batchId)
    {
        await _aiAnalysisPage.RestoreAiAnalysisAsync(batchId).ConfigureAwait(true);
        await ResultReview.TryRestoreRenderingAsync(batchId, _aiBatchProjection).ConfigureAwait(true);
    }

    private void BatchSwitchAiStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiAnalysisViewModel.IsAiBusy) or nameof(AiAnalysisViewModel.AiProjection))
            BatchInput.RefreshBatchSwitchCommands();
    }

    private void BatchSwitchRenderStateChanged(object? sender, EventArgs e) => BatchInput.RefreshBatchSwitchCommands();

    private void BatchSwitchSizeEditorStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SizeTableEditorViewModel.IsBusy) or nameof(SizeTableEditorViewModel.IsSizeTableEditorVisible))
            BatchInput.RefreshBatchSwitchCommands();
    }

    private void OnBatchNavigationChanged()
    {
        // Navigation is a shell concern, not a batch-workflow reset.  Keep the
        // active screen, batch id, selected style and replacement state intact
        // while the user visits Settings/Templates/Tasks; returning to the
        // workbench must show the exact page that was left.
        AiAnalysisPageViewModel.NotifyShellViewChanged();
        OnPropertyChanged(nameof(IsBatchInputVisible));
        OnPropertyChanged(nameof(IsAiAnalysisVisible));
        OnPropertyChanged(nameof(IsAiStyleReviewVisible));
        OnPropertyChanged(nameof(IsResultOverviewVisible));
        OnPropertyChanged(nameof(IsResultDetailVisible));
    }

    private void SetBatchWindowWidth(double width)
    {
        var narrow = width <= 1180;
        if (_isBatchNarrowLayout == narrow)
        {
            return;
        }

        _isBatchNarrowLayout = narrow;
        OnPropertyChanged(nameof(IsBatchNarrowLayout));
        _aiAnalysisPage.NotifyShellViewChanged();
    }

    private void NotifyBatchScreenChanged()
    {
        OnPropertyChanged(nameof(IsWorkbenchVisible));
        OnPropertyChanged(nameof(IsBatchInputVisible));
        OnPropertyChanged(nameof(IsAiAnalysisVisible));
        OnPropertyChanged(nameof(IsAiStyleReviewVisible));
        OnPropertyChanged(nameof(IsResultOverviewVisible));
        OnPropertyChanged(nameof(IsResultDetailVisible));
        OnPropertyChanged(nameof(IsPlaceholderVisible));
        AiAnalysisPageViewModel.NotifyShellViewChanged();
    }

    private enum BatchWorkspaceScreen
    {
        Workbench,
        BatchInput,
        AiAnalysis,
        AiStyleReview,
        ResultOverview,
        ResultDetail
    }
}
