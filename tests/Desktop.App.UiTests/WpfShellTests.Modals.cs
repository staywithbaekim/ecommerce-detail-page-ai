using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Desktop.Application.Ai;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;

namespace Desktop.App.UiTests;

public sealed partial class WpfShellTests
{
    [Fact]
    public void ModalNestedInstanceSelectionDisablesPreviewUntilCancelled()
    {
        RunInSta(() =>
        {
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(model);
            try
            {
                window.Show(); ((IAiAnalysisHost)model).ShowAiAnalysis();
                var ai = model.AiAnalysisPageViewModel;
                ai.IsAiCandidatePreviewVisible = true;
                typeof(AiAnalysisViewModel).GetField("_instanceReview", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(ai, new TemplateInstanceSelectionReview("batch", "style", "frame", "asset", 1, "digest", []));
                typeof(AiAnalysisViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(ai, [nameof(AiAnalysisViewModel.IsInstanceSelectionVisible)]);
                DrainDispatcher(); FlushLayout(window);
                var host = Assert.IsType<ModalHost>(window.FindName("ModalHost"));
                var preview = Assert.Single(host.Children.OfType<AiCandidatePreviewDialog>());
                var instance = FindByAutomationId<Border>(host, "InstanceSelectionDialog");
                Assert.True(instance.IsVisible);
                Assert.True(instance.IsEnabled);
                Assert.True(instance.IsKeyboardFocusWithin);
                Assert.True(preview.IsVisible);
                Assert.False(preview.IsEnabled);
                ai.CancelInstanceSelectionCommand.Execute(null);
                DrainDispatcher(); FlushLayout(window);
                Assert.True(host.IsOpen);
                Assert.True(preview.IsEnabled);
                Assert.True(preview.IsKeyboardFocusWithin);
                model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "settings");
                FlushLayout(window);
                Assert.False(ai.IsInstanceSelectionVisible);
                Assert.False(host.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModalDuplicateReviewCancelsOnlyPendingImportOnSectionOrTemplateScreenChange(bool innerScreen)
    {
        RunInSta(() =>
        {
            var decisions = new List<TemplateDuplicateDecision>();
            var workspace = new TemplateLibraryWorkspaceStub
            {
                ImportOverride = decision =>
                {
                    decisions.Add(decision);
                    return Task.FromResult(new TemplateWorkspaceImportResult(
                        new(TemplatePageImportStatus.Cancelled, null, [], null, false), null));
                }
            };
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null), workspace);
            var window = new MainWindow(model);
            try
            {
                window.Show(); model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "templates");
                var owner = model.TemplateWorkspacePageViewModel.Coordinator;
                var draft = new TemplateDraftSession("group", "草稿", "revision", 1, new HashSet<string>());
                typeof(TemplateWorkspaceViewModel).GetField("_templateDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, draft);
                var pendingType = typeof(TemplateWorkspaceViewModel).GetNestedType("PendingTemplateImport", BindingFlags.NonPublic)!;
                var pending = Activator.CreateInstance(pendingType, "command", "resource", @"C:\fixture.psd", "页面", "duplicate");
                var pendingField = typeof(TemplateWorkspaceViewModel).GetField("_pendingTemplateImport", BindingFlags.Instance | BindingFlags.NonPublic)!;
                pendingField.SetValue(owner, pending);
                owner.IsDuplicateReviewVisible = true;
                DrainDispatcher(); FlushLayout(window);
                Assert.True(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
                if (innerScreen)
                {
                    var change = typeof(TemplateWorkspaceViewModel).GetMethod("SetTemplateScreen", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    change.Invoke(owner, [Enum.Parse(change.GetParameters()[0].ParameterType, "Details")]);
                }
                else model.SelectedNavigationItem = model.NavigationItems[0];
                DrainDispatcher(); FlushLayout(window);
                Assert.False(owner.IsDuplicateReviewVisible);
                Assert.Null(pendingField.GetValue(owner));
                Assert.Equal(TemplateDuplicateDecision.Cancel, Assert.Single(decisions));
                Assert.Equal(0, workspace.DeleteCount);
                Assert.Same(draft, typeof(TemplateWorkspaceViewModel).GetField("_templateDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));
                Assert.False(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ModalUploadConsentNavigationCancelsContinuationAndRequiresFreshAcknowledgement()
    {
        RunInSta(() =>
        {
            var workflow = new RecordingAiBatchAnalysisWorkflow();
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null), null, aiBatchAnalysisWorkflow: workflow);
            var window = new MainWindow(model);
            try
            {
                window.Show(); model.OpenBatchInputCommand.Execute(null);
                var ai = model.AiAnalysisPageViewModel;
                Assert.False(ai.EnsureAiUploadConsentForScanAsync().GetAwaiter().GetResult());
                ai.AiUploadConsentAcknowledged = true;
                DrainDispatcher(); FlushLayout(window);
                Assert.True(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
                model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "templates");
                DrainDispatcher(); FlushLayout(window);
                Assert.False(ai.IsAiUploadConsentDialogVisible);
                Assert.False(ai.AiUploadConsentAcknowledged);
                Assert.False(ai.IsBatchScanConsentPending);
                ai.ConfirmAiUploadConsentCommand.Execute(null);
                Assert.Equal(0, workflow.GrantCount);
                Assert.False(ai.EnsureAiUploadConsentForReferenceLearningAsync().GetAwaiter().GetResult());
                DrainDispatcher(); FlushLayout(window);
                Assert.True(ai.IsReferenceLearningConsentPending);
                Assert.False(ai.AiUploadConsentAcknowledged);
                Assert.True(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
                var change = typeof(TemplateWorkspaceViewModel).GetMethod("SetTemplateScreen", BindingFlags.Instance | BindingFlags.NonPublic)!;
                change.Invoke(model.TemplateWorkspacePageViewModel.Coordinator,
                    [Enum.Parse(change.GetParameters()[0].ParameterType, "Details")]);
                DrainDispatcher(); FlushLayout(window);
                Assert.False(ai.IsAiUploadConsentDialogVisible);
                Assert.False(ai.IsReferenceLearningConsentPending);
                Assert.False(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1100, 760)]
    public void ModalHostOwnsEveryOverlayAndBlocksBackgroundInput(double width, double height)
    {
        RunInSta(() =>
        {
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(model);
            try
            {
                window.Show(); SetWindowSize(window, width, height);
                var host = Assert.IsType<ModalHost>(window.FindName("ModalHost"));
                var root = Assert.IsType<Grid>(window.Content);
                Assert.Same(root, VisualTreeHelper.GetParent(host));
                Assert.Same(host, root.Children[root.Children.Count - 1]);
                Assert.Equal(2, Grid.GetRowSpan(host));
                Assert.Equal(7, host.Children.Count);
                Assert.All(host.Children.Cast<UIElement>(), child => Assert.Equal(0, Panel.GetZIndex(child)));
                Assert.False(host.IsOpen);

                model.OpenBatchInputCommand.Execute(null);
                var navigation = FindByAutomationId<ListBox>(window, "PrimaryNavigation");
                Assert.True(navigation.Focus());
                model.BatchInputPageViewModel.State.OpenPageSelection();
                FlushLayout(window); DrainDispatcher();
                var dialog = Assert.Single(host.Children.OfType<BatchPageSelectionDialog>());
                var shell = Assert.IsType<Grid>(window.FindName("ShellContent"));
                Assert.True(host.IsOpen);
                Assert.True(dialog.IsVisible);
                Assert.False(shell.IsEnabled);
                Assert.True(dialog.IsKeyboardFocusWithin);
                Assert.Equal(root.ActualWidth, host.ActualWidth, 1);
                Assert.Equal(root.ActualHeight, host.ActualHeight, 1);
                Assert.False(FindByAutomationId<ListBox>(window, "PrimaryNavigation").Focus());
                for (var index = 0; index < 12; index++)
                {
                    Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement)
                        .MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Assert.True(dialog.IsKeyboardFocusWithin);
                }
                model.BatchInputPageViewModel.Coordinator.CancelBatchPageSelectionCommand.Execute(null);
                DrainDispatcher(); FlushLayout(window);
                Assert.False(host.IsOpen);
                Assert.True(shell.IsEnabled);
                Assert.True(navigation.IsKeyboardFocusWithin);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("templates")]
    [InlineData("settings")]
    [InlineData("tasks")]
    public void ModalNavigationCancelsUnconfirmedBatchSelectionWithoutResettingBatch(string destination)
    {
        RunInSta(() =>
        {
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(model);
            try
            {
                window.Show(); model.OpenBatchInputCommand.Execute(null);
                var state = model.BatchInputPageViewModel.State;
                state.CurrentBatchId = "modal_batch";
                state.InputDirectory = @"C:\fixture-input";
                state.BatchPages.Add(new("page", "详情", "normal", true));
                state.OpenPageSelection(); state.BatchPages[0].IsSelected = false;
                model.SelectedNavigationItem = model.SelectedNavigationItem;
                Assert.True(state.IsPageSelectionVisible);
                model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == destination);
                FlushLayout(window);
                Assert.False(state.IsPageSelectionVisible);
                Assert.True(state.BatchPages[0].IsSelected);
                Assert.Equal("modal_batch", state.CurrentBatchId);
                Assert.Equal(@"C:\fixture-input", state.InputDirectory);
                model.SelectedNavigationItem = model.NavigationItems[0]; FlushLayout(window);
                Assert.True(model.IsBatchInputVisible);
                Assert.False(state.IsPageSelectionVisible);
                Assert.False(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ModalNavigationCompletesTemplateConfirmationAsCancellationAndDoesNotResurrectIt()
    {
        RunInSta(() =>
        {
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(model);
            try
            {
                window.Show(); model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "templates");
                var owner = model.TemplateWorkspacePageViewModel.Coordinator;
                var show = typeof(TemplateWorkspaceViewModel).GetMethod("ShowTemplateDialogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var pending = Assert.IsAssignableFrom<Task>(show.Invoke(owner,
                    ["删除页面？", "只用于取消回归", "源文件不变", "删除", "取消", null, true]));
                FlushLayout(window);
                Assert.True(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
                model.SelectedNavigationItem = model.NavigationItems[0]; FlushLayout(window);
                Assert.True(pending.IsCompletedSuccessfully);
                Assert.Equal("Cancelled", pending.GetType().GetProperty("Result")!.GetValue(pending)!.ToString());
                Assert.False(owner.IsTemplateDialogVisible);
                model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "templates");
                FlushLayout(window);
                Assert.False(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ModalVisibilityAlsoRequiresTheOwningPageEvenIfAnOldFlagRemainsSet()
    {
        RunInSta(() =>
        {
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(model);
            try
            {
                window.Show(); model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "settings");
                model.BatchInputPageViewModel.State.OpenPageSelection();
                model.AiAnalysisPageViewModel.IsAiCandidatePreviewVisible = true;
                model.AiAnalysisPageViewModel.IsAiUploadConsentDialogVisible = true;
                typeof(AiAnalysisViewModel).GetField("_instanceReview", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(model.AiAnalysisPageViewModel, new TemplateInstanceSelectionReview("batch", "style", "frame", "asset", 1, "digest", []));
                typeof(AiAnalysisViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(model.AiAnalysisPageViewModel, [nameof(AiAnalysisViewModel.IsInstanceSelectionVisible)]);
                model.TemplateWorkspacePageViewModel.Coordinator.IsDuplicateReviewVisible = true;
                model.TemplateWorkspacePageViewModel.State.IsTemplateDialogVisible = true;
                typeof(SizeTableEditorViewModel).GetProperty(nameof(SizeTableEditorViewModel.IsSizeTableEditorVisible))!
                    .SetValue(model.SizeTableEditorPage, true);
                FlushLayout(window);
                var host = Assert.IsType<ModalHost>(window.FindName("ModalHost"));
                Assert.All(host.Children.Cast<UIElement>(), child => Assert.Equal(Visibility.Collapsed, child.Visibility));
                Assert.False(host.IsOpen);
                Assert.True(Assert.IsType<Grid>(window.FindName("ShellContent")).IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ModalCandidatePreviewClosesOnBothSectionAndWorkbenchScreenChanges()
    {
        RunInSta(() =>
        {
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(model);
            try
            {
                window.Show(); ((IAiAnalysisHost)model).ShowAiAnalysis();
                var candidate = new AiCandidateViewModel(new AiCandidateProjection(
                    "batch", "style", "frame", "asset", "fixture.png", 1, 0.9m, [], false, "frame"));
                model.AiAnalysisPageViewModel.OpenAiCandidatePreview(candidate); FlushLayout(window);
                Assert.True(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
                model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "templates");
                FlushLayout(window);
                Assert.False(model.AiAnalysisPageViewModel.IsAiCandidatePreviewVisible);
                Assert.Null(model.AiAnalysisPageViewModel.AiCandidatePreview);
                model.SelectedNavigationItem = model.NavigationItems[0];
                model.AiAnalysisPageViewModel.OpenAiCandidatePreview(candidate);
                ((IAiAnalysisHost)model).ShowResultOverview(); FlushLayout(window);
                Assert.False(model.AiAnalysisPageViewModel.IsAiCandidatePreviewVisible);
                Assert.True(model.IsResultOverviewVisible);
                Assert.False(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ModalSizeEditorClosesWithoutSavingOrDiscardingDraftOnNavigation()
    {
        RunInSta(() =>
        {
            var workflow = new RecordingSizeTableEditingWorkflow(new("table", "batch", "style", "size", "asset",
                "sha256:" + new string('a', 64), 1, SizeTableEditorStatus.Valid,
                [new("size", "尺码"), new("length", "衣长")], [new("row", ["L", "61"])]));
            using var model = new MainWindowViewModel(new AppLaunchOptions(true, null), null, sizeTableEditingWorkflow: workflow);
            var window = new MainWindow(model);
            try
            {
                window.Show(); model.BatchInputPageViewModel.State.CurrentBatchId = "batch";
                ((IAiAnalysisHost)model).ShowResultOverview();
                var page = ResultPageCardViewModel.Create("款式", null,
                    new("style", "size", AiRenderPagePreparationStatus.ReadyToRender, "AI_SIZE_TABLE_VALID"),
                    AiRenderPreparationStatus.WaitingForPhotoshop);
                model.SizeTableEditorPage.OpenSizeTableEditorCommand.Execute(page); FlushLayout(window);
                Assert.True(model.SizeTableEditorPage.IsSizeTableEditorVisible);
                Assert.True(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
                model.SizeTableEditorPage.SizeTableEditor.Rows[0].Cells[1].SourceText = "62";
                model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "settings");
                FlushLayout(window);
                Assert.False(model.SizeTableEditorPage.IsSizeTableEditorVisible);
                Assert.Equal("62", model.SizeTableEditorPage.SizeTableEditor.Rows[0].Cells[1].SourceText);
                Assert.Equal(0, workflow.SaveCount);
                model.SelectedNavigationItem = model.NavigationItems[0]; FlushLayout(window);
                Assert.False(Assert.IsType<ModalHost>(window.FindName("ModalHost")).IsOpen);
            }
            finally { window.Close(); }
        });
    }
}
