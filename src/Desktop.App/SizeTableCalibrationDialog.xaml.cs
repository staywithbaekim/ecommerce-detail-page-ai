using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;

namespace Desktop.App;

public partial class SizeTableCalibrationDialog : Window
{
    private readonly TemplateDraftPage _page;
    public TemplateSizeCalibration? Calibration { get; private set; }

    public SizeTableCalibrationDialog(TemplateDraftPage page)
    {
        InitializeComponent();
        _page = page;
        var inspection = page.Resource.Inspection;
        var signature = "sha256:" + (inspection.StructureSignature.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? inspection.StructureSignature[7..] : inspection.StructureSignature).ToLowerInvariant();
        var layers = inspection.Layers.Where(layer => layer.Path.Count == 1
            && layer.Visible && layer.EffectiveVisible && !layer.Clipping
            && layer.Kind is "group" or "type" or "pixel")
            .OrderByDescending(layer => layer.Path[0])
            .Select(layer => new LayerChoice($"{layer.Name} · {layer.Kind}", new(layer.Path[0] + 1,
                layer.LayerId, layer.Kind, layer.Name, signature))).ToArray();
        Anchor.ItemsSource = layers;
        HiddenLayers.ItemsSource = layers;
        var saved = page.Page.SizeTableCalibration;
        if (saved is not null)
        {
            Anchor.SelectedItem = layers.SingleOrDefault(layer => layer.Reference.LayerId == saved.AboveLayer.LayerId);
            foreach (var layer in layers.Where(layer => saved.HiddenLayers.Any(hidden => hidden.LayerId == layer.Reference.LayerId)))
                HiddenLayers.SelectedItems.Add(layer);
        }
        var bounds = saved?.SafeBounds ?? new TemplateSizeArea(0, 0, inspection.Canvas.Width, inspection.Canvas.Height);
        var style = saved?.Style ?? new TemplateSizeStyle("Microsoft YaHei UI", 24, 1000, 56, 24, 12,
            "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3");
        LeftInput.Text = Format(bounds.Left); TopInput.Text = Format(bounds.Top);
        RightInput.Text = Format(bounds.Right); BottomInput.Text = Format(bounds.Bottom);
        FontInput.Text = style.FontFamily; FontSizeInput.Text = Format(style.FontSize); WidthInput.Text = Format(style.TargetWidth);
        RowHeightInput.Text = Format(style.MinimumRowHeight); HorizontalPaddingInput.Text = Format(style.HorizontalCellPadding);
        VerticalPaddingInput.Text = Format(style.VerticalCellPadding); BackgroundInput.Text = style.BackgroundColor;
        HeaderInput.Text = style.HeaderBackgroundColor; TextColorInput.Text = style.TextColor; GridColorInput.Text = style.GridColor;
        LayerNameInput.Text = saved?.LayerName ?? "动态尺码表";
        if (page.PreviewSourceReference is { } preview && System.IO.File.Exists(preview))
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(preview); bitmap.EndInit(); bitmap.Freeze(); TemplatePreview.Source = bitmap;
        }
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Anchor.SelectedItem is not LayerChoice anchor || HiddenLayers.SelectedItems.Count == 0)
                throw new InvalidOperationException("SIZE_TABLE_CALIBRATION_INVALID");
            var value = new TemplateSizeCalibration("size-table-template/v1", _page.Resource.Sha256,
                LayerNameInput.Text.Trim(), anchor.Reference,
                HiddenLayers.SelectedItems.Cast<LayerChoice>().Select(layer => layer.Reference).ToImmutableArray(),
                new(Number(LeftInput.Text), Number(TopInput.Text), Number(RightInput.Text), Number(BottomInput.Text)),
                new(FontInput.Text.Trim(), Number(FontSizeInput.Text), int.Parse(WidthInput.Text, CultureInfo.InvariantCulture),
                    Number(RowHeightInput.Text), Number(HorizontalPaddingInput.Text), Number(VerticalPaddingInput.Text),
                    BackgroundInput.Text.Trim(), HeaderInput.Text.Trim(), TextColorInput.Text.Trim(), GridColorInput.Text.Trim()));
            if (!value.Matches(_page.Resource.Inspection))
                throw new InvalidOperationException("SIZE_TABLE_CALIBRATION_INVALID");
            Calibration = value;
            DialogResult = true;
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidOperationException)
        {
            ValidationMessage.Text = "请检查图层选择、区域和样式。区域须在画布内，字号 8–96，栅格宽度 320–8192；新图层名称不可与模板重复。";
        }
    }

    private sealed record LayerChoice(string Label, TemplateSizeLayer Reference);
}
