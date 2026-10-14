using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Desktop.App;

public partial class TemplatePageRuleCard : UserControl
{
    public TemplatePageRuleCard()
    {
        InitializeComponent();
        Preview.FrameInvoked += frame =>
        {
            FrameList.ScrollIntoView(frame); FrameList.UpdateLayout();
            if (FrameList.ItemContainerGenerator.ContainerFromItem(frame) is DependencyObject row) FindTextBox(row)?.Focus();
        };
    }
    private static TextBox? FindTextBox(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox box) return box;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }
    private TemplateRulePageViewModel? Page => DataContext as TemplateRulePageViewModel;
    private TemplateRuleWorkbenchControl? Board => Ancestor<TemplateRuleWorkbenchControl>(this);
    private void Whole_Click(object sender, RoutedEventArgs args)
    { Preview.FitWhole = !Preview.FitWhole; FitButton.Content = Preview.FitWhole ? "适宽" : "整页"; }
    private void Card_SizeChanged(object sender, SizeChangedEventArgs args)
    { if (RulesColumn is not null) RulesColumn.Width = new(Math.Clamp(args.NewSize.Width * 0.42, 200, 300)); }
    private void Enlarge_Click(object sender, RoutedEventArgs args)
    {
        // Same component, same page/selection/edit bindings, not a second editor.
        var card = new TemplatePageRuleCard { DataContext = Page };
        new Window { Owner = Window.GetWindow(this), Title = Page?.Name ?? "PSD", Width = 1050, Height = 700,
            Content = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private async void Purpose_Click(object sender, RoutedEventArgs args)
    { if (Board is { } board) await board.EditGroupAsync(); }
    private async void ToggleExclude_Click(object sender, RoutedEventArgs args)
    {
        if (Board is not { } board || Page is not { IsReadOnly: false } page
            || ((FrameworkElement)sender).DataContext is not TemplateRuleFrameViewModel frame) return;
        await board.RunAsync(() => frame.IsExcluded
            ? board.Model!.RestoreReplacingAsync(page, frame)
            : board.Model!.StopReplacingAsync(page, frame));
    }
    private async void Remove_Click(object sender, RoutedEventArgs args)
    { if (Page is { IsReadOnly: false } page && Board is { } board) await board.RemoveAsync(page); }
    private void Rule_GotFocus(object sender, KeyboardFocusChangedEventArgs args)
    { if (Page is { } page && ((FrameworkElement)sender).DataContext is TemplateRuleFrameViewModel frame) page.SelectedFrame = frame; }
    private void FrameList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (FrameList.SelectedItem is not { } frame) return;
        FrameList.ScrollIntoView(frame);
        if (FrameList.ItemContainerGenerator.ContainerFromItem(frame) is FrameworkElement row) row.BringIntoView();
    }
    internal static T? Ancestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null) { if (child is T target) return target; child = VisualTreeHelper.GetParent(child); }
        return null;
    }
    private void ChainWheel(object sender, MouseWheelEventArgs args)
    {
        var scroll = (ScrollViewer)sender;
        if (args.Delta > 0 && scroll.VerticalOffset > 0 || args.Delta < 0 && scroll.VerticalOffset < scroll.ScrollableHeight) return;
        var outer = Ancestor<ScrollViewer>(VisualTreeHelper.GetParent(scroll));
        if (outer is null) return;
        outer.ScrollToVerticalOffset(outer.VerticalOffset - args.Delta); args.Handled = true;
    }
}
