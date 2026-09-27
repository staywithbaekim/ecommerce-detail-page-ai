using System.ComponentModel;
using System.Windows.Input;
using Desktop.Application.Ai;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : IAiAnalysisHost
{
    private AiAnalysisViewModel _aiAnalysisPage = null!;
    private AiBatchAnalysisProjection? _aiBatchProjection => _aiAnalysisPage.AiProjection;
    public AiAnalysisViewModel AiAnalysisPageViewModel => _aiAnalysisPage;
    public ICommand GrantAiUploadConsentCommand => _aiAnalysisPage.GrantAiUploadConsentCommand;
    public ICommand RevokeAiUploadConsentCommand => _aiAnalysisPage.RevokeAiUploadConsentCommand;
    public ICommand RefreshAiConsentCommand => _aiAnalysisPage.RefreshAiConsentCommand;
    public bool IsAiAnalysisVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.AiAnalysis;

    public bool IsAiStyleReviewVisible => SelectedNavigationItem.Key == "workbench"
        && _batchScreen == BatchWorkspaceScreen.AiStyleReview
        && AiAnalysisPageViewModel.SelectedAiStyle is not null;
    public string AiUploadConsentStatusText => _aiAnalysisPage.AiUploadConsentStatusText;
    public string AiUploadConsentDetailText => _aiAnalysisPage.AiUploadConsentDetailText;
    public bool CanGrantAiUploadConsent => _aiAnalysisPage.CanGrantAiUploadConsent;
    public bool CanRevokeAiUploadConsent => _aiAnalysisPage.CanRevokeAiUploadConsent;

    private void InitializeAiBatchAnalysis()
    {
        _aiAnalysisPage.PropertyChanged += AiAnalysisPage_PropertyChanged;
        _aiAnalysisPage.Initialize();
    }

    private void AiAnalysisPage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsAiAnalysisVisible) or nameof(IsAiStyleReviewVisible)
            or nameof(AiUploadConsentStatusText) or nameof(AiUploadConsentDetailText)
            or nameof(CanGrantAiUploadConsent) or nameof(CanRevokeAiUploadConsent))
            OnPropertyChanged(e.PropertyName);
    }

    string? IAiAnalysisHost.CurrentBatchId => CurrentBatchId;
    bool IAiAnalysisHost.IsAiStyleReviewScreen => _batchScreen == BatchWorkspaceScreen.AiStyleReview;
    bool IAiAnalysisHost.CanRestartBatchAnalysis => BatchInput.CanRestartAnalysis;
    string IAiAnalysisHost.BatchStatusMessage { get => BatchStatusMessage; set => BatchStatusMessage = value; }
    string IAiAnalysisHost.ReferenceLearningStatusMessage
    {
        get => TemplateWorkspace.ReferenceLearningStatusMessage;
        set => TemplateWorkspace.ReferenceLearningStatusMessage = value;
    }
    void IAiAnalysisHost.NotifyMainVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsWorkbenchVisible));
        OnPropertyChanged(nameof(IsPlaceholderVisible));
    }
    void IAiAnalysisHost.ShowAiAnalysis()
    {
        _batchScreen = BatchWorkspaceScreen.AiAnalysis;
        NotifyBatchScreenChanged();
    }
    void IAiAnalysisHost.ShowAiStyleReview()
    {
        _batchScreen = BatchWorkspaceScreen.AiStyleReview;
        NotifyBatchScreenChanged();
    }
    void IAiAnalysisHost.ShowResultOverview()
    {
        _batchScreen = BatchWorkspaceScreen.ResultOverview;
        NotifyBatchScreenChanged();
    }
    void IAiAnalysisHost.RaiseResultCommandsChanged() => RaiseResultCommandsChanged();
    void IAiAnalysisHost.ApplyRenderPreparation(AiRenderPlanPreparation preparation) => ApplyRenderPreparation(preparation);
    void IAiAnalysisHost.SetBatchStatus(BatchInputUiStatus status, string message) => SetBatchStatus(status, message);
    Task<bool> IAiAnalysisHost.EnsureCredentialConfiguredAsync() => EnsureAiCredentialConfiguredForScanAsync();
    Task IAiAnalysisHost.StartReferenceLearningAsync() => TemplateWorkspace.StartReferenceLearningAsync();
    Task IAiAnalysisHost.ScanBatchInputAsync() => ScanBatchInputAsync();
    Task IAiAnalysisHost.RestartBatchAnalysisAsync() => BatchInput.RestartAnalysisAsync();
}
