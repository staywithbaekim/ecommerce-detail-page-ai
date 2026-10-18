using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Desktop.Application.Templates;

namespace Desktop.App;

public partial class TemplateRuleWorkbenchControl : UserControl
{
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double),
        typeof(TemplateRuleWorkbenchControl), new PropertyMetadata(550d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    private TemplateWorkspaceViewModel? Coordinator => DataContext as TemplateWorkspaceViewModel;
    public TemplateRuleWorkbenchViewModel? Model => Coordinator?.RuleWorkbench;
    private int _mentionStart = -1;
    private bool _insertingMention;
    private bool _navigatingPicker;
    private bool _composing;
    private TemplateRuleConfirmation? _confirmation;
    private TemplateRuleWorkbenchViewModel? _shownModel;
    public TemplateRuleWorkbenchControl()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is TemplateWorkspaceViewModel old)
                System.ComponentModel.PropertyChangedEventManager.RemoveHandler(old, CoordinatorChanged, nameof(TemplateWorkspaceViewModel.RuleWorkbench));
            if (args.NewValue is TemplateWorkspaceViewModel current)
                System.ComponentModel.PropertyChangedEventManager.AddHandler(current, CoordinatorChanged, nameof(TemplateWorkspaceViewModel.RuleWorkbench));
            ResetForDraft();
        };
        Overview.ReturnRequested += BackToCards;
        Overview.LocateRequested += (page, frame) => { BackToCards(); Locate(new(page, frame, "", true)); };
        Overview.EditRequested += EditGroupAsync;
        Overview.ConfirmRequested += () => RunAsync(async () =>
        {
            if (_confirmation is not null && Coordinator is { } owner)
                await owner.ConfirmRuleWorkbenchAsync(_confirmation);
        });
        TextCompositionManager.AddPreviewTextInputStartHandler(Input, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(Input, (_, _) => _composing = false);
    }
    private void CoordinatorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => ResetForDraft();
    private void ResetForDraft()
    {
        if (ReferenceEquals(_shownModel, Model)) return;
        _shownModel = Model;
        ResetNotice();
        _confirmation = null;
        BackToCards();
        Overview.DataContext = null;
        Input.Clear(); _mentionStart = -1;
        ReferencePicker.Visibility = Visibility.Collapsed;
        WorkspaceScroll.ScrollToTop();
    }
    public async Task RunAsync(Func<Task> action)
    {
        ResetNotice();
        try { await action(); }
        catch (OperationCanceledException) { ShowFailure("本次解释已取消，输入和草稿保留。不会自动重发。"); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { ShowFailure("操作未完成，输入和草稿已保留。请按提示修改或返回刷新总览：" + error.Message); }
    }
    private void ShowFailure(string message)
    {
        Notice.Tag = "Error";
        Notice.Text = message;
        if (Overview.Visibility == Visibility.Visible) Overview.ShowError(message);
    }
    private void ResetNotice()
    {
        Notice.ClearValue(TextBlock.TextProperty);
        Notice.SetBinding(TextBlock.TextProperty, new Binding("RuleWorkbench.Status"));
        Notice.Tag = null;
    }
    private void Board_SizeChanged(object sender, SizeChangedEventArgs args)
    { ResizeCards(); }
    private void Content_SizeChanged(object sender, SizeChangedEventArgs args) => ResizeCards();
    private void ResizeCards()
    {
        if (WorkspaceScroll is null) return;
        var width = Math.Max(292, WorkspaceScroll.ActualWidth - 24);
        var columns = Math.Clamp((int)(width / 540), 1, 3);
        CardWidth = Math.Max(280, width / columns - 16);
    }
    private void Composer_SizeChanged(object sender, SizeChangedEventArgs args) { if (EndClearance is not null) EndClearance.Height = args.NewSize.Height + 42; }
    private void Import_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new TemplateRuleImportDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && Coordinator is { } owner) _ = RunAsync(() => owner.ImportPsdFilesAsync(dialog.Paths));
    }
    private async void Paths_Drop(object sender, DragEventArgs args)
    {
        if (!args.Data.GetDataPresent(DataFormats.FileDrop) || Coordinator is not { } owner) return;
        var dialog = new TemplateRuleImportDialog { Owner = Window.GetWindow(this) };
        dialog.AddPaths((string[])args.Data.GetData(DataFormats.FileDrop));
        args.Handled = true;
        if (dialog.ShowDialog() == true) await RunAsync(() => owner.ImportPsdFilesAsync(dialog.Paths));
    }
    private void Paths_DragOver(object sender, DragEventArgs args)
    {
        args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }
    private async void Undo_Click(object sender, RoutedEventArgs args) { if (Model is { } model) await RunAsync(model.UndoAsync); }
    private void Previous_Click(object sender, RoutedEventArgs args) { Model?.ChangeSegment(-1); WorkspaceScroll.ScrollToTop(); }
    private void Next_Click(object sender, RoutedEventArgs args) { Model?.ChangeSegment(1); WorkspaceScroll.ScrollToTop(); }
    private async void Learn_Click(object sender, RoutedEventArgs args)
    { if (Coordinator is { } owner) await RunAsync(owner.LearnRulesInWorkbenchAsync); }
    public async Task RemoveAsync(TemplateRulePageViewModel page)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.RemovePageAsync(page));
    }
    public Task ChangePageTypeAsync(TemplateRulePageViewModel page, TemplatePageContentTypeOption contentType) =>
        Coordinator is { } coordinator ? RunAsync(() => coordinator.SetTemplatePageTypeAsync(page, contentType)) : Task.CompletedTask;
    public Task ConfigureSizeTableAsync(string pageId, Window owner) => Coordinator is { } coordinator
        ? RunAsync(() => coordinator.ConfigureSizeTableAsync(owner, pageId)) : Task.CompletedTask;
    private void Locate_Click(object sender, RoutedEventArgs args) { if (((FrameworkElement)sender).DataContext is TemplateRuleReferenceChoice reference) Locate(reference); }
    private void Locate(TemplateRuleReferenceChoice reference)
    {
        Model?.Locate(reference.PageId, reference.FrameId); Cards.UpdateLayout();
        var page = Model?.Pages.SingleOrDefault(page => page.PageId == reference.PageId);
        if (page is not null && Cards.ItemContainerGenerator.ContainerFromItem(page) is FrameworkElement card) card.BringIntoView();
    }
    private async void Overview_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { } model || Coordinator is not { } owner) return;
        await RunAsync(async () =>
        {
            _confirmation = await model.OverviewAsync();
            Overview.ShowModel(model);
            EditorView.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            Overview.Visibility = Visibility.Visible;
        });
    }
    private void BackToCards()
    {
        Overview.Visibility = Visibility.Collapsed; Toolbar.Visibility = Visibility.Visible;
        EditorView.Visibility = Visibility.Visible;
    }
    public Task EditGroupAsync() => RunAsync(async () =>
    {
        if (Model is not { } model) return;
            await model.FlushAsync();
            var editor = new TemplateGroupRuleDialog(model) { Owner = Window.GetWindow(this) };
            if (editor.ShowDialog() == true) await model.ApplyBranchesAsync(editor.Changes);
            if (Overview.Visibility == Visibility.Visible)
            { _confirmation = await model.OverviewAsync(); Overview.ShowModel(model); }
    });
    private void References_Click(object sender, RoutedEventArgs args)
    {
        var start = Input.SelectionStart;
        Input.SelectedText = "@";
        Input.CaretIndex = start + 1;
        Input.Focus(); UpdateReferencePicker();
    }
    private void Input_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (!_insertingMention && Model is { CanInterpret: false } model) model.CancelInterpretation();
        if (ReferencePicker is not null && !_insertingMention)
            _ = Input.Dispatcher.BeginInvoke(UpdateReferencePicker);
    }
    private void Input_SelectionChanged(object sender, RoutedEventArgs args)
    { if (ReferencePicker is not null && !_insertingMention) UpdateReferencePicker(); }
    private void UpdateReferencePicker()
    {
        if (_composing || _insertingMention || ReferencePicker is null) return;
        var caret = Input.CaretIndex;
        var start = caret == 0 ? -1 : Input.Text.LastIndexOf('@', caret - 1);
        var query = start < 0 ? "" : Input.Text[(start + 1)..caret];
        if (start < 0 || Input.SelectionLength != 0 || query.Length > 80
            || query.Any(character => char.IsWhiteSpace(character) || "，。；：,;:".Contains(character)))
        {
            ReferencePicker.Visibility = Visibility.Collapsed; _mentionStart = -1; return;
        }
        _mentionStart = start;
        _navigatingPicker = true;
        try
        {
            ReferencePicker.ItemsSource = (Model?.ReferenceChoices ?? []).Where(reference => reference.IsActive
                && reference.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            ReferencePicker.SelectedIndex = -1;
        }
        finally { _navigatingPicker = false; }
        ReferencePicker.Visibility = ReferencePicker.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Reference_Selected(object sender, SelectionChangedEventArgs args)
        => InsertSelectedReference();
    private void InsertSelectedReference()
    {
        if (_navigatingPicker || _insertingMention) return;
        if (ReferencePicker.SelectedItem is not TemplateRuleReferenceChoice reference) return;
        if (Model?.ReferenceChoices.Any(current => current.PageId == reference.PageId
            && current.FrameId == reference.FrameId && current.IsActive) != true)
        { ShowFailure("这个引用已失效，不会指向其他对象。"); return; }
        var caret = Input.CaretIndex;
        var start = _mentionStart >= 0 ? _mentionStart : Input.SelectionStart;
        _insertingMention = true;
        try
        {
            Input.Select(start, Math.Max(0, caret - start));
            Input.SelectedText = reference.Label + " ";
            Input.CaretIndex = start + reference.Label.Length + 1;
            ReferencePicker.SelectedItem = null; ReferencePicker.Visibility = Visibility.Collapsed;
            _mentionStart = -1;
        }
        finally { _insertingMention = false; }
        if (Model is { CanInterpret: false } model) model.CancelInterpretation();
        Input.Focus();
    }
    private async void Input_KeyDown(object sender, KeyEventArgs args)
    {
        if (_composing || args.Key == Key.ImeProcessed) return;
        if (ReferencePicker.Visibility == Visibility.Visible && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (args.Key is Key.Down or Key.Up)
            {
                args.Handled = true; _navigatingPicker = true;
                try { ReferencePicker.SelectedIndex = Math.Clamp(ReferencePicker.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, ReferencePicker.Items.Count - 1); }
                finally { _navigatingPicker = false; }
                return;
            }
            if (args.Key == Key.Enter && ReferencePicker.SelectedItem is not null)
            { args.Handled = true; InsertSelectedReference(); return; }
            if (args.Key == Key.Escape)
            { args.Handled = true; ReferencePicker.Visibility = Visibility.Collapsed; _mentionStart = -1; return; }
        }
        if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        { args.Handled = true; await SendAsync(); }
    }
    private async void Send_Click(object sender, RoutedEventArgs args) => await SendAsync();
    private Task SendAsync() => RunAsync(async () =>
    {
        if (Model is not { } model || string.IsNullOrWhiteSpace(Input.Text)) return;
        var text = Input.Text;
        // A just-typed candidate may acquire its stable frame alias during the
        // pending save. Resolve inline references only after that write closes.
        await model.FlushAsync();
        if (!ReferenceEquals(Model, model) || Input.Text != text) return;
        var references = new List<TemplateRuleReferenceChoice>();
        foreach (Match match in Regex.Matches(text, @"@(\d+)(?:\s*(?:的)?\s*框\s*(\d+))?", RegexOptions.CultureInvariant))
        {
            var label = "@" + match.Groups[1].Value + (match.Groups[2].Success ? " 的框" + match.Groups[2].Value : "");
            var reference = model.ReferenceChoices.SingleOrDefault(choice => choice.Label == label && choice.IsActive)
                ?? throw new InvalidOperationException("引用已失效，请删除正文中的旧引用并重新选择。");
            references.Add(reference);
        }
        if (!await model.InterpretAsync(text, references.Distinct().ToArray())) return;
        if (ReferenceEquals(Model, model) && Input.Text == text) Input.Clear();
    });
}
