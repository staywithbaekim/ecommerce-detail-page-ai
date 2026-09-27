using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.App;

public partial class TemplateConfirmationDialog : UserControl
{
    private void TemplateDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not TemplateWorkspacePageViewModel viewModel)
        {
            return;
        }

        if (viewModel.TemplateDialogHasTertiaryAction)
        {
            viewModel.TertiaryTemplateDialogCommand.Execute(null);
        }
        else if (viewModel.TemplateDialogHasSecondaryAction)
        {
            viewModel.SecondaryTemplateDialogCommand.Execute(null);
        }
        else
        {
            viewModel.ConfirmTemplateDialogCommand.Execute(null);
        }

        e.Handled = true;
    }
    public TemplateConfirmationDialog()
    {
        InitializeComponent();
    }
}
