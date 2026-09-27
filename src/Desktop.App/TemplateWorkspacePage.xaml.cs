using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Desktop.Application.Templates;
using Microsoft.Win32;

namespace Desktop.App;

public partial class TemplateWorkspacePage : UserControl
{
    private TemplateWorkspacePageViewModel? Page => DataContext as TemplateWorkspacePageViewModel;

    private Point _templatePageDragStart;
    private TemplatePageRowViewModel? _draggedTemplatePage;
    private TemplatePageRowViewModel? _draggedBoardPage;
    private Point _boardDragStart;
    private Point _boardPageStart;
    private Dictionary<TemplatePageRowViewModel, Point>? _boardGroupStart;
    private bool _isBoardPanning;
    private Point _boardPanStart;
    private Point _calibrationDragStart;
    private Point _calibrationStartOffset;
    private bool _isCalibrationDragging;
    private Point _calibrationAssetDragStart;
    private TemplateCalibrationAsset? _draggedCalibrationAsset;

    private async void TemplateWorkspacePage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel { IsTemplateCalibrationVisible: true } viewModel
            || viewModel.IsReferenceLearningReviewMode
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
        if (dialog.ShowDialog(Window.GetWindow(this)) == true
            && Page is TemplateWorkspacePageViewModel viewModel)
        {
            await viewModel.ImportPsdFilesAsync(dialog.FileNames).ConfigureAwait(true);
        }
    }

    private async void ConfigureSizeTable_Click(object sender, RoutedEventArgs e)
    {
        if (Page is { } page && Window.GetWindow(this) is { } owner)
            await page.Coordinator.ConfigureSizeTableAsync(owner).ConfigureAwait(true);
    }

    private async void ChoosePsdFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Page is TemplateWorkspacePageViewModel viewModel)
        {
            await viewModel.ChoosePsdFolderAsync().ConfigureAwait(true);
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
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && Page is TemplateWorkspacePageViewModel viewModel)
        {
            await viewModel.BindCalibrationAssetFileAsync(dialog.FileName).ConfigureAwait(true);
        }
    }


    private void CalibrationFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element
            || Page is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        var frameId = element.DataContext switch
        {
            TemplateFrameProjection frame => frame.FrameId,
            ReferenceLearningFrameViewModel frame => frame.FrameId,
            _ => null
        };
        if (frameId is null)
        {
            return;
        }

        viewModel.SelectCalibrationFrame(frameId);
        e.Handled = true;
    }


    private void CalibrationCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = Page is TemplateWorkspacePageViewModel { IsReferenceLearningReviewMode: true }
            ? DragDropEffects.None
            : e.Data.GetDataPresent(typeof(TemplateCalibrationAsset))
            || GetDroppedCalibrationAssetPaths(e.Data).Length > 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = true;
    }


    private async void CalibrationCanvas_Drop(object sender, DragEventArgs e)
    {
        if (Page is TemplateWorkspacePageViewModel { IsReferenceLearningReviewMode: false } viewModel)
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
            || Page is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        var frameContext = FindAncestor<FrameworkElement>(e.OriginalSource as DependencyObject)?.DataContext;
        var frameId = frameContext switch
        {
            TemplateFrameProjection frame => frame.FrameId,
            ReferenceLearningFrameViewModel frame => frame.FrameId,
            _ => null
        };
        if (frameId is not null)
        {
            viewModel.SelectCalibrationFrame(frameId);
            var selected = frameContext switch
            {
                TemplateFrameProjection frame => frame.IsSelected,
                ReferenceLearningFrameViewModel frame => frame.IsSelected,
                _ => false
            };
            if (!selected || viewModel.CalibrationPreviewImagePath is null)
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
            || Page is not TemplateWorkspacePageViewModel viewModel)
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
        if (Page is TemplateWorkspacePageViewModel { IsReferenceLearningReviewMode: false } viewModel)
        {
            await viewModel.CommitCalibrationGeometryAsync().ConfigureAwait(true);
        }
        e.Handled = true;
    }


    private void CalibrationCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel viewModel)
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


    private void CalibrationFitWidth_Click(object sender, RoutedEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        var availableWidth = Math.Max(1, CalibrationCanvasScrollViewer.ViewportWidth - 32);
        viewModel.SetCalibrationViewportZoom(availableWidth / Math.Max(1, viewModel.CalibrationCanvasWidth));
        CalibrationCanvasScrollViewer.ScrollToHorizontalOffset(0);
        CalibrationCanvasScrollViewer.ScrollToVerticalOffset(0);
    }


    private void CalibrationFitPage_Click(object sender, RoutedEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        var availableWidth = Math.Max(1, CalibrationCanvasScrollViewer.ViewportWidth - 32);
        var availableHeight = Math.Max(1, CalibrationCanvasScrollViewer.ViewportHeight - 32);
        viewModel.SetCalibrationViewportZoom(Math.Min(
            availableWidth / Math.Max(1, viewModel.CalibrationCanvasWidth),
            availableHeight / Math.Max(1, viewModel.CalibrationCanvasHeight)));
        CalibrationCanvasScrollViewer.ScrollToHorizontalOffset(0);
        CalibrationCanvasScrollViewer.ScrollToVerticalOffset(0);
    }


    private void CalibrationActualSize_Click(object sender, RoutedEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        viewModel.SetCalibrationViewportZoom(1);
        CalibrationCanvasScrollViewer.ScrollToHorizontalOffset(0);
        CalibrationCanvasScrollViewer.ScrollToVerticalOffset(0);
    }


    private async void CalibrationCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel { CalibrationPreviewImagePath: not null } viewModel)
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
            || Page is not TemplateWorkspacePageViewModel viewModel
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
        if (Page is TemplateWorkspacePageViewModel viewModel
            && e.Data.GetData(typeof(TemplatePageRowViewModel)) is TemplatePageRowViewModel source
            && FindItemAt<TemplatePageRowViewModel>(e.OriginalSource as DependencyObject) is { } target)
        {
            await viewModel.MoveTemplatePageAsync(source, target).ConfigureAwait(true);
        }

        _draggedTemplatePage = null;
        e.Handled = true;
    }


    private void TemplateBoardPage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel viewModel
            || FindItemAt<TemplatePageRowViewModel>(e.OriginalSource as DependencyObject) is not { } page
            || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        viewModel.SelectedTemplatePage = page;
        _draggedBoardPage = page;
        _boardDragStart = e.GetPosition(TemplateBoardScrollViewer);
        _boardPageStart = new Point(page.CanvasX, page.CanvasY);
        _boardGroupStart = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? viewModel.TemplatePages.ToDictionary(
                item => item,
                item => new Point(item.CanvasX, item.CanvasY))
            : null;
        TemplateBoardScrollViewer.CaptureMouse();
        e.Handled = true;
    }


    private void TemplateBoardPage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedBoardPage is null
            || e.LeftButton != MouseButtonState.Pressed
            || Page is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        var current = e.GetPosition(TemplateBoardScrollViewer);
        var zoom = Math.Max(0.01, viewModel.TemplateBoardZoom);
        var deltaX = (current.X - _boardDragStart.X) / zoom;
        var deltaY = (current.Y - _boardDragStart.Y) / zoom;
        if (_boardGroupStart is not null)
        {
            foreach (var (page, start) in _boardGroupStart)
            {
                page.CanvasX = start.X + deltaX;
                page.CanvasY = start.Y + deltaY;
            }
        }
        else
        {
            _draggedBoardPage.CanvasX = _boardPageStart.X + deltaX;
            _draggedBoardPage.CanvasY = _boardPageStart.Y + deltaY;
        }
        e.Handled = true;
    }


    private void TemplateBoardPage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedBoardPage is null)
        {
            return;
        }

        _draggedBoardPage = null;
        _boardGroupStart = null;
        TemplateBoardScrollViewer.ReleaseMouseCapture();
        e.Handled = true;
    }


    private void TemplateBoard_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Page is not TemplateWorkspacePageViewModel viewModel
            || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        viewModel.AdjustTemplateBoardZoom(e.Delta);
        e.Handled = true;
    }


    private void TemplateBoardSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle
            || Page is not TemplateWorkspacePageViewModel)
        {
            return;
        }

        _boardPanStart = e.GetPosition(TemplateBoardScrollViewer);
        _isBoardPanning = TemplateBoardScrollViewer.CaptureMouse();
        e.Handled = _isBoardPanning;
    }


    private void TemplateBoardSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isBoardPanning
            || e.MiddleButton != MouseButtonState.Pressed
            || Page is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        var current = e.GetPosition(TemplateBoardScrollViewer);
        viewModel.PanTemplateBoard(current.X - _boardPanStart.X, current.Y - _boardPanStart.Y);
        _boardPanStart = current;
        e.Handled = true;
    }


    private void TemplateBoardSurface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isBoardPanning || e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _isBoardPanning = false;
        TemplateBoardScrollViewer.ReleaseMouseCapture();
        e.Handled = true;
    }


    private void TemplateBoardZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (Page is TemplateWorkspacePageViewModel viewModel)
        {
            viewModel.AdjustTemplateBoardZoom(120);
        }
    }


    private void TemplateBoardZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (Page is TemplateWorkspacePageViewModel viewModel)
        {
            viewModel.AdjustTemplateBoardZoom(-120);
        }
    }


    private void TemplateBoardReset_Click(object sender, RoutedEventArgs e)
    {
        if (Page is TemplateWorkspacePageViewModel viewModel)
        {
            viewModel.ResetTemplateBoardView();
        }
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
        if (Page is not TemplateWorkspacePageViewModel viewModel)
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

    private static string[] GetDroppedCalibrationAssetPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp" };
        return paths.Where(path => extensions.Contains(System.IO.Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static double GetCalibrationCanvasScale(TemplateWorkspacePageViewModel viewModel)
        => Math.Max(0.0001, viewModel.CalibrationViewportZoom);

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
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    public TemplateWorkspacePage()
    {
        InitializeComponent();
    }
}

