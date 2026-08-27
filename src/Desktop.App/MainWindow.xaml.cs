using System.Windows;

namespace Desktop.App;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => PrimaryNavigationList.SelectedItem = viewModel.SelectedNavigationItem;
        SourceInitialized += (_, _) => ApplyStartupBounds();
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
    }
}
