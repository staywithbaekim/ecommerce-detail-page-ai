using System.ComponentModel;

namespace Desktop.App.UiTests;

public sealed class PageNotificationTests
{
    [Fact]
    public void BatchPageObservesLocalStateWithoutShellRelayAndDetachesOnDispose()
    {
        var host = new BatchHost();
        using var page = new BatchInputPageViewModel(host);
        var changed = new List<string?>();
        page.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        page.State.InputDirectory = @"C:\isolated\input";
        Assert.Equal("已选择：input", page.BatchInputDirectoryDisplay);
        Assert.Contains(nameof(page.BatchInputDirectoryDisplay), changed);
        changed.Clear();

        // A host may still relay coordinator events for legacy consumers.
        // The page must receive each local notification from its owner only.
        host.Notify(nameof(page.BatchInputDirectoryDisplay));
        Assert.Empty(changed);
        host.ShowBatchInput();
        Assert.True(page.IsBatchInputVisible);
        Assert.Equal(new[] { nameof(page.IsBatchInputVisible) }, changed);

        page.Dispose();
        changed.Clear();
        host.ShowWorkbench();
        page.State.InputDirectory = @"C:\isolated\after-close";
        Assert.Empty(changed);
    }

    [Fact]
    public void TemplatePageReceivesDialogAndViewportChangesWithoutConcreteShell()
    {
        var host = new TemplateHost();
        var coordinator = new TemplateWorkspaceViewModel(null, null, null, null, null, null, null, host);
        using var page = new TemplateWorkspacePageViewModel(host, coordinator);
        var changed = new List<string?>();
        page.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        page.State.TemplateDialogTitle = "保存模板";
        Assert.Equal("保存模板", page.TemplateDialogTitle);
        Assert.Contains(nameof(page.TemplateDialogTitle), changed);
        coordinator.SetWindowWidth(1100);
        Assert.True(page.IsCalibrationNarrowLayout);
        Assert.Contains(nameof(page.IsCalibrationNarrowLayout), changed);

        changed.Clear();
        host.Notify(nameof(page.TemplateDialogTitle));
        Assert.Empty(changed);
        host.IsTemplateSectionVisible = true;
        host.Notify(nameof(page.IsTemplateSectionVisible));
        Assert.True(page.IsTemplateSectionVisible);
        Assert.Equal(new[] { nameof(page.IsTemplateSectionVisible) }, changed);

        page.Dispose();
        changed.Clear();
        page.State.TemplateDialogTitle = "关闭后";
        host.Notify(nameof(page.IsTemplateSectionVisible));
        Assert.Empty(changed);
    }

    private sealed class TemplateHost : ITemplateWorkspacePageHost, ITemplateWorkspaceHost
    {
        public bool IsTemplateSectionVisible { get; set; }
        public Task<bool> EnsureUploadConsentAsync() => Task.FromResult(false);
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class BatchHost : IBatchInputPageHost
    {
        public bool IsBatchInputVisible { get; private set; }
        public IReadOnlyList<TemplateLibraryCardViewModel> TemplateGroups => [];
        public IReadOnlyList<TemplateLibraryCardViewModel> BatchTemplateGroups => [];
        public bool HasImportedTemplateFiles => false;
        public Task LoadTemplateLibraryAsync() => Task.CompletedTask;
        public Task ImportPsdFilesAsync(IReadOnlyList<string> paths) => Task.CompletedTask;
        public Task ChoosePsdFolderAsync() => Task.CompletedTask;
        public void CreateTemplate() { }
        public void ImportTemplate() { }
        public Task<bool> EnsureUploadConsentAsync() => Task.FromResult(false);
        public Task StartAiAnalysisAsync(string batchId) => Task.CompletedTask;
        public Task RestoreAiAnalysisAsync(string batchId) => Task.CompletedTask;
        public void ShowBatchInput()
        {
            IsBatchInputVisible = true;
            Notify(nameof(IsBatchInputVisible));
        }
        public void ShowWorkbench()
        {
            IsBatchInputVisible = false;
            Notify(nameof(IsBatchInputVisible));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
