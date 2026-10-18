using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Desktop.App;

/// <summary>Window-root presentation host; dialog state and actions stay with their page owners.</summary>
public sealed class ModalHost : Grid
{
    private static readonly DependencyPropertyKey IsOpenPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsOpen), typeof(bool), typeof(ModalHost), new PropertyMetadata(false));
    public static readonly DependencyProperty IsOpenProperty = IsOpenPropertyKey.DependencyProperty;
    public bool IsOpen => (bool)GetValue(IsOpenProperty);

    private UIElement? _activeDialog;
    private IInputElement? _returnFocus;

    public ModalHost()
    {
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
        Loaded += (_, _) => UpdateActiveDialog();
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is UIElement removed) removed.IsVisibleChanged -= DialogVisibilityChanged;
        if (visualAdded is UIElement added)
        {
            added.IsVisibleChanged += DialogVisibilityChanged;
            KeyboardNavigation.SetTabNavigation(added, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(added, KeyboardNavigationMode.Cycle);
        }
        if (IsLoaded) UpdateActiveDialog();
    }

    private void DialogVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateActiveDialog();

    private void UpdateActiveDialog()
    {
        if (!IsLoaded) return;
        // Child order defines nested-modal order. No page or per-dialog ZIndex participates.
        var active = Children.OfType<UIElement>().LastOrDefault(child => child.IsVisible);
        if (ReferenceEquals(active, _activeDialog)) return;
        if (_activeDialog is null && active is not null) _returnFocus = Keyboard.FocusedElement;
        _activeDialog = active;
        foreach (UIElement child in Children)
            child.SetCurrentValue(IsEnabledProperty, active is null || ReferenceEquals(child, active));
        SetValue(IsOpenPropertyKey, active is not null);

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(active, _activeDialog)) return;
            if (active is not null)
            {
                if (!active.IsKeyboardFocusWithin)
                    active.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            else
            {
                if (_returnFocus is UIElement { IsVisible: true, IsEnabled: true } target)
                    Keyboard.Focus(target);
                _returnFocus = null;
            }
        }));
    }
}
