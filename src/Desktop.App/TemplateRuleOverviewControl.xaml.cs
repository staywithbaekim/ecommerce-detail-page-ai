using System.Windows;
using System.Windows.Controls;

namespace Desktop.App;

public partial class TemplateRuleOverviewControl : UserControl
{
    public event Action? ReturnRequested;
    public event Func<Task>? ConfirmRequested;
    public event Func<Task>? EditRequested;
    public event Action<string, string?>? LocateRequested;
    public TemplateRuleOverviewControl() => InitializeComponent();
    public void ShowError(string message) => Error.Text = message;
    public void ShowModel(TemplateRuleWorkbenchViewModel model)
    {
        DataContext = model;
        Confirm.IsEnabled = model.Draft?.Diagnostics.IsEmpty == true && !model.IsEmpty;
        Error.Text = model.DiagnosticsText;
    }
    private void Back_Click(object sender, RoutedEventArgs e) => ReturnRequested?.Invoke();
    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirm.IsEnabled = false;
        try { if (ConfirmRequested is { } action) await action(); }
        finally { if (DataContext is TemplateRuleWorkbenchViewModel model) Confirm.IsEnabled = model.Draft?.Diagnostics.IsEmpty == true && !model.IsEmpty; }
    }
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (EditRequested is { } action) await action();
        if (DataContext is TemplateRuleWorkbenchViewModel model) ShowModel(model);
    }
    private void LocatePage_Click(object sender, RoutedEventArgs e)
    { if (((FrameworkElement)sender).DataContext is TemplateRuleRelationNode node) LocateRequested?.Invoke(node.PageId, null); }
    private void LocateFrame_Click(object sender, RoutedEventArgs e)
    { if (((FrameworkElement)sender).DataContext is TemplateRuleRelationFrame frame) LocateRequested?.Invoke(frame.PageId, frame.FrameId); }
}
