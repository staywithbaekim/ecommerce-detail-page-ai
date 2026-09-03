using Desktop.Domain.SizeTables;

namespace Desktop.Application.SizeTables;

public sealed record SizeTableRasterStyle
{
    private SizeTableRasterStyle(
        string styleVersion,
        string layoutVersion,
        string fontFamily,
        double fontSize,
        int targetWidth,
        double minimumRowHeight,
        double horizontalCellPadding,
        double verticalCellPadding,
        string backgroundColor,
        string headerBackgroundColor,
        string textColor,
        string gridColor)
    {
        StyleVersion = styleVersion;
        LayoutVersion = layoutVersion;
        FontFamily = fontFamily;
        FontSize = fontSize;
        TargetWidth = targetWidth;
        MinimumRowHeight = minimumRowHeight;
        HorizontalCellPadding = horizontalCellPadding;
        VerticalCellPadding = verticalCellPadding;
        BackgroundColor = backgroundColor;
        HeaderBackgroundColor = headerBackgroundColor;
        TextColor = textColor;
        GridColor = gridColor;
    }

    public string StyleVersion { get; }

    public string LayoutVersion { get; }

    public string FontFamily { get; }

    public double FontSize { get; }

    public int TargetWidth { get; }

    public double MinimumRowHeight { get; }

    public double HorizontalCellPadding { get; }

    public double VerticalCellPadding { get; }

    public string BackgroundColor { get; }

    public string HeaderBackgroundColor { get; }

    public string TextColor { get; }

    public string GridColor { get; }

    public static SizeTableRasterStyle Create(
        string styleVersion,
        string layoutVersion,
        string fontFamily,
        double fontSize,
        int targetWidth,
        double minimumRowHeight,
        double horizontalCellPadding,
        double verticalCellPadding,
        string backgroundColor,
        string headerBackgroundColor,
        string textColor,
        string gridColor)
    {
        ValidateIdentifier(styleVersion, "SIZE_TABLE_STYLE_VERSION_INVALID");
        ValidateIdentifier(layoutVersion, "SIZE_TABLE_LAYOUT_VERSION_INVALID");
        if (string.IsNullOrWhiteSpace(fontFamily) || fontFamily.Length > 128
            || fontFamily.Any(char.IsControl)
            || !double.IsFinite(fontSize) || fontSize is < 8 or > 96
            || targetWidth is < 320 or > 8192
            || !double.IsFinite(minimumRowHeight) || minimumRowHeight is < 16 or > 512
            || !double.IsFinite(horizontalCellPadding) || horizontalCellPadding is < 0 or > 256
            || !double.IsFinite(verticalCellPadding) || verticalCellPadding is < 0 or > 256)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_RASTER_STYLE_INVALID");
        }

        ValidateColor(backgroundColor);
        ValidateColor(headerBackgroundColor);
        ValidateColor(textColor);
        ValidateColor(gridColor);
        return new SizeTableRasterStyle(
            styleVersion,
            layoutVersion,
            fontFamily,
            fontSize,
            targetWidth,
            minimumRowHeight,
            horizontalCellPadding,
            verticalCellPadding,
            backgroundColor.ToUpperInvariant(),
            headerBackgroundColor.ToUpperInvariant(),
            textColor.ToUpperInvariant(),
            gridColor.ToUpperInvariant());
    }

    private static void ValidateIdentifier(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new SizeTableRuleViolationException(code);
        }
    }

    private static void ValidateColor(string value)
    {
        if (value is null || value.Length != 9 || value[0] != '#'
            || value.Skip(1).Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_RASTER_COLOR_INVALID");
        }
    }
}

public sealed record SizeTableRasterizationRequest(
    SizeTable Table,
    SizeTableRasterStyle Style);

public sealed record SizeTableRasterizedResource(
    string ResourceId,
    string Role,
    string MediaType,
    string Sha256,
    long ByteLength,
    int PixelWidth,
    int PixelHeight,
    string TableDataDigest,
    string StyleVersion,
    string LayoutVersion,
    string OwnedRelativePath,
    string RasterizerVersion = "wpf-size-table-rasterizer/v1",
    string FontSignature = "",
    int Dpi = 96);

public interface ISizeTableRasterizer
{
    Task<SizeTableRasterizedResource> RasterizeAsync(
        SizeTableRasterizationRequest request,
        CancellationToken cancellationToken);
}
