using System.Runtime.ExceptionServices;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Globalization;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
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
    public void BoundedPreviewConverterDoesNotDecodeOriginalPhotoDimensions()
    {
        RunInSta(() =>
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "desktop-bounded-preview-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var source = new WriteableBitmap(
                    2400,
                    1200,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null);
                var pixels = new byte[2400 * 1200 * 4];
                source.WritePixels(
                    new Int32Rect(0, 0, 2400, 1200),
                    pixels,
                    2400 * 4,
                    0);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var output = File.Create(path))
                {
                    encoder.Save(output);
                }

                var converted = new BoundedPreviewImageConverter().Convert(
                    path,
                    typeof(BitmapImage),
                    null,
                    CultureInfo.InvariantCulture);
                var preview = Assert.IsType<BitmapImage>(converted);

                Assert.Equal(320, preview.PixelWidth);
                Assert.Equal(160, preview.PixelHeight);
                Assert.True(preview.IsFrozen);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        });
    }

    [Fact]
    public void WpfHostReusesSingleApplicationAcrossSequentialWindows()
    {
        Desktop.App.App? firstApplication = null;
        System.Windows.Threading.Dispatcher? firstDispatcher = null;

        RunInSta(() =>
        {
            (firstApplication, firstDispatcher) = OpenWindowAndReadApplicationResources();
        });

        RunInSta(() =>
        {
            var (secondApplication, secondDispatcher) = OpenWindowAndReadApplicationResources();

            Assert.Same(firstApplication, secondApplication);
            Assert.Same(firstDispatcher, secondDispatcher);
        });
    }

    [Fact]
    public void StartupDiagnosticsWritesRedactedFailureEvidence()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "desktop-startup-diagnostics-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var written = StartupDiagnostics.TryLogFailure(
                new Desktop.Infrastructure.Persistence.DesktopDataOptions(root),
                "DESKTOP_STARTUP_FAILED",
                "store_initialization",
                new InvalidOperationException(@"Cannot open C:\Customers\secret\photo.psd"));

            Assert.True(written);
            var log = File.ReadAllText(Path.Combine(root, StartupDiagnostics.DisplayLogPath));
            Assert.Contains("DESKTOP_STARTUP_FAILED", log, StringComparison.Ordinal);
            Assert.Contains("store_initialization", log, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
            Assert.Contains("[PATH]", log, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\Customers", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("photo.psd", log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
            var application = Assert.IsAssignableFrom<System.Windows.Application>(
                System.Windows.Application.Current);
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
            }
        });
    }

    [Fact]
    public void CalibrationFrameListRefreshesWhenLoadedPageChanges()
    {
        RunInSta(() =>
        {
            var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var setTemplateScreen = typeof(MainWindowViewModel).GetMethod(
                "SetTemplateScreen",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var calibrationScreen = Enum.Parse(
                setTemplateScreen.GetParameters()[0].ParameterType,
                "Calibration");
            setTemplateScreen.Invoke(viewModel, [calibrationScreen]);

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                var frameList = FindByAutomationId<ListBox>(window, "CalibrationFrameList");

                ApplyCalibrationDraft(
                    viewModel,
                    CreateCalibrationDraft(
                        "revision_page_one",
                        "page_one",
                        "frame_page_one",
                        "第一页图片框"));
                FlushLayout(window);

                var firstFrame = Assert.Single(frameList.Items.Cast<TemplateFrameProjection>());
                Assert.Equal("frame_page_one", firstFrame.FrameId);
                Assert.Equal("第一页图片框", firstFrame.LayerName);
                Assert.Equal("frame_page_one", frameList.SelectedValue);

                ApplyCalibrationDraft(
                    viewModel,
                    CreateCalibrationDraft(
                        "revision_page_two",
                        "page_two",
                        "frame_page_two",
                        "第二页图片框"));
                FlushLayout(window);

                var secondFrame = Assert.Single(frameList.Items.Cast<TemplateFrameProjection>());
                Assert.Equal("frame_page_two", secondFrame.FrameId);
                Assert.Equal("第二页图片框", secondFrame.LayerName);
                Assert.Equal("frame_page_two", frameList.SelectedValue);
                Assert.DoesNotContain(
                    frameList.Items.Cast<TemplateFrameProjection>(),
                    frame => frame.FrameId == "frame_page_one");
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
            }
        });
    }

    [Fact]
    public void CalibrationFrameSelectionRestoresEachFramesOwnRuleAssetAndGeometry()
    {
        RunInSta(() =>
        {
            var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
            try
            {
                ApplyCalibrationDraft(viewModel, CreateMultiFrameCalibrationDraft());

                viewModel.SelectCalibrationFrame("frame_sleeve");
                Assert.Equal("asset_sleeve", viewModel.CalibrationAssetIdInput);
                Assert.Equal("袖口细节图", viewModel.CalibrationRequirementInput);
                Assert.Equal(1.45, viewModel.CalibrationScaleInput);
                Assert.Equal(-9, viewModel.CalibrationOffsetXInput);
                Assert.Equal(7, viewModel.CalibrationOffsetYInput);

                viewModel.SelectCalibrationFrame("frame_front");
                Assert.Equal("asset_front", viewModel.CalibrationAssetIdInput);
                Assert.Equal("商品正面图", viewModel.CalibrationRequirementInput);
                Assert.Equal(1.1, viewModel.CalibrationScaleInput);
                Assert.Equal(3, viewModel.CalibrationOffsetXInput);
                Assert.Equal(-2, viewModel.CalibrationOffsetYInput);

                viewModel.SelectCalibrationFrame("frame_sleeve");
                Assert.Equal("袖口细节图", viewModel.CalibrationRequirementInput);
                Assert.Equal("asset_sleeve", viewModel.CalibrationDraft!.SelectedAssetId);
            }
            finally
            {
                viewModel.Dispose();
            }
        });
    }

    [Fact]
    public async Task StaleCalibrationPageLoadCannotReplaceCurrentPage()
    {
        var calibration = new DeferredCalibrationService();
        var viewModel = new MainWindowViewModel(
            new AppLaunchOptions(true, null),
            templateWorkspace: null,
            templateCalibration: calibration);
        var session = new TemplateDraftSession(
            "group_calibration_race",
            "校准切页竞态",
            "revision_calibration_race",
            1,
            new HashSet<string>(StringComparer.Ordinal));
        typeof(MainWindowViewModel).GetField(
                "_templateDraft",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, session);

        var firstPage = CreateReferenceLearningPage("page_race_first");
        var secondPage = CreateReferenceLearningPage("page_race_second");
        viewModel.SelectedTemplatePage = secondPage;
        var setTemplateScreen = typeof(MainWindowViewModel).GetMethod(
            "SetTemplateScreen",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var calibrationScreen = Enum.Parse(
            setTemplateScreen.GetParameters()[0].ParameterType,
            "Calibration");
        setTemplateScreen.Invoke(viewModel, [calibrationScreen]);

        var loadMethod = typeof(MainWindowViewModel).GetMethod(
            "LoadCalibrationPageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var versionField = typeof(MainWindowViewModel).GetField(
            "_calibrationPageLoadVersion",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        versionField.SetValue(viewModel, 2L);
        var firstLoad = Assert.IsAssignableFrom<Task>(loadMethod.Invoke(
            viewModel,
            [firstPage, 1L]));
        var secondLoad = Assert.IsAssignableFrom<Task>(loadMethod.Invoke(
            viewModel,
            [secondPage, 2L]));

        try
        {
            calibration.Complete(
                firstPage.PageId,
                CreateCalibrationDraft(
                    "revision_race_first",
                    firstPage.PageId,
                    "frame_race_first",
                    "第一页图片框"));
            await firstLoad;
            Assert.Null(viewModel.CalibrationDraft);

            calibration.Complete(
                secondPage.PageId,
                CreateCalibrationDraft(
                    "revision_race_second",
                    secondPage.PageId,
                    "frame_race_second",
                    "第二页图片框"));
            await secondLoad;

            Assert.Equal(secondPage.PageId, viewModel.CalibrationDraft?.PageId);
            var frame = Assert.Single(viewModel.CalibrationFrameItems.Cast<TemplateFrameProjection>());
            Assert.Equal("frame_race_second", frame.FrameId);
        }
        finally
        {
            viewModel.Dispose();
        }
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
        Assert.Null(workflow.Submissions[^1].BatchId);
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
        Assert.Null(workflow.Submissions[^1].BatchId);
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
                Assert.True(FindByAutomationId<Border>(window, "ReferenceLearningRuleSummaryPanel").IsVisible);
                Assert.True(FindByAutomationId<ListBox>(window, "ReferenceLearningPhotoList").IsVisible);
                Assert.True(FindByAutomationId<StackPanel>(window, "ReferenceLearningReviewActions").IsVisible);
                Assert.Equal("未复核", viewModel.ReferenceLearningDecisionText);
                Assert.Equal("front.png", viewModel.SelectedReferenceLearningPhoto!.DisplayName);
                Assert.Null(viewModel.CalibrationPreviewImagePath);
                Assert.Null(viewModel.CalibrationPreviewImageSource);
                Assert.Equal(
                    "左侧选择图片框；中间查看 PSD 位置；右侧确认或修改素材、用途和布局",
                    viewModel.CalibrationCanvasInstruction);
                Assert.Equal(
                    Visibility.Visible,
                    FindByAutomationId<Expander>(window, "AdvancedCalibrationExpander").Visibility);
                Assert.Equal(
                     Visibility.Collapsed,
                     FindByAutomationId<Button>(window, "ReturnReferenceLearningToManualButton").Visibility);
                Assert.True(FindByAutomationId<TextBox>(window, "CalibrationRequirementInput").IsEnabled);
                Assert.False(FindByAutomationId<Button>(window, "SaveTemplateGroupButton").IsEnabled);
                Assert.False(FindByAutomationId<Button>(window, "ApplyAndConfirmCalibrationButton").IsEnabled);
                Assert.True(FindByAutomationId<Button>(window, "RejectReferenceLearningFrameButton").IsEnabled);
                Assert.True(FindByAutomationId<Button>(window, "CancelReferenceLearningButton").IsVisible);

                viewModel.CalibrationRequirementInput = "商品完整正面图";
                var firstFrameId = viewModel.SelectedReferenceLearningFrame!.FrameId;
                var secondCandidate = viewModel.SelectedReferenceLearningFrame.Candidate with
                {
                    FrameId = "learn_frame_8",
                    LayerId = 8,
                    LayerName = "图层 8"
                };
                viewModel.ReferenceLearningFrames.Add(new ReferenceLearningFrameViewModel(secondCandidate));
                viewModel.SelectCalibrationFrame("learn_frame_8");
                viewModel.CalibrationRequirementInput = "商品完整背面图";
                viewModel.SelectCalibrationFrame(firstFrameId);
                Assert.Equal("商品完整正面图", viewModel.CalibrationRequirementInput);
                Assert.Equal("front.png", viewModel.SelectedReferenceLearningPhoto!.DisplayName);
                FlushLayout(window);
                Assert.True(FindByAutomationId<Button>(window, "ApplyAndConfirmCalibrationButton").IsEnabled);
                var applyReview = typeof(MainWindowViewModel).GetMethod(
                    "ApplyAndConfirmCalibrationAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(applyReview.Invoke(viewModel, null))
                    .GetAwaiter()
                    .GetResult();
                FlushLayout(window);

                Assert.Equal("已确认规则", viewModel.ReferenceLearningDecisionText);
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
            }
        });
    }

    [Fact]
    public void TemplateLibraryRowOpensAndInlineActionsRenameOrDeleteWithoutAccidentalNavigation()
    {
        RunTemplateLibraryScenario((window, viewModel, workspace) =>
        {
            var list = FindByAutomationId<ItemsControl>(window, "TemplateGroupList");
            var rowButton = FindByAutomationId<Button>(list, "OpenTemplateGroupRowButton");
            var rowChrome = FindByAutomationId<Border>(list, "TemplateGroupRowChrome");
            var renameButton = FindByAutomationId<Button>(list, "RenameTemplateGroupButton");
            var deleteButton = FindByAutomationId<Button>(list, "DeleteTemplateGroupFromLibraryButton");
            Assert.True(rowChrome.ActualHeight >= 70, $"Template row chrome collapsed to {rowChrome.ActualHeight}px.");
            Assert.True(rowButton.Focusable);
            Assert.True(renameButton.Focusable);
            Assert.True(deleteButton.Focusable);
            Assert.Same(viewModel.RenameTemplateCommand, renameButton.Command);
            Assert.Same(viewModel.DeleteTemplateFromLibraryCommand, deleteButton.Command);
            Assert.IsType<TemplateLibraryCardViewModel>(renameButton.CommandParameter);
            Assert.IsType<TemplateLibraryCardViewModel>(deleteButton.CommandParameter);
            Assert.Contains("打开模板组", AutomationProperties.GetName(rowButton), StringComparison.Ordinal);
            Assert.DoesNotContain(
                EnumerateVisualDescendants<TextBlock>(list),
                text => string.Equals(text.Text, "查看", StringComparison.Ordinal));

            InvokeButton(deleteButton);
            DrainDispatcher();
            FlushLayout(window);
            Assert.Equal("删除模板组？", viewModel.TemplateDialogTitle);
            InvokeButton(FindByAutomationId<Button>(window, "TemplateDialogSecondaryButton"));
            DrainDispatcher();
            Assert.Equal(0, workspace.DeleteCount);
            Assert.Equal(0, workspace.GetDetailsCount);

            InvokeButton(rowButton);
            DrainDispatcher();
            FlushLayout(window);
            Assert.Equal(1, workspace.GetDetailsCount);
            Assert.True(viewModel.IsTemplateDetailsVisible);
        });
    }

    [Fact]
    public void TemplateLibraryInlineRenameValidatesAndUpdatesWithoutOpeningDetails()
    {
        RunTemplateLibraryScenario((window, viewModel, workspace) =>
        {
            var renameButton = FindByAutomationId<Button>(window, "RenameTemplateGroupButton");
            InvokeButton(renameButton);
            DrainDispatcher();
            FlushLayout(window);

            Assert.Equal(0, workspace.GetDetailsCount);
            Assert.Equal("重命名模板组", viewModel.TemplateDialogTitle);
            var renameInput = FindByAutomationId<TextBox>(window, "TemplateRenameInput");
            var primaryButton = FindByAutomationId<Button>(window, "TemplateDialogPrimaryButton");
            Assert.Equal("模板库交互测试", renameInput.Text);

            renameInput.Text = "   ";
            renameInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            FlushLayout(window);
            Assert.False(primaryButton.IsEnabled);

            renameInput.Text = "  已重命名模板  ";
            renameInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            FlushLayout(window);
            Assert.True(primaryButton.IsEnabled);
            InvokeButton(primaryButton);
            DrainDispatcher();
            FlushLayout(window);

            Assert.Equal(1, workspace.RenameCount);
            Assert.Equal("已重命名模板", Assert.Single(viewModel.TemplateGroups).Name);
            Assert.Equal(0, workspace.GetDetailsCount);
            Assert.True(viewModel.IsTemplateLibraryVisible);
            Assert.Contains("已重命名", viewModel.TemplateStatusMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TemplateLibraryInlineDeleteRemovesGroupWithoutOpeningDetails()
    {
        RunTemplateLibraryScenario((window, viewModel, workspace) =>
        {
            InvokeButton(FindByAutomationId<Button>(window, "DeleteTemplateGroupFromLibraryButton"));
            DrainDispatcher();
            FlushLayout(window);
            Assert.Equal("删除模板组？", viewModel.TemplateDialogTitle);
            Assert.True(viewModel.TemplateDialogIsDestructive);

            InvokeButton(FindByAutomationId<Button>(window, "TemplateDialogPrimaryButton"));
            DrainDispatcher();
            FlushLayout(window);

            Assert.Equal(1, workspace.DeleteCount);
            Assert.Empty(viewModel.TemplateGroups);
            Assert.Equal(0, workspace.GetDetailsCount);
            Assert.Equal(
                Visibility.Visible,
                FindByAutomationId<Border>(window, "TemplateLibraryEmptyState").Visibility);
            var deleteStatus = FindByAutomationId<TextBlock>(window, "TemplateLibraryStatus");
            Assert.Equal(Visibility.Visible, deleteStatus.Visibility);
            Assert.Contains("用户源 PSD", deleteStatus.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TemplateDetailsEditButtonsBindToPageEditCommand()
    {
        RunInSta(() =>
        {
            var workspace = new PageEditWorkspaceStub();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: workspace);
            var details = new TemplateGroupDetails(
                "group_edit_ui",
                "编辑入口测试",
                "revision_edit_ui",
                2,
                [
                    new TemplatePageProjection(
                        "page_edit_ui",
                        0,
                        "主图页",
                        TemplatePageContentTypeOption.NormalComposition,
                        TemplateGenerationModeOption.OncePerProduct,
                        null,
                        true,
                        "resource_edit_ui")
                ],
                true,
                DateTimeOffset.UtcNow);
            typeof(MainWindowViewModel).GetField(
                    "_templateDetails",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, details);
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.Key == "templates");
            var setTemplateScreen = typeof(MainWindowViewModel).GetMethod(
                "SetTemplateScreen",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var detailsScreen = Enum.Parse(
                setTemplateScreen.GetParameters()[0].ParameterType,
                "Details");
            setTemplateScreen.Invoke(viewModel, [detailsScreen]);

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                var detailPage = FindByAutomationId<Grid>(window, "TemplateDetailsPage");
                var editButtons = EnumerateVisualDescendants<Button>(detailPage)
                    .Where(button => string.Equals(button.Content?.ToString(), "编辑", StringComparison.Ordinal))
                    .ToArray();
                var editButton = Assert.Single(editButtons);
                Assert.Same(viewModel.EditTemplatePageCommand, editButton.Command);
                var parameter = Assert.IsType<TemplatePageDetailsRowViewModel>(editButton.CommandParameter);
                Assert.Equal("page_edit_ui", parameter.PageId);
                Assert.True(editButton.Command!.CanExecute(editButton.CommandParameter));

                editButton.Command.Execute(editButton.CommandParameter);
                Assert.True(viewModel.IsTemplatePageSettingsVisible);
                Assert.Equal("group_edit_ui", workspace.LastGroupId);
                Assert.Equal("page_edit_ui", workspace.LastPageId);
            }
            finally
            {
                viewModel.Dispose();
                window.Close();
            }
        });
    }

    [Fact]
    public void TemplateDetailsEditGroupButtonStartsFullTemplateEdit()
    {
        RunInSta(() =>
        {
            var workspace = new PageEditWorkspaceStub();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: workspace);
            var details = new TemplateGroupDetails(
                "group_edit_ui",
                "编辑入口测试",
                "revision_edit_ui",
                2,
                [
                    new TemplatePageProjection(
                        "page_edit_ui",
                        0,
                        "主图页",
                        TemplatePageContentTypeOption.NormalComposition,
                        TemplateGenerationModeOption.OncePerProduct,
                        null,
                        true,
                        "resource_edit_ui")
                ],
                true,
                DateTimeOffset.UtcNow);
            typeof(MainWindowViewModel).GetField(
                    "_templateDetails",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, details);
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.Key == "templates");
            var setTemplateScreen = typeof(MainWindowViewModel).GetMethod(
                "SetTemplateScreen",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var detailsScreen = Enum.Parse(
                setTemplateScreen.GetParameters()[0].ParameterType,
                "Details");
            setTemplateScreen.Invoke(viewModel, [detailsScreen]);

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                var editButton = FindByAutomationId<Button>(window, "EditTemplateGroupButton");
                Assert.Same(viewModel.EditTemplateGroupCommand, editButton.Command);
                Assert.True(editButton.Command!.CanExecute(null));

                editButton.Command.Execute(null);

                Assert.True(viewModel.IsTemplatePageSettingsVisible);
                Assert.Equal("group_edit_ui", workspace.LastFullGroupId);
            }
            finally
            {
                viewModel.Dispose();
                window.Close();
            }
        });
    }

    [Fact]
    public void ManualCalibrationRuleFieldBecomesEditableAfterDraftLoads()
    {
        RunInSta(() =>
        {
            var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var session = new TemplateDraftSession(
                "group_manual_rule_ui",
                "人工规则编辑",
                "revision_manual_rule_ui",
                1,
                new HashSet<string>(StringComparer.Ordinal));
            typeof(MainWindowViewModel).GetField(
                    "_templateDraft",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, session);
            viewModel.TemplatePages.Add(CreateReferenceLearningPage());
            viewModel.SelectedTemplatePage = viewModel.TemplatePages[0];
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.Key == "templates");

            var setTemplateScreen = typeof(MainWindowViewModel).GetMethod(
                "SetTemplateScreen",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var calibrationScreen = Enum.Parse(
                setTemplateScreen.GetParameters()[0].ParameterType,
                "Calibration");
            setTemplateScreen.Invoke(viewModel, [calibrationScreen]);

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                ApplyCalibrationDraft(viewModel, CreateCalibrationDraft("revision_manual_rule_ui"));
                FlushLayout(window);

                var ruleInput = FindByAutomationId<TextBox>(window, "CalibrationRequirementInput");
                Assert.True(viewModel.CanEditCalibration);
                Assert.True(viewModel.CanEditReferenceLearningRule);
                Assert.True(ruleInput.IsEnabled);

                ruleInput.Text = "侧面图";
                FlushLayout(window);
                Assert.Equal("侧面图", viewModel.CalibrationRequirementInput);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
            }
        });
    }

    [Fact]
    public void ReferenceLearningAiUnavailableIsVisibleAndRequiresUserOverride()
    {
        RunInSta(() =>
        {
            var sourceRoot = Path.Combine(
                Path.GetTempPath(),
                "tpl-learn-ai-unavailable-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            var photoPath = Path.Combine(sourceRoot, "front.png");
            File.WriteAllBytes(photoPath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

            var baseDocument = CreateReferenceLearningDocument();
            var candidate = baseDocument.Candidates[0] with
            {
                SemanticLabel = "商品展示图（与参考款保持同一视角，请确认）",
                Confidence = 0.72,
                SemanticMetadata = new ReferenceSemanticMetadata(
                    ReferenceSemanticSource.AiUnavailable,
                    "reference-learning-ai/v2",
                    "reference-learning-ai-result/v2",
                    "doubao-seed-2.1-pro-260628",
                    ReferenceLearningAiProtocol.SemanticPromptVersion,
                    "sha256:" + new string('e', 64),
                    [],
                    [])
            };
            var document = baseDocument.WithCandidates(
                [candidate],
                ReferenceLearningState.AwaitingReview,
                "请求超时，结果状态未知，请查询或人工确认后再继续。",
                baseDocument.UpdatedAt.AddSeconds(1));
            var learning = new RecordingReferenceLearningService(document);
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                referenceLearningService: learning);
            var session = new TemplateDraftSession(
                "group_learning_ai_unavailable_ui",
                "AI 未完成警示",
                "revision_learning_ai_unavailable_ui",
                1,
                new HashSet<string>(StringComparer.Ordinal));
            var page = CreateReferenceLearningPage("page_learning_ai_unavailable_ui");
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
            typeof(MainWindowViewModel).GetMethod(
                    "SetTemplateCalibrationMode",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [TemplateCalibrationMode.ReferenceLearning]);
            Assert.IsAssignableFrom<Task>(typeof(MainWindowViewModel).GetMethod(
                    "StartReferenceLearningAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null))
                .GetAwaiter()
                .GetResult();

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                Assert.True(viewModel.IsReferenceLearningAiUnavailable);
                Assert.Contains("AI 语义识别未完成", viewModel.ReferenceLearningSemanticWarningText, StringComparison.Ordinal);
                Assert.Contains("请求超时", viewModel.ReferenceLearningSemanticWarningText, StringComparison.Ordinal);
                Assert.Equal(
                    Visibility.Visible,
                    FindByAutomationId<Border>(window, "ReferenceLearningAiUnavailableWarning").Visibility);
                Assert.False(FindByAutomationId<Button>(window, "ApplyAndConfirmCalibrationButton").IsEnabled);

                viewModel.CalibrationRequirementInput = "商品展示图（与参考款保持同一视角，请确认）";
                FlushLayout(window);
                Assert.False(FindByAutomationId<Button>(window, "ApplyAndConfirmCalibrationButton").IsEnabled);

                viewModel.CalibrationRequirementInput = "人物正面全身图";
                FlushLayout(window);
                Assert.True(FindByAutomationId<Button>(window, "ApplyAndConfirmCalibrationButton").IsEnabled);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
                if (Directory.Exists(sourceRoot))
                {
                    Directory.Delete(sourceRoot, recursive: true);
                }

            }
        });
    }

    [Fact]
    public void ReferenceLearningStartsWholeGroupSemanticAnalysisOnce()
    {
        RunInSta(() =>
        {
            var learning = new MultiPageReferenceLearningService(CreateReferenceLearningDocument());
            var semantic = new RecordingReferenceLearningAiService();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                referenceLearningService: learning,
                referenceLearningAiService: semantic);
            typeof(MainWindowViewModel).GetField("_templateDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, new TemplateDraftSession(
                    "group_learning_batch_ai", "整组语义分析", "revision_learning_batch_ai", 1,
                    new HashSet<string>(StringComparer.Ordinal)));
            typeof(MainWindowViewModel).GetField("_referenceLearningPhotoDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, Path.GetTempPath());
            viewModel.TemplatePages.Add(CreateReferenceLearningPage("page_batch_ai_01"));
            viewModel.TemplatePages.Add(CreateReferenceLearningPage("page_batch_ai_02"));
            viewModel.SelectedTemplatePage = viewModel.TemplatePages[0];
            typeof(MainWindowViewModel).GetMethod("SetTemplateCalibrationMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [TemplateCalibrationMode.ReferenceLearning]);

            var start = typeof(MainWindowViewModel).GetMethod(
                "StartReferenceLearningAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<Task>(start.Invoke(viewModel, null)).GetAwaiter().GetResult();

            Assert.Equal(1, semantic.BatchCalls);
            Assert.Equal(0, semantic.SingleCalls);
            Assert.Equal(2, semantic.LastBatchSize);
        });
    }

    [Fact]
    public void ReferenceLearningRefreshDoesNotLetCapturedPlaceholderMaskAiV2Rule()
    {
        RunInSta(() =>
        {

            var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var session = new TemplateDraftSession(
                "group_learning_refresh_ui",
                "刷新规则",
                "revision_learning_refresh_ui",
                1,
                new HashSet<string>(StringComparer.Ordinal));
            var page = CreateReferenceLearningPage();
            typeof(MainWindowViewModel).GetField("_templateDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, session);
            viewModel.TemplatePages.Add(page);
            viewModel.SelectedTemplatePage = page;
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.Key == "templates");

            var setScreen = typeof(MainWindowViewModel).GetMethod(
                "SetTemplateScreen", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var referenceScreen = Enum.Parse(
                setScreen.GetParameters()[0].ParameterType,
                "ReferenceLearning");
            setScreen.Invoke(viewModel, [referenceScreen]);

            var documents = (Dictionary<string, ReferenceLearningDocument>)typeof(MainWindowViewModel)
                .GetField("_referenceLearningDocuments", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel)!;
            var localPlaceholder = CreateReferenceLearningDocument().WithCandidates(
                [CreateReferenceLearningDocument().Candidates[0] with
                {
                    SemanticLabel = "商品展示图（与参考款保持同一视角，请确认）",
                    Confidence = 0.72
                }],
                ReferenceLearningState.AwaitingReview,
                null,
                DateTimeOffset.UtcNow);
            documents[page.PageId] = localPlaceholder;

            var display = typeof(MainWindowViewModel).GetMethod(
                "DisplayReferenceLearningPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            display.Invoke(viewModel, [page, null]);

            viewModel.CalibrationRequirementInput = localPlaceholder.Candidates[0].SemanticLabel;
            typeof(MainWindowViewModel).GetMethod(
                "CaptureReferenceLearningInputs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, null);

            var aiCandidate = localPlaceholder.Candidates[0] with
            {
                SemanticLabel = "领口与尺码标细节图",
                Confidence = 0.98,
                SemanticMetadata = new ReferenceSemanticMetadata(
                    ReferenceSemanticSource.AiV2,
                    "reference-learning-ai/v2",
                    "reference-learning-ai-result/v2",
                    "doubao-seed-2.1-pro-260628",
                    ReferenceLearningAiProtocol.SemanticPromptVersion,
                    "sha256:" + new string('f', 64),
                    ["领口内侧可见红色尺码标"],
                    ["排除完整人物和室内场景"])
            };
            documents[page.PageId] = localPlaceholder.WithCandidates(
                [aiCandidate],
                ReferenceLearningState.AwaitingReview,
                null,
                DateTimeOffset.UtcNow.AddSeconds(1));

            display.Invoke(viewModel, [page, null]);

            Assert.Equal("领口与尺码标细节图", viewModel.CalibrationRequirementInput);
            Assert.Equal("领口与尺码标细节图", viewModel.SelectedReferenceLearningFrame!.SemanticLabel);
            Assert.False(viewModel.IsReferenceLearningSemanticWarningVisible);

            viewModel.CalibrationRequirementInput = "领口尺码标特写图";
            typeof(MainWindowViewModel).GetMethod(
                "CaptureReferenceLearningInputs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, null);
            documents[page.PageId] = localPlaceholder.WithCandidates(
                [aiCandidate with { SemanticLabel = "领口局部细节图" }],
                ReferenceLearningState.AwaitingReview,
                null,
                DateTimeOffset.UtcNow.AddSeconds(2));
            display.Invoke(viewModel, [page, null]);

            Assert.Equal("领口尺码标特写图", viewModel.CalibrationRequirementInput);

            viewModel.Dispose();
        });
    }

    [Fact]
    public void TemplateEditFailureExplainsUnreadableResourceInsteadOfShowingGenericRestartHint()
    {
        var formatter = typeof(MainWindowViewModel).GetMethod(
            "ToTemplateOperationError",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var message = Assert.IsType<string>(formatter.Invoke(
            null,
            new object?[] { new System.Text.Json.JsonException("invalid inspection") }));

        Assert.Contains("模板内部资源记录无法读取", message, StringComparison.Ordinal);
        Assert.DoesNotContain("必要时重新启动应用", message, StringComparison.Ordinal);

        var unknown = Assert.IsType<string>(formatter.Invoke(
            null,
            new object?[] { new InvalidCastException("unexpected draft shape") }));
        Assert.Contains("程序内部：InvalidCastException", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceLearningOffersPageAndBatchConfirmationForQualifiedCandidates()
    {
        RunInSta(() =>
        {
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
    public void SettingsPageRendersAiCredentialCardAndKeepsKeyRedacted()
    {
        RunInSta(() =>
        {
            var credential = new RecordingAiCredentialManagement();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiCredentialManagement: credential);
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.Key == "settings");
                FlushLayout(window);

                Assert.True(FindByAutomationId<Border>(window, "AiCredentialCard").IsVisible);
                Assert.True(FindByAutomationId<PasswordBox>(window, "AiApiKeyInput").IsVisible);
                Assert.Equal("尚未配置 Key", FindByAutomationId<TextBlock>(window, "AiCredentialStatus").Text);
                Assert.False(FindByAutomationId<Button>(window, "RemoveAiCredentialButton").IsEnabled);

                viewModel.SetAiCredentialInput("ark-test-secret-9876");
                Assert.True(FindByAutomationId<Button>(window, "SaveAiCredentialButton").IsEnabled);
                viewModel.SaveAiCredentialCommand.Execute(null);
                FlushLayout(window);

                Assert.Equal(1, credential.SaveCount);
                Assert.Equal("••••9876", viewModel.AiCredentialMaskedSuffix);
                Assert.DoesNotContain("ark-test-secret", FindByAutomationId<TextBlock>(window, "AiCredentialStatus").Text, StringComparison.Ordinal);
                Assert.True(FindByAutomationId<Button>(window, "RemoveAiCredentialButton").IsEnabled);

                viewModel.RemoveAiCredentialCommand.Execute(null);
                FlushLayout(window);
                Assert.Equal(1, credential.RemoveCount);
                Assert.Equal(AiCredentialState.NotConfigured, viewModel.AiCredentialState);
                Assert.Equal("未显示", viewModel.AiCredentialMaskedSuffix);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FirstScanRequiresExplicitUncheckedUploadConsentAndContinuesAfterGrant()
    {
        RunInSta(() =>
        {
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
            }
        });
    }

    [Fact]
    public void ReferenceLearningRequiresExplicitUploadConsentBeforeSemanticAnalysis()
    {
        RunInSta(() =>
        {
            var sourceRoot = Path.Combine(
                Path.GetTempPath(),
                "tpl-learn-consent-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllBytes(Path.Combine(sourceRoot, "front.png"), [1]);

            var learning = new RecordingReferenceLearningService(CreateReferenceLearningDocument());
            var semantic = new RecordingReferenceLearningAiService();
            var workflow = new RecordingAiBatchAnalysisWorkflow();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiBatchAnalysisWorkflow: workflow,
                referenceLearningService: learning,
                referenceLearningAiService: semantic);
            var session = new TemplateDraftSession(
                "group_learning_consent_ui",
                "授权门模板",
                "revision_learning_consent_ui",
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
            typeof(MainWindowViewModel).GetMethod(
                    "SetTemplateCalibrationMode",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [TemplateCalibrationMode.ReferenceLearning]);

            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

                var start = typeof(MainWindowViewModel).GetMethod(
                    "StartReferenceLearningAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(start.Invoke(viewModel, null))
                    .GetAwaiter()
                    .GetResult();
                FlushLayout(window);

                Assert.True(viewModel.IsAiUploadConsentDialogVisible);
                Assert.Contains("参考学习", viewModel.ReferenceLearningStatusMessage, StringComparison.Ordinal);
                Assert.Equal(0, learning.StrictResumeCount);
                Assert.Equal(0, semantic.BatchCalls);
                Assert.Equal(
                    Visibility.Visible,
                    FindByAutomationId<Grid>(window, "AiUploadConsentDialog").Visibility);

                viewModel.AiUploadConsentAcknowledged = true;
                var confirm = typeof(MainWindowViewModel).GetMethod(
                    "ConfirmAiUploadConsentAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsAssignableFrom<Task>(confirm.Invoke(viewModel, null))
                    .GetAwaiter()
                    .GetResult();
                FlushLayout(window);

                Assert.False(viewModel.IsAiUploadConsentDialogVisible);
                Assert.Equal(1, workflow.GrantCount);
                Assert.Equal(1, learning.StrictResumeCount);
                Assert.Equal(1, semantic.BatchCalls);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
                if (Directory.Exists(sourceRoot))
                {
                    Directory.Delete(sourceRoot, recursive: true);
                }

            }
        });
    }

    [Fact]
    public void AiAnalysisPageSupportsWideNarrowLongListUiaAndChineseStatus()
    {
        RunInSta(() =>
        {
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
                Assert.Contains("已处理 40/40 页", viewModel.AiBatchProgressText, StringComparison.Ordinal);
                var scroll = FindByAutomationId<ScrollViewer>(window, "AiAnalysisScrollViewer");
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.NotNull(FindByAutomationId<FrameworkElement>(window, "PauseAiAnalysisButton"));
                Assert.NotNull(FindByAutomationId<FrameworkElement>(window, "ResumeAiAnalysisButton"));
                Assert.NotNull(FindByAutomationId<FrameworkElement>(window, "CancelAiAnalysisButton"));
                Assert.True(FindByAutomationId<Button>(window, "EnterGenerationButton").IsEnabled);

                var firstStyle = viewModel.AiStyles[0];
                firstStyle.IsExpanded = true;
                FlushLayout(window);
                Assert.Equal("3 张素材 · 已处理 1/1 页", firstStyle.MetaText);
                var blockedStyle = viewModel.AiStyles[^1];
                Assert.Equal("3 张素材 · 已处理 1/1 页", blockedStyle.MetaText);
                var candidate = Assert.Single(firstStyle.Pages[0].Candidates);
                viewModel.OpenAiCandidatePreviewCommand.Execute(candidate);
                FlushLayout(window);
                Assert.Equal(Visibility.Visible, FindByAutomationId<Grid>(window, "AiCandidatePreviewDialog").Visibility);
                Assert.Equal("用于：PSD 页面 中文主图页 · 选择完整清晰的商品主体图。", viewModel.AiCandidatePreview!.UsageText);
                Assert.DoesNotContain(
                    EnumerateVisualDescendants<Button>(window).Select(button => button.Content?.ToString()),
                    content => content is "选用这张" or "确认候选");
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

                ApplyAiProjection(
                    viewModel,
                    projection with
                    {
                        Status = Desktop.Application.Ai.AiBatchUiStatus.RecoveryRequired,
                        StatusText = "AI 请求体未完整上传，火山未受理。请检查网络后重试分析。",
                        ActionText = "重试分析",
                        ReadyPageCount = 0
                    });
                FlushLayout(window);
                var retryButton = FindByAutomationId<Button>(window, "RetryAiAnalysisButton");
                Assert.True(retryButton.IsVisible);
                Assert.True(retryButton.IsEnabled);
                Assert.Same(viewModel.RetryAiAnalysisCommand, retryButton.Command);

                SetWindowSize(window, 1440, 900);
                SetWindowSize(window, 1100, 760);
                var bounds = page.TransformToAncestor(window).TransformBounds(new Rect(page.RenderSize));
                Assert.True(bounds.Left >= 0);
                Assert.True(bounds.Right <= window.ActualWidth + 0.5);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AiStyleReviewShowsAllUsageAndAssetsAndReplacesWithoutProviderRequest()
    {
        RunInSta(() =>
        {
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
                    "人物正面图",
                    Desktop.Application.Ai.AiRequirementSelectionUiStatus.ReviewRequired,
                    "AI 推荐 · 可生成，建议检查",
                    "asset_01",
                    "old.jpg",
                    "照片 01");
                var secondRequirement = requirement with
                {
                    RequirementId = "frame_02",
                    FrameDisplayName = "图片框 2",
                    SelectionRequirement = "面料纹理特写图",
                    SelectedAssetId = "asset_03",
                    SelectedPreviewPath = "third.jpg",
                    SelectedAssetDisplayName = "照片 03"
                };
                var initialPage = new Desktop.Application.Ai.AiPageAnalysisProjection(
                    "batch_ui",
                    "style_1",
                    "page_1",
                    "中文主图页",
                    Desktop.Application.Ai.AiPageUiStatus.ReviewRequired,
                    "可生成 · 建议检查",
                    "可生成 · 建议检查",
                    [],
                    [],
                    Requirements: [requirement, secondRequirement]);
                var assets = new Desktop.Application.Ai.AiStyleAssetProjection[]
                {
                    new("batch_ui", "style_1", "asset_01", "old.jpg", 1),
                    new("batch_ui", "style_1", "asset_02", "new.jpg", 2),
                    new("batch_ui", "style_1", "asset_03", "other.jpg", 3)
                };
                var initialStyle = new Desktop.Application.Ai.AiStyleAnalysisProjection(
                    "batch_ui",
                    "style_1",
                    "款式 01",
                    Desktop.Application.Ai.AiStyleUiStatus.ReviewRequired,
                    "可生成 · 建议检查",
                    [initialPage],
                    assets.Length,
                    assets);
                var initial = new Desktop.Application.Ai.AiBatchAnalysisProjection(
                    "batch_ui",
                    Desktop.Application.Ai.AiBatchUiStatus.ReviewRequired,
                    "分析完成；绿色和黄色页面均可直接生成。",
                    "进入生成",
                    1,
                    1,
                    1,
                    1,
                    [initialStyle],
                    Desktop.Application.Ai.AiAnalysisSchedulingControlState.Running,
                    null);
                workflow.LockProjectionFactory = lockCount =>
                {
                    var firstAssetId = lockCount >= 4 ? "asset_03" : lockCount >= 2 ? "asset_02" : "asset_01";
                    var secondAssetId = lockCount >= 3 ? "asset_01" : "asset_03";
                    var first = requirement with
                    {
                        Status = lockCount >= 2
                            ? Desktop.Application.Ai.AiRequirementSelectionUiStatus.ManualLocked
                            : requirement.Status,
                        StatusText = lockCount >= 2 ? "人工选用 · 已锁定" : requirement.StatusText,
                        SelectedAssetId = firstAssetId,
                        SelectedPreviewPath = firstAssetId switch
                        {
                            "asset_01" => "old.jpg",
                            "asset_02" => "new.jpg",
                            _ => "third.jpg"
                        },
                        SelectedAssetDisplayName = firstAssetId switch
                        {
                            "asset_01" => "照片 01",
                            "asset_02" => "照片 02",
                            _ => "照片 03"
                        }
                    };
                    var second = secondRequirement with
                    {
                        Status = lockCount >= 3
                            ? Desktop.Application.Ai.AiRequirementSelectionUiStatus.ManualLocked
                            : secondRequirement.Status,
                        StatusText = lockCount >= 3 ? "人工选用 · 已锁定" : secondRequirement.StatusText,
                        SelectedAssetId = secondAssetId,
                        SelectedPreviewPath = secondAssetId == "asset_01" ? "old.jpg" : "third.jpg",
                        SelectedAssetDisplayName = secondAssetId == "asset_01" ? "照片 01" : "照片 03"
                    };
                    return initial with
                    {
                        Styles =
                        [
                            initialStyle with
                            {
                                Pages = [initialPage with { Requirements = [first, second] }]
                            }
                        ]
                    };
                };

                ApplyAiProjection(viewModel, initial);
                SetAiScreen(viewModel);
                var style = Assert.Single(viewModel.AiStyles);
                Assert.True(FindByAutomationId<Button>(window, "EnterGenerationButton").IsEnabled);
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
                Assert.Equal(2, style.Pages[0].Requirements.Count);
                var reviewTexts = EnumerateVisualDescendants<TextBlock>(window)
                    .Select(text => text.Text)
                    .ToArray();
                Assert.Contains("人物正面图", reviewTexts);
                Assert.Contains("PSD 页面：中文主图页", reviewTexts);
                Assert.Contains("模板组：当前已校准模板 · 当前款式：款式 01", reviewTexts);
                Assert.DoesNotContain("图片框 1", reviewTexts);

                var target = style.Pages[0].Requirements[0];
                Assert.False(viewModel.ConfirmAiReplacementCommand.CanExecute(null));
                viewModel.BeginAiReplacementCommand.Execute(target);
                Assert.True(viewModel.SelectAiReplacementAssetCommand.CanExecute(style.Assets[1]));
                Assert.False(viewModel.ConfirmAiReplacementCommand.CanExecute(null));
                viewModel.SelectAiReplacementAssetCommand.Execute(style.Assets[1]);
                Assert.True(viewModel.CanConfirmAiReplacement);
                Assert.True(viewModel.ConfirmAiReplacementCommand.CanExecute(null));
                Assert.Equal("确认", viewModel.ConfirmAiReplacementText);
                Assert.Equal(
                    "确认",
                    FindByAutomationId<Button>(window, "ConfirmAiStyleReplacementButton").Content);
                Assert.Contains("当前用图：照片 01", viewModel.AiReplacementInstructionText, StringComparison.Ordinal);
                Assert.Contains("已选择 照片 02", viewModel.AiReplacementInstructionText, StringComparison.Ordinal);
                Assert.DoesNotContain("待保存", viewModel.AiReplacementInstructionText, StringComparison.Ordinal);
                workflow.LockException = new IOException("simulated local persistence failure");
                viewModel.ConfirmAiReplacementCommand.Execute(null);
                FlushLayout(window);

                Assert.Equal(1, workflow.LockCount);
                Assert.Contains("本地选图保存失败", viewModel.AiStatusMessage, StringComparison.Ordinal);
                Assert.Equal(
                    viewModel.AiStatusMessage,
                    FindByAutomationId<TextBlock>(window, "AiStyleReviewStatus").Text);
                Assert.True(viewModel.IsAiReplacementActive);

                workflow.LockException = null;
                viewModel.ConfirmAiReplacementCommand.Execute(null);
                FlushLayout(window);

                Assert.Equal(2, workflow.LockCount);
                Assert.Equal(("batch_ui", "style_1", "frame_01", "asset_02"), workflow.LastLock);
                var locked = Assert.Single(Assert.Single(viewModel.AiStyles).Pages[0].Requirements.Take(1));
                Assert.Equal("asset_02", locked.SelectedAssetId);
                Assert.Equal("人工选用 · 已锁定", locked.StatusText);
                Assert.Equal("new.jpg", locked.PreviewPath);
                Assert.Same(viewModel.AiStyles[0], viewModel.SelectedAiStyle);
                Assert.False(viewModel.IsAiReplacementActive);
                Assert.Equal(
                    viewModel.AiStatusMessage,
                    FindByAutomationId<TextBlock>(window, "AiStyleReviewStatus").Text);
                Assert.DoesNotContain(
                    EnumerateVisualDescendants<TextBlock>(window).Select(text => text.Text),
                    text => text.Contains("asset_", StringComparison.Ordinal)
                        || text.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
                        || text.Contains(@"C:\", StringComparison.OrdinalIgnoreCase));

                // A second frame can be replaced immediately after the first
                // commit, and the first frame can be replaced again. Every
                // confirmation must reach the durable lock path and rebuild
                // the left preview and usage counters.
                style = Assert.Single(viewModel.AiStyles);
                var secondTarget = style.Pages[0].Requirements[1];
                viewModel.BeginAiReplacementCommand.Execute(secondTarget);
                viewModel.SelectAiReplacementAssetCommand.Execute(style.Assets[0]);
                Assert.True(viewModel.ConfirmAiReplacementCommand.CanExecute(null));
                viewModel.ConfirmAiReplacementCommand.Execute(null);
                DrainDispatcher();
                Assert.Equal(3, workflow.LockCount);
                Assert.Equal(("batch_ui", "style_1", "frame_02", "asset_01"), workflow.LastLock);
                style = Assert.Single(viewModel.AiStyles);
                Assert.Equal("asset_02", style.Pages[0].Requirements[0].SelectedAssetId);
                Assert.Equal("asset_01", style.Pages[0].Requirements[1].SelectedAssetId);
                Assert.True(style.Assets[0].IsCurrentlyUsed);
                Assert.True(style.Assets[1].IsCurrentlyUsed);
                Assert.False(style.Assets[2].IsCurrentlyUsed);

                var firstTargetAgain = style.Pages[0].Requirements[0];
                viewModel.BeginAiReplacementCommand.Execute(firstTargetAgain);
                viewModel.SelectAiReplacementAssetCommand.Execute(style.Assets[2]);
                Assert.True(viewModel.ConfirmAiReplacementCommand.CanExecute(null));
                viewModel.ConfirmAiReplacementCommand.Execute(null);
                DrainDispatcher();
                Assert.Equal(4, workflow.LockCount);
                Assert.Equal(("batch_ui", "style_1", "frame_01", "asset_03"), workflow.LastLock);
                style = Assert.Single(viewModel.AiStyles);
                Assert.Equal("asset_03", style.Pages[0].Requirements[0].SelectedAssetId);
                Assert.Equal("asset_01", style.Pages[0].Requirements[1].SelectedAssetId);
                Assert.True(style.Assets[0].IsCurrentlyUsed);
                Assert.False(style.Assets[1].IsCurrentlyUsed);
                Assert.True(style.Assets[2].IsCurrentlyUsed);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
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
    public void AiAnalysisAutoRefreshContinuesInStyleReviewWhileSiblingPagesArePending()
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
                var readyPage = new AiPageAnalysisProjection(
                    "batch_review_refresh",
                    "style_review_refresh",
                    "page_ready",
                    "主图页",
                    AiPageUiStatus.Ready,
                    "可生成",
                    "已通过",
                    [],
                    []);
                var analysingPage = readyPage with
                {
                    PageId = "page_pending",
                    PageName = "细节页",
                    Status = AiPageUiStatus.Analysing,
                    StatusText = "分析中",
                    ActionText = "分析中"
                };
                var partialStyle = new AiStyleAnalysisProjection(
                    "batch_review_refresh",
                    "style_review_refresh",
                    "款式 01",
                    AiStyleUiStatus.ReviewRequired,
                    "待人工复核",
                    [readyPage, analysingPage],
                    2);
                var partial = new AiBatchAnalysisProjection(
                    "batch_review_refresh",
                    AiBatchUiStatus.ReviewRequired,
                    "部分页面已完成，仍有页面分析中。",
                    "继续处理",
                    1,
                    0,
                    1,
                    2,
                    [partialStyle],
                    AiAnalysisSchedulingControlState.Running,
                    null);
                SetCurrentBatchId(viewModel, partial.BatchId);
                ApplyAiProjection(viewModel, partial);
                SetAiScreen(viewModel);

                var style = Assert.Single(viewModel.AiStyles);
                viewModel.OpenAiStyleReviewCommand.Execute(style);
                var timer = GetAiRefreshTimer(viewModel);
                Assert.True(viewModel.IsAiStyleReviewVisible);
                Assert.True(timer.IsEnabled);
                Assert.Equal("2 张素材 · 已处理 1/2 页", style.MetaText);

                var settledStyle = partialStyle with
                {
                    Status = AiStyleUiStatus.Ready,
                    StatusText = "可生成",
                    Pages = [readyPage, analysingPage with
                    {
                        Status = AiPageUiStatus.Ready,
                        StatusText = "可生成",
                        ActionText = "已通过"
                    }]
                };
                workflow.RefreshProjection = partial with
                {
                    Status = AiBatchUiStatus.Ready,
                    StatusText = "分析完成，所有页面均可生成。",
                    ActionText = "进入生成",
                    CompletedStyleCount = 1,
                    ReadyPageCount = 2,
                    Styles = [settledStyle]
                };

                RunAiAutoRefreshOnce(viewModel);

                Assert.Equal(1, workflow.RefreshCount);
                Assert.Equal("已完成 1/1 款 · 已处理 2/2 页 · 可生成 2/2 页", viewModel.AiBatchProgressText);
                Assert.Equal("2 张素材 · 已处理 2/2 页", Assert.Single(viewModel.AiStyles).MetaText);
                Assert.False(timer.IsEnabled);
            }
            finally
            {
                viewModel.Dispose();
            }
        });
    }

    [Fact]
    public void AiRequestTooLargeProjectionShowsBoundedBodyAndRebuildAction()
    {
        RunInSta(() =>
        {
            var page = new AiPageAnalysisProjection(
                "batch_request_too_large",
                "style_01",
                "page_01",
                "主图页",
                AiPageUiStatus.SystemError,
                "请求体过大，需要重新建立批次",
                "重新建立批次",
                [],
                ["最终 UTF-8 JSON 请求体超过 16 MiB；未发送 AI 请求。请减少本款照片后重新建立批次"]);
            var projection = new AiBatchAnalysisProjection(
                "batch_request_too_large",
                AiBatchUiStatus.RecoveryRequired,
                "本款最终 JSON 请求体超过 16 MiB；未发送 AI 请求。请减少本款照片后重新建立批次。",
                "重新建立批次",
                1,
                1,
                0,
                1,
                [new AiStyleAnalysisProjection(
                    "batch_request_too_large",
                    "style_01",
                    "款式 01",
                    AiStyleUiStatus.SystemError,
                    "需要恢复",
                    [page],
                    24)],
                AiAnalysisSchedulingControlState.Running,
                null);
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiBatchAnalysisWorkflow: new RecordingAiBatchAnalysisWorkflow());
            try
            {
                ApplyAiProjection(viewModel, projection);

                Assert.Contains("16 MiB", viewModel.AiStatusMessage, StringComparison.Ordinal);
                Assert.Contains("未发送 AI 请求", viewModel.AiStatusMessage, StringComparison.Ordinal);
                Assert.Contains("重新建立批次", viewModel.AiStatusMessage, StringComparison.Ordinal);
                var projectedPage = Assert.Single(Assert.Single(viewModel.AiStyles).Pages);
                Assert.Equal("请求体过大，需要重新建立批次", projectedPage.StatusText);
                Assert.Equal("重新建立批次", projectedPage.ActionText);
                Assert.Contains("16 MiB", Assert.Single(projectedPage.WarningTexts), StringComparison.Ordinal);
            }
            finally
            {
                viewModel.Dispose();
            }
        });
    }

    [Fact]
    public void AiReferenceMismatchProjectionShowsReasonAndRebuildAction()
    {
        RunInSta(() =>
        {
            var page = new AiPageAnalysisProjection(
                "batch_reference_mismatch",
                "style_01",
                "page_01",
                "主图页",
                AiPageUiStatus.SystemError,
                "AI 返回引用不匹配，需要重新建立批次",
                "重新建立批次",
                [],
                ["AI 已返回，但结果中的请求、图片或图片框标识无法与本批次安全对应，已阻止选图和生成。请使用修复版本重新建立批次"]);
            var projection = new AiBatchAnalysisProjection(
                "batch_reference_mismatch",
                AiBatchUiStatus.RecoveryRequired,
                "AI 已返回，但结果引用无法与本批次安全对应，已阻止选图和生成。请使用修复版本重新建立批次。",
                "重新建立批次",
                1,
                1,
                0,
                1,
                [new AiStyleAnalysisProjection(
                    "batch_reference_mismatch",
                    "style_01",
                    "款式 01",
                    AiStyleUiStatus.SystemError,
                    "需要恢复",
                    [page],
                    24)],
                AiAnalysisSchedulingControlState.Running,
                null);
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: null,
                aiBatchAnalysisWorkflow: new RecordingAiBatchAnalysisWorkflow());
            try
            {
                ApplyAiProjection(viewModel, projection);

                Assert.Contains("结果引用", viewModel.AiStatusMessage, StringComparison.Ordinal);
                Assert.Contains("修复版本", viewModel.AiStatusMessage, StringComparison.Ordinal);
                Assert.Contains("重新建立批次", viewModel.AiStatusMessage, StringComparison.Ordinal);
                var projectedPage = Assert.Single(Assert.Single(viewModel.AiStyles).Pages);
                Assert.Equal("AI 返回引用不匹配，需要重新建立批次", projectedPage.StatusText);
                Assert.Equal("重新建立批次", projectedPage.ActionText);
                Assert.Contains("阻止选图和生成", Assert.Single(projectedPage.WarningTexts), StringComparison.Ordinal);
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
            }
        });
    }

    private static TemplateCalibrationDraftView CreateCalibrationDraft(
        string revisionId,
        string pageId = "page_1",
        string frameId = "frame_257",
        string layerName = "back_main") => new(
        revisionId,
        pageId,
        790,
        1252,
        TemplatePreviewSourceReference: null,
        IsReadOnly: false,
        Frames:
        [
            new TemplateFrameProjection(
                frameId,
                pageId,
                [0, 1],
                257,
                layerName,
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
            {
                Scale = 1,
                SelectionRequirement = "只使用用户素材"
            }
        ],
        Assets: [],
        SelectedAssetId: null,
         SelectedFrameId: frameId,
        SelectedScale: 1,
        SelectedOffsetX: 0,
        SelectedOffsetY: 0,
        SelectionRequirement: "只使用用户素材",
        StatusMessage: "已保存",
        CanUndo: true,
        CanRedo: false,
        IsBlocked: false);

    private static TemplateCalibrationDraftView CreateMultiFrameCalibrationDraft()
    {
        var front = new TemplateFrameProjection(
            "frame_front",
            "page_multi",
            [2],
            257,
            "左上主图",
            new string('a', 64),
            0,
            0,
            300,
            400,
            HasMask: false,
            HasVectorMask: false,
            IsVisible: true,
            IsClipping: false,
            Opacity: 255,
            BlendMode: "normal",
            IsPreviewSupported: true,
            IsRegistered: true)
        {
            IsSelected = true,
            HasPersistedBinding = true,
            BoundAssetId = "asset_front",
            Scale = 1.1,
            OffsetX = 3,
            OffsetY = -2,
            SelectionRequirement = "商品正面图"
        };
        var sleeve = front with
        {
            FrameId = "frame_sleeve",
            LayerPath = [3],
            LayerId = 258,
            LayerName = "右上细节",
            IsSelected = false,
            BoundAssetId = "asset_sleeve",
            Scale = 1.45,
            OffsetX = -9,
            OffsetY = 7,
            SelectionRequirement = "袖口细节图"
        };
        return new TemplateCalibrationDraftView(
            "revision_multi",
            "page_multi",
            790,
            1252,
            TemplatePreviewSourceReference: null,
            IsReadOnly: false,
            Frames: [front, sleeve],
            Assets:
            [
                new TemplateCalibrationAsset("asset_front", "front.jpg", "front.jpg", 100, 100),
                new TemplateCalibrationAsset("asset_sleeve", "sleeve.jpg", "sleeve.jpg", 100, 100)
            ],
            SelectedAssetId: front.BoundAssetId,
            SelectedFrameId: front.FrameId,
            SelectedScale: front.Scale,
            SelectedOffsetX: front.OffsetX,
            SelectedOffsetY: front.OffsetY,
            SelectionRequirement: front.SelectionRequirement,
            StatusMessage: "已保存",
            CanUndo: false,
            CanRedo: false,
            IsBlocked: false);
    }

    private static TemplatePageRowViewModel CreateReferenceLearningPage(
        string pageId = "page_learning_ui")
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
                pageId,
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
                 Desktop.Domain.Templates.TemplateRect.Create(0, 0, 790, 1252)),
            PixelEvidence: new PsdLayerPixelEvidence(
                true,
                790 * 1252,
                790 * 1252,
                new PsdLayerBounds(0, 0, 790, 1252)));
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

    private static void DrainDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        var provider = Assert.IsAssignableFrom<IInvokeProvider>(
            peer.GetPattern(PatternInterface.Invoke));
        provider.Invoke();
    }

    private static void RunTemplateLibraryScenario(
        Action<MainWindow, MainWindowViewModel, TemplateLibraryWorkspaceStub> scenario)
    {
        RunInSta(() =>
        {
            var workspace = new TemplateLibraryWorkspaceStub();
            var viewModel = new MainWindowViewModel(
                new AppLaunchOptions(true, null),
                templateWorkspace: workspace);
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(
                item => item.Key == "templates");
            var window = new MainWindow(viewModel);

            try
            {
                window.Show();
                DrainDispatcher();
                FlushLayout(window);
                scenario(window, viewModel, workspace);
            }
            finally
            {
                viewModel.Dispose();
                window.Close();
            }
        });
    }

    private static (Desktop.App.App Application, System.Windows.Threading.Dispatcher Dispatcher)
        OpenWindowAndReadApplicationResources()
    {
        var application = Assert.IsAssignableFrom<Desktop.App.App>(System.Windows.Application.Current);
        Assert.IsType<SolidColorBrush>(application.TryFindResource("SurfaceBrush"));

        var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            DrainDispatcher();
            FlushLayout(window);
            Assert.NotNull(window.Content);
            return (application, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        }
        finally
        {
            Assert.Equal(ShutdownMode.OnExplicitShutdown, application.ShutdownMode);
            window.Close();
            Assert.Same(application, System.Windows.Application.Current);
            viewModel.Dispose();
        }
    }

    private static void RunInSta(Action action)
    {
        WpfApplicationHost.Value.Invoke(action);
    }

    private static readonly Lazy<WpfApplicationTestHost> WpfApplicationHost = new(
        static () => new WpfApplicationTestHost(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed class WpfApplicationTestHost
    {
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource<System.Windows.Threading.Dispatcher> _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        public WpfApplicationTestHost()
        {
            var thread = new Thread(RunDispatcher)
            {
                IsBackground = true,
                Name = "Desktop.App.UiTests.WpfHost"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            try
            {
                _dispatcher = _started.Task
                    .WaitAsync(OperationTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    "The shared WPF application host did not start within 30 seconds.",
                    exception);
            }
        }

        public void Invoke(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                throw new InvalidOperationException("The shared WPF application host has stopped.");
            }

            var operation = _dispatcher.InvokeAsync(
                () => ExecuteIsolated(action),
                System.Windows.Threading.DispatcherPriority.Send);
            try
            {
                operation.Task
                    .WaitAsync(OperationTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    "WPF shell test did not complete within 30 seconds.",
                    exception);
            }
        }

        private void RunDispatcher()
        {
            try
            {
                var application = new WpfTestApplication();
                application.InitializeResources();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                _started.TrySetResult(dispatcher);
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception exception)
            {
                _started.TrySetException(exception);
            }
        }

        private static void ExecuteIsolated(Action action)
        {
            ExceptionDispatchInfo? failure = null;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                try
                {
                    CloseRemainingWindows();
                    DrainDispatcher();
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }

            failure?.Throw();
        }

        private static void CloseRemainingWindows()
        {
            var application = Assert.IsAssignableFrom<Desktop.App.App>(System.Windows.Application.Current);
            foreach (Window window in application.Windows.Cast<Window>().ToArray())
            {
                window.Close();
            }

            application.MainWindow = null;
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        private sealed class WpfTestApplication : Desktop.App.App
        {
            public void InitializeResources()
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/Desktop.App;component/Styles/DesignSystem.xaml",
                        UriKind.Relative)
                });
                Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                Resources["BoundedPreviewImageConverter"] = new BoundedPreviewImageConverter();
            }

            protected override void OnStartup(StartupEventArgs e)
            {
                // The test host owns the dispatcher and must not run the production composition root.
            }

            protected override void OnExit(ExitEventArgs e)
            {
                // The process owns this single test application for the full test assembly lifetime.
            }
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

    private sealed class DeferredCalibrationService : ITemplateCalibrationService
    {
        private readonly ConcurrentDictionary<
            string,
            TaskCompletionSource<TemplateCalibrationDraftView>> _pending = new(StringComparer.Ordinal);

        public Task<TemplateCalibrationDraftView> LoadAsync(
            TemplateDraftSession session,
            string pageId,
            CancellationToken cancellationToken) => _pending.GetOrAdd(
            pageId,
            _ => new TaskCompletionSource<TemplateCalibrationDraftView>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public void Complete(string pageId, TemplateCalibrationDraftView draft)
        {
            if (!_pending.TryGetValue(pageId, out var completion))
            {
                throw new InvalidOperationException($"No deferred calibration load for {pageId}.");
            }

            completion.TrySetResult(draft);
        }

        public Task<TemplateCalibrationDraftView> BindAssetAsync(
            TemplateDraftSession session,
            string pageId,
            string frameId,
            string assetId,
            CancellationToken cancellationToken) => Unsupported<TemplateCalibrationDraftView>();

        public Task<TemplateCalibrationDraftView> BindReadOnlyAssetAsync(
            TemplateDraftSession session,
            string pageId,
            string frameId,
            string sourcePath,
            CancellationToken cancellationToken) => Unsupported<TemplateCalibrationDraftView>();

        public Task<TemplateCalibrationDraftView> ClearAssetAsync(
            TemplateDraftSession session,
            string pageId,
            string frameId,
            CancellationToken cancellationToken) => Unsupported<TemplateCalibrationDraftView>();

        public Task<TemplateCalibrationDraftView> SetGeometryAsync(
            TemplateDraftSession session,
            string pageId,
            string frameId,
            double scale,
            double offsetX,
            double offsetY,
            CancellationToken cancellationToken) => Unsupported<TemplateCalibrationDraftView>();

        public Task<TemplateCalibrationDraftView> SetSelectionRequirementAsync(
            TemplateDraftSession session,
            string pageId,
            string frameId,
            string selectionRequirement,
            CancellationToken cancellationToken) => Unsupported<TemplateCalibrationDraftView>();

        public Task<TemplateCalibrationDraftView> ApplyHistoryAsync(
            TemplateDraftSession session,
            string pageId,
            CalibrationUndoAction action,
            CancellationToken cancellationToken) => Unsupported<TemplateCalibrationDraftView>();

        private static Task<T> Unsupported<T>() => Task.FromException<T>(
            new NotSupportedException("Only deferred calibration loading is supported by this test."));
    }

    private sealed class PageEditWorkspaceStub : ITemplateWorkspaceService
    {
        public string? LastGroupId { get; private set; }
        public string? LastPageId { get; private set; }
        public string? LastFullGroupId { get; private set; }

        public Task<TemplateDraftSession> BeginPageEditAsync(
            string groupId,
            string pageId,
            CancellationToken cancellationToken)
        {
            LastGroupId = groupId;
            LastPageId = pageId;
            return Task.FromResult(new TemplateDraftSession(
                groupId,
                "编辑入口测试",
                "revision_edit_ui_draft",
                3,
                new HashSet<string>([pageId], StringComparer.Ordinal))
            {
                EditablePageIds = new HashSet<string>([pageId], StringComparer.Ordinal)
            });
        }

        public Task<TemplateDraftView> GetDraftAsync(
            TemplateDraftSession session,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TemplateDraftView(session, []));

        public Task<TemplateDraftSession> BeginFullEditAsync(
            string groupId,
            CancellationToken cancellationToken)
        {
            LastFullGroupId = groupId;
            return Task.FromResult(new TemplateDraftSession(
                groupId,
                "编辑入口测试",
                "revision_edit_ui_full_draft",
                3,
                new HashSet<string>(["page_edit_ui"], StringComparer.Ordinal))
            {
                EditablePageIds = new HashSet<string>(["page_edit_ui"], StringComparer.Ordinal)
            });
        }

        public Task<IReadOnlyList<TemplateLibraryItem>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails?> GetDetailsAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails?> GetCommittedDetailsAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RenameTemplateGroupAsync(string groupId, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginCreateAsync(string groupName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginIncrementAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> ResumeDraftAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateWorkspaceImportResult> ImportPageAsync(TemplateDraftSession session, string commandId, string resourceId, string sourcePsdPath, string pageName, TemplateDuplicateDecision duplicateDecision, string? selectedDuplicateResourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ConfigurePageAsync(TemplateDraftSession session, string pageId, string name, TemplatePageContentTypeOption contentType, TemplateGenerationModeOption generationMode, int? variantCapacity, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReorderPagesAsync(TemplateDraftSession session, IReadOnlyList<string> orderedPageIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemovePageAsync(TemplateDraftSession session, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails> CommitDraftAsync(TemplateDraftSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails> CommitDraftAsNewGroupAsync(TemplateDraftSession session, string newGroupName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateBatchSnapshotView> CreateBatchSnapshotAsync(string groupId, string batchId, IReadOnlyList<string> selectedPageIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateBatchSnapshotView> UpdateDraftBatchSnapshotAsync(string groupId, string batchId, IReadOnlyList<string> selectedPageIds, bool analysisStarted, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DeleteTemplateGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemplateLibraryWorkspaceStub : ITemplateWorkspaceService
    {
        private const string GroupId = "group_template_library_ui";
        private const string RevisionId = "revision_template_library_ui";
        private string _name = "模板库交互测试";
        private bool _deleted;

        public int GetDetailsCount { get; private set; }

        public int RenameCount { get; private set; }

        public int DeleteCount { get; private set; }

        public Task<IReadOnlyList<TemplateLibraryItem>> ListAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<TemplateLibraryItem> items = _deleted
                ? []
                :
                [
                    new TemplateLibraryItem(
                        GroupId,
                        _name,
                        RevisionId,
                        1,
                        "可用",
                        new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero))
                ];
            return Task.FromResult(items);
        }

        public Task<TemplateGroupDetails?> GetDetailsAsync(
            string groupId,
            CancellationToken cancellationToken)
        {
            GetDetailsCount++;
            TemplateGroupDetails? details = _deleted
                ? null
                : new TemplateGroupDetails(
                    GroupId,
                    _name,
                    RevisionId,
                    1,
                    [
                        new TemplatePageProjection(
                            "page_template_library_ui",
                            0,
                            "主图页",
                            TemplatePageContentTypeOption.NormalComposition,
                            TemplateGenerationModeOption.OncePerProduct,
                            null,
                            true,
                            "resource_template_library_ui")
                    ],
                    true,
                    new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero));
            return Task.FromResult(details);
        }

        public Task RenameTemplateGroupAsync(
            string groupId,
            string newName,
            CancellationToken cancellationToken)
        {
            Assert.Equal(GroupId, groupId);
            RenameCount++;
            _name = newName;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> DeleteTemplateGroupAsync(
            string groupId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(GroupId, groupId);
            DeleteCount++;
            _deleted = true;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<TemplateGroupDetails?> GetCommittedDetailsAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginCreateAsync(string groupName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginIncrementAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginPageEditAsync(string groupId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> BeginFullEditAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftSession> ResumeDraftAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateWorkspaceImportResult> ImportPageAsync(TemplateDraftSession session, string commandId, string resourceId, string sourcePsdPath, string pageName, TemplateDuplicateDecision duplicateDecision, string? selectedDuplicateResourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateDraftView> GetDraftAsync(TemplateDraftSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ConfigurePageAsync(TemplateDraftSession session, string pageId, string name, TemplatePageContentTypeOption contentType, TemplateGenerationModeOption generationMode, int? variantCapacity, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReorderPagesAsync(TemplateDraftSession session, IReadOnlyList<string> orderedPageIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemovePageAsync(TemplateDraftSession session, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails> CommitDraftAsync(TemplateDraftSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateGroupDetails> CommitDraftAsNewGroupAsync(TemplateDraftSession session, string newGroupName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateBatchSnapshotView> CreateBatchSnapshotAsync(string groupId, string batchId, IReadOnlyList<string> selectedPageIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplateBatchSnapshotView> UpdateDraftBatchSnapshotAsync(string groupId, string batchId, IReadOnlyList<string> selectedPageIds, bool analysisStarted, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingBatchInputWorkflow : IBatchInputWorkflow
    {
        public BatchInputSubmission? Submission { get; private set; }

        public List<BatchInputSubmission> Submissions { get; } = [];

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
            Submissions.Add(submission);
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

    private sealed class RecordingAiCredentialManagement : IAiCredentialManagementPort
    {
        private string? _value;

        public int SaveCount { get; private set; }

        public int RemoveCount { get; private set; }

        public Task<AiCredentialStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_value is null
                ? new AiCredentialStatusSnapshot(AiCredentialState.NotConfigured, null)
                : new AiCredentialStatusSnapshot(AiCredentialState.Configured, AiCredentialRules.MaskSuffix(_value)));
        }

        public Task SaveAsync(string apiKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _value = AiCredentialRules.NormalizeAndValidate(apiKey);
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _value = null;
            RemoveCount++;
            return Task.CompletedTask;
        }
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

        public Func<int, AiBatchAnalysisProjection>? LockProjectionFactory { get; set; }

        public Exception? LockException { get; set; }

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
            if (LockException is not null)
            {
                return Task.FromException<AiBatchAnalysisProjection>(LockException);
            }
            return Task.FromResult(
                LockProjectionFactory?.Invoke(LockCount)
                ?? LockProjection
                ?? throw new InvalidOperationException("TEST_LOCK_PROJECTION_MISSING"));
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

    private sealed class MultiPageReferenceLearningService(ReferenceLearningDocument template)
        : IReferenceLearningService
    {
        public Task<ReferenceLearningResult> AnalyzeAsync(ReferenceLearningRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReferenceLearningResult.Success(ForRequest(request)));

        public Task<ReferenceLearningResult> ResumeAsync(ReferenceLearningRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReferenceLearningResult.Success(ForRequest(request)));

        public Task<ReferenceLearningResult> ResumeAsync(string learningId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReferenceLearningResult> ApplyReviewAsync(ReferenceLearningDocument document, IReadOnlyList<ReferenceFrameReview> reviews, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReferenceLearningResult> CancelAsync(ReferenceLearningDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private ReferenceLearningDocument ForRequest(ReferenceLearningRequest request)
        {
            var candidates = template.Candidates.Select(candidate => candidate with
            {
                FrameId = "frame_" + request.PageId,
                PageId = request.PageId
            }).ToArray();
            return template with
            {
                LearningId = request.LearningId,
                PageId = request.PageId,
                Candidates = candidates,
                RulesDigest = ReferenceLearningDigest.Compute(
                    template.PsdSha256,
                    template.StructureSignature,
                    template.ReferenceFolderDigest,
                    template.ReferencePhotos,
                    candidates)
            };
        }
    }

    private sealed class RecordingReferenceLearningAiService : IReferenceLearningAiService
    {
        public int BatchCalls { get; private set; }
        public int SingleCalls { get; private set; }
        public int LastBatchSize { get; private set; }

        public Task<ReferenceSemanticGenerationResult> GenerateSelectionRequirementsAsync(
            ReferenceSemanticGenerationCommand command,
            CancellationToken cancellationToken = default)
        {
            SingleCalls++;
            return Task.FromResult(new ReferenceSemanticGenerationResult(command.Document, "cache", false, null));
        }

        public Task<ReferenceSemanticBatchGenerationResult> GenerateSelectionRequirementsAsync(
            IReadOnlyList<ReferenceSemanticGenerationCommand> commands,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            LastBatchSize = commands.Count;
            return Task.FromResult(new ReferenceSemanticBatchGenerationResult(
                commands.Select(command => command.Document).ToArray(), "cache", false, null));
        }

        public Task<ReferenceLearningDocument> ApplyUserOverrideAsync(ReferenceLearningDocument document, string frameId, string selectionRequirement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
