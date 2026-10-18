using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Domain.Templates;

namespace Desktop.Application.SizeTables;

/// <summary>Resolves the explicitly calibrated contain-and-center rule on the desktop.</summary>
public static class SizeTableCalibrationResolver
{
    public static bool Matches(SizeTableTemplateCalibration calibration, PsdInspectionDocument inspection)
    {
        try { calibration.Validate(); }
        catch (TemplateRuleViolationException) { return false; }
        var bounds = calibration.SafeBounds;
        if (!string.Equals(calibration.TemplateSha256, inspection.FileSha256, StringComparison.OrdinalIgnoreCase)
            || inspection.Canvas.Depth != 8 || inspection.Canvas.ColorMode != "RGB"
            || bounds.Right > inspection.Canvas.Width || bounds.Bottom > inspection.Canvas.Height
            || inspection.Layers.Any(layer => layer.Name == calibration.LayerName)) return false;
        foreach (var reference in calibration.HiddenLayers.Append(calibration.AboveLayer))
        {
            var layer = inspection.Layers.SingleOrDefault(item => item.Path.Count == 1 && item.Path[0] == reference.RootIndex - 1);
            if (layer is null || layer.LayerId != reference.LayerId || layer.Kind != reference.Kind || layer.Name != reference.Name
                || !layer.Visible || !layer.EffectiveVisible || layer.Clipping
                || !string.Equals(reference.StructureSignature[7..],
                    inspection.StructureSignature.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? inspection.StructureSignature[7..] : inspection.StructureSignature,
                    StringComparison.OrdinalIgnoreCase)) return false;
        }
        return !inspection.Layers.Any(layer => layer.Path.Count == 1
            && layer.Path[0] == calibration.AboveLayer.RootIndex && layer.Clipping);
    }

    public static SizeTableRasterStyle RasterStyle(SizeTableTemplateCalibration calibration)
    {
        calibration.Validate();
        var suffix = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(calibration)))[..16];
        var style = calibration.Style;
        return SizeTableRasterStyle.Create("calibrated_" + suffix, "size_contain_center_v1", style.FontFamily,
            style.FontSize, style.TargetWidth, style.MinimumRowHeight, style.HorizontalCellPadding, style.VerticalCellPadding,
            style.BackgroundColor, style.HeaderBackgroundColor, style.TextColor, style.GridColor);
    }

    public static SizeTableInsertionTarget Resolve(TemplatePage page, SizeTableRasterizedResource raster)
    {
        if (page.ContentType != TemplatePageContentType.DynamicSizeTable)
            throw new InvalidOperationException("SIZE_TABLE_RENDER_TARGET_REQUIRED");
        var calibration = page.SizeTableCalibration ?? throw new InvalidOperationException("SIZE_TABLE_RENDER_TARGET_REQUIRED");
        calibration.Validate();
        if (raster.PixelWidth <= 0 || raster.PixelHeight <= 0) throw new InvalidOperationException("SIZE_TABLE_CALIBRATION_INVALID");
        var bounds = calibration.SafeBounds;
        var width = (decimal)(bounds.Right - bounds.Left);
        var height = (decimal)(bounds.Bottom - bounds.Top);
        var scale = Math.Min(width / raster.PixelWidth, height / raster.PixelHeight);
        var placedWidth = raster.PixelWidth * scale;
        var placedHeight = raster.PixelHeight * scale;
        static SizeTableLayerReference Reference(SizeTableTemplateLayer layer) => new(
            [layer.RootIndex], layer.LayerId, layer.Kind, layer.Name, layer.StructureSignature);
        return new(page.RevisionId, calibration.TemplateSha256, page.PageId,
            new(calibration.LayerName, Reference(calibration.AboveLayer), calibration.HiddenLayers.Select(Reference).ToArray(),
                bounds, new(raster.PixelWidth, raster.PixelHeight,
                    (decimal)bounds.Left + (width - placedWidth) / 2,
                    (decimal)bounds.Top + (height - placedHeight) / 2, placedWidth, placedHeight)));
    }
}
