using System.ComponentModel;

namespace Desktop.App.UiTests;

public sealed class PageNotificationTests
{
    [Fact]
    public void BatchPageEditablePsdChangesAutoSaveAndRestoreWithoutScanning()
    {
        var host = new BatchHost();
        var workflow = new RecordingBatchWorkflow();
        using var page = CreateReadyBatchPage(host, workflow);

        page.BatchIncludeEditablePsd = true;

        Assert.True(Assert.Single(workflow.SavedSubmissions).IncludeEditablePsd);
        using var reopened = new BatchInputPageViewModel(host, workflow: workflow);
        reopened.Coordinator.RestoreBatchInputCommand.Execute(null);
        Assert.True(reopened.BatchIncludeEditablePsd);
        Assert.Equal(page.State.CurrentBatchId, reopened.State.CurrentBatchId);

        page.BatchIncludeEditablePsd = false;
        Assert.Equal(2, workflow.SavedSubmissions.Count);
        Assert.False(workflow.SavedSubmissions[^1].IncludeEditablePsd);
        Assert.Equal(0, workflow.Scans);
    }

    [Fact]
    public void BatchPageSelectionModeChangesUseTheDraftSavingCoordinator()
    {
        var workflow = new RecordingBatchWorkflow();
        using var page = CreateReadyBatchPage(new BatchHost(), workflow);

        page.IsBatchManualSelection = true;
        Assert.Equal(BatchSelectionMode.Manual, Assert.Single(workflow.SavedSubmissions).SelectionMode);

        page.IsBatchAutomaticSelection = true;
        Assert.Equal(2, workflow.SavedSubmissions.Count);
        Assert.Equal(BatchSelectionMode.Automatic, workflow.SavedSubmissions[^1].SelectionMode);
        Assert.Equal(0, workflow.Scans);
    }

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

    private static BatchInputPageViewModel CreateReadyBatchPage(BatchHost host, RecordingBatchWorkflow workflow)
    {
        var page = new BatchInputPageViewModel(host, workflow: workflow);
        page.State.SelectedTemplate = new("group_autosave", "draft fixture", "1 page", "today", "Committed",
            HasCommittedRevision: true);
        page.State.InputDirectory = @"C:\isolated\input";
        page.State.OutputDirectory = @"C:\isolated\output";
        page.State.BatchPages.Add(new("page_autosave", "page fixture", "test", true));
        Assert.True(page.CanScanBatch);
        return page;
    }

    private sealed class RecordingBatchWorkflow : IBatchInputWorkflow
    {
        public List<BatchInputSubmission> SavedSubmissions { get; } = [];
        public int Scans { get; private set; }
        private BatchInputProjection? _saved;

        public Task<BatchInputProjection?> RestoreLatestDraftAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_saved);

        public Task<BatchInputProjection> SaveDraftAsync(BatchInputSubmission submission, CancellationToken cancellationToken)
        {
            SavedSubmissions.Add(submission);
            _saved = new(submission.BatchId ?? "draft_autosave", submission.TemplateGroupId, BatchInputUiStatus.Draft,
                submission.InputDirectory, submission.OutputDirectory, submission.SelectedPageIds,
                submission.SelectionMode, submission.IncludeEditablePsd, "test fixture", "saved", submission.OutputMode);
            return Task.FromResult(_saved);
        }

        public Task<BatchInputProjection> ScanAsync(BatchInputSubmission submission, CancellationToken cancellationToken)
        {
            Scans++;
            throw new InvalidOperationException("A draft-setting test must not scan or start AI.");
        }
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
