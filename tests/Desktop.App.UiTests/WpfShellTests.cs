using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    [Fact]
    public void ShellSupportsWideNarrowUiaFocusAndChineseText()
    {
        RunInSta(() =>
        {
            var application = new Desktop.App.App();
            application.InitializeComponent();
            var viewModel = new MainWindowViewModel(new AppLaunchOptions(true, null));
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                FlushLayout(window);

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
}
