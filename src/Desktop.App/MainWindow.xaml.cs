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
