using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Desktop.App;

/// <summary>One import container; the OS file/folder dialogs remain honestly separate capabilities.</summary>
public sealed class TemplateRuleImportDialog : Window
{
    private readonly ObservableCollection<string> _paths = [];
    private readonly TextBox _input = new() { MinWidth = 400 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
    private readonly List<string> _folders = [];
    public IReadOnlyList<string> Paths => _paths.ToArray();
    public TemplateRuleImportDialog()
    {
        Title = "添加模板 PSD"; Width = 760; Height = 590; MinWidth = 620; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AllowDrop = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/Desktop.App;component/Styles/TemplateWorkbench.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WorkbenchQuietBrush");
        SetResourceReference(FontFamilyProperty, "AppFontFamily");
        var shell = new Border { Padding = new(24), CornerRadius = new(16) };
        shell.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        shell.SetResourceReference(Border.BorderBrushProperty, "WorkbenchLineBrush");
        shell.BorderThickness = new(1);
        Content = shell;
        var root = new DockPanel(); shell.Child = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var cancel = new Button { Content = "取消", IsCancel = true, ToolTip = "关闭窗口，不导入清单中的文件" };
        cancel.SetResourceReference(StyleProperty, "WorkbenchGhostButtonStyle");
        footer.Children.Add(cancel);
        var confirm = new Button { Content = "添加到模板工作台", IsEnabled = false, ToolTip = "把清单中的 PSD 加入当前模板草稿" };
        confirm.SetResourceReference(StyleProperty, "WorkbenchPrimaryButtonStyle");
        _paths.CollectionChanged += (_, _) => confirm.IsEnabled = _paths.Count > 0;
        confirm.Click += (_, _) => { if (_paths.Count > 0) DialogResult = true; };
        footer.Children.Add(confirm);
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "添加模板页面", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "选择或拖入 PSD；原文件只读，系统会复制到模板工作台后再解析。", Margin = new(0, 6, 0, 14), Foreground = Brushes.Gray });
        var actions = new WrapPanel(); header.Children.Add(actions);
        AddButton(actions, "选择 PSD 文件", "选择一个或多个 .psd 文件。", () => { var picker = new OpenFileDialog { Multiselect = true, Filter = "PSD 文件|*.psd" }; if (picker.ShowDialog(this) == true) AddPaths(picker.FileNames); });
        AddButton(actions, "选择模板文件夹", "扫描文件夹直属的 PSD；不会自动深入子目录。", () => { var picker = new OpenFolderDialog { Multiselect = true }; if (picker.ShowDialog(this) == true) AddPaths(picker.FolderNames); });
        AddButton(actions, "扫描子文件夹", "扫描已经选择的文件夹中的所有子目录，并追加 PSD；不会清空当前清单。", () => { foreach (var folder in _folders.ToArray()) ReadFolder(folder, true); });
        var paths = new DockPanel { Margin = new(0, 12, 0, 0) }; var add = new Button { Content = "添加路径" };
        add.SetResourceReference(StyleProperty, "WorkbenchButtonStyle");
        add.Click += (_, _) => { AddPaths(_input.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(path => path.Trim().Trim('"'))); _input.Clear(); };
        _input.MinHeight = 36; _input.ToolTip = "粘贴 PSD 或文件夹路径，每行一条"; _input.SetResourceReference(StyleProperty, "WorkbenchTextBoxStyle");
        DockPanel.SetDock(add, Dock.Right); paths.Children.Add(add); paths.Children.Add(_input); header.Children.Add(paths);
        var drop = new Border { Margin = new(0, 14, 0, 10), Padding = new(18), CornerRadius = new(12), AllowDrop = true };
        drop.SetResourceReference(Border.BackgroundProperty, "WorkbenchQuietBrush");
        drop.SetResourceReference(Border.BorderBrushProperty, "WorkbenchConnectorBrush"); drop.BorderThickness = new(1);
        var dropText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        dropText.Children.Add(new TextBlock { Text = "把 PSD 文件或文件夹拖到这里", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        dropText.Children.Add(new TextBlock { Text = "拖入后自动加入清单；文件夹默认扫描直属 PSD，可再点“扫描子文件夹”", Margin = new(0, 5, 0, 0), Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        drop.Child = dropText; header.Children.Add(drop);
        var list = new ListBox { ItemsSource = _paths, MinHeight = 170, MaxHeight = 260, Padding = new(8) };
        list.SetResourceReference(StyleProperty, "WorkbenchListBoxStyle"); root.Children.Add(list);
        drop.DragOver += (_, args) => SetDropEffect(args);
        drop.Drop += (_, args) => HandleDrop(args);
        DragOver += (_, args) => SetDropEffect(args);
        Drop += (_, args) => HandleDrop(args);
        _message.SetResourceReference(FrameworkElement.StyleProperty, "WorkbenchCaptionStyle");
        header.Children.Add(_message);
        list.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Delete && list.SelectedItem is string path) _paths.Remove(path); };
        Drop += (_, args) => { if (args.Data.GetData(DataFormats.FileDrop) is string[] values) { AddPaths(values); args.Handled = true; } };
    }
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { if (!_folders.Contains(path, StringComparer.OrdinalIgnoreCase)) _folders.Add(path); ReadFolder(path, false); }
            else if (File.Exists(path) && Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase)) AddFile(path);
            else _message.Text = "已忽略不存在或不是 PSD 的路径；其他条目保留。";
        }
    }
    private void AddFile(string path) { if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase)) _paths.Add(path); }
    private void ReadFolder(string folder, bool recursive)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(folder, "*.psd", options)) AddFile(file);
            _message.Text = !recursive && Directory.EnumerateDirectories(folder).Any()
                ? "发现子目录。当前只加入直属 PSD；需要时可点击上方“包含子目录”。"
                : "核对清单；选中条目按 Delete 可从清单移除。原始文件只读。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _message.Text = "部分目录无法读取；已加入条目保留。"; }
    }
    private static void AddButton(Panel panel, string title, string tooltip, Action action)
    { var button = new Button { Content = title, ToolTip = tooltip, Margin = new(0, 0, 8, 0) }; button.SetResourceReference(StyleProperty, "WorkbenchButtonStyle"); button.Click += (_, _) => action(); panel.Children.Add(button); }
    private static void SetDropEffect(DragEventArgs args)
    { args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; }
    private void HandleDrop(DragEventArgs args)
    { if (args.Data.GetData(DataFormats.FileDrop) is string[] values) { AddPaths(values); args.Handled = true; } }
}
