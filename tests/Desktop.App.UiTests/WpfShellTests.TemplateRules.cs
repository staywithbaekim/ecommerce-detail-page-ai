using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Application.Psd;
using Desktop.Application.Ai;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.App.UiTests;

public sealed partial class WpfShellTests
{
    [Theory]
    [InlineData(550)]
    [InlineData(820)]
    public void RuleVisualRefreshKeepsReadableFieldsAndNativeSelection(double width)
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            var page = model.Pages[0];
            PumpRuleTask(model.RegisterAsync(page, Assert.Single(page.Candidates), string.Concat(Enumerable.Repeat("中文长规则：保持本款颜色与完整衣服结构。", 16))));
            var card = new TemplatePageRuleCard { DataContext = page };
            var window = new Window { Width = width, Height = 700, Content = card };
            try
            {
                window.Show(); FlushLayout(window);
                var box = Assert.Single(Descendants<TextBox>(card));
                Assert.Equal(14, box.FontSize);
                Assert.True(box.ActualWidth >= 150);
                Assert.True(box.ActualHeight <= 190);
                Assert.Equal(TextWrapping.Wrap, box.TextWrapping);
                box.Focus(); FlushLayout(window);
                Assert.True(box.IsKeyboardFocusWithin);
                Assert.Same(page.Frames[0], page.SelectedFrame);
                var chrome = (Border)box.Template.FindName("Chrome", box);
                Assert.Equal(((SolidColorBrush)card.FindResource("WorkbenchAccentBrush")).Color, ((SolidColorBrush)chrome.BorderBrush).Color);
                var rows = (ListBox)card.FindName("FrameList");
                var row = (ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(0);
                var highlight = (Border)row.Template.FindName("RowChrome", row);
                Assert.Equal(((SolidColorBrush)card.FindResource("WorkbenchSelectedBrush")).Color, ((SolidColorBrush)highlight.Background).Color);
                var scroll = (ScrollViewer)card.FindName("RulesScroll");
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                var preview = (TemplateRulePreview)card.FindName("Preview");
                Assert.InRange(preview.ActualHeight / preview.ActualWidth, 9.99, 10.01);
                box.IsEnabled = false; FlushLayout(window);
                Assert.Equal(((SolidColorBrush)card.FindResource("TextDisabledBrush")).Color, ((SolidColorBrush)box.Foreground).Color);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(580)]
    [InlineData(1100)]
    public void RuleOverviewVisualHierarchyWrapsLongTextAndPreservesActions(double width)
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            var page = model.Pages[0];
            PumpRuleTask(model.RegisterAsync(page, Assert.Single(page.Candidates), string.Concat(Enumerable.Repeat("长内容仍须换行并保留同一框的定位。", 20))));
            var graph = new TemplateRuleOverviewControl(); graph.ShowModel(model);
            string? located = null; graph.LocateRequested += (_, frame) => located = frame;
            var window = new Window { Width = width, Height = 720, Content = graph };
            try
            {
                window.Show(); FlushLayout(window);
                var scroll = (ScrollViewer)graph.FindName("OverviewScroll");
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                var button = Assert.Single(Descendants<Button>(graph), item => item.DataContext is TemplateRuleRelationFrame);
                Assert.True(button.ActualWidth <= scroll.ViewportWidth);
                Assert.True(button.ActualWidth > scroll.ViewportWidth * 0.65);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(page.Frames[0].FrameId, located);
                var expander = Descendants<Expander>(graph).First();
                var toggle = (System.Windows.Controls.Primitives.ToggleButton)expander.Template.FindName("HeaderToggle", expander);
                toggle.IsChecked = false; FlushLayout(window); Assert.False(expander.IsExpanded);
                toggle.IsChecked = true; FlushLayout(window); Assert.True(expander.IsExpanded);
                var errorSurface = (Border)graph.FindName("ErrorSurface");
                Assert.Equal(Visibility.Collapsed, errorSurface.Visibility);
                graph.ShowError("@1 的使用条件重叠，请返回修改；草稿已保留。"); FlushLayout(window);
                Assert.Equal(Visibility.Visible, errorSurface.Visibility);
                Assert.True(BoundsInWindow(errorSurface, window).Bottom < window.ActualHeight);
                var confirm = (Button)graph.FindName("Confirm");
                Assert.Same(graph.FindResource("WorkbenchPrimaryButtonStyle"), confirm.Style);
                confirm.IsEnabled = false; FlushLayout(window);
                var chrome = (Border)confirm.Template.FindName("Chrome", confirm);
                Assert.NotEqual(((SolidColorBrush)graph.FindResource("PrimaryBrush")).Color, ((SolidColorBrush)chrome.Background).Color);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RuleComposerVisualErrorKeepsInputAndClearsToneOnNextAction()
    {
        RunInSta(() =>
        {
            var board = new TemplateRuleWorkbenchControl();
            var window = new Window { Width = 840, Height = 650, Content = board };
            try
            {
                window.Show(); FlushLayout(window);
                var input = (TextBox)board.FindName("Input"); input.Text = "中文输入保留，不发送。"; input.Focus(); FlushLayout(window);
                PumpRuleTask(board.RunAsync(() => throw new IOException("本地草稿暂时不可写，请检查隔离目录。")));
                var notice = (TextBlock)board.FindName("Notice");
                Assert.Equal("Error", notice.Tag);
                Assert.Equal(((SolidColorBrush)board.FindResource("DangerBrush")).Color, ((SolidColorBrush)notice.Foreground).Color);
                Assert.Equal("中文输入保留，不发送。", input.Text); Assert.True(input.IsKeyboardFocusWithin);
                var composer = (Border)board.FindName("Composer");
                Assert.True(BoundsInWindow(composer, window).Bottom < window.ActualHeight);
                PumpRuleTask(board.RunAsync(() => Task.CompletedTask)); Assert.Null(notice.Tag);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RuleWorkbenchClearsStaleOverviewWhenDraftModelChanges()
    {
        RunInSta(() =>
        {
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            using var shell = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var owner = shell.TemplateWorkspacePageViewModel.Coordinator;
            using var first = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            using var second = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            var property = typeof(TemplateWorkspaceViewModel).GetProperty(nameof(TemplateWorkspaceViewModel.RuleWorkbench))!;
            property.SetValue(owner, first);
            var board = new TemplateRuleWorkbenchControl { DataContext = owner };
            var overview = (TemplateRuleOverviewControl)board.FindName("Overview");
            ((TextBlock)board.FindName("Notice")).Tag = "Error";
            overview.DataContext = first; overview.Visibility = Visibility.Visible;
            ((Grid)board.FindName("EditorView")).Visibility = Visibility.Collapsed;
            property.SetValue(owner, second);
            typeof(TemplateWorkspaceViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, [nameof(TemplateWorkspaceViewModel.RuleWorkbench)]);
            Assert.Equal(Visibility.Collapsed, overview.Visibility);
            Assert.Null(((TextBlock)board.FindName("Notice")).Tag);
            Assert.Null(overview.DataContext);
            Assert.Equal(Visibility.Visible, ((Grid)board.FindName("EditorView")).Visibility);
        });
    }

    [Fact]
    public void RuleProcessingAndFailureKeepInputAndDraftWithoutAutomaticRetry()
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            var interpreter = new PendingRuleInterpreter();
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes, interpreter);
            PumpRuleTask(model.RefreshAsync());
            var before = model.Draft!.RuleDigest;
            var board = new TemplateRuleWorkbenchControl();
            var input = (TextBox)board.FindName("Input"); input.Text = "保留中文输入，不盲目重发";
            var running = model.InterpretAsync(input.Text, []);
            PumpRuleTask(interpreter.Started.Task);
            Assert.False(model.CanInterpret); Assert.Contains("正在解释", model.Status);
            interpreter.Result.SetException(new InvalidOperationException("AI_RESULT_UNKNOWN：查询本次请求状态，不要重复发送"));
            PumpRuleTask(board.RunAsync(async () => { await running; }));
            Assert.True(model.CanInterpret); Assert.Equal(1, interpreter.Calls);
            Assert.Equal(before, model.Draft.RuleDigest);
            Assert.Equal("保留中文输入，不盲目重发", input.Text);
            Assert.Contains("不要重复发送", ((TextBlock)board.FindName("Notice")).Text);
            Assert.Single(model.Pages[0].Candidates);
            Assert.Empty(model.Pages[0].Frames); // Failed interpretation cannot register a candidate.
        });
    }

    private sealed class PendingRuleInterpreter : ITemplateRuleInterpreter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TemplateRuleInterpretationResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<TemplateRuleInterpretationResult> InterpretAsync(TemplateDraftSession session, TemplateRuleInterpretationCommand command, CancellationToken cancellationToken)
        { Calls++; Started.SetResult(); return Result.Task; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateRuleCanBeTypedWithoutReferencePhotosAndKeepsTheFocusedRowOnSave(bool twoCandidates)
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture(twoCandidates); using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            var page = model.Pages[0];
            Assert.Empty(page.Frames);
            Assert.Equal(twoCandidates ? 2 : 1, page.EditableFrames.Count);
            var candidate = page.EditableFrames.Last();
            Assert.True(candidate.IsCandidate);
            Assert.Equal(twoCandidates ? "框2" : "框1", candidate.Label);
            var card = new TemplatePageRuleCard { DataContext = page };
            var window = new Window { Width = 820, Height = 650, Content = card };
            try
            {
                window.Show(); FlushLayout(window);
                var box = Descendants<TextBox>(card).Last();
                box.Focus(); box.Text = "假模特正面完整图；不使用背面照片。";
                PumpRuleTask(model.FlushAsync()); FlushLayout(window);
                Assert.Same(candidate, Assert.Single(page.Frames));
                Assert.Same(candidate, page.EditableFrames.Last());
                Assert.False(candidate.IsCandidate);
                Assert.DoesNotContain("待设置", candidate.Label, StringComparison.Ordinal);
                Assert.Same(box, Descendants<TextBox>(card).Last());
                Assert.True(box.IsKeyboardFocusWithin);
                Assert.Equal("假模特正面完整图；不使用背面照片。", box.Text);
                Assert.Empty(model.Pages[1].Frames);
                var revision = fixture.Store.GetRevisionAsync(fixture.Session.DraftRevisionId, default).GetAwaiter().GetResult()!;
                var binding = Assert.Single(revision.FrameBindings);
                Assert.DoesNotContain(binding.SelectionRequirement.Criteria, criterion => criterion.Key == "asset_id");
                using var reopened = new TemplateRuleWorkbenchViewModel(fixture.Workspace,
                    new TemplateCalibrationService(fixture.Store, fixture.Store), fixture.Session, writes);
                PumpRuleTask(reopened.RefreshAsync());
                Assert.Equal(box.Text, Assert.Single(reopened.Pages[0].Frames).Description);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CandidateRowsRejectKnownCoveredLayersAndDoNotRegisterEmptyInputs()
    {
        RunInSta(() =>
        {
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            var page = model.Pages[0];
            var view = fixture.Calibration.LoadAsync(fixture.Session, page.PageId, default).GetAwaiter().GetResult();
            var target = Assert.Single(view.Frames);
            page.SetFrames(view with { Frames = [target with { HasCompositeContribution = false }] }, model.Draft!, () => { });
            Assert.Empty(page.Candidates); Assert.Empty(page.EditableFrames);
            page.SetFrames(view with { Frames = [target with { HasCompositeContribution = true }] }, model.Draft!, () => { });
            Assert.Single(page.EditableFrames); Assert.Empty(page.Frames);
            PumpRuleTask(model.FlushAsync());
            Assert.Empty(fixture.Store.GetRevisionAsync(fixture.Session.DraftRevisionId, default).GetAwaiter().GetResult()!.FrameBindings);
        });
    }

    [Fact]
    public void InlineMentionsInsertAtCaretKeepMultipleTargetsAndForgetDeletedMentions()
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            var interpreter = new ClarifyingRuleInterpreter(fixture.Workspace, fixture.Session);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes, interpreter);
            PumpRuleTask(model.RefreshAsync());
            using var shell = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var owner = shell.TemplateWorkspacePageViewModel.Coordinator;
            typeof(TemplateWorkspaceViewModel).GetProperty(nameof(TemplateWorkspaceViewModel.RuleWorkbench))!.SetValue(owner, model);
            var board = new TemplateRuleWorkbenchControl { DataContext = owner };
            var window = new Window { Width = 1100, Height = 760, Content = board };
            try
            {
                window.Show(); FlushLayout(window);
                var input = (TextBox)board.FindName("Input");
                var picker = (ListBox)board.FindName("ReferencePicker");
                input.Text = "先用正面，随后用反面"; input.CaretIndex = 2;
                typeof(TemplateRuleWorkbenchControl).GetMethod("References_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(board, [board, new RoutedEventArgs()]);
                picker.SelectedItem = picker.Items.Cast<TemplateRuleReferenceChoice>().Single(choice => choice.Label == "@1");
                Assert.Equal("先用@1 正面，随后用反面", input.Text);
                input.CaretIndex = input.Text.IndexOf("反面", StringComparison.Ordinal);
                typeof(TemplateRuleWorkbenchControl).GetMethod("References_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(board, [board, new RoutedEventArgs()]);
                picker.SelectedItem = picker.Items.Cast<TemplateRuleReferenceChoice>().Single(choice => choice.Label == "@2");
                Assert.Equal("先用@1 正面，随后用@2 反面", input.Text);
                Assert.True(input.IsKeyboardFocusWithin);
                Assert.Null(board.FindName("ReferenceTags"));
                PumpRuleTask((Task)typeof(TemplateRuleWorkbenchControl).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(board, null)!);
                Assert.Collection(interpreter.Commands.Single().References,
                    reference => Assert.Equal("page_a", reference.PageId),
                    reference => Assert.Equal("page_b", reference.PageId));
                input.Text = input.Text.Replace("@2 ", "", StringComparison.Ordinal);
                PumpRuleTask((Task)typeof(TemplateRuleWorkbenchControl).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(board, null)!);
                Assert.Equal("page_a", Assert.Single(interpreter.Commands[1].References).PageId);
                Assert.Equal("先用@1 正面，随后用反面", input.Text); // Clarification retains the prompt.
                PumpRuleTask(model.RemovePageAsync(model.Pages[0]));
                PumpRuleTask((Task)typeof(TemplateRuleWorkbenchControl).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(board, null)!);
                Assert.Equal(2, interpreter.Commands.Count);
                Assert.Contains("引用已失效", ((TextBlock)board.FindName("Notice")).Text);
            }
            finally { window.Close(); }
        });
    }

    private sealed class ClarifyingRuleInterpreter(ITemplateRuleEditingService editor, TemplateDraftSession session) : ITemplateRuleInterpreter
    {
        public List<TemplateRuleInterpretationCommand> Commands { get; } = [];
        public async Task<TemplateRuleInterpretationResult> InterpretAsync(TemplateDraftSession ignored,
            TemplateRuleInterpretationCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return new("", null, "请补充条件。", await editor.GetRulesAsync(session, cancellationToken), true);
        }
    }

    [Fact]
    public void UnfilledFrameMentionsCanBeSentAndPopulateTheSameSidebarThroughTheInterpreter()
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture(pixelEvidence: true); using var writes = new SemaphoreSlim(1);
            PumpRuleTask(fixture.Store.GrantAsync(AiConsentScopes.AnalysisCopy, AiConsentPolicy.Version, default));
            var provider = new FirstRuleUiProvider();
            var interpreter = new TemplateRuleInterpretationService(fixture.Workspace, provider, fixture.Store, fixture.Store,
                new AtomicFileStore(new LocalAppPathPolicy(fixture.Root)), TimeProvider.System, fixture.Store);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes, interpreter);
            PumpRuleTask(model.RefreshAsync());
            Assert.All(model.Pages, page => Assert.Empty(page.Frames));
            Assert.Equal(2, model.ReferenceChoices.Count(choice => choice.FrameId is not null && choice.IsActive));
            var firstRow = model.Pages[0].EditableFrames[0];
            using var shell = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var owner = shell.TemplateWorkspacePageViewModel.Coordinator;
            typeof(TemplateWorkspaceViewModel).GetProperty(nameof(TemplateWorkspaceViewModel.RuleWorkbench))!.SetValue(owner, model);
            var board = new TemplateRuleWorkbenchControl { DataContext = owner };
            var input = (TextBox)board.FindName("Input");
            input.Text = "@1 的框1 用假模特正面图，@2 的框1 用同色背面图";
            PumpRuleTask((Task)typeof(TemplateRuleWorkbenchControl).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(board, null)!);
            Assert.Empty(input.Text); Assert.Equal(1, provider.Calls);
            Assert.Equal("假模特正面图", Assert.Single(model.Pages[0].Frames).Description);
            Assert.Equal("同色背面图", Assert.Single(model.Pages[1].Frames).Description);
            Assert.Same(firstRow, model.Pages[0].EditableFrames[0]);
            Assert.Equal("框1", firstRow.Label); Assert.False(firstRow.IsCandidate);
        });
    }

    private sealed class FirstRuleUiProvider : IAiServicePort
    {
        public int Calls { get; private set; }
        public Task<TemplateRuleAiExecution> InterpretTemplateRulesAsync(TemplateRuleAiRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TemplateRuleAiExecution(AiProviderOutcome.Completed,
                new(TemplateRuleAiProtocol.ResultVersion, [.. request.Frames.Select(frame => new TemplateFrameRuleEdit(frame.FrameId,
                    frame.PageId == "page_a" ? "假模特正面图" : "同色背面图"))], [], "已填写选图规则", null), null, null));
        }
        public Task<AiAnalysisExecutionResult> AnalyzeAsync(Desktop.Contracts.Ai.AiAnalysisRequestV1 request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void CalibratedMaskedBindingRemainsEditableWithoutPromotingUnsupportedCandidates()
    {
        RunInSta(() =>
        {
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            var page = model.Pages[0];
            PumpRuleTask(model.RegisterAsync(page, Assert.Single(page.Candidates), "领口特写"));
            var view = fixture.Calibration.LoadAsync(fixture.Session, page.PageId, default).GetAwaiter().GetResult();
            var target = Assert.Single(view.Frames) with { HasMask = true, IsPreviewSupported = false, PreviewBounds = new(0, 1700, 200, 200) };
            page.SetFrames(view with { Frames = [target] }, model.Draft!, () => { });
            Assert.Equal(target.PersistedFrameId, Assert.Single(page.Frames).FrameId);
            Assert.Empty(page.Candidates);
            page.SetFrames(view with { Frames = [target with { HasPersistedBinding = false, PersistedFrameId = null }] }, model.Draft!, () => { });
            Assert.Empty(page.Frames); Assert.Empty(page.Candidates);
            page.SetFrames(view with { Frames = [target with { PreviewBounds = null }] }, model.Draft!, () => { });
            Assert.Empty(page.Frames);
        });
    }

    [Fact]
    public void RuleRelationsPreserveRepeatedPsdConditionalCompositionAndReopen()
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture();
            using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            foreach (var page in model.Pages.ToArray()) PumpRuleTask(model.RegisterAsync(page, Assert.Single(page.Candidates), "真人模特正面全身图"));
            Assert.Single(model.Relations);
            Assert.Equal(2, model.Relations[0].Nodes.Count);
            var pageA = model.Pages[0];
            TemplatePageRecipeView Use(string id) => new(id, pageA.PageId, TemplateRuleRepeatOption.Once, TemplateRuleColorRangeOption.None,
                [new("main", TemplateRuleColorSourceOption.Primary)], [new(pageA.Frames[0].FrameId, "main")]);
            PumpRuleTask(model.ApplyBranchesAsync([
                new("one", new TemplateRuleBranchView("one", new(1, 1, TemplateRuleParityOption.Any), [Use("first"), Use("second")])),
                new("many", new TemplateRuleBranchView("many", new(2, null, TemplateRuleParityOption.Any), [
                    new("repeat", pageA.PageId, TemplateRuleRepeatOption.ColorChunks, TemplateRuleColorRangeOption.Secondary,
                        [new("secondary", TemplateRuleColorSourceOption.Allocated)], [new(pageA.Frames[0].FrameId, "secondary")])]))]));
            Assert.Equal(2, model.Relations.Count);
            Assert.All(model.Relations[0].Nodes, node => Assert.Equal(pageA.PageId, node.PageId));
            Assert.Contains("重复", model.Relations[1].Nodes[0].Usage);
            Assert.Contains("副色", model.Relations[1].Nodes[0].Usage);
            Assert.Contains("第 2 步", pageA.Purpose);
            pageA.Frames[0].Description = "真人模特上身背面图，双手自然下垂";
            PumpRuleTask(model.FlushAsync());
            Assert.All(model.Relations[0].Nodes, node => Assert.Equal(pageA.Frames[0].Description, node.Frames[0].Description));
            using var reopened = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(reopened.RefreshAsync());
            Assert.Equal(model.Draft!.RuleDigest, reopened.Draft!.RuleDigest);
            Assert.Equal(2, reopened.Relations[0].Nodes.Count);
            Assert.Equal(pageA.Frames[0].Description, reopened.Pages[0].Frames[0].Description);
            var graph = new TemplateRuleOverviewControl(); graph.ShowModel(reopened);
            var window = new Window { Width = 580, Height = 720, Content = graph };
            try
            {
                window.Show(); FlushLayout(window);
                Assert.Equal(2, ((ItemsControl)graph.FindName("Groups")).Items.Count);
                Assert.True(((Button)graph.FindName("Confirm")).IsEnabled);
                Assert.Contains(Descendants<Expander>(graph), expander => expander.IsExpanded);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RuleTypingRetainsControlSelectionFocusAndDoesNotScrollOnEachCharacter()
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var fixture = new RuleUiFixture(); using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            var page = model.Pages[0]; PumpRuleTask(model.RegisterAsync(page, Assert.Single(page.Candidates), "正面图"));
            var card = new TemplatePageRuleCard { DataContext = page };
            var window = new Window { Width = 550, Height = 700, Content = card };
            try
            {
                window.Show(); FlushLayout(window);
                var box = Assert.Single(Descendants<TextBox>(card)); box.Focus(); FlushLayout(window);
                var frame = page.Frames[0]; Assert.Same(frame, page.SelectedFrame);
                var scroll = (ScrollViewer)card.FindName("PreviewScroll"); var offset = scroll.VerticalOffset;
                Assert.True(offset > 0);
                box.Text = "中文逐框规则输入"; PumpRuleTask(model.FlushAsync()); FlushLayout(window);
                Assert.Same(frame, page.Frames[0]);
                Assert.Same(box, Assert.Single(Descendants<TextBox>(card)));
                Assert.True(box.IsKeyboardFocusWithin); Assert.Equal(offset, scroll.VerticalOffset);
                Assert.Equal("中文逐框规则输入", model.Draft!.Frames.Single(item => item.FrameId == frame.FrameId).Description);
            }
            finally { window.Close(); }
        });
    }
    [Fact]
    public void RuleSidebarContainsOnlyPurposeAndRulesWithoutPromotingCandidates()
    {
        RunInSta(() =>
        {
            using var fixture = new RuleUiFixture();
            using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            var page = model.Pages[0];
            var target = Assert.Single(page.Candidates);
            var card = new TemplatePageRuleCard { DataContext = page };
            var window = new Window { Width = 700, Height = 700, Content = card };
            try
            {
                window.Show(); FlushLayout(window);
                Assert.Empty(page.Frames); // Structural smart-object candidates are not product evidence.
                Assert.NotNull(target);
                Assert.Empty(Descendants<ComboBox>(card));
                Assert.Empty(Descendants<Expander>(card));
                Assert.Contains(Descendants<TextBlock>(card), text => text.Text == "本页用途");
                Assert.Contains(Descendants<TextBlock>(card), text => text.Text == "逐框选图规则");
                Assert.DoesNotContain(Descendants<Button>(card), button => Equals(button.Content, "设置"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RuleWorkbenchRefreshRebindsPageTypeAfterCalibrationChanges()
    {
        RunInSta(() =>
        {
            using var fixture = new RuleUiFixture();
            using var writes = new SemaphoreSlim(1);
            using var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);

            PumpRuleTask(model.RefreshAsync());
            Assert.False(model.Pages[0].IsSizeTable);

            PumpRuleTask(fixture.Workspace.ConfigurePageAsync(
                fixture.Session,
                "page_a",
                "尺码页",
                TemplatePageContentTypeOption.DynamicSizeTable,
                TemplateGenerationModeOption.OncePerProduct,
                null,
                enabled: true,
                CancellationToken.None));
            PumpRuleTask(model.RefreshAsync(flush: false));

            Assert.True(model.Pages[0].IsSizeTable);
            Assert.Equal("尺码页", model.Pages[0].Name);
            Assert.Empty(model.Pages[0].Frames);
        });
    }

    [Fact]
    public void RuleWorkbenchDisposeIsIdempotentWithPendingDebounce()
    {
        RunInSta(() =>
        {
            using var fixture = new RuleUiFixture();
            using var writes = new SemaphoreSlim(1);
            var model = new TemplateRuleWorkbenchViewModel(fixture.Workspace, fixture.Calibration, fixture.Session, writes);
            PumpRuleTask(model.RefreshAsync());
            PumpRuleTask(model.RegisterAsync(model.Pages[0], Assert.Single(model.Pages[0].Candidates), "正面"));

            model.Pages[0].Frames[0].Description = "待保存规则";
            model.Dispose();
            model.Dispose();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuleImportDialogCancelsWithoutAcceptingPendingFiles(bool withPendingFile)
    {
        RunInSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"tpl02-import-{Guid.NewGuid():N}.psd");
            var bytes = "selection-only test fixture, not a render sample"u8.ToArray();
            File.WriteAllBytes(path, bytes);
            var dialog = new TemplateRuleImportDialog();
            try
            {
                if (withPendingFile) dialog.AddPaths([path]);
                var cancel = Assert.Single(Descendants<Button>((DependencyObject)dialog.Content), button => Equals(button.Content, "取消"));
                Assert.True(cancel.IsCancel);
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => InvokeButton(cancel));
                Assert.False(dialog.ShowDialog());
                Assert.Equal(withPendingFile ? 1 : 0, dialog.Paths.Count);
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }
            finally { dialog.Close(); File.Delete(path); }
        });
    }

    [Fact]
    public void RuleImportDialogEnablesConfirmationOnlyForNonemptySelection()
    {
        RunInSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"tpl02-import-{Guid.NewGuid():N}.psd");
            File.WriteAllText(path, "selection-only test fixture, not a render sample");
            var dialog = new TemplateRuleImportDialog();
            try
            {
                dialog.Show(); FlushLayout(dialog);
                var confirm = Assert.Single(Descendants<Button>(dialog), button => Equals(button.Content, "添加到模板工作台"));
                Assert.False(confirm.IsEnabled);
                dialog.AddPaths([path + ".missing"]);
                Assert.Empty(dialog.Paths); Assert.False(confirm.IsEnabled);
                dialog.AddPaths([path, path]);
                Assert.Single(dialog.Paths); Assert.True(confirm.IsEnabled);
                var list = Assert.Single(Descendants<ListBox>(dialog));
                list.SelectedIndex = 0;
                list.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(dialog), 0, System.Windows.Input.Key.Delete)
                    { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                Assert.Empty(dialog.Paths); Assert.False(confirm.IsEnabled);
                Assert.True(File.Exists(path));
            }
            finally { dialog.Close(); File.Delete(path); }
        });
    }

    [Fact]
    public void RuleImportDialogExplainsFolderActionsAndAcceptsDropPaths()
    {
        RunInSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "tpl02-drop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var psd = Path.Combine(root, "drop.psd"); File.WriteAllText(psd, "drop fixture");
            var dialog = new TemplateRuleImportDialog();
            try
            {
                dialog.Show(); FlushLayout(dialog);
                Assert.NotNull(Descendants<Button>(dialog).SingleOrDefault(button => Equals(button.Content, "选择 PSD 文件")));
                Assert.NotNull(Descendants<Button>(dialog).SingleOrDefault(button => Equals(button.Content, "选择模板文件夹")));
                var recursive = Assert.Single(Descendants<Button>(dialog), button => Equals(button.Content, "扫描子文件夹"));
                Assert.Contains("所有子目录", recursive.ToolTip?.ToString());
                Assert.True(dialog.AllowDrop);
                // AddPaths is the same normalization used by the dialog's Drop
                // handler; exercise the accepted dropped-path payload without
                // relying on WPF's internal DragEventArgs constructor.
                dialog.AddPaths([psd]);
                Assert.Single(dialog.Paths);
            }
            finally { dialog.Close(); File.Delete(psd); Directory.Delete(root); }
        });
    }

    [Theory]
    [InlineData(1600, 920)]
    [InlineData(1024, 720)]
    public void RuleWorkbenchUsesRealRevisionBindingsAndIndependentCardsWithFloatingComposer(double width, double height)
    {
        RunInSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            using var fixture = new RuleUiFixture();
            using var shell = new MainWindowViewModel(new AppLaunchOptions(true, null), templateWorkspace: fixture.Workspace, templateCalibration: fixture.Calibration);
            var coordinator = shell.TemplateWorkspacePageViewModel.Coordinator;
            var open = typeof(TemplateWorkspaceViewModel).GetMethod("OpenTemplateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            PumpRuleTask((Task)open.Invoke(coordinator, [new TemplateLibraryCardViewModel("group", "UI fixture", "2", "", "草稿", false)])!);
            var model = Assert.IsType<TemplateRuleWorkbenchViewModel>(coordinator.RuleWorkbench);
            Assert.Equal(2, model.Pages.Count);
            foreach (var page in model.Pages.ToArray()) PumpRuleTask(model.RegisterAsync(page, Assert.Single(page.Candidates), "正面"));
            Assert.NotEqual(model.Pages[0].Frames[0].FrameId, model.Pages[1].Frames[0].FrameId);
            model.Pages[0].Frames[0].Description = "左卡片正面";
            model.Pages[1].Frames[0].Description = "右卡片反面";
            PumpRuleTask(model.FlushAsync());
            var saved = fixture.Store.GetRevisionAsync("revision", default).GetAwaiter().GetResult()!;
            Assert.Equal("左卡片正面", saved.FrameBindings.Single(frame => frame.PageId == "page_a").SelectionRequirement.Description);
            Assert.Equal("右卡片反面", saved.FrameBindings.Single(frame => frame.PageId == "page_b").SelectionRequirement.Description);

            var board = new TemplateRuleWorkbenchControl { DataContext = coordinator };
            var window = new Window { Width = width, Height = height, Content = board, WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                window.Show(); FlushLayout(window);
                var composer = (Border)board.FindName("Composer");
                Assert.Null(board.FindName("GroupPanel"));
                Assert.DoesNotContain(Descendants<Button>(composer), button => Equals(button.Content, "取消"));
                var scroll = (ScrollViewer)board.FindName("WorkspaceScroll");
                Assert.Equal(16, composer.CornerRadius.BottomLeft);
                Assert.Equal(0, Grid.GetRow(composer));
                Assert.True(BoundsInWindow(composer, window).Top < BoundsInWindow(scroll, window).Bottom);
                Assert.True(((Border)board.FindName("EndClearance")).Height > composer.ActualHeight);
                var cards = Descendants<TemplatePageRuleCard>(board).ToArray();
                Assert.Equal(2, cards.Length);
                Assert.All(cards, card => Assert.True(((TemplateRulePreview)card.FindName("Preview")).HasComposite));
                if (width >= 1500) Assert.Equal(BoundsInWindow(cards[0], window).Top, BoundsInWindow(cards[1], window).Top);
                var preview = (TemplateRulePreview)cards[0].FindName("Preview");
                Assert.True(preview.ActualHeight > 480);
                Assert.InRange(preview.ActualHeight / preview.ActualWidth, 9.99, 10.01);
                preview.FitWhole = true; FlushLayout(window); Assert.True(preview.ActualHeight <= 480.1);
                preview.FitWhole = false; FlushLayout(window);
                var frame = model.Pages[0].Frames[0]; model.Locate("page_a", frame.FrameId); FlushLayout(window);
                Assert.Same(frame, model.Pages[0].SelectedFrame);
                Assert.True(((ScrollViewer)cards[0].FindName("PreviewScroll")).VerticalOffset > 0);
                var output = Path.GetFullPath(Path.Combine("tmp", "tpl02-c-20260928")); Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(output, $"workbench-fixture-{width}.png")); encoder.Save(image);
            }
            finally { window.Close(); }
            PumpRuleTask(model.StopReplacingAsync(model.Pages[0], model.Pages[0].Frames[0]));
            Assert.Empty(model.Pages[0].Frames); PumpRuleTask(model.UndoAsync()); Assert.Single(model.Pages[0].Frames);
            PumpRuleTask(model.RemovePageAsync(model.Pages[0])); Assert.Single(model.Pages);
            PumpRuleTask(model.UndoAsync()); Assert.Equal(2, model.Pages.Count);
            Assert.Equal("@1", model.Pages[0].Label); Assert.Equal("@2", model.Pages[1].Label);
        });
    }

    [Fact]
    public void RulePreviewNeverDrawsInventedPsdWhenCompositeIsMissing()
    {
        RunInSta(() =>
        {
            using var fixture = new RuleUiFixture();
            var page = fixture.Workspace.GetDraftAsync(fixture.Session, default).GetAwaiter().GetResult().Pages[0];
            var preview = new TemplateRulePreview { Page = new(page with { PreviewSourceReference = null }, "@1") };
            var window = new Window { Width = 500, Height = 400, Content = preview };
            try { window.Show(); FlushLayout(window); Assert.False(preview.HasComposite); }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) yield return found;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void PumpRuleTask(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private sealed class RuleUiFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tpl02-ui-" + Guid.NewGuid().ToString("N"));
        public SqliteDesktopStore Store { get; }
        public TemplateWorkspaceService Workspace { get; }
        public TemplateCalibrationService Calibration { get; }
        public TemplateDraftSession Session { get; } = new("group", "UI fixture", "revision", 1, new HashSet<string>());
        public string Root => _root;
        public RuleUiFixture(bool twoCandidates = false, bool pixelEvidence = false)
        {
            Store = new(new(_root)); Store.InitializeAsync().GetAwaiter().GetResult();
            var paths = new LocalAppPathPolicy(_root); var files = new AtomicFileStore(paths);
            Workspace = new(Store, Store, null!, TimeProvider.System, paths: paths, files: files);
            Calibration = new(Store, Store, paths: paths);
            var hash = new string('a', 64); var signature = "sha256:" + new string('b', 64);
            var layer = new PsdLayerInspection([0], 7, "existing target", "smartobject", false, true, true, false,
                255, 255, "norm", new(0, 1700, 200, 1900), false, false, null,
                new("smart", "embedded.png", "data", "png", "png", 4, false, [0, 1700, 200, 1700, 200, 1900, 0, 1900]));
            if (pixelEvidence) layer = layer with { PixelEvidence = new(true, 40000, 40000, new(0, 1700, 200, 1900)) };
            var second = layer with { Path = [1], LayerId = 8, Bounds = new(0, 1400, 200, 1600),
                SmartObject = layer.SmartObject! with { TransformBox = [0, 1400, 200, 1400, 200, 1600, 0, 1600] } };
            var inspection = new PsdInspectionDocument(hash, 4, new(200, 2000, 8, "RGB"), twoCandidates ? 2 : 1, twoCandidates ? 2 : 1, 0, 0, 0, signature,
                new(1, 1, 2), 1024, new(200, 2000, "RGB", hash, 4), twoCandidates ? [layer, second] : [layer]);
            var path = "templates/groups/group/drafts/revision/staging/resource/template.psd";
            Store.SaveGroupAsync(TemplateGroup.Create("group", "UI fixture", DateTimeOffset.UnixEpoch), default).GetAwaiter().GetResult();
            Store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch), default).GetAwaiter().GetResult();
            Store.SaveImportedAsync("import", new("resource", "group", "revision", path, 4, hash, signature, inspection, DateTimeOffset.UnixEpoch), default).GetAwaiter().GetResult();
            var draft = Store.GetRevisionAsync("revision", default).GetAwaiter().GetResult()!;
            foreach (var id in new[] { "page_a", "page_b" }) draft = draft.AddPage(TemplatePage.Create(id, "revision", draft.Pages.Length, "布局测试图（非真实PSD验收）", TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true, "resource"));
            Store.SaveDraftRevisionAsync(draft, default).GetAwaiter().GetResult();
            var preview = paths.ResolveOwnedPath(TemplatePageImportService.GetPreviewRelativePath(path)); Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
            var bitmap = BitmapSource.Create(200, 2000, 96, 96, PixelFormats.Bgr24, null, Enumerable.Repeat((byte)215, 200 * 2000 * 3).ToArray(), 600);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(preview); encoder.Save(stream);
        }
        public void Dispose()
        {
            Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Path.GetDirectoryName(Path.GetFullPath(_root)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())) Directory.Delete(_root, recursive: true);
        }
    }
}
