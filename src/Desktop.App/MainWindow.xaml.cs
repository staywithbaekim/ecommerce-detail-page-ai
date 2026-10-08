using System.Windows;

namespace Desktop.App;

public partial class MainWindow : Window
{

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            PrimaryNavigationList.SelectedItem = viewModel.SelectedNavigationItem;
            viewModel.InitializeTaskList();
        };
        SourceInitialized += (_, _) => ApplyStartupBounds();
        SizeChanged += (_, _) => viewModel.SetWindowWidth(ActualWidth);
        var flushedForClose = false;
        var flushingForClose = false;
        Closing += async (_, args) =>
        {
            if (flushedForClose || viewModel.TemplateWorkspacePageViewModel.Coordinator.RuleWorkbench is null) return;
            args.Cancel = true;
            if (flushingForClose) return;
            flushingForClose = true;
            try
            {
                await viewModel.TemplateWorkspacePageViewModel.Coordinator.FlushRuleWorkbenchAsync();
                flushedForClose = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
            catch (Exception error) when (error is InvalidOperationException or System.IO.IOException)
            {
                MessageBox.Show(this, "规则草稿尚未保存，窗口保持打开。\n" + error.Message, "保存未完成");
            }
            finally { flushingForClose = false; }
        };
        Closed += (_, _) =>
        {
            viewModel.Dispose();
        };
    }

    private void ApplyStartupBounds()
    {
        var bounds = WindowBoundsCalculator.Fit(
            SystemParameters.WorkArea,
            new Size(1440, 900),
            new Size(MinWidth, MinHeight));

        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetWindowWidth(bounds.Width);
        }
    }

}
