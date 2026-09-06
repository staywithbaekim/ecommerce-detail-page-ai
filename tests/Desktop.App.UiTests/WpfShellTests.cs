using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.SizeTables;
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

    [Fact]
    public void SizeTableEditorPreservesLegalSymbolsAndBlocksTrueEmptyCells()
    {
        var document = new SizeTableEditorDocument(
            "size_01",
            "batch_01",
            "style_01",
            "page_01",
            "asset_size",
            "sha256:" + new string('a', 64),
            0,
            SizeTableEditorStatus.Valid,
            [new("col_001", "尺码"), new("col_002", "袖长")],
            [new("row_001", ["L", "/"]), new("row_002", ["XL", "/"])]);
        var editor = new SizeTableEditorState();
        editor.Load(document, sourcePreviewPath: null);

        Assert.False(editor.CanSave);
        editor.Rows[0].Cells[1].SourceText = "";
        Assert.False(editor.CanSave);
        Assert.Contains("不能保存", editor.StatusMessage, StringComparison.Ordinal);
        editor.Rows[0].Cells[1].SourceText = "\\";
        Assert.True(editor.CanSave);
        Assert.True(editor.TryBuild(out var edited));
        Assert.Equal("\\", edited.Rows[0].Cells[1]);

        editor.AddColumn();
        Assert.False(editor.CanSave);
        foreach (var row in editor.Rows) row.Cells[^1].SourceText = "-";
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void DynamicSizeTableEditorRendersSourceGridAndTemplatePreviewAtWideAndNarrowSizes()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();

            var samplePath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "需求样本", "SIZE-01金标", "真实来源",
                "size-real-a-577fa4d19f919a7a.jpg"));
            var sizeWorkflow = new RecordingSizeTableEditingWorkflow(
                new SizeTableEditorDocument(
                    "size_gold_a",
                    "batch_gold_a",
                    "style_gold_a",
                    "page_size",
                    "asset_size_gold_a",
                    "sha256:" + new string('a', 64),
                    0,
                    SizeTableEditorStatus.Valid,
                    [
                        new("col_001", "尺码"),
                        new("col_002", "衣长"),
                        new("col_003", "胸围"),
                        new("col_004", "肩宽"),
                        new("col_005", "袖长")
                    ],
                    [
                        new("row_001", ["L", "61", "102", "40", "/"]),
                        new("row_002", ["XL", "62", "107", "41", "/"]),
                        new("row_003", ["2XL", "63", "112", "42", "/"]),
                        new("row_004", ["3XL", "64", "117", "43", "/"]),
                        new("row_005", ["4XL", "65", "122", "44", "/"]),
                        new("row_006", ["5XL", "66", "127", "45", "/"])
                    ]));
            var aiWorkflow = new RecordingAiBatchAnalysisWorkflow
            {
                RenderPreparation = new AiRenderPlanPreparation(
                    "batch_gold_a",
                    AiRenderPreparationStatus.WaitingForPhotoshop,
                    "PHOTOSHOP_FORMAL_ADAPTER_NOT_AVAILABLE",
                    [new AiRenderPagePreparation(
                        "style_gold_a",
                        "page_size",
                        AiRenderPagePreparationStatus.ReadyToRender,
                        "AI_SIZE_TABLE_VALID",
                        "{\"page_type\":\"dynamic_size_table\"}",
                        "sha256:" + new string('b', 64))])
            };
            var projection = new AiBatchAnalysisProjection(
                "batch_gold_a",
                AiBatchUiStatus.Ready,
                "分析完成",
                "开始生成",
                1,
                1,
                1,
                1,
                [new AiStyleAnalysisProjection(
                    "batch_gold_a",
                    "style_gold_a",
                    "真实金标款式 A",
                    AiStyleUiStatus.Ready,
                    "可生成",
                    [new AiPageAnalysisProjection(
                        "batch_gold_a",
                        "style_gold_a",
                        "page_size",
                        "动态尺码页",
                        AiPageUiStatus.Ready,
                        "可生成",
                        "开始生成",
                        [new AiCandidateProjection(
                            "batch_gold_a",
                            "style_gold_a",
                            "size_table_source",
                            "asset_size_gold_a",
                            "市场尺码图",
                            1,
                            0.99m,
                            ["clear_subject"],
                            true,
                            "尺码表来源",
                            samplePath,
                            "清晰动态尺码来源",
                            "动态尺码页")],
                        [],
                        Requirements:
                        [new AiRequirementSelectionProjection(
                            "batch_gold_a",
                            "style_gold_a",
                            "page_size",
                            "动态尺码页",
                            "size_table_source",
                            "市场尺码图",
                            "清晰动态尺码来源",
                            AiRequirementSelectionUiStatus.ManualLocked,
                            "人工选用 · 已锁定",
                            "asset_size_gold_a",
                            samplePath)])],
                    1,
                    [new AiStyleAssetProjection(
                        "batch_gold_a",
                        "style_gold_a",
                        "asset_size_gold_a",
                        samplePath,
                        0)])],
                AiAnalysisSchedulingControlState.Running,
                null);
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiBatchAnalysisWorkflow: aiWorkflow,
                sizeTableEditingWorkflow: sizeWorkflow);
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                ApplyAiProjection(viewModel, projection);
                SetAiScreen(viewModel);
                FlushLayout(window);

                viewModel.PrepareRenderingCommand.Execute(null);
                FlushLayout(window);
                var page = Assert.Single(Assert.Single(viewModel.ResultStyleGroups).Pages);
                Assert.True(page.IsSizeTablePage);
                viewModel.OpenResultPageCommand.Execute(page);
                FlushLayout(window);
                Assert.True(FindByAutomationId<Button>(window, "EditSizeTableDataButton").IsVisible);

                viewModel.OpenSizeTableEditorCommand.Execute(page);
                FlushLayout(window);
                Assert.True(viewModel.IsSizeTableEditorVisible);
                Assert.Equal(Visibility.Visible, FindByAutomationId<Grid>(window, "SizeTableEditorDialog").Visibility);
                Assert.True(FindByAutomationId<Border>(window, "SizeTableSourcePanel").IsVisible);
                Assert.True(FindByAutomationId<Border>(window, "SizeTableStructuredGridPanel").IsVisible);
                Assert.True(FindByAutomationId<Border>(window, "SizeTableTemplatePreviewPanel").IsVisible);
                Assert.Equal("/", viewModel.SizeTableEditor.Rows[0].Cells[^1].SourceText);
                Assert.False(FindByAutomationId<Button>(window, "SaveSizeTableButton").IsEnabled);

                viewModel.SizeTableEditor.Rows[0].Cells[^1].SourceText = "\\";
                FlushLayout(window);
                Assert.True(FindByAutomationId<Button>(window, "SaveSizeTableButton").IsEnabled);
                viewModel.SizeTableEditor.Rows[0].Cells[^1].SourceText = "";
                FlushLayout(window);
                Assert.False(FindByAutomationId<Button>(window, "SaveSizeTableButton").IsEnabled);
                Assert.Contains("不能保存", viewModel.SizeTableEditor.StatusMessage, StringComparison.Ordinal);

                SetWindowSize(window, 1440, 900);
                AssertEditorBounds(window);
                SetWindowSize(window, 1100, 760);
                AssertEditorBounds(window);
                _output.WriteLine(
                    $"O-03 editor wide/narrow bounds passed; actual DPI {VisualTreeHelper.GetDpi(window).DpiScaleX:P0}.");
            }
            finally
            {
                viewModel.Dispose();
                window.Close();
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
    }

    private static void AssertEditorBounds(Window window)
    {
        var dialog = FindByAutomationId<Grid>(window, "SizeTableEditorDialog");
        var bounds = dialog
            .TransformToAncestor(window)
            .TransformBounds(new Rect(dialog.RenderSize));
        Assert.True(bounds.Left >= 0, "尺码编辑弹层超出窗口左边界。");
        Assert.True(bounds.Right <= window.ActualWidth + 0.5, "尺码编辑弹层超出窗口右边界。");
        Assert.True(bounds.Top >= 0, "尺码编辑弹层超出窗口上边界。");
        Assert.True(bounds.Bottom <= window.ActualHeight + 0.5, "尺码编辑弹层超出窗口下边界。");
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
        "RestoreLatestBatchAction",
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
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
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
                var consentCard = FindByAutomationId<Border>(window, "AiUploadConsentCard");
                Assert.NotNull(consentCard);
                Assert.Equal(
                    "尚未授权上传压缩分析图",
                    FindByAutomationId<TextBlock>(window, "AiUploadConsentStatus").Text);
                Assert.False(FindByAutomationId<Button>(window, "GrantAiUploadConsentButton").IsEnabled);

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
                if (ownsApplication)
                {
                    application.Shutdown();
                }
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
        var restoreEntry = FindByAutomationId<Button>(window, "RestoreLatestBatchAction");
        Assert.True(entry.IsHitTestVisible);
        Assert.True(entry.Focusable);
        Assert.True(restoreEntry.IsHitTestVisible);
        Assert.True(restoreEntry.Focusable);
        viewModel.OpenBatchInputCommand.Execute(null);
        FlushLayout(window);

        var page = FindByAutomationId<Grid>(window, "BatchInputPage");
        Assert.Equal(Visibility.Visible, page.Visibility);
        Assert.Equal(
            "新建批次",
            FindByAutomationId<TextBlock>(window, "BatchInputTitle").Text);

        viewModel.TemplateGroups.Add(new TemplateLibraryCardViewModel(
            "group_draft_only",
            "尚未提交模板",
            "1 个页面 · 草稿",
            "今天 09:00",
            "草稿"));
        viewModel.TemplateGroups.Add(new TemplateLibraryCardViewModel(
            "group_batch_ui",
            "通用品类模板",
            "2 个页面 · Committed",
            "今天 10:00",
            "Committed",
            HasCommittedRevision: true));
        viewModel.SelectedBatchTemplate = viewModel.TemplateGroups[1];
        FlushLayout(window);
        var templateSelect = FindByAutomationId<ComboBox>(window, "BatchTemplateSelect");
        Assert.Contains(
            EnumerateVisualDescendants<TextBlock>(templateSelect),
            text => string.Equals(text.Text, "通用品类模板", StringComparison.Ordinal));
        Assert.DoesNotContain(
            EnumerateVisualDescendants<TextBlock>(templateSelect),
            text => string.Equals(text.Text, "尚未提交模板", StringComparison.Ordinal));
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
        Assert.Equal(0, workflow.RestoreCount);
        Assert.Equal("", viewModel.BatchInputDirectory);
        Assert.Equal("", viewModel.BatchOutputDirectory);
        Assert.Equal("新批次草稿", viewModel.BatchDraftSaveText);
        Assert.False(viewModel.IsBatchScanReady);

        viewModel.CancelBatchInputCommand.Execute(null);
        viewModel.RestoreBatchInputCommand.Execute(null);
        FlushLayout(window);
        Assert.Equal(1, workflow.RestoreCount);
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
        viewModel.RestoreBatchInputCommand.Execute(null);
        FlushLayout(window);
        Assert.Equal(2, workflow.RestoreCount);
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
    public void ReferenceLearningUsesCalibrationEditorAndBlocksSaveUntilExplicitReview()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();
            var sourceRoot = Path.Combine(
                Path.GetTempPath(),
                "tpl-learn-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            var photoPath = Path.Combine(sourceRoot, "front.png");
            File.WriteAllBytes(photoPath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var document = CreateReferenceLearningDocument();
            var learning = new RecordingReferenceLearningService(document);
            var commit = new RecordingReferenceLearningCommitService();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                referenceLearningService: learning,
                referenceLearningCommitService: commit);
            var session = new TemplateDraftSession(
                "group_learning_ui",
                "人工结果学习模板",
                "revision_learning_ui",
                1,
                new HashSet<string>(StringComparer.Ordinal));
            var page = CreateReferenceLearningPage();
            typeof(MainWindowViewModel).GetField(
                    "_templateDraft",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, session);
            typeof(MainWindowViewModel).GetField(
                    "_referenceLearningPhotoDirectory",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, sourceRoot);
            viewModel.TemplatePages.Add(page);
            viewModel.SelectedTemplatePage = page;
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.Key == "templates");
            var setMode = typeof(MainWindowViewModel).GetMethod(
                "SetTemplateCalibrationMode",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            setMode.Invoke(viewModel, [TemplateCalibrationMode.ReferenceLearning]);
            var start = typeof(MainWindowViewModel).GetMethod(
                "StartReferenceLearningAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<Task>(start.Invoke(viewModel, null)).GetAwaiter().GetResult();

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                Assert.True(viewModel.IsReferenceLearningReviewMode);
                Assert.True(FindByAutomationId<Grid>(window, "TemplateCalibrationPage").IsVisible);
                Assert.True(FindByAutomationId<StackPanel>(window, "ReferenceLearningEvidencePanel").IsVisible);
                Assert.True(FindByAutomationId<ListBox>(window, "ReferenceLearningPhotoList").IsVisible);
                Assert.True(FindByAutomationId<StackPanel>(window, "ReferenceLearningReviewActions").IsVisible);
                Assert.Equal("待复核", viewModel.ReferenceLearningDecisionText);
                Assert.Equal("front.png", viewModel.SelectedReferenceLearningPhoto!.DisplayName);
                Assert.False(FindByAutomationId<Button>(window, "SaveTemplateGroupButton").IsEnabled);
                Assert.False(FindByAutomationId<Button>(window, "AcceptReferenceLearningFrameButton").IsEnabled);
                Assert.True(FindByAutomationId<Button>(window, "RejectReferenceLearningFrameButton").IsEnabled);
                Assert.True(FindByAutomationId<Button>(window, "CancelReferenceLearningButton").IsVisible);

                viewModel.CalibrationRequirementInput = "完整正面图，主体完整";
                FlushLayout(window);
                Assert.True(FindByAutomationId<Button>(window, "AcceptReferenceLearningFrameButton").IsEnabled);
                var applyReview = typeof(MainWindowViewModel).GetMethod(
                    "ApplyReferenceLearningReviewAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(applyReview.Invoke(
                    viewModel,
                    [ReferenceReviewDecision.Accepted])).GetAwaiter().GetResult();
                FlushLayout(window);

                Assert.Equal("已确认", viewModel.ReferenceLearningDecisionText);
                Assert.True(viewModel.CanCommitReferenceLearning);
                Assert.True(FindByAutomationId<Button>(window, "SaveTemplateGroupButton").IsEnabled);

                SetWindowSize(window, 1100, 760);
                Assert.True(viewModel.IsCalibrationNarrowLayout);
                Assert.True(FindByAutomationId<StackPanel>(window, "CalibrationNarrowPanelSwitcher").IsVisible);

                var save = typeof(MainWindowViewModel).GetMethod(
                    "SaveTemplateAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(save.Invoke(viewModel, null)).GetAwaiter().GetResult();
                FlushLayout(window);
                Assert.True(commit.CommitCalled);
                Assert.Equal(2, learning.StrictResumeCount);
                Assert.False(viewModel.IsReferenceLearningReviewMode);
                Assert.True(FindByAutomationId<Grid>(window, "TemplateDetailsPage").IsVisible);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
                if (Directory.Exists(sourceRoot))
                {
                    Directory.Delete(sourceRoot, recursive: true);
                }
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
    }

    [Fact]
    public void ReferenceLearningOffersPageAndBatchConfirmationForQualifiedCandidates()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();
            var sourceRoot = Path.Combine(
                Path.GetTempPath(),
                "tpl-learn-batch-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllBytes(Path.Combine(sourceRoot, "front.png"), [1]);
            var document = CreateReferenceLearningDocument(inferredSemantic: true);
            var learning = new RecordingReferenceLearningService(document);
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                referenceLearningService: learning);
            var session = new TemplateDraftSession(
                "group_learning_batch_ui",
                "人工结果学习模板",
                "revision_learning_batch_ui",
                1,
                new HashSet<string>(StringComparer.Ordinal));
            var page = CreateReferenceLearningPage();
            typeof(MainWindowViewModel).GetField(
                    "_templateDraft",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, session);
            typeof(MainWindowViewModel).GetField(
                    "_referenceLearningPhotoDirectory",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, sourceRoot);
            viewModel.TemplatePages.Add(page);
            viewModel.SelectedTemplatePage = page;
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.Key == "templates");
            var setMode = typeof(MainWindowViewModel).GetMethod(
                "SetTemplateCalibrationMode",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            setMode.Invoke(viewModel, [TemplateCalibrationMode.ReferenceLearning]);
            var start = typeof(MainWindowViewModel).GetMethod(
                "StartReferenceLearningAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<Task>(start.Invoke(viewModel, null)).GetAwaiter().GetResult();

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                Assert.True(viewModel.CanConfirmReferenceLearningPage);
                Assert.True(viewModel.CanConfirmAllQualifiedReferenceLearning);
                Assert.True(FindByAutomationId<Button>(window, "ConfirmReferenceLearningPageButton").IsEnabled);
                Assert.True(FindByAutomationId<Button>(window, "ConfirmAllQualifiedReferenceLearningButton").IsEnabled);

                var confirmPage = typeof(MainWindowViewModel).GetMethod(
                    "ConfirmReferenceLearningPageAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(confirmPage.Invoke(viewModel, null)).GetAwaiter().GetResult();
                FlushLayout(window);

                Assert.Equal(1, learning.LastReviewCount);
                Assert.Equal(ReferenceReviewDecision.Accepted, viewModel.ReferenceLearningFrames[0].Decision);
                Assert.False(viewModel.CanConfirmReferenceLearningPage);
                Assert.False(viewModel.CanConfirmAllQualifiedReferenceLearning);
                Assert.True(viewModel.CanCommitReferenceLearning);

                learning.Reset(document);
                var reanalyze = typeof(MainWindowViewModel).GetMethod(
                    "ReanalyzeCurrentReferenceLearningPageAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(reanalyze.Invoke(viewModel, null)).GetAwaiter().GetResult();
                Assert.True(viewModel.CanConfirmAllQualifiedReferenceLearning);
                var confirmAll = typeof(MainWindowViewModel).GetMethod(
                    "ConfirmAllQualifiedReferenceLearningAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(confirmAll.Invoke(viewModel, null)).GetAwaiter().GetResult();
                Assert.Equal(1, learning.LastReviewCount);
                Assert.False(viewModel.CanConfirmAllQualifiedReferenceLearning);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
                if (Directory.Exists(sourceRoot))
                {
                    Directory.Delete(sourceRoot, recursive: true);
                }
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
    }

    [Fact]
    public void ImportedPsdImmediatelyRequeriesContinueCommand()
    {
        var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
        var canExecuteChanged = 0;
        viewModel.ContinuePageSettingsCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        Assert.False(viewModel.ContinuePageSettingsCommand.CanExecute(null));

        viewModel.ImportedTemplateFiles.Add(new ImportedPsdRowViewModel(
            "page_ui_test",
            "resource_ui_test",
            "template.psd",
            "790 × 1252 · 8 位 RGB",
            "可用"));

        Assert.True(viewModel.ContinuePageSettingsCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);
        viewModel.Dispose();
    }

    [Fact]
    public void NewBatchDoesNotRestoreCompletedAiProjectionAndRecoveryStillWorks()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();
            var batchWorkflow = new RecordingBatchInputWorkflow
            {
                RestoreProjection = new BatchInputProjection(
                    "batch_completed_ai",
                    "group_ai",
                    BatchInputUiStatus.ScanReady,
                    @"C:\旧商品目录",
                    @"D:\旧输出目录",
                    ["page_1"],
                    BatchSelectionMode.Automatic,
                    false,
                    "已识别为单款批次 · 1 款",
                    "扫描完成。")
            };
            var aiWorkflow = new RecordingAiBatchAnalysisWorkflow
            {
                RefreshProjection = new AiBatchAnalysisProjection(
                    "batch_completed_ai",
                    AiBatchUiStatus.ReviewRequired,
                    "旧批次分析已完成，等待人工复核。",
                    "继续处理",
                    1,
                    1,
                    0,
                    1,
                    [],
                    AiAnalysisSchedulingControlState.Running,
                    null)
            };
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                batchInputWorkflow: batchWorkflow,
                aiBatchAnalysisWorkflow: aiWorkflow);
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                viewModel.OpenBatchInputCommand.Execute(null);
                FlushLayout(window);

                Assert.True(viewModel.IsBatchInputVisible);
                Assert.False(viewModel.IsAiAnalysisVisible);
                Assert.Equal("", viewModel.BatchInputDirectory);
                Assert.Equal("", viewModel.BatchOutputDirectory);
                Assert.Equal(0, batchWorkflow.RestoreCount);
                Assert.Equal(0, aiWorkflow.RefreshCount);

                viewModel.CancelBatchInputCommand.Execute(null);
                viewModel.RestoreBatchInputCommand.Execute(null);
                FlushLayout(window);

                Assert.False(viewModel.IsBatchInputVisible);
                Assert.True(viewModel.IsAiAnalysisVisible);
                Assert.Equal(@"C:\旧商品目录", viewModel.BatchInputDirectory);
                Assert.Equal(@"D:\旧输出目录", viewModel.BatchOutputDirectory);
                Assert.Equal(1, batchWorkflow.RestoreCount);
                Assert.Equal(1, aiWorkflow.RefreshCount);
            }
            finally
            {
                viewModel.Dispose();
                window.Close();
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
    }

    [Fact]
    public void BatchOutputPreviewTracksTheActuallySelectedPages()
    {
        var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
        viewModel.BatchPages.Add(new BatchPageOptionViewModel("page_1", "主图页", "每款一次", true));
        viewModel.BatchPages.Add(new BatchPageOptionViewModel("page_2", "细节页", "每款一次", true));
        viewModel.BatchPages.Add(new BatchPageOptionViewModel("page_3", "尺码页", "每款一次", true));

        Assert.Equal(
            ["01-主图页.jpg", "02-细节页.jpg", "03-尺码页.jpg"],
            viewModel.BatchOutputPreviewFiles);

        viewModel.BatchPages[1].IsSelected = false;
        Assert.Equal(["01-主图页.jpg", "02-尺码页.jpg"], viewModel.BatchOutputPreviewFiles);
        Assert.Equal("每款独立目录 · 2 个 JPG", viewModel.BatchOutputPreviewSummary);

        viewModel.BatchIncludeEditablePsd = true;
        Assert.Equal("每款独立目录 · 2 个 JPG + 同名可编辑 PSD", viewModel.BatchOutputPreviewSummary);
        viewModel.Dispose();
    }

    [Fact]
    public void FirstScanRequiresExplicitUncheckedUploadConsentAndContinuesAfterGrant()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();
            var batchWorkflow = new RecordingBatchInputWorkflow
            {
                NextStatus = BatchInputUiStatus.ScanBlocked,
                NextStatusMessage = "测试扫描已完成授权门验证。"
            };
            var aiWorkflow = new RecordingAiBatchAnalysisWorkflow();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                folderPicker: new QueueFolderPicker(@"C:\商品素材", @"D:\正式输出"),
                batchInputWorkflow: batchWorkflow,
                aiBatchAnalysisWorkflow: aiWorkflow);
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                viewModel.OpenBatchInputCommand.Execute(null);
                viewModel.TemplateGroups.Add(new TemplateLibraryCardViewModel(
                    "group_consent_ui",
                    "授权验证模板",
                    "1 个页面 · Committed",
                    "今天 10:00",
                    "Committed",
                    HasCommittedRevision: true));
                viewModel.SelectedBatchTemplate = viewModel.TemplateGroups[0];
                viewModel.BatchPages.Add(new BatchPageOptionViewModel(
                    "page_1",
                    "主图页",
                    "普通套图页 · 每款一次",
                    true));
                viewModel.ChooseBatchInputDirectoryCommand.Execute(null);
                viewModel.ChooseBatchOutputDirectoryCommand.Execute(null);
                FlushLayout(window);

                viewModel.ScanBatchInputCommand.Execute(null);
                FlushLayout(window);

                var dialog = FindByAutomationId<Grid>(window, "AiUploadConsentDialog");
                var acknowledgement = FindByAutomationId<CheckBox>(window, "AiUploadConsentAcknowledgement");
                var confirm = FindByAutomationId<Button>(window, "ConfirmAiUploadConsentButton");
                Assert.Equal(Visibility.Visible, dialog.Visibility);
                Assert.False(acknowledgement.IsChecked);
                Assert.False(confirm.IsEnabled);
                Assert.Null(batchWorkflow.Submission);
                Assert.Equal(0, aiWorkflow.GrantCount);

                viewModel.CancelAiUploadConsentCommand.Execute(null);
                viewModel.ScanBatchInputCommand.Execute(null);
                FlushLayout(window);
                Assert.Equal(Visibility.Visible, dialog.Visibility);
                Assert.False(acknowledgement.IsChecked);

                viewModel.AiUploadConsentAcknowledged = true;
                FlushLayout(window);
                Assert.True(confirm.IsEnabled);
                viewModel.ConfirmAiUploadConsentCommand.Execute(null);
                FlushLayout(window);

                Assert.Equal(Visibility.Collapsed, dialog.Visibility);
                Assert.Equal(1, aiWorkflow.GrantCount);
                Assert.True(viewModel.IsAiUploadConsentGranted);
                Assert.NotNull(batchWorkflow.Submission);
                Assert.Equal("测试扫描已完成授权门验证。", viewModel.BatchStatusMessage);
            }
            finally
            {
                window.Close();
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
    }

    [Fact]
    public void AiAnalysisPageSupportsWideNarrowLongListUiaAndChineseStatus()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();
            var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                var projection = new Desktop.Application.Ai.AiBatchAnalysisProjection(
                    "batch_ui",
                    Desktop.Application.Ai.AiBatchUiStatus.ReviewRequired,
                    "分析完成，部分页面需要人工复核后才能生成。",
                    "继续处理",
                    40,
                    40,
                    39,
                    40,
                    Enumerable.Range(1, 40).Select(index => new Desktop.Application.Ai.AiStyleAnalysisProjection(
                        "batch_ui",
                        $"style_{index}",
                        $"款式 {index:D2}",
                        index == 40
                            ? Desktop.Application.Ai.AiStyleUiStatus.BusinessError
                            : Desktop.Application.Ai.AiStyleUiStatus.Ready,
                        index == 40 ? "需要修复" : "可生成",
                        [new Desktop.Application.Ai.AiPageAnalysisProjection(
                            "batch_ui",
                            $"style_{index}",
                            $"page_{index}",
                            "中文主图页",
                            index == 40
                                ? Desktop.Application.Ai.AiPageUiStatus.BusinessError
                                : Desktop.Application.Ai.AiPageUiStatus.Ready,
                            index == 40 ? "需要修复" : "可生成",
                            index == 40 ? "处理此页" : "已通过",
                            index == 1
                                ? [new Desktop.Application.Ai.AiCandidateProjection(
                                    "batch_ui",
                                    "style_1",
                                    "frame_01",
                                    "asset_01",
                                    "internal-file-name.jpg",
                                    1,
                                    0.98m,
                                    ["SUBJECT_COMPLETE", "NO_EXTRA_DISTRACTIONS"],
                                    false,
                                    "图片框 1",
                                    null,
                                    "选择完整清晰的商品主体图。",
                                    "中文主图页")]
                                : [],
                            index == 40 ? ["缺少必需素材，请补充后重试。"] : [])],
                        3)).ToArray(),
                    Desktop.Application.Ai.AiAnalysisSchedulingControlState.Running,
                    null);
                ApplyAiProjection(viewModel, projection);
                SetAiScreen(viewModel);
                FlushLayout(window);

                var page = FindByAutomationId<Grid>(window, "AiAnalysisPage");
                Assert.Equal(Visibility.Visible, page.Visibility);
                Assert.Equal("生成前检查", FindByAutomationId<TextBlock>(window, "AiAnalysisTitle").Text);
                Assert.Equal(40, viewModel.AiStyles.Count);
                Assert.Contains("人工复核", FindByAutomationId<TextBlock>(window, "AiAnalysisStatus").Text, StringComparison.Ordinal);
                var scroll = FindByAutomationId<ScrollViewer>(window, "AiAnalysisScrollViewer");
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.NotNull(FindByAutomationId<FrameworkElement>(window, "PauseAiAnalysisButton"));
                Assert.NotNull(FindByAutomationId<FrameworkElement>(window, "ResumeAiAnalysisButton"));
                Assert.NotNull(FindByAutomationId<FrameworkElement>(window, "CancelAiAnalysisButton"));
                Assert.True(FindByAutomationId<Button>(window, "EnterGenerationButton").IsEnabled);

                var firstStyle = viewModel.AiStyles[0];
                firstStyle.IsExpanded = true;
                FlushLayout(window);
                var candidate = Assert.Single(firstStyle.Pages[0].Candidates);
                viewModel.OpenAiCandidatePreviewCommand.Execute(candidate);
                FlushLayout(window);
                Assert.Equal(Visibility.Visible, FindByAutomationId<Grid>(window, "AiCandidatePreviewDialog").Visibility);
                Assert.Equal("用于：中文主图页 · 图片框 1", viewModel.AiCandidatePreview!.UsageText);
                Assert.Equal("选用这张", FindByAutomationId<Button>(window, "ConfirmAiCandidateSelectionButton").Content);
                Assert.DoesNotContain(
                    EnumerateVisualDescendants<Button>(window).Select(button => button.Content?.ToString()),
                    content => content is "锁定此图" or "主体分析" or "当前首选");
                viewModel.CloseAiCandidatePreviewCommand.Execute(null);
                FlushLayout(window);

                ApplyAiProjection(
                    viewModel,
                    projection with
                    {
                        Status = Desktop.Application.Ai.AiBatchUiStatus.BudgetStopped,
                        StatusText = "本轮 AI 预算已达到停止线；请降低范围或等待人工批准后继续。",
                        ActionText = "继续处理",
                        ReadyPageCount = 0
                    });
                FlushLayout(window);
                Assert.Contains("预算", FindByAutomationId<TextBlock>(window, "AiAnalysisStatus").Text, StringComparison.Ordinal);
                Assert.Equal("稍后继续", FindByAutomationId<Button>(window, "EnterGenerationButton").Content);
                Assert.False(FindByAutomationId<Button>(window, "EnterGenerationButton").IsEnabled);
                Assert.False(FindByAutomationId<Button>(window, "PauseAiAnalysisButton").IsEnabled);
                Assert.True(FindByAutomationId<Button>(window, "CancelAiAnalysisButton").IsEnabled);

                SetWindowSize(window, 1440, 900);
                SetWindowSize(window, 1100, 760);
                var bounds = page.TransformToAncestor(window).TransformBounds(new Rect(page.RenderSize));
                Assert.True(bounds.Left >= 0);
                Assert.True(bounds.Right <= window.ActualWidth + 0.5);
            }
            finally
            {
                window.Close();
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
    }

    [Fact]
    public void AiStyleReviewShowsAllUsageAndAssetsAndReplacesWithoutProviderRequest()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();
            var workflow = new RecordingAiBatchAnalysisWorkflow();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiBatchAnalysisWorkflow: workflow);
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                var requirement = new Desktop.Application.Ai.AiRequirementSelectionProjection(
                    "batch_ui",
                    "style_1",
                    "page_1",
                    "中文主图页",
                    "frame_01",
                    "图片框 1",
                    "商品主体清晰完整，构图适合当前图片框。",
                    Desktop.Application.Ai.AiRequirementSelectionUiStatus.AiSelected,
                    "AI 推荐 · 已选用",
                    "asset_01",
                    null);
                var initialPage = new Desktop.Application.Ai.AiPageAnalysisProjection(
                    "batch_ui",
                    "style_1",
                    "page_1",
                    "中文主图页",
                    Desktop.Application.Ai.AiPageUiStatus.Ready,
                    "可生成",
                    "已通过",
                    [],
                    [],
                    Requirements: [requirement]);
                var assets = new Desktop.Application.Ai.AiStyleAssetProjection[]
                {
                    new("batch_ui", "style_1", "asset_01", null, 1),
                    new("batch_ui", "style_1", "asset_02", null, 2),
                    new("batch_ui", "style_1", "asset_03", null, 3)
                };
                var initialStyle = new Desktop.Application.Ai.AiStyleAnalysisProjection(
                    "batch_ui",
                    "style_1",
                    "款式 01",
                    Desktop.Application.Ai.AiStyleUiStatus.Ready,
                    "可生成",
                    [initialPage],
                    assets.Length,
                    assets);
                var initial = new Desktop.Application.Ai.AiBatchAnalysisProjection(
                    "batch_ui",
                    Desktop.Application.Ai.AiBatchUiStatus.Ready,
                    "分析完成，所有已选页面均可生成。",
                    "进入生成",
                    1,
                    1,
                    1,
                    1,
                    [initialStyle],
                    Desktop.Application.Ai.AiAnalysisSchedulingControlState.Running,
                    null);
                var lockedRequirement = requirement with
                {
                    Status = Desktop.Application.Ai.AiRequirementSelectionUiStatus.ManualLocked,
                    StatusText = "人工选用 · 已锁定",
                    SelectedAssetId = "asset_02"
                };
                workflow.LockProjection = initial with
                {
                    Styles =
                    [
                        initialStyle with
                        {
                            Pages = [initialPage with { Requirements = [lockedRequirement] }]
                        }
                    ]
                };

                ApplyAiProjection(viewModel, initial);
                SetAiScreen(viewModel);
                var style = Assert.Single(viewModel.AiStyles);
                viewModel.OpenAiStyleReviewCommand.Execute(style);
                FlushLayout(window);

                Assert.True(viewModel.IsAiStyleReviewVisible);
                Assert.False(viewModel.IsWorkbenchVisible);
                Assert.True(FindByAutomationId<Grid>(window, "AiStyleReviewPage").IsVisible);
                Assert.True(FindByAutomationId<Button>(window, "ReturnFromAiStyleReviewButton").IsVisible);
                Assert.Equal(
                    ["全部", "可生成 1", "待检查 0", "不可生成 0"],
                    viewModel.AiStatusFilters.Select(filter => filter.DisplayText).ToArray());
                Assert.Equal(ScrollBarVisibility.Visible,
                    FindByAutomationId<ScrollViewer>(window, "AiStyleReviewUsageScrollViewer").VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Visible,
                    FindByAutomationId<ScrollViewer>(window, "AiStyleReviewAssetScrollViewer").VerticalScrollBarVisibility);
                Assert.Equal(3, style.Assets.Count);
                Assert.Single(style.Pages[0].Requirements);

                var target = style.Pages[0].Requirements[0];
                viewModel.BeginAiReplacementCommand.Execute(target);
                viewModel.SelectAiReplacementAssetCommand.Execute(style.Assets[1]);
                Assert.True(viewModel.CanConfirmAiReplacement);
                viewModel.ConfirmAiReplacementCommand.Execute(null);
                FlushLayout(window);

                Assert.Equal(1, workflow.LockCount);
                Assert.Equal(("batch_ui", "style_1", "frame_01", "asset_02"), workflow.LastLock);
                var locked = Assert.Single(Assert.Single(viewModel.AiStyles).Pages[0].Requirements);
                Assert.Equal("asset_02", locked.SelectedAssetId);
                Assert.Equal("人工选用 · 已锁定", locked.StatusText);
                Assert.False(viewModel.IsAiReplacementActive);
                Assert.DoesNotContain(
                    EnumerateVisualDescendants<TextBlock>(window).Select(text => text.Text),
                    text => text.Contains("asset_", StringComparison.Ordinal)
                        || text.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
                        || text.Contains(@"C:\", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
    }

    [Fact]
    public void AiAnalysisAutoRefreshReadsLocalStateAndStopsAfterAnalysisSettles()
    {
        RunInSta(() =>
        {
            var workflow = new RecordingAiBatchAnalysisWorkflow();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiBatchAnalysisWorkflow: workflow);
            try
            {
                var analysing = new AiBatchAnalysisProjection(
                    "batch_auto_refresh",
                    AiBatchUiStatus.Analysing,
                    "正在分析",
                    "等待分析完成",
                    1,
                    0,
                    0,
                    1,
                    [],
                    AiAnalysisSchedulingControlState.Running,
                    null);
                SetCurrentBatchId(viewModel, analysing.BatchId);
                ApplyAiProjection(viewModel, analysing);
                SetAiScreen(viewModel);

                var timer = GetAiRefreshTimer(viewModel);
                Assert.True(timer.IsEnabled);

                workflow.RefreshProjection = analysing with
                {
                    Status = AiBatchUiStatus.ReviewRequired,
                    StatusText = "分析完成，等待检查",
                    ActionText = "开始生成",
                    CompletedStyleCount = 1
                };
                RunAiAutoRefreshOnce(viewModel);

                Assert.Equal(1, workflow.RefreshCount);
                Assert.False(timer.IsEnabled);

                RunAiAutoRefreshOnce(viewModel);
                Assert.Equal(1, workflow.RefreshCount);
            }
            finally
            {
                viewModel.Dispose();
            }
        });
    }

    [Fact]
    public void ResultOverviewAndDetailUsePreparedPlansWithoutInventingFormalOutput()
    {
        RunInSta(() =>
        {
            var application = System.Windows.Application.Current as Desktop.App.App;
            var ownsApplication = application is null;
            application ??= new Desktop.App.App();
            application.InitializeComponent();
            var requirement = new AiRequirementSelectionProjection(
                "batch_results",
                "style_01",
                "page_01",
                "主图页",
                "frame_01",
                "主商品框",
                "商品主体清晰完整",
                AiRequirementSelectionUiStatus.ManualLocked,
                "人工选用 · 已锁定",
                "asset_front",
                null);
            var sourcePage = new AiPageAnalysisProjection(
                "batch_results",
                "style_01",
                "page_01",
                "主图页",
                AiPageUiStatus.Ready,
                "可生成",
                "已通过",
                [new AiCandidateProjection(
                    "batch_results",
                    "style_01",
                    "frame_01",
                    "asset_front",
                    "internal.jpg",
                    1,
                    0.96m,
                    ["SUBJECT_COMPLETE"],
                    true,
                    "主商品框",
                    null,
                    "商品主体清晰完整",
                    "主图页")],
                [],
                Requirements: [requirement]);
            var projection = new AiBatchAnalysisProjection(
                "batch_results",
                AiBatchUiStatus.Ready,
                "分析完成",
                "开始生成",
                1,
                1,
                1,
                1,
                [new AiStyleAnalysisProjection(
                    "batch_results",
                    "style_01",
                    "款式 01",
                    AiStyleUiStatus.Ready,
                    "可生成",
                    [sourcePage],
                    1)],
                AiAnalysisSchedulingControlState.Running,
                null);
            var workflow = new RecordingAiBatchAnalysisWorkflow
            {
                RenderPreparation = new AiRenderPlanPreparation(
                    "batch_results",
                    AiRenderPreparationStatus.WaitingForPhotoshop,
                    "PHOTOSHOP_FORMAL_ADAPTER_NOT_AVAILABLE",
                    [new AiRenderPagePreparation(
                        "style_01",
                        "page_01",
                        AiRenderPagePreparationStatus.ReadyToRender,
                        "AI_RENDER_PLAN_VALID",
                        "{}",
                        "sha256:" + new string('a', 64))])
            };
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiBatchAnalysisWorkflow: workflow);
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                ApplyAiProjection(viewModel, projection);
                SetAiScreen(viewModel);
                FlushLayout(window);

                viewModel.PrepareRenderingCommand.Execute(null);
                FlushLayout(window);

                Assert.Equal(1, workflow.PrepareRenderingCount);
                Assert.True(viewModel.IsResultOverviewVisible);
                Assert.Contains("安全停在待渲染", viewModel.ResultStatusText, StringComparison.Ordinal);
                Assert.Contains("尚无正式输出", viewModel.ResultFormalOutputText, StringComparison.Ordinal);
                Assert.Equal(
                    ["全部 1", "需检查 1", "完成但有问题 0", "运行失败 0"],
                    viewModel.ResultFilters.Select(filter => filter.DisplayText).ToArray());
                Assert.True(FindByAutomationId<Grid>(window, "ResultOverviewPage").IsVisible);

                var page = Assert.Single(Assert.Single(viewModel.ResultStyleGroups).Pages);
                Assert.Equal("待渲染", page.StatusText);
                Assert.False(page.HasFormalOutput);
                viewModel.OpenResultPageCommand.Execute(page);
                FlushLayout(window);

                Assert.True(viewModel.IsResultDetailVisible);
                Assert.True(FindByAutomationId<Grid>(window, "ResultDetailPage").IsVisible);
                Assert.Equal("主图页", FindByAutomationId<TextBlock>(window, "ResultDetailTitle").Text);
                Assert.False(FindByAutomationId<Button>(window, "RegenerateResultPageButton").IsEnabled);
                Assert.False(FindByAutomationId<Button>(window, "EditResultPageInPhotoshopButton").IsEnabled);
                var candidateList = FindByAutomationId<ItemsControl>(window, "ResultCandidateList");
                var candidateContainer = Assert.IsType<ContentPresenter>(
                    candidateList.ItemContainerGenerator.ContainerFromIndex(0));
                var candidateAutomationName = AutomationProperties.GetName(candidateContainer);
                Assert.Contains("Top-1", candidateAutomationName, StringComparison.Ordinal);
                Assert.DoesNotContain(@"C:\", candidateAutomationName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("front.jpg", candidateAutomationName, StringComparison.OrdinalIgnoreCase);

                SetWindowSize(window, 1440, 900);
                SetWindowSize(window, 1100, 760);
                Assert.True(viewModel.IsResultDetailNarrowLayout);
                Assert.Equal(172, FindByAutomationId<Border>(window, "ResultCandidatePanel").ActualWidth, precision: 0);
                Assert.True(FindByAutomationId<Border>(window, "ResultPagePreviewPanel").ActualWidth >= 400);
                Assert.Equal(216, FindByAutomationId<Border>(window, "ResultAdjustmentPanel").ActualWidth, precision: 0);
                var bounds = FindByAutomationId<Grid>(window, "ResultDetailPage")
                    .TransformToAncestor(window)
                    .TransformBounds(new Rect(FindByAutomationId<Grid>(window, "ResultDetailPage").RenderSize));
                Assert.True(bounds.Left >= 0);
                Assert.True(bounds.Right <= window.ActualWidth + 0.5);
            }
            finally
            {
                viewModel.Dispose();
                window.Close();
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
        });
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

    private static TemplatePageRowViewModel CreateReferenceLearningPage()
    {
        var inspection = new PsdInspectionDocument(
            new string('b', 64),
            4,
            new PsdCanvasInspection(790, 1252, 8, "RGB"),
            1,
            1,
            0,
            0,
            0,
            "sha256:" + new string('c', 64),
            new PsdInspectionTiming(1, 1, 2),
            1024,
            null,
            []);
        var page = new TemplateDraftPage(
            new TemplatePageProjection(
                "page_learning_ui",
                0,
                "详情页",
                TemplatePageContentTypeOption.NormalComposition,
                TemplateGenerationModeOption.OncePerProduct,
                null,
                true,
                "resource_learning_ui"),
            new ImportedTemplateResource(
                "resource_learning_ui",
                "group_learning_ui",
                "revision_learning_ui",
                "templates/groups/group_learning_ui/drafts/revision_learning_ui/staging/resource_learning_ui/template.psd",
                4,
                new string('b', 64),
                "sha256:" + new string('c', 64),
                inspection,
                new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero)),
            true,
            null);
        return new TemplatePageRowViewModel(page, _ => { });
    }

    private static ReferenceLearningDocument CreateReferenceLearningDocument(bool inferredSemantic = false)
    {
        var photo = new ReferencePhotoFingerprint(
            "asset_learning_front",
            "front.png",
            "front.png",
            68,
            new string('a', 64),
            "image/png",
            1,
            1);
        var candidate = new ReferenceFrameCandidate(
            "learn_frame_7",
            "page_learning_ui",
            [0],
            7,
            "图层 7",
            "sha256:" + new string('c', 64),
            true,
            true,
            false,
            false,
            false,
            255,
            "normal",
            [0, 0, 790, 0, 790, 1252, 0, 1252],
            ReferenceMatchState.Exact,
            photo.AssetId,
            photo.RelativePath,
            photo.Sha256,
            inferredSemantic ? "完整正面图" : "",
            1,
            Desktop.Domain.Templates.LayoutIntent.Create(
                "subject_cover",
                1,
                0,
                0,
                Desktop.Domain.Templates.TemplateRect.Create(0, 0, 790, 1252)));
        var candidates = new[] { candidate };
        var photos = new[] { photo };
        return new ReferenceLearningDocument(
            "learn_ui",
            "page_learning_ui",
            "templates/groups/group_learning_ui/drafts/revision_learning_ui/staging/resource_learning_ui/template.psd",
            "sha256:" + new string('b', 64),
            4,
            "sha256:" + new string('c', 64),
            new PsdCanvasInspection(790, 1252, 8, "RGB"),
            "sha256:" + new string('d', 64),
            photos,
            candidates,
            ReferenceLearningState.AwaitingReview,
            ReferenceLearningDigest.Compute(
                "sha256:" + new string('b', 64),
                "sha256:" + new string('c', 64),
                "sha256:" + new string('d', 64),
                photos,
                candidates),
            new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));
    }

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

    private static void ApplyAiProjection(
        MainWindowViewModel viewModel,
        Desktop.Application.Ai.AiBatchAnalysisProjection projection)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ApplyAiProjection",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AI projection method not found.");
        method.Invoke(viewModel, [projection]);
    }

    private static void SetAiScreen(MainWindowViewModel viewModel)
    {
        var screenField = typeof(MainWindowViewModel).GetField(
            "_batchScreen",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Batch screen field not found.");
        var value = Enum.Parse(screenField.FieldType, "AiAnalysis");
        screenField.SetValue(viewModel, value);
        var notify = typeof(MainWindowViewModel).GetMethod(
            "NotifyBatchScreenChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Batch screen notification not found.");
        notify.Invoke(viewModel, null);
    }

    private static void SetCurrentBatchId(MainWindowViewModel viewModel, string batchId)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_currentBatchId",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Current batch field not found.");
        field.SetValue(viewModel, batchId);
    }

    private static System.Windows.Threading.DispatcherTimer GetAiRefreshTimer(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_aiRefreshTimer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AI refresh timer field not found.");
        return Assert.IsType<System.Windows.Threading.DispatcherTimer>(field.GetValue(viewModel));
    }

    private static void RunAiAutoRefreshOnce(MainWindowViewModel viewModel)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "AutoRefreshAiAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AI auto-refresh method not found.");
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(viewModel, null));
        task.GetAwaiter().GetResult();
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

    private static void SetWindowSize(Window window, double width, double height)
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
        Assert.Equal(fittedBounds.Width, window.ActualWidth, precision: 0);
        Assert.Equal(fittedBounds.Height, window.ActualHeight, precision: 0);
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
        ResetWpfApplicationState();
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

        ResetWpfApplicationState();
    }

    private static void ResetWpfApplicationState()
    {
        var applicationType = typeof(System.Windows.Application);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        applicationType.GetField("_appInstance", flags)?.SetValue(null, null);
        applicationType.GetField("_appCreatedInThisAppDomain", flags)?.SetValue(null, false);
        applicationType.GetField("_isShuttingDown", flags)?.SetValue(null, false);
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

        public int RestoreCount { get; private set; }

        public BatchInputProjection? RestoreProjection { get; set; }

        public TaskCompletionSource? ScanGate { get; set; }

        public BatchInputUiStatus NextStatus { get; set; } = BatchInputUiStatus.ScanReady;

        public string NextRecognitionText { get; set; } = "已识别为正常批次 · 2 款";

        public string NextStatusMessage { get; set; } = "扫描完成，可进入后续分析。";

        public Task<BatchInputProjection?> RestoreLatestDraftAsync(CancellationToken cancellationToken)
        {
            RestoreCount++;
            return Task.FromResult(RestoreProjection);
        }

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

    private sealed class RecordingAiBatchAnalysisWorkflow : IAiBatchAnalysisWorkflow
    {
        private AiUploadConsentRecord? _consent;

        public int GrantCount { get; private set; }

        public int RefreshCount { get; private set; }

        public int LockCount { get; private set; }

        public int PrepareRenderingCount { get; private set; }

        public (string BatchId, string StyleId, string RequirementId, string AssetId)? LastLock { get; private set; }

        public AiBatchAnalysisProjection? RefreshProjection { get; set; }

        public AiBatchAnalysisProjection? LockProjection { get; set; }

        public AiRenderPlanPreparation? RenderPreparation { get; set; }

        public Task<AiUploadConsentRecord?> GetUploadConsentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_consent);

        public Task<AiUploadConsentRecord> GrantUploadConsentAsync(CancellationToken cancellationToken)
        {
            GrantCount++;
            _consent = new AiUploadConsentRecord(
                AiConsentScopes.AnalysisCopy,
                "consent-ui-test",
                AiUploadConsentState.Granted,
                AiConsentPolicy.Version,
                new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.FromHours(8)));
            return Task.FromResult(_consent);
        }

        public Task<AiUploadConsentRecord> RevokeUploadConsentAsync(CancellationToken cancellationToken)
        {
            _consent = new AiUploadConsentRecord(
                AiConsentScopes.AnalysisCopy,
                "consent-ui-test",
                AiUploadConsentState.Revoked,
                AiConsentPolicy.Version,
                new DateTimeOffset(2026, 8, 31, 10, 1, 0, TimeSpan.FromHours(8)));
            return Task.FromResult(_consent);
        }

        public Task<AiBatchAnalysisProjection> StartAsync(string batchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AiBatchAnalysisProjection?> RefreshAsync(string batchId, CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.FromResult(RefreshProjection);
        }

        public Task<AiBatchAnalysisProjection> PauseAsync(string batchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AiBatchAnalysisProjection> ResumeAsync(string batchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AiBatchAnalysisProjection> CancelAsync(string batchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AiBatchAnalysisProjection> WaitForNetworkAsync(string batchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AiBatchAnalysisProjection> LockSelectionAsync(
            string batchId,
            string styleId,
            string requirementId,
            string assetId,
            CancellationToken cancellationToken)
        {
            LockCount++;
            LastLock = (batchId, styleId, requirementId, assetId);
            return Task.FromResult(LockProjection ?? throw new InvalidOperationException("TEST_LOCK_PROJECTION_MISSING"));
        }

        public Task<AiBatchAnalysisProjection> AnalyzeSelectedAssetAsync(
            string batchId,
            string styleId,
            string assetId,
            bool includeSizeTableSource,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiRenderPlanPreparation> PrepareRenderingAsync(
            string batchId,
            CancellationToken cancellationToken)
        {
            PrepareRenderingCount++;
            return Task.FromResult(RenderPreparation
                ?? throw new InvalidOperationException("TEST_RENDER_PREPARATION_MISSING"));
        }
    }

    private sealed class RecordingReferenceLearningService(ReferenceLearningDocument initial)
        : IReferenceLearningService
    {
        private ReferenceLearningDocument _document = initial;

        public int StrictResumeCount { get; private set; }

        public int LastReviewCount { get; private set; }

        public void Reset(ReferenceLearningDocument document) => _document = document;

        public Task<ReferenceLearningResult> AnalyzeAsync(
            ReferenceLearningRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
            ReferenceLearningResult.Success(_document));

        public Task<ReferenceLearningResult> ApplyReviewAsync(
            ReferenceLearningDocument document,
            IReadOnlyList<ReferenceFrameReview> reviews,
            CancellationToken cancellationToken = default)
        {
            LastReviewCount = reviews.Count;
            var review = Assert.Single(reviews);
            var candidate = Assert.Single(document.Candidates);
            var photo = Assert.Single(document.ReferencePhotos);
            var reviewed = candidate with
            {
                MatchState = ReferenceMatchState.Exact,
                ReferenceAssetId = review.ReferenceAssetId ?? photo.AssetId,
                ReferencePhotoRelativePath = photo.RelativePath,
                SemanticLabel = review.SemanticLabel ?? candidate.SemanticLabel,
                Confidence = 1,
                ReviewDecision = review.Decision,
                LayoutIntent = Desktop.Domain.Templates.LayoutIntent.Create(
                    candidate.LayoutIntent.Intent,
                    review.Scale ?? candidate.LayoutIntent.Scale,
                    review.OffsetX ?? candidate.LayoutIntent.OffsetX,
                    review.OffsetY ?? candidate.LayoutIntent.OffsetY,
                    candidate.LayoutIntent.SafeBounds)
            };
            var state = review.Decision == ReferenceReviewDecision.Accepted
                ? ReferenceLearningState.ReadyToCommit
                : ReferenceLearningState.RequiresManualCalibration;
            _document = document.WithCandidates(
                [reviewed],
                state,
                state == ReferenceLearningState.ReadyToCommit ? null : "需要人工校准。",
                document.UpdatedAt.AddSeconds(1));
            return Task.FromResult(ReferenceLearningResult.Success(_document));
        }

        public Task<ReferenceLearningResult> ResumeAsync(
            string learningId,
            CancellationToken cancellationToken = default) => Task.FromResult(
            ReferenceLearningResult.Success(_document));

        public Task<ReferenceLearningResult> ResumeAsync(
            ReferenceLearningRequest request,
            CancellationToken cancellationToken = default)
        {
            StrictResumeCount++;
            return Task.FromResult(ReferenceLearningResult.Success(_document));
        }

        public Task<ReferenceLearningResult> CancelAsync(
            ReferenceLearningDocument document,
            CancellationToken cancellationToken = default)
        {
            _document = document with
            {
                State = ReferenceLearningState.Cancelled,
                UpdatedAt = document.UpdatedAt.AddSeconds(1),
                BlockReason = "用户取消学习。"
            };
            return Task.FromResult(ReferenceLearningResult.Success(_document));
        }
    }

    private sealed class RecordingReferenceLearningCommitService : IReferenceLearningCommitService
    {
        public bool CommitCalled { get; private set; }

        public Task<ReferenceLearningCommitResult> CommitAsync(
            ReferenceLearningDocument document,
            TemplateDraftSession draftSession,
            CancellationToken cancellationToken = default) => CommitAsync(
            [document],
            draftSession,
            cancellationToken);

        public Task<ReferenceLearningCommitResult> CommitAsync(
            IReadOnlyList<ReferenceLearningDocument> documents,
            TemplateDraftSession draftSession,
            CancellationToken cancellationToken = default)
        {
            CommitCalled = true;
            var document = Assert.Single(documents) with
            {
                State = ReferenceLearningState.Committed,
                UpdatedAt = documents[0].UpdatedAt.AddSeconds(1)
            };
            var details = new TemplateGroupDetails(
                draftSession.GroupId,
                draftSession.GroupName,
                draftSession.DraftRevisionId,
                draftSession.RevisionNo,
                [new TemplatePageProjection(
                    "page_learning_ui",
                    0,
                    "详情页",
                    TemplatePageContentTypeOption.NormalComposition,
                    TemplateGenerationModeOption.OncePerProduct,
                    null,
                    true,
                    "resource_learning_ui")],
                true,
                document.UpdatedAt);
            return Task.FromResult(ReferenceLearningCommitResult.Success(document, details));
        }
    }

    private sealed class RecordingSizeTableEditingWorkflow(SizeTableEditorDocument document)
        : ISizeTableEditingWorkflow
    {
        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public Task<SizeTableEditorLoadResult> LoadAsync(
            string batchId,
            string styleId,
            string pageId,
            CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(new SizeTableEditorLoadResult(
                SizeTableEditorLoadStatus.Loaded,
                document,
                null));
        }

        public Task<SizeTableEditorSaveResult> SaveAsync(
            SizeTableEditorSaveCommand command,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(new SizeTableEditorSaveResult(
                SizeTableEditorSaveStatus.Saved,
                document with { Revision = command.ExpectedRevision + 1 },
                null));
        }
    }
}
