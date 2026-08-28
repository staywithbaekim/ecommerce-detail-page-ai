using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Desktop.Application.Templates;
using Microsoft.Win32;

namespace Desktop.App;

public partial class MainWindow : Window
{
    private Point _templatePageDragStart;
    private TemplatePageRowViewModel? _draggedTemplatePage;
    private Point _calibrationDragStart;
    private Point _calibrationStartOffset;
    private bool _isCalibrationDragging;
    private Point _calibrationAssetDragStart;
    private TemplateCalibrationAsset? _draggedCalibrationAsset;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => PrimaryNavigationList.SelectedItem = viewModel.SelectedNavigationItem;
        SourceInitialized += (_, _) => ApplyStartupBounds();
        SizeChanged += (_, _) => viewModel.SetWindowWidth(ActualWidth);
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closed += (_, _) => viewModel.Dispose();
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

    private async void ChoosePsdFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择模板 PSD",
            Filter = "Photoshop 文档 (*.psd)|*.psd",
            Multiselect = true,
            CheckFileExists = true,
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(this) == true && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ImportPsdFilesAsync(dialog.FileNames).ConfigureAwait(true);
        }
    }

    private async void ChooseCalibrationAsset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择代表款素材（只读）",
            Filter = "商品图片 (*.jpg;*.jpeg;*.png;*.webp;*.tif;*.tiff;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.tif;*.tiff;*.bmp",
            Multiselect = false,
            CheckFileExists = true,
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(this) == true && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.BindCalibrationAssetFileAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || !viewModel.IsTemplateCalibrationVisible
            || Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            viewModel.UndoCalibrationCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            viewModel.RedoCalibrationCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                 && e.Key == Key.Z)
        {
            viewModel.RedoCalibrationCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None
                 && e.Key == Key.Delete
                 && viewModel.CanClearCalibrationAsset)
        {
            await viewModel.ClearCalibrationAssetAsync().ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private void CalibrationFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TemplateFrameProjection frame }
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectCalibrationFrame(frame.FrameId);
            e.Handled = true;
        }
    }

    private void CalibrationCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TemplateCalibrationAsset))
            || GetDroppedCalibrationAssetPaths(e.Data).Length > 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = true;
    }

    private async void CalibrationCanvas_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            if (e.Data.GetData(typeof(TemplateCalibrationAsset)) is TemplateCalibrationAsset asset)
            {
                await viewModel.BindCalibrationAssetIdAsync(asset.AssetId).ConfigureAwait(true);
            }
            else if (GetDroppedCalibrationAssetPaths(e.Data).FirstOrDefault() is { } path)
            {
                await viewModel.BindCalibrationAssetFileAsync(path).ConfigureAwait(true);
            }
        }
        e.Handled = true;
    }

    private void CalibrationCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement canvas
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (FindAncestor<FrameworkElement>(e.OriginalSource as DependencyObject)?.DataContext
            is TemplateFrameProjection frame)
        {
            viewModel.SelectCalibrationFrame(frame.FrameId);
            if (!frame.IsSelected || viewModel.CalibrationPreviewImagePath is null)
            {
                e.Handled = true;
                return;
            }
        }

        if (viewModel.CalibrationPreviewImagePath is null)
        {
            return;
        }

        _calibrationDragStart = e.GetPosition(canvas);
        _calibrationStartOffset = new Point(
            viewModel.CalibrationOffsetXInput,
            viewModel.CalibrationOffsetYInput);
        canvas.Focus();
        _isCalibrationDragging = canvas.CaptureMouse();
        e.Handled = _isCalibrationDragging;
    }

    private void CalibrationCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCalibrationDragging || sender is not UIElement canvas
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var current = e.GetPosition(canvas);
        var scale = GetCalibrationCanvasScale(viewModel);
        viewModel.CalibrationOffsetXInput = _calibrationStartOffset.X
            + (current.X - _calibrationDragStart.X) / scale;
        viewModel.CalibrationOffsetYInput = _calibrationStartOffset.Y
            + (current.Y - _calibrationDragStart.Y) / scale;
        e.Handled = true;
    }

    private async void CalibrationCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isCalibrationDragging || sender is not UIElement canvas)
        {
            return;
        }

        _isCalibrationDragging = false;
        canvas.ReleaseMouseCapture();
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.CommitCalibrationGeometryAsync().ConfigureAwait(true);
        }
        e.Handled = true;
    }

    private void CalibrationCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { CalibrationPreviewImagePath: not null } viewModel)
        {
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Let the surrounding scroll viewer handle an ordinary wheel gesture.
            return;
        }

        viewModel.AdjustCalibrationViewportZoom(e.Delta);
        e.Handled = true;
    }

    private async void CalibrationCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { CalibrationPreviewImagePath: not null } viewModel)
        {
            return;
        }

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                if (!viewModel.CanClearCalibrationAsset)
                {
                    return;
                }

                await viewModel.ClearCalibrationAssetAsync().ConfigureAwait(true);
                e.Handled = true;
                return;
            case Key.Left:
                viewModel.CalibrationOffsetXInput -= step;
                break;
            case Key.Right:
                viewModel.CalibrationOffsetXInput += step;
                break;
            case Key.Up:
                viewModel.CalibrationOffsetYInput -= step;
                break;
            case Key.Down:
                viewModel.CalibrationOffsetYInput += step;
                break;
            case Key.Add:
            case Key.OemPlus:
                viewModel.CalibrationScaleInput = Math.Clamp(viewModel.CalibrationScaleInput * 1.05, 0.1, 8);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                viewModel.CalibrationScaleInput = Math.Clamp(viewModel.CalibrationScaleInput * 0.95, 0.1, 8);
                break;
            default:
                return;
        }

        await viewModel.CommitCalibrationGeometryAsync().ConfigureAwait(true);
        e.Handled = true;
    }

    private async void CalibrationFrameList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete
            || Keyboard.Modifiers != ModifierKeys.None
            || DataContext is not MainWindowViewModel viewModel
            || !viewModel.CanClearCalibrationAsset)
        {
            return;
        }

        // The list has focus after the user selects a frame on the left.  Keep
        // Delete equivalent to the inspector's "清除绑定" button without
        // deleting the source photo on disk.
        await viewModel.ClearCalibrationAssetAsync().ConfigureAwait(true);
        e.Handled = true;
    }

    private void CalibrationAssetList_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _calibrationAssetDragStart = e.GetPosition(null);
        _draggedCalibrationAsset = FindItemAt<TemplateCalibrationAsset>(e.OriginalSource as DependencyObject);
    }

    private void CalibrationAssetList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedCalibrationAsset is null)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _calibrationAssetDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _calibrationAssetDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            new DataObject(typeof(TemplateCalibrationAsset), _draggedCalibrationAsset),
            DragDropEffects.Copy);
        _draggedCalibrationAsset = null;
    }

    private void PsdDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedPsdPaths(e.Data).Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void PsdDropZone_Drop(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPsdPaths(e.Data);
        if (paths.Length > 0 && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ImportPsdFilesAsync(paths).ConfigureAwait(true);
        }

        e.Handled = true;
    }

    private void TemplatePageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _templatePageDragStart = e.GetPosition(null);
        _draggedTemplatePage = FindItemAt<TemplatePageRowViewModel>(e.OriginalSource as DependencyObject);
    }

    private void TemplatePageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTemplatePage is null)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _templatePageDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _templatePageDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            new DataObject(typeof(TemplatePageRowViewModel), _draggedTemplatePage),
            DragDropEffects.Move);
    }

    private void TemplatePageList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TemplatePageRowViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void TemplatePageList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && e.Data.GetData(typeof(TemplatePageRowViewModel)) is TemplatePageRowViewModel source
            && FindItemAt<TemplatePageRowViewModel>(e.OriginalSource as DependencyObject) is { } target)
        {
            await viewModel.MoveTemplatePageAsync(source, target).ConfigureAwait(true);
        }

        _draggedTemplatePage = null;
        e.Handled = true;
    }

    private async void MoveTemplatePageUp_Click(object sender, RoutedEventArgs e) =>
        await MoveTemplatePageByOffsetAsync(sender, -1).ConfigureAwait(true);

    private async void MoveTemplatePageDown_Click(object sender, RoutedEventArgs e) =>
        await MoveTemplatePageByOffsetAsync(sender, 1).ConfigureAwait(true);

    private async void TemplatePageList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control
            || e.Key is not (Key.Up or Key.Down)
            || sender is not ListBox { SelectedItem: TemplatePageRowViewModel page })
        {
            return;
        }

        await MoveTemplatePageByOffsetAsync(page, e.Key == Key.Up ? -1 : 1).ConfigureAwait(true);
        e.Handled = true;
    }

    private Task MoveTemplatePageByOffsetAsync(object sender, int offset)
    {
        var page = sender is Button { Tag: TemplatePageRowViewModel taggedPage }
            ? taggedPage
            : sender as TemplatePageRowViewModel;
        return page is null ? Task.CompletedTask : MoveTemplatePageByOffsetAsync(page, offset);
    }

    private async Task MoveTemplatePageByOffsetAsync(TemplatePageRowViewModel page, int offset)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var index = viewModel.TemplatePages.IndexOf(page);
        var targetIndex = index + offset;
        if (index >= 0 && targetIndex >= 0 && targetIndex < viewModel.TemplatePages.Count)
        {
            await viewModel.MoveTemplatePageAsync(page, viewModel.TemplatePages[targetIndex]).ConfigureAwait(true);
            viewModel.SelectedTemplatePage = page;
        }
    }

    private static string[] GetDroppedPsdPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        return paths.Where(path => string.Equals(
                System.IO.Path.GetExtension(path),
                ".psd",
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetDroppedCalibrationAssetPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp"
        };
        return paths.Where(path => extensions.Contains(System.IO.Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private double GetCalibrationCanvasScale(MainWindowViewModel viewModel)
    {
        var widthScale = CalibrationPreviewViewbox.ActualWidth / Math.Max(1, viewModel.CalibrationCanvasWidth);
        var heightScale = CalibrationPreviewViewbox.ActualHeight / Math.Max(1, viewModel.CalibrationCanvasHeight);
        return Math.Max(0.0001, Math.Min(widthScale, heightScale))
            * viewModel.CalibrationViewportZoom;
    }

    private static T? FindItemAt<T>(DependencyObject? source)
        where T : class
    {
        var container = FindAncestor<ListBoxItem>(source);
        return container?.DataContext as T;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
