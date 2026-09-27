using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Desktop.App;

public sealed partial class MainWindowViewModel : ITemplateWorkspaceHost, ITemplateWorkspacePageHost
{
    private TemplateWorkspaceViewModel TemplateWorkspace => TemplateWorkspacePageViewModel.Coordinator;

    public ObservableCollection<TemplateLibraryCardViewModel> TemplateGroups => TemplateWorkspace.TemplateGroups;
    public IReadOnlyList<TemplateLibraryCardViewModel> BatchTemplateGroups => TemplateWorkspace.BatchTemplateGroups;
    public bool IsTemplateCalibrationVisible => TemplateWorkspace.IsTemplateCalibrationVisible;
    public bool HasImportedTemplateFiles => TemplateWorkspace.HasImportedTemplateFiles;
    public Task ImportPsdFilesAsync(IReadOnlyList<string> paths) => TemplateWorkspace.ImportPsdFilesAsync(paths);
    public Task ChoosePsdFolderAsync() => TemplateWorkspace.ChoosePsdFolderAsync();
    public void SetWindowWidth(double width)
    {
        SetBatchWindowWidth(width);
        ResultReviewPageViewModel.Coordinator.SetResultWindowWidth(width);
        TemplateWorkspace.SetWindowWidth(width);
    }

    private void TemplateWorkspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TemplateGroups) or nameof(BatchTemplateGroups)
            or nameof(IsTemplateCalibrationVisible) or nameof(HasImportedTemplateFiles))
            OnPropertyChanged(e.PropertyName);
    }
    private Task LoadTemplateLibraryAsync() => TemplateWorkspace.LoadTemplateLibraryAsync();
    Task<bool> ITemplateWorkspaceHost.EnsureUploadConsentAsync() =>
        _aiAnalysisPage.EnsureAiUploadConsentForReferenceLearningAsync();
}
