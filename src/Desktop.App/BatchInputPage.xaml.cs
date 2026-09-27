using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Desktop.App;

public partial class BatchInputPage : UserControl
{
    private async void ChoosePsdFiles_Click(object sender, RoutedEventArgs e)
    {
        await ChoosePsdFilesAsync().ConfigureAwait(true);
    }

    private async void ChoosePsdFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BatchInputPageViewModel page)
        {
            await page.ChoosePsdFolderAsync().ConfigureAwait(true);
        }
    }

    private async Task ChoosePsdFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择模板 PSD",
            Filter = "Photoshop 文档 (*.psd)|*.psd",
            Multiselect = true,
            CheckFileExists = true,
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && DataContext is BatchInputPageViewModel page)
        {
            await page.ImportPsdFilesAsync(dialog.FileNames).ConfigureAwait(true);
        }
    }

    private async void PsdDropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not BatchInputPageViewModel page
            || page.HasImportedTemplateFiles
            || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        await ChoosePsdFilesAsync().ConfigureAwait(true);
        e.Handled = true;
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
        if (paths.Length > 0 && DataContext is BatchInputPageViewModel page)
        {
            await page.ImportPsdFilesAsync(paths).ConfigureAwait(true);
        }

        e.Handled = true;
    }

    private static string[] GetDroppedPsdPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        var psdPaths = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    psdPaths.AddRange(Directory.EnumerateFiles(
                        path,
                        "*.psd",
                        SearchOption.TopDirectoryOnly));
                }
                catch (IOException)
                {
                    // The import operation reports readable-file failures. A drag-over
                    // probe must stay side-effect free and must not throw for a folder
                    // that is temporarily unavailable.
                }
                catch (UnauthorizedAccessException)
                {
                    // See the IOException branch above.
                }
            }
            else if (File.Exists(path)
                     && string.Equals(
                         System.IO.Path.GetExtension(path),
                         ".psd",
                         StringComparison.OrdinalIgnoreCase))
            {
                psdPaths.Add(path);
            }
        }

        return psdPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    public BatchInputPage()
    {
        InitializeComponent();
    }
}
