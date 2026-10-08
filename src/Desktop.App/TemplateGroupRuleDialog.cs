using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Desktop.Application.Templates;

namespace Desktop.App;

/// <summary>Manual, typed editing remains available without an API or product photos.</summary>
public sealed class TemplateGroupRuleDialog : Window
{
    private readonly TemplateRuleWorkbenchViewModel _model;
    private readonly StackPanel _body = new();
    private readonly List<BranchEditor> _branches = [];
    public IReadOnlyList<TemplateBranchRuleEdit> Changes { get; private set; } = [];
    public TemplateGroupRuleDialog(TemplateRuleWorkbenchViewModel model)
    {
        _model = model; Title = "手工编辑模板组规则"; Width = 890; Height = 760; MinWidth = 680; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(20) }; Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick, MaxWidth = 450, Margin = new(0, 0, 12, 0) }; footer.Children.Add(error);
        Button(footer, "取消", () => DialogResult = false);
        Button(footer, "保存规则草稿", () =>
        {
            try
            {
                var values = _branches.Select(branch => branch.Value()).ToArray();
                Changes = values.Select(branch => new TemplateBranchRuleEdit(branch.BranchId, branch))
                    .Concat((model.Draft?.Rules?.Branches ?? []).Where(branch => values.All(value => value.BranchId != branch.BranchId))
                        .Select(branch => new TemplateBranchRuleEdit(branch.BranchId, (TemplateRuleBranchView?)null))).ToArray();
                if (Changes.Count == 0) { DialogResult = false; return; }
                DialogResult = true;
            }
            catch (Exception failure) when (failure is FormatException or OverflowException or InvalidOperationException)
            { error.Text = "请检查颜色数量及框的颜色选择。"; }
        });
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "先按颜色数量分情况，再按从上到下的顺序加入 PSD。框数不等于颜色数。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        Button(top, "添加一种情况", () => AddBranch(null));
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var branch in model.Draft?.Rules?.Branches ?? []) AddBranch(branch);
        if (_branches.Count == 0) AddBranch(null);
    }
    private void AddBranch(TemplateRuleBranchView? basis)
    {
        var editor = new BranchEditor(_model, basis); _branches.Add(editor); _body.Children.Add(editor.Root);
        Button(editor.Header, "删除这条情况", () => { _branches.Remove(editor); _body.Children.Remove(editor.Root); });
    }
    private static void Button(Panel panel, string label, Action action)
    { var button = new Button { Content = label, Margin = new(0, 0, 8, 6), Padding = new(8, 4, 8, 4) }; button.Click += (_, _) => action(); panel.Children.Add(button); }
    private static TextBox Number(string value) => new() { Text = value, Width = 55, Margin = new(6, 0, 10, 6) };
    private static TextBlock Text(string value) => new() { Text = value, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 6) };
    private sealed class BranchEditor
    {
        private readonly string _id;
        private readonly TextBox _minimum, _maximum;
        private readonly ComboBox _parity;
        private readonly CheckBox _coverage;
        private readonly List<RecipeEditor> _recipes = [];
        public Border Root { get; }
        public WrapPanel Header { get; } = new();
        public BranchEditor(TemplateRuleWorkbenchViewModel model, TemplateRuleBranchView? basis)
        {
            _id = basis?.BranchId ?? "branch_" + Guid.NewGuid().ToString("N");
            var body = new StackPanel { Margin = new(12) }; Root = new() { Child = body, BorderBrush = System.Windows.Media.Brushes.LightGray, BorderThickness = new(1), CornerRadius = new(8), Margin = new(0, 12, 0, 0) };
            body.Children.Add(Header); Header.Children.Add(Text("颜色数至少")); _minimum = Number((basis?.Condition.Minimum ?? 1).ToString(CultureInfo.InvariantCulture)); Header.Children.Add(_minimum);
            Header.Children.Add(Text("最多（空为不限）")); _maximum = Number(basis?.Condition.Maximum?.ToString(CultureInfo.InvariantCulture) ?? ""); Header.Children.Add(_maximum);
            _parity = new() { ItemsSource = new[] { "不限奇偶", "奇数", "偶数" }, SelectedIndex = (int)(basis?.Condition.Parity ?? TemplateRuleParityOption.Any), Width = 100, Margin = new(0, 0, 8, 6) }; Header.Children.Add(_parity);
            _coverage = new() { Content = "确保所有颜色各展示一次，不遗漏、不重复", IsChecked = basis?.RequireCompleteColorCoverage ?? false, Margin = new(0, 5, 0, 12) }; body.Children.Add(_coverage);
            var recipes = new StackPanel(); body.Children.Add(recipes);
            void Add(TemplatePageRecipeView? recipe)
            {
                var editor = new RecipeEditor(model, recipe); _recipes.Add(editor); recipes.Children.Add(editor.Root);
                Button(editor.Actions, "移除这次 PSD 使用", () => { _recipes.Remove(editor); recipes.Children.Remove(editor.Root); });
            }
            Button(body, "加入 PSD 到输出顺序", () => Add(null));
            foreach (var recipe in basis?.Recipes ?? []) Add(recipe);
        }
        public TemplateRuleBranchView Value() => new(_id, new(int.Parse(_minimum.Text, CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(_maximum.Text) ? null : int.Parse(_maximum.Text, CultureInfo.InvariantCulture), (TemplateRuleParityOption)_parity.SelectedIndex),
            [.. _recipes.Select(recipe => recipe.Value())], _coverage.IsChecked == true);
    }
    private sealed class RecipeEditor
    {
        private static readonly string[] FixedColors = ["主色", "唯一副色"];
        private readonly string _id;
        private readonly ComboBox _page, _repeat;
        private readonly TextBox _expected;
        private readonly StackPanel _frames = new();
        private readonly List<(string FrameId, ComboBox Choice)> _assignments = [];
        public StackPanel Root { get; } = new() { Margin = new(0, 8, 0, 16) };
        public WrapPanel Actions { get; } = new();
        public RecipeEditor(TemplateRuleWorkbenchViewModel model, TemplatePageRecipeView? basis)
        {
            _id = basis?.RecipeId ?? "recipe_" + Guid.NewGuid().ToString("N"); Root.Children.Add(Actions);
            _page = new() { ItemsSource = model.Pages, DisplayMemberPath = nameof(TemplateRulePageViewModel.DisplayName), Width = 210, Margin = new(0, 0, 8, 6) };
            _page.SelectedItem = model.Pages.SingleOrDefault(page => page.PageId == basis?.PageId) ?? model.Pages.FirstOrDefault(); Actions.Children.Add(_page);
            _repeat = new() { ItemsSource = new[] { "每款一次", "全部颜色依次分组", "仅副色依次分组" }, SelectedIndex = basis?.Repeat == TemplateRuleRepeatOption.ColorChunks ? basis.ColorRange == TemplateRuleColorRangeOption.Secondary ? 2 : 1 : 0, Width = 160, Margin = new(0, 0, 8, 6) }; Actions.Children.Add(_repeat);
            var expected = new WrapPanel(); Root.Children.Add(expected); expected.Children.Add(Text("预期张数（可不填）")); _expected = Number(basis?.ExpectedInstanceCount?.ToString(CultureInfo.InvariantCulture) ?? ""); expected.Children.Add(_expected);
            Root.Children.Add(new TextBlock { Text = "共享同一个颜色的框，选择相同的颜色。分组时按第1、第2…个颜色配对。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
            Root.Children.Add(_frames); Rebuild(basis);
            _page.SelectionChanged += (_, _) => Rebuild(null);
        }
        private void Rebuild(TemplatePageRecipeView? basis)
        {
            _frames.Children.Clear(); _assignments.Clear();
            if (_page.SelectedItem is not TemplateRulePageViewModel page) return;
            var allocated = (basis?.ColorSlots ?? []).Where(slot => slot.Source == TemplateRuleColorSourceOption.Allocated).Select(slot => slot.SlotId).ToArray();
            foreach (var frame in page.Frames)
            {
                var row = new DockPanel { Margin = new(0, 0, 0, 6) };
                var colors = FixedColors.Concat(Enumerable.Range(1, Math.Max(16, page.Frames.Count)).Select(index => $"第{index}个颜色")).ToArray();
                var choice = new ComboBox { ItemsSource = colors, SelectedIndex = 0, Width = 150 };
                if (basis?.FrameBindings.SingleOrDefault(binding => binding.FrameId == frame.FrameId) is { } binding)
                {
                    var slot = basis.ColorSlots.Single(item => item.SlotId == binding.SlotId);
                    choice.SelectedIndex = slot.Source == TemplateRuleColorSourceOption.Primary ? 0 : slot.Source == TemplateRuleColorSourceOption.SingleSecondary ? 1 : Array.IndexOf(allocated, slot.SlotId) + 2;
                }
                DockPanel.SetDock(choice, Dock.Right); row.Children.Add(choice);
                row.Children.Add(new TextBlock { Text = $"{frame.Label}  {frame.Description}", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 12, 0) });
                _frames.Children.Add(row); _assignments.Add((frame.FrameId, choice));
            }
        }
        public TemplatePageRecipeView Value()
        {
            var page = (TemplateRulePageViewModel?)_page.SelectedItem ?? throw new InvalidOperationException();
            var selections = _assignments.Select(item => item.Choice.SelectedIndex).Distinct().Order().ToArray();
            return new(_id, page.PageId, _repeat.SelectedIndex == 0 ? TemplateRuleRepeatOption.Once : TemplateRuleRepeatOption.ColorChunks,
                _repeat.SelectedIndex == 0 ? TemplateRuleColorRangeOption.None : _repeat.SelectedIndex == 1 ? TemplateRuleColorRangeOption.All : TemplateRuleColorRangeOption.Secondary,
                [.. selections.Select(index => new TemplateRuleColorSlotView("color_" + index, index == 0 ? TemplateRuleColorSourceOption.Primary : index == 1 ? TemplateRuleColorSourceOption.SingleSecondary : TemplateRuleColorSourceOption.Allocated))],
                [.. _assignments.Select(item => new TemplateFrameColorBindingView(item.FrameId, "color_" + item.Choice.SelectedIndex))],
                string.IsNullOrWhiteSpace(_expected.Text) ? null : int.Parse(_expected.Text, CultureInfo.InvariantCulture));
        }
    }
}
