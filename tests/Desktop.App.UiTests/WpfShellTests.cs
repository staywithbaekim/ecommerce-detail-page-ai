using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Desktop.Application.Templates;
using Xunit.Abstractions;

namespace Desktop.App.UiTests;

public sealed class WpfShellTests
{
    private readonly ITestOutputHelper _output;

    public WpfShellTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string[] ExpectedAutomationIds =
    [
        "DesktopShellRoot",
        "PrimaryNavigation",
        "WorkbenchNavigation",
        "TemplatesNavigation",
        "TasksNavigation",
        "SettingsNavigation",
        "PageTitle",
        "PageContent",
        "ActiveTaskList",
        "RecentTaskList",
        "ChineseImeFixtureInput",
        "ActiveTaskBar"
    ];

    private static readonly string[] BoundedElementIds =
        ["PrimaryNavigation", "PageTitle", "PageContent", "ActiveTaskBar"];

    private static readonly string[] ExpectedBatchAutomationIds =
    [
        "BatchTemplateSelect",
        "BatchInputDirectoryField",
        "ChooseBatchInputDirectoryButton",
        "BatchRecognitionStatus",
        "BatchOutputDirectoryField",
        "ChooseBatchOutputDirectoryButton",
        "BatchAutomaticSelectionOption",
        "BatchManualSelectionOption",
        "BatchPageSelectionSummary",
        "OpenBatchPageSelectionButton",
        "BatchEditablePsdToggle",
        "BatchInputStatus",
        "CancelBatchInputButton",
        "ScanBatchInputButton"
    ];

    [Fact]
    public void ShellSupportsWideNarrowUiaFocusAndChineseText()
    {
        RunInSta(() =>
        {
            var application = new Desktop.App.App();
            application.InitializeComponent();
            var workflow = new RecordingBatchInputWorkflow();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                folderPicker: new QueueFolderPicker(
                    @"C:\商品 素材\秋季上新",
                    @"D:\生产输出\秋季上新"),
                batchInputWorkflow: workflow);
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                var comboStyle = Assert.IsType<Style>(application.FindResource("FieldComboBoxStyle"));
                Assert.Contains(
                    comboStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate);

                var textBoxStyle = Assert.IsType<Style>(application.FindResource("FieldTextBoxStyle"));
                Assert.Contains(
                    textBoxStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == FrameworkElement.MinHeightProperty
                        && Equals(setter.Value, 36d));
                Assert.DoesNotContain(
                    textBoxStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == FrameworkElement.HeightProperty);

                VerifySize(window, 1440, 900);
                VerifySize(window, 1100, 760);

                var elements = ExpectedAutomationIds.ToDictionary(
                    id => id,
                    id => FindByAutomationId<FrameworkElement>(window, id),
                    StringComparer.Ordinal);
                Assert.All(elements.Values, element => Assert.NotNull(element));

                var navigation = Assert.IsType<ListBox>(elements["PrimaryNavigation"]);
                Assert.Equal(4, navigation.Items.Count);
                Assert.Equal(0, navigation.SelectedIndex);
                Assert.Equal("workbench", viewModel.SelectedNavigationItem.Key);
                Assert.Equal(
                    ["工作台", "模板", "任务", "设置"],
                    viewModel.NavigationItems.Select(item => item.Label));
                for (var index = 0; index < navigation.Items.Count; index++)
                {
                    var item = Assert.IsType<ListBoxItem>(navigation.ItemContainerGenerator.ContainerFromIndex(index));
                    Assert.Equal(40, item.ActualHeight);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(item)));
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(item)));
                }

                var firstItem = Assert.IsType<ListBoxItem>(navigation.ItemContainerGenerator.ContainerFromIndex(0));
                var secondItem = Assert.IsType<ListBoxItem>(navigation.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.True(firstItem.Focus());
                Assert.True(firstItem.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(secondItem.IsKeyboardFocusWithin);

                var input = Assert.IsType<TextBox>(elements["ChineseImeFixtureInput"]);
                Assert.True(InputMethod.GetIsInputMethodEnabled(input));
                Assert.True(input.Focus());
                input.Text = "中文输入验证";
                Assert.Equal("中文输入验证", input.Text);

                navigation.SelectedIndex = 3;
                FlushLayout(window);
                Assert.Equal("设置", viewModel.PageTitle);
                Assert.Equal(5, viewModel.PageRows.Count);

                var dpi = VisualTreeHelper.GetDpi(window);
                Assert.True(dpi.DpiScaleX > 0);
                Assert.True(dpi.DpiScaleY > 0);
                _output.WriteLine(
                    $"WPF DPI: {dpi.PixelsPerInchX:F0}x{dpi.PixelsPerInchY:F0}; " +
                    $"scale: {dpi.DpiScaleX:P0} x {dpi.DpiScaleY:P0}.");

                var expectedDpiText = Environment.GetEnvironmentVariable("DESKTOP_EXPECTED_DPI_PERCENT");
                if (!string.IsNullOrWhiteSpace(expectedDpiText))
                {
                    Assert.True(
                        int.TryParse(expectedDpiText, out var expectedDpiPercent),
                        "DESKTOP_EXPECTED_DPI_PERCENT must be an integer percentage.");
                    var expectedScale = expectedDpiPercent / 100d;
                    Assert.Equal(expectedScale, dpi.DpiScaleX, precision: 2);
                    Assert.Equal(expectedScale, dpi.DpiScaleY, precision: 2);
                }

                var frameList = FindByAutomationId<ListBox>(window, "CalibrationFrameList");
                ApplyCalibrationDraft(viewModel, CreateCalibrationDraft("revision_before"));
                FlushLayout(window);
                Assert.Equal("frame_257", viewModel.CalibrationFrameIdInput);
                Assert.Equal("frame_257", frameList.SelectedValue);

                ApplyCalibrationDraft(viewModel, CreateCalibrationDraft("revision_after"));
                FlushLayout(window);
                Assert.Equal("frame_257", viewModel.CalibrationFrameIdInput);
                Assert.Equal("frame_257", frameList.SelectedValue);

                var showDialog = typeof(MainWindowViewModel).GetMethod(
                    "ShowTemplateDialogAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Template dialog entry point not found.");
                var dialogTask = Assert.IsAssignableFrom<Task>(showDialog.Invoke(
                    viewModel,
                    [
                        "放弃当前模板组？",
                        "将删除软件内部草稿。",
                        "用户源 PSD 保持只读。",
                        "放弃模板组",
                        "取消",
                        null,
                        true
                    ]));
                FlushLayout(window);

                Assert.True(viewModel.IsTemplateDialogVisible);
                var overlay = FindByAutomationId<Grid>(window, "TemplateConfirmationDialog");
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                var primary = FindByAutomationId<Button>(window, "TemplateDialogPrimaryButton");
                Assert.Equal("放弃模板组", primary.Content);
                Assert.Equal(application.FindResource("DangerBrush"), primary.Background);

                viewModel.SecondaryTemplateDialogCommand.Execute(null);
                FlushLayout(window);
                Assert.True(dialogTask.IsCompleted);
                Assert.False(viewModel.IsTemplateDialogVisible);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);

                VerifyBatchInputPage(window, viewModel, workflow);
            }
            finally
            {
                window.Close();
                application.Shutdown();
            }
        });
    }

    [Fact]
    public void StartupBoundsKeepNativeTitleBarAndWindowInsideWorkingArea()
    {
        var workArea = new Rect(68, 0, 1427, 833);

        var bounds = WindowBoundsCalculator.Fit(
            workArea,
            new Size(1440, 900),
            new Size(1100, 760));

        Assert.Equal(workArea.Left, bounds.Left);
        Assert.Equal(workArea.Top, bounds.Top);
        Assert.Equal(workArea.Width, bounds.Width);
        Assert.Equal(workArea.Height, bounds.Height);
        Assert.True(bounds.Top >= workArea.Top);
        Assert.True(bounds.Bottom <= workArea.Bottom);
        Assert.True(bounds.Right <= workArea.Right);

        var fullDesignBounds = WindowBoundsCalculator.Fit(
            new Rect(0, 0, 1600, 1000),
            new Size(1440, 900),
            new Size(1100, 760));
        Assert.Equal(new Size(1440, 900), fullDesignBounds.Size);

        var narrowDesignBounds = WindowBoundsCalculator.Fit(
            new Rect(0, 0, 1100, 760),
            new Size(1440, 900),
            new Size(1100, 760));
        Assert.Equal(new Size(1100, 760), narrowDesignBounds.Size);
    }

    private static void VerifyBatchInputPage(
        MainWindow window,
        MainWindowViewModel viewModel,
        RecordingBatchInputWorkflow workflow)
    {
        viewModel.SelectedNavigationItem = viewModel.NavigationItems[0];
        FlushLayout(window);
        var entry = FindByAutomationId<Button>(window, "PrimaryPageAction");
        Assert.True(entry.IsHitTestVisible);
        Assert.True(entry.Focusable);
        viewModel.OpenBatchInputCommand.Execute(null);
        FlushLayout(window);

        var page = FindByAutomationId<Grid>(window, "BatchInputPage");
        Assert.Equal(Visibility.Visible, page.Visibility);
        Assert.Equal(
            "新建批次",
            FindByAutomationId<TextBlock>(window, "BatchInputTitle").Text);

        viewModel.TemplateGroups.Add(new TemplateLibraryCardViewModel(
            "group_batch_ui",
            "通用品类模板",
            "2 个页面 · Committed",
            "今天 10:00",
            "Committed"));
        viewModel.SelectedBatchTemplate = viewModel.TemplateGroups[0];
        FlushLayout(window);
        var templateSelect = FindByAutomationId<ComboBox>(window, "BatchTemplateSelect");
        Assert.Contains(
            EnumerateVisualDescendants<TextBlock>(templateSelect),
            text => string.Equals(text.Text, "通用品类模板", StringComparison.Ordinal));
        Assert.DoesNotContain(
            EnumerateVisualDescendants<TextBlock>(templateSelect),
            text => text.Text.Contains(nameof(TemplateLibraryCardViewModel), StringComparison.Ordinal));
        viewModel.BatchPages.Add(new BatchPageOptionViewModel(
            "page_1",
            "详情页一",
            "普通套图页 · 每款一次",
            true));
        viewModel.BatchPages.Add(new BatchPageOptionViewModel(
            "page_2",
            "详情页二",
            "普通套图页 · 每款一次",
            true));

        Assert.False(viewModel.ScanBatchInputCommand.CanExecute(null));
        viewModel.ChooseBatchInputDirectoryCommand.Execute(null);
        viewModel.ChooseBatchOutputDirectoryCommand.Execute(null);
        FlushLayout(window);
        Assert.Equal(@"C:\商品 素材\秋季上新", viewModel.BatchInputDirectory);
        Assert.Equal(@"D:\生产输出\秋季上新", viewModel.BatchOutputDirectory);
        Assert.True(viewModel.ScanBatchInputCommand.CanExecute(null));

        Assert.All(
            ExpectedBatchAutomationIds,
            id => Assert.NotNull(FindByAutomationId<FrameworkElement>(window, id)));

        viewModel.OpenBatchPageSelectionCommand.Execute(null);
        FlushLayout(window);
        Assert.Equal(
            Visibility.Visible,
            FindByAutomationId<Grid>(window, "BatchPageSelectionDialog").Visibility);
        viewModel.AreAllBatchPagesSelected = false;
        Assert.Equal(0, viewModel.SelectedBatchPageCount);
        Assert.False(viewModel.ScanBatchInputCommand.CanExecute(null));
        viewModel.BatchPages[0].IsSelected = true;
        viewModel.ApplyBatchPageSelectionCommand.Execute(null);
        Assert.True(viewModel.ScanBatchInputCommand.CanExecute(null));

        viewModel.IsBatchManualSelection = true;
        viewModel.BatchIncludeEditablePsd = true;
        workflow.NextStatus = BatchInputUiStatus.ScanBlocked;
        workflow.NextRecognitionText = "目录结构需要整理后重新扫描。";
        workflow.NextStatusMessage =
            "所选目录同时包含根目录图片和款式子文件夹。请整理后重新选择；软件不会猜测图片归属。";
        var scanGate = new TaskCompletionSource();
        workflow.ScanGate = scanGate;
        viewModel.ScanBatchInputCommand.Execute(null);
        FlushLayout(window);
        Assert.True(viewModel.IsBatchScanning);
        Assert.Equal(
            Visibility.Visible,
            FindByAutomationId<Button>(window, "CancelBatchScanButton").Visibility);
        scanGate.SetResult();
        FlushLayout(window);

        var submission = Assert.IsType<BatchInputSubmission>(workflow.Submission);
        Assert.Equal("group_batch_ui", submission.TemplateGroupId);
        Assert.Single(submission.SelectedPageIds);
        Assert.Equal("page_1", submission.SelectedPageIds[0]);
        Assert.Equal(BatchSelectionMode.Manual, submission.SelectionMode);
        Assert.True(submission.IncludeEditablePsd);
        Assert.True(viewModel.IsBatchScanBlocked);
        Assert.Contains("不会猜测图片归属", viewModel.BatchStatusMessage, StringComparison.Ordinal);

        workflow.NextStatusMessage =
            "未找到可用商品图片。请选择直接包含商品图片的单款目录，或包含款式子文件夹的批次目录。";
        viewModel.RetryBatchScanCommand.Execute(null);
        FlushLayout(window);
        Assert.Contains("未找到可用商品图片", viewModel.BatchStatusMessage, StringComparison.Ordinal);

        workflow.NextStatusMessage = "扫描未能安全提交。请重试；如果问题持续，请导出诊断信息。";
        viewModel.RetryBatchScanCommand.Execute(null);
        FlushLayout(window);
        Assert.Contains("扫描未能安全提交", viewModel.BatchStatusMessage, StringComparison.Ordinal);

        workflow.NextStatus = BatchInputUiStatus.ScanReady;
        workflow.NextRecognitionText = "已识别为单款批次 · 1 款";
        workflow.NextStatusMessage = "扫描完成，共识别 1 款、3 张商品图片，可进入后续分析。";
        viewModel.RetryBatchScanCommand.Execute(null);
        FlushLayout(window);
        Assert.True(viewModel.IsBatchScanReady);
        Assert.Equal("已识别为单款批次 · 1 款", viewModel.BatchRecognitionText);
        Assert.Equal("扫描完成", viewModel.BatchPrimaryActionText);
        Assert.False(viewModel.ScanBatchInputCommand.CanExecute(null));
        Assert.False(viewModel.ChooseBatchInputDirectoryCommand.CanExecute(null));
        Assert.False(viewModel.ChooseBatchOutputDirectoryCommand.CanExecute(null));
        Assert.False(viewModel.OpenBatchPageSelectionCommand.CanExecute(null));
        Assert.False(FindByAutomationId<ComboBox>(window, "BatchTemplateSelect").IsEnabled);
        Assert.False(FindByAutomationId<RadioButton>(window, "BatchAutomaticSelectionOption").IsEnabled);
        Assert.False(FindByAutomationId<CheckBox>(window, "BatchEditablePsdToggle").IsEnabled);

        workflow.RestoreProjection = new BatchInputProjection(
            "batch_ready_recovered",
            "group_batch_ui",
            BatchInputUiStatus.ScanReady,
            @"C:\商品 素材\秋季上新",
            @"D:\生产输出\秋季上新",
            ["page_1"],
            BatchSelectionMode.Manual,
            true,
            "已识别为单款批次 · 1 款",
            "扫描完成，共识别 1 款、3 张商品图片，可进入后续分析。");
        viewModel.CancelBatchInputCommand.Execute(null);
        viewModel.OpenBatchInputCommand.Execute(null);
        FlushLayout(window);
        Assert.Equal(@"C:\商品 素材\秋季上新", viewModel.BatchInputDirectory);
        Assert.Equal(@"D:\生产输出\秋季上新", viewModel.BatchOutputDirectory);
        Assert.Equal("草稿已自动保存", viewModel.BatchDraftSaveText);
        Assert.True(viewModel.IsBatchScanReady);
        Assert.Equal("group_batch_ui", viewModel.SelectedBatchTemplate?.GroupId);
        Assert.Equal(1, viewModel.SelectedBatchPageCount);

        var narrowBounds = WindowBoundsCalculator.Fit(
            SystemParameters.WorkArea,
            new Size(1100, 760),
            new Size(window.MinWidth, window.MinHeight));
        window.Left = narrowBounds.Left;
        window.Top = narrowBounds.Top;
        window.Width = narrowBounds.Width;
        window.Height = narrowBounds.Height;
        viewModel.SetWindowWidth(narrowBounds.Width);
        FlushLayout(window);
        Assert.Equal(1100, window.ActualWidth, precision: 0);
        Assert.Equal(760, window.ActualHeight, precision: 0);
        Assert.True(viewModel.IsBatchNarrowLayout);
        var pageBounds = page.TransformToAncestor(window).TransformBounds(new Rect(page.RenderSize));
        Assert.True(pageBounds.Left >= 0);
        Assert.True(pageBounds.Right <= window.ActualWidth + 0.5);

        workflow.RestoreProjection = new BatchInputProjection(
            "batch_recovered",
            "group_batch_ui",
            BatchInputUiStatus.ScanBlocked,
            @"C:\商品 素材\秋季上新",
            @"D:\生产输出\秋季上新",
            ["page_1"],
            BatchSelectionMode.Manual,
            true,
            "已识别为正常批次 · 2 款",
            "上次扫描未安全完成。源图片没有更改，请重新扫描。");
        viewModel.CancelBatchInputCommand.Execute(null);
        viewModel.OpenBatchInputCommand.Execute(null);
        FlushLayout(window);
        Assert.Equal("草稿已自动保存", viewModel.BatchDraftSaveText);
        Assert.Equal("group_batch_ui", viewModel.SelectedBatchTemplate?.GroupId);
        Assert.True(viewModel.IsBatchScanBlocked);
        Assert.Equal(1, viewModel.SelectedBatchPageCount);
    }

    [Fact]
    public async Task SingleInstanceGateReleasesAfterPrimaryStops()
    {
        var applicationId = $"EcommerceDetailPage.DesktopV1.Tests.{Guid.NewGuid():N}";
        var primary = new SingleInstanceCoordinator(applicationId);
        Assert.True(await primary.TryAcquireAsync());
        primary.StartActivationListener(() => { });

        await using (var duplicate = new SingleInstanceCoordinator(applicationId))
        {
            Assert.False(await duplicate.TryAcquireAsync());
        }

        await primary.DisposeAsync();

        await using var successor = new SingleInstanceCoordinator(applicationId);
        Assert.True(await successor.TryAcquireAsync());
    }

    [Fact]
    public void CalibrationLayoutUsesPanelSwitcherAtNarrowWidthAndThreePanelsAtWideWidth()
    {
        var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));

        viewModel.SetWindowWidth(1100);
        Assert.True(viewModel.IsTemplatePageSettingsCompactLayout);
        Assert.Equal(248, viewModel.TemplatePageSettingsPageColumnWidth.Value);
        Assert.Equal(300, viewModel.TemplatePageSettingsInspectorColumnWidth.Value);
        Assert.True(viewModel.IsCalibrationNarrowLayout);
        Assert.False(viewModel.IsCalibrationPagePanelVisible);
        Assert.True(viewModel.IsCalibrationCanvasPanelVisible);
        Assert.False(viewModel.IsCalibrationInspectorPanelVisible);

        viewModel.ShowCalibrationInspectorCommand.Execute(null);
        Assert.False(viewModel.IsCalibrationCanvasPanelVisible);
        Assert.True(viewModel.IsCalibrationInspectorPanelVisible);

        viewModel.SetWindowWidth(1440);
        Assert.False(viewModel.IsTemplatePageSettingsCompactLayout);
        Assert.Equal(300, viewModel.TemplatePageSettingsPageColumnWidth.Value);
        Assert.Equal(352, viewModel.TemplatePageSettingsInspectorColumnWidth.Value);
        Assert.False(viewModel.IsCalibrationNarrowLayout);
        Assert.True(viewModel.IsCalibrationPagePanelVisible);
        Assert.True(viewModel.IsCalibrationCanvasPanelVisible);
        Assert.True(viewModel.IsCalibrationInspectorPanelVisible);
        viewModel.Dispose();
    }

    [Fact]
    public void ImportedPsdImmediatelyRequeriesContinueCommand()
    {
        var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
        var canExecuteChanged = 0;
        viewModel.ContinuePageSettingsCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        Assert.False(viewModel.ContinuePageSettingsCommand.CanExecute(null));

        viewModel.ImportedTemplateFiles.Add(new ImportedPsdRowViewModel(
            "resource_ui_test",
            "template.psd",
            "790 × 1252 · 8 位 RGB",
            "可用"));

        Assert.True(viewModel.ContinuePageSettingsCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);
        viewModel.Dispose();
    }

    private static TemplateCalibrationDraftView CreateCalibrationDraft(string revisionId) => new(
        revisionId,
        "page_1",
        790,
        1252,
        TemplatePreviewSourceReference: null,
        IsReadOnly: false,
        Frames:
        [
            new TemplateFrameProjection(
                "frame_257",
                "page_1",
                [0, 1],
                257,
                "back_main",
                new string('a', 64),
                10,
                20,
                300,
                400,
                HasMask: false,
                HasVectorMask: false,
                IsVisible: true,
                IsClipping: false,
                Opacity: 255,
                BlendMode: "norm",
                IsPreviewSupported: true,
                IsRegistered: true)
        ],
        Assets: [],
        SelectedAssetId: null,
        SelectedFrameId: "frame_257",
        SelectedScale: 1,
        SelectedOffsetX: 0,
        SelectedOffsetY: 0,
        SelectionRequirement: "只使用用户素材",
        StatusMessage: "已保存",
        CanUndo: true,
        CanRedo: false,
        IsBlocked: false);

    private static void ApplyCalibrationDraft(
        MainWindowViewModel viewModel,
        TemplateCalibrationDraftView draft)
    {
        var draftProperty = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.CalibrationDraft),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("CalibrationDraft property not found.");
        draftProperty.SetValue(viewModel, draft);

        var updateMethod = typeof(MainWindowViewModel).GetMethod(
            "UpdateCalibrationInputs",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UpdateCalibrationInputs method not found.");
        updateMethod.Invoke(viewModel, [draft]);
    }

    private static void VerifySize(Window window, double width, double height)
    {
        var fittedBounds = WindowBoundsCalculator.Fit(
            SystemParameters.WorkArea,
            new Size(width, height),
            new Size(window.MinWidth, window.MinHeight));
        window.Left = fittedBounds.Left;
        window.Top = fittedBounds.Top;
        window.Width = fittedBounds.Width;
        window.Height = fittedBounds.Height;
        FlushLayout(window);
        Assert.Equal(fittedBounds.Width, window.Width);
        Assert.Equal(fittedBounds.Height, window.Height);
        Assert.True(window.ActualWidth > 0);
        Assert.True(window.ActualHeight > 0);
        if (width == 1100 && height == 760)
        {
            Assert.Equal(width, window.ActualWidth, precision: 0);
            Assert.Equal(height, window.ActualHeight, precision: 0);
        }

        foreach (var id in BoundedElementIds)
        {
            var element = FindByAutomationId<FrameworkElement>(window, id);
            var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            Assert.True(bounds.Left >= 0, $"{id} extends left of the window.");
            Assert.True(bounds.Top >= 0, $"{id} extends above the window.");
            Assert.True(bounds.Right <= window.ActualWidth + 0.5, $"{id} extends right of the window.");
            Assert.True(bounds.Bottom <= window.ActualHeight + 0.5, $"{id} extends below the window.");
        }
    }

    private static T FindByAutomationId<T>(DependencyObject root, string automationId)
        where T : FrameworkElement
    {
        if (root is T candidate
            && string.Equals(
                AutomationProperties.GetAutomationId(candidate),
                automationId,
                StringComparison.Ordinal))
        {
            return candidate;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try
            {
                return FindByAutomationId<T>(VisualTreeHelper.GetChild(root, index), automationId);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException($"Automation element not found: {automationId}");
    }

    private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate)
            {
                yield return candidate;
            }

            foreach (var descendant in EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void FlushLayout(Window window) => window.UpdateLayout();

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("WPF shell test did not complete within 30 seconds.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class QueueFolderPicker(params string[] paths) : IFolderPicker
    {
        private readonly Queue<string> _paths = new(paths);

        public Task<string?> PickFolderAsync(
            string title,
            string? initialDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(_paths.Dequeue());
        }
    }

    private sealed class RecordingBatchInputWorkflow : IBatchInputWorkflow
    {
        public BatchInputSubmission? Submission { get; private set; }

        public BatchInputProjection? RestoreProjection { get; set; }

        public TaskCompletionSource? ScanGate { get; set; }

        public BatchInputUiStatus NextStatus { get; set; } = BatchInputUiStatus.ScanReady;

        public string NextRecognitionText { get; set; } = "已识别为正常批次 · 2 款";

        public string NextStatusMessage { get; set; } = "扫描完成，可进入后续分析。";

        public Task<BatchInputProjection?> RestoreLatestDraftAsync(CancellationToken cancellationToken) =>
            Task.FromResult(RestoreProjection);

        public async Task<BatchInputProjection> ScanAsync(
            BatchInputSubmission submission,
            CancellationToken cancellationToken)
        {
            Submission = submission;
            if (ScanGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
                ScanGate = null;
            }

            return new BatchInputProjection(
                "batch_ui_test",
                submission.TemplateGroupId,
                NextStatus,
                submission.InputDirectory,
                submission.OutputDirectory,
                submission.SelectedPageIds,
                submission.SelectionMode,
                submission.IncludeEditablePsd,
                NextRecognitionText,
                NextStatusMessage);
        }

        public Task<BatchInputProjection> SaveDraftAsync(
            BatchInputSubmission submission,
            CancellationToken cancellationToken) => Task.FromResult(new BatchInputProjection(
                submission.BatchId ?? "batch_ui_test",
                submission.TemplateGroupId,
                BatchInputUiStatus.Draft,
                submission.InputDirectory,
                submission.OutputDirectory,
                submission.SelectedPageIds,
                submission.SelectionMode,
                submission.IncludeEditablePsd,
                "目录已选择；等待扫描识别。",
                "草稿已自动保存。"));
    }
}
