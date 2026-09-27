using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Desktop.App;

public partial class SizeTableEditorDialog : UserControl
{
    private IInputElement? _focusBeforeOpen;

    public SizeTableEditorDialog()
    {
        InitializeComponent();
    }

    private void EditorOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (EditorOverlay.IsVisible)
        {
            _focusBeforeOpen = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!EditorOverlay.IsVisible)
                {
                    return;
                }

                FocusManager.SetFocusedElement(EditorOverlay, CloseSizeTableEditorButton);
                Keyboard.Focus(CloseSizeTableEditorButton);
            }));
            return;
        }

        var focus = _focusBeforeOpen;
        _focusBeforeOpen = null;
        if (focus is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => focus.Focus()));
    }

    private void EditorOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape
            || DataContext is not SizeTableEditorViewModel viewModel
            || !viewModel.CloseSizeTableEditorCommand.CanExecute(null))
        {
            return;
        }

        viewModel.CloseSizeTableEditorCommand.Execute(null);
        e.Handled = true;
    }
}
