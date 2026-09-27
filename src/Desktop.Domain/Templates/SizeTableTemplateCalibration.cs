using System.Collections.Immutable;

namespace Desktop.Domain.Templates;

/// <summary>Explicit page-local insertion rules, bound to immutable template bytes.</summary>
public sealed record SizeTableTemplateCalibration(
    string Version, string TemplateSha256, string LayerName,
    SizeTableTemplateLayer AboveLayer, ImmutableArray<SizeTableTemplateLayer> HiddenLayers,
    TemplateRect SafeBounds, SizeTableTemplateStyle Style)
{
    public void Validate()
    {
        if (Version != "size-table-template/v1" || TemplateSha256 is not { Length: 64 }
            || !TemplateSha256.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(LayerName)
            || LayerName.Length > 128 || LayerName.Any(char.IsControl) || AboveLayer is null
            || HiddenLayers.IsDefaultOrEmpty || HiddenLayers.Length > 64 || HiddenLayers.Any(layer => layer is null)
            || HiddenLayers.Select(layer => layer.LayerId).Distinct().Count() != HiddenLayers.Length
            || HiddenLayers.Select(layer => layer.RootIndex).Distinct().Count() != HiddenLayers.Length
            || Style is null) throw Invalid();
        _ = TemplateRect.Create(SafeBounds.Left, SafeBounds.Top, SafeBounds.Right, SafeBounds.Bottom);
        foreach (var layer in HiddenLayers.Append(AboveLayer)) layer.Validate();
        Style.Validate();
    }

    private static TemplateRuleViolationException Invalid() => new("SIZE_TABLE_CALIBRATION_INVALID");
}

/// <summary>One-based bottom-to-top root index; includes independent ID/name/signature evidence.</summary>
public sealed record SizeTableTemplateLayer(int RootIndex, int LayerId, string Kind, string Name, string StructureSignature)
{
    public void Validate()
    {
        if (RootIndex is < 1 or > 100000 || LayerId < 1 || Kind is not ("group" or "pixel" or "type")
            || string.IsNullOrWhiteSpace(Name) || Name.Length > 256 || StructureSignature is not { Length: 71 }
            || !StructureSignature.StartsWith("sha256:", StringComparison.Ordinal)
            || !StructureSignature.AsSpan(7).ToArray().All(char.IsAsciiHexDigit))
            throw new TemplateRuleViolationException("SIZE_TABLE_CALIBRATION_INVALID");
    }
}

public sealed record SizeTableTemplateStyle(string FontFamily, double FontSize, int TargetWidth,
    double MinimumRowHeight, double HorizontalCellPadding, double VerticalCellPadding,
    string BackgroundColor, string HeaderBackgroundColor, string TextColor, string GridColor)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 128 || FontFamily.Any(char.IsControl)
            || !double.IsFinite(FontSize) || FontSize is < 8 or > 96 || TargetWidth is < 320 or > 8192
            || !double.IsFinite(MinimumRowHeight) || MinimumRowHeight is < 16 or > 512
            || !double.IsFinite(HorizontalCellPadding) || HorizontalCellPadding is < 0 or > 256
            || !double.IsFinite(VerticalCellPadding) || VerticalCellPadding is < 0 or > 256
            || new[] { BackgroundColor, HeaderBackgroundColor, TextColor, GridColor }.Any(color =>
                color is not { Length: 9 } || color[0] != '#' || !color.Skip(1).All(char.IsAsciiHexDigit)))
            throw new TemplateRuleViolationException("SIZE_TABLE_CALIBRATION_INVALID");
    }
}
