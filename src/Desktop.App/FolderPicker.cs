using Microsoft.Win32;
using System.IO;

namespace Desktop.App;

public interface IFolderPicker
{
    Task<string?> PickFolderAsync(
        string title,
        string? initialDirectory,
        CancellationToken cancellationToken);
}

public sealed class WindowsFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync(
        string title,
        string? initialDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory)
                && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            var owner = System.Windows.Application.Current.MainWindow;
            var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return accepted == true ? dialog.FolderName : null;
        });
    }
}
