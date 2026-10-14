using System.Text.Json;
using Desktop.Application.Psd;

namespace Desktop.Application.Rendering;

/// <summary>Closes a validated plan against the inspection of its exact immutable PSD bytes.</summary>
public static class RenderTemplateEvidenceValidator
{
    public const string RasterReplacementCapability = "image-layer/replace-v1";
    public const string CoveredRasterStackCapability = "image-layer/covered-stack-v1";

    public static bool Matches(string planJson, PsdInspectionDocument? inspection)
    {
        if (inspection is null) return false;
        using var json = JsonDocument.Parse(planJson);
        var plan = json.RootElement;
        var coveredRaster = plan.GetProperty("targets").EnumerateArray().Any(target =>
            target.GetProperty("expected_layer").GetProperty("kind").GetString() == "pixel"
            && target.GetProperty("superseded_layers").GetArrayLength() > 0);
        if (coveredRaster != plan.GetProperty("required_capabilities").EnumerateArray()
            .Any(item => item.GetString() == CoveredRasterStackCapability)) return false;
        var document = plan.GetProperty("document");
        var template = plan.GetProperty("resources").EnumerateArray().Single(resource =>
            resource.GetProperty("resource_id").GetString() == document.GetProperty("template_resource_id").GetString());
        var canvas = document.GetProperty("canvas");
        if (!EqualDigest(template.GetProperty("sha256").GetString()!, inspection.FileSha256)
            || !template.TryGetProperty("byte_length", out var length) || length.GetInt64() != inspection.SizeBytes
            || canvas.GetProperty("width").GetInt32() != inspection.Canvas.Width
            || canvas.GetProperty("height").GetInt32() != inspection.Canvas.Height
            || inspection.Canvas.Depth != 8 || inspection.Canvas.ColorMode != "RGB"
            || inspection.Layers.Count == 0 || inspection.LayerCount != inspection.Layers.Count)
            return false;

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var layerIds = new HashSet<int>();
        foreach (var layer in inspection.Layers)
        {
            if (layer.Path.Count == 0 || layer.Path.Any(segment => segment < 0)
                || !paths.Add(string.Join('/', layer.Path)) || !layerIds.Add(layer.LayerId)) return false;
        }
        var touched = new HashSet<int>();
        if (RenderPlanPlacementMigration.HasSizeInsertion(plan))
        {
            var insertion = plan.GetProperty("size_table").GetProperty("insertion");
            if (inspection.Layers.Any(layer => layer.Name == insertion.GetProperty("layer_name").GetString())) return false;
            foreach (var reference in insertion.GetProperty("hidden_layers").EnumerateArray().Append(insertion.GetProperty("above_layer")))
            {
                var layer = Resolve(inspection, reference.GetProperty("layer_path"));
                if (layer is null || layer.Path.Count != 1 || layer.LayerId != reference.GetProperty("layer_id").GetInt32()
                    || layer.Kind != reference.GetProperty("kind").GetString() || layer.Name != reference.GetProperty("name").GetString()
                    || !layer.Visible || !layer.EffectiveVisible || layer.Clipping
                    || !EqualDigest(inspection.StructureSignature, reference.GetProperty("structure_signature").GetString()!)) return false;
            }
            var anchor = insertion.GetProperty("above_layer").GetProperty("layer_path")[0].GetInt32() - 1;
            // Inserting under a clipped root would change its clipping base.
            if (inspection.Layers.Any(layer => layer.Path.Count == 1 && layer.Path[0] == anchor + 1 && layer.Clipping)) return false;
        }
        var sizeTable = plan.GetProperty("required_capabilities").EnumerateArray()
            .Any(item => item.GetString() == RenderPlanPlacementMigration.SizeTableCapability);
        foreach (var target in plan.GetProperty("targets").EnumerateArray())
        {
            var layer = Resolve(inspection, target.GetProperty("layer_path"));
            var expected = target.GetProperty("expected_layer");
            var raster = expected.GetProperty("kind").GetString() == "pixel";
            if (layer is null || !touched.Add(layer.LayerId)
                || (raster ? !IsVisibleRasterTarget(layer, plan) : !IsEmbeddedVisibleSmartObject(layer))
                || layer.LayerId != expected.GetProperty("layer_id").GetInt32()
                || layer.Name != expected.GetProperty("name_before").GetString()
                || layer.Visible != expected.GetProperty("visible").GetBoolean()
                || layer.Clipping != expected.GetProperty("clipping").GetBoolean()
                || layer.HasMask != expected.GetProperty("user_mask").GetBoolean()
                || layer.HasVectorMask != expected.GetProperty("vector_mask").GetBoolean()
                || layer.Opacity != expected.GetProperty("opacity").GetInt32()
                || NormalizeBlend(layer.BlendMode) != NormalizeBlend(expected.GetProperty("blend_mode").GetString()!)
                || !EqualDigest(inspection.StructureSignature, expected.GetProperty("structure_signature").GetString()!))
                return false;
            if (sizeTable && inspection.Layers.Where(item => item.Path.Count <= layer.Path.Count
                    && layer.Path.Take(item.Path.Count).SequenceEqual(item.Path)).Any(item =>
                        item.HasMask || item.HasVectorMask || item.Clipping || item.Opacity != 255 || item.FillOpacity != 255
                        || !new[] { "norm", "normal", "pass", "passThrough" }.Contains(item.BlendMode, StringComparer.Ordinal)
                        || item.ContentEvidence is not { Available: true, EffectsSha256: null })) return false;
            foreach (var superseded in target.GetProperty("superseded_layers").EnumerateArray())
            {
                var old = Resolve(inspection, superseded.GetProperty("layer_path"));
                if (old is null || !touched.Add(old.LayerId) || !IsEmbeddedVisibleSmartObject(old)
                    || old.Visible != superseded.GetProperty("expected_visible").GetBoolean()
                    || old.Clipping != superseded.GetProperty("expected_clipping").GetBoolean()
                    || superseded.TryGetProperty("expected_layer_id", out var id) && old.LayerId != id.GetInt32())
                    return false;
            }
        }
        return true;
    }

    private static bool IsVisibleRasterTarget(PsdLayerInspection layer, JsonElement plan) =>
        plan.GetProperty("document").GetProperty("content_type").GetString() == "ordinary_set"
        && plan.GetProperty("required_capabilities").EnumerateArray().Any(item => item.GetString() == RasterReplacementCapability)
        && RenderPlanPlacementMigration.IsExplicit(plan)
        && layer.Kind == "pixel" && !layer.IsGroup && layer.SmartObject is null
        && layer.Visible && layer.EffectiveVisible && !layer.HasMask && !layer.HasVectorMask
        && layer.PixelEvidence.HasReplaceableRasterContent;

    private static PsdLayerInspection? Resolve(PsdInspectionDocument inspection, JsonElement path)
    {
        // Worker paths are zero based; the frozen RenderPlan paths are one based.
        var segments = path.EnumerateArray().Select(segment => segment.GetInt32() - 1).ToArray();
        return inspection.Layers.SingleOrDefault(layer => layer.Path.SequenceEqual(segments));
    }

    private static bool IsEmbeddedVisibleSmartObject(PsdLayerInspection layer) =>
        layer.Visible && layer.EffectiveVisible && IsEmbeddedSmartObject(layer);

    private static bool IsEmbeddedSmartObject(PsdLayerInspection layer) =>
        !layer.IsGroup && layer.SmartObject?.Kind == "data"
        && layer.Kind.Replace("_", "", StringComparison.Ordinal).Equals("smartobject", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBlend(string value) => value == "norm" ? "normal" : value;
    private static bool EqualDigest(string left, string right) =>
        string.Equals(left.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? left[7..] : left,
            right.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? right[7..] : right,
            StringComparison.OrdinalIgnoreCase);
}
