using System.Collections.Immutable;
using Desktop.Application.Psd;
using Desktop.Application.SizeTables;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

// Application projections keep the WPF editor independent of Domain models.
public sealed record TemplateSizeLayer(int RootIndex, int LayerId, string Kind, string Name, string StructureSignature);
public sealed record TemplateSizeArea(double Left, double Top, double Right, double Bottom);
public sealed record TemplateSizeStyle(string FontFamily, double FontSize, int TargetWidth,
    double MinimumRowHeight, double HorizontalCellPadding, double VerticalCellPadding,
    string BackgroundColor, string HeaderBackgroundColor, string TextColor, string GridColor);
public sealed record TemplateSizeCalibration(string Version, string TemplateSha256, string LayerName,
    TemplateSizeLayer AboveLayer, IReadOnlyList<TemplateSizeLayer> HiddenLayers, TemplateSizeArea SafeBounds, TemplateSizeStyle Style)
{
    internal SizeTableTemplateCalibration ToDomain()
    {
        if (AboveLayer is null || HiddenLayers is null || HiddenLayers.Any(layer => layer is null)
            || SafeBounds is null || Style is null) throw new InvalidOperationException("SIZE_TABLE_CALIBRATION_INVALID");
        static SizeTableTemplateLayer Layer(TemplateSizeLayer layer) => new(layer.RootIndex, layer.LayerId, layer.Kind, layer.Name, layer.StructureSignature);
        var result = new SizeTableTemplateCalibration(Version, TemplateSha256, LayerName, Layer(AboveLayer),
            HiddenLayers.Select(Layer).ToImmutableArray(), TemplateRect.Create(SafeBounds.Left, SafeBounds.Top, SafeBounds.Right, SafeBounds.Bottom),
            new(Style.FontFamily, Style.FontSize, Style.TargetWidth, Style.MinimumRowHeight,
                Style.HorizontalCellPadding, Style.VerticalCellPadding, Style.BackgroundColor, Style.HeaderBackgroundColor, Style.TextColor, Style.GridColor));
        result.Validate();
        return result;
    }

    internal static TemplateSizeCalibration? FromDomain(SizeTableTemplateCalibration? value)
    {
        if (value is null) return null;
        static TemplateSizeLayer Layer(SizeTableTemplateLayer layer) => new(layer.RootIndex, layer.LayerId, layer.Kind, layer.Name, layer.StructureSignature);
        var style = value.Style;
        return new(value.Version, value.TemplateSha256, value.LayerName, Layer(value.AboveLayer), value.HiddenLayers.Select(Layer).ToArray(),
            new(value.SafeBounds.Left, value.SafeBounds.Top, value.SafeBounds.Right, value.SafeBounds.Bottom),
            new(style.FontFamily, style.FontSize, style.TargetWidth, style.MinimumRowHeight, style.HorizontalCellPadding,
                style.VerticalCellPadding, style.BackgroundColor, style.HeaderBackgroundColor, style.TextColor, style.GridColor));
    }

    public bool Matches(PsdInspectionDocument inspection)
    {
        try { return SizeTableCalibrationResolver.Matches(ToDomain(), inspection); }
        catch (InvalidOperationException) { return false; }
    }
}
