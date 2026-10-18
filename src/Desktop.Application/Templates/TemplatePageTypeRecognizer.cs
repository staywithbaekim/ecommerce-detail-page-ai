using Desktop.Application.Psd;
using Desktop.Domain.Templates;
using System.Text.RegularExpressions;

namespace Desktop.Application.Templates;

/// <summary>
/// Conservative PSD-page classifier. It uses inspected PSD structure only;
/// product size-image recognition remains in the separate batch AI workflow.
/// </summary>
public static partial class TemplatePageTypeRecognizer
{
    public static TemplatePageTypeRecognition Recognize(string pageName, PsdInspectionDocument inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (HasSizeTableLabel(pageName)
            || inspection.Layers.Any(layer => IsVisibleOnCanvas(layer, inspection.Canvas)
                && (HasSizeTableLabel(layer.Name)
                    || layer.SmartObject is { } smart
                        && HasSizeTableLabel(Path.GetFileNameWithoutExtension(smart.Filename))))
            || HasMeasurementGrid(inspection))
        {
            return TemplatePageTypeRecognition.SizeTablePage;
        }

        // Ordinary composition remains the conservative baseline when PSD
        // structure does not provide positive size-table evidence. Users can
        // switch either way in the workspace without a third pending state.
        return TemplatePageTypeRecognition.OrdinaryPage;
    }

    public static string ResultLabel(TemplatePageTypeRecognition type) => type switch
    {
        TemplatePageTypeRecognition.SizeTablePage => "尺码页",
        TemplatePageTypeRecognition.OrdinaryPage => "普通页",
        _ => "普通页"
    };

    private static bool IsVisibleOnCanvas(PsdLayerInspection layer, PsdCanvasInspection canvas) =>
        layer.Visible && layer.EffectiveVisible && layer.Opacity > 0 && layer.FillOpacity > 0
        && layer.Bounds.Right > layer.Bounds.Left && layer.Bounds.Bottom > layer.Bounds.Top
        && layer.Bounds.Right > 0 && layer.Bounds.Bottom > 0
        && layer.Bounds.Left < canvas.Width && layer.Bounds.Top < canvas.Height;

    private static bool HasMeasurementGrid(PsdInspectionDocument inspection)
    {
        // Inspection exposes layer names, not text contents. Require independent
        // measurement headers and complete, aligned rows; generic names, raster
        // tables and isolated model measurements cannot establish this evidence.
        // These names/values classify the template only; they never supply product data.
        var text = inspection.Layers.Where(layer => layer.Kind == "type"
            && IsVisibleOnCanvas(layer, inspection.Canvas)).ToArray();
        var headers = text.Where(layer => MeasurementHeader().IsMatch(layer.Name.Trim())).ToArray();
        var sizes = text.Where(layer => SizeToken().IsMatch(layer.Name.Trim())).ToArray();
        var values = text.Where(layer => NumericCell().IsMatch(layer.Name.Trim())).ToArray();
        foreach (var first in headers)
        foreach (var second in headers)
        {
            if (first.Bounds.Right > second.Bounds.Left || !SameRow(first.Bounds, second.Bounds)
                || string.Equals(MeasurementHeader().Match(first.Name.Trim()).Groups[1].Value,
                    MeasurementHeader().Match(second.Name.Trim()).Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                continue;

            var rows = sizes.Where(size => size.Bounds.Right <= first.Bounds.Left
                && size.Bounds.Top >= Math.Max(first.Bounds.Bottom, second.Bounds.Bottom)
                && values.Any(left => SameRow(size.Bounds, left.Bounds) && SameColumn(first.Bounds, left.Bounds)
                    && values.Any(right => left.Bounds.Right <= right.Bounds.Left
                        && SameRow(size.Bounds, right.Bounds) && SameColumn(second.Bounds, right.Bounds))))
                .ToArray();
            if (rows.Any(upper => rows.Any(lower => lower.Bounds.Top >= upper.Bounds.Bottom
                && SameColumn(upper.Bounds, lower.Bounds)
                && !string.Equals(upper.Name.Trim(), lower.Name.Trim(), StringComparison.OrdinalIgnoreCase))))
                return true;
        }
        return false;
    }

    private static bool SameRow(PsdLayerBounds first, PsdLayerBounds second) =>
        Math.Abs(((double)first.Top + first.Bottom) - ((double)second.Top + second.Bottom))
        <= Math.Min((double)first.Bottom - first.Top, (double)second.Bottom - second.Top);

    private static bool SameColumn(PsdLayerBounds first, PsdLayerBounds second) =>
        Math.Abs(((double)first.Left + first.Right) - ((double)second.Left + second.Right))
        <= Math.Min((double)first.Right - first.Left, (double)second.Right - second.Left);

    private static bool HasSizeTableLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        return text == "尺码" || text.Equals("measurements", StringComparison.OrdinalIgnoreCase)
            || SizeTableLabel().IsMatch(text);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:尺码表|尺码数据|尺寸表|size[\s_-]*chart|size[\s_-]*table|measurement[\s_-]+chart)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeTableLabel();

    [GeneratedRegex(@"\A(胸围|腰围|臀围|肩宽|衣长|袖长|裤长|胸宽|chest|bust|waist|hip|shoulder|length|sleeve)(?:\s*[（(]cm[）)])?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeasurementHeader();

    [GeneratedRegex(@"\A(?:XXS|XS|S|M|L|XL|XXL|XXXL|[2-9]XL)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeToken();

    [GeneratedRegex(@"\A[0-9]{1,3}(?:\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericCell();
}
