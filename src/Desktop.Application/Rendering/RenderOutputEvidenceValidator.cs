using System.Text.Json;
using Desktop.Application.Psd;

namespace Desktop.Application.Rendering;

/// <summary>Independent PSD inspection gate; adapter assertions cannot authorize publication.</summary>
public static class RenderOutputEvidenceValidator
{
    public const string VerificationPsdCapability = "output/verification-psd-v1";

    public static bool MatchesPsd(string planJson, PsdInspectionDocument baseline, PsdInspectionDocument output)
    {
        if (!RenderTemplateEvidenceValidator.Matches(planJson, baseline)) return false;
        using var json = JsonDocument.Parse(planJson);
        var plan = json.RootElement;
        var inserting = RenderPlanPlacementMigration.HasSizeInsertion(plan);
        var insertion = inserting ? plan.GetProperty("size_table").GetProperty("insertion") : default;
        if (plan.GetProperty("document").GetProperty("content_type").GetString() == "dynamic_size_table"
            && !inserting
            && (!plan.GetProperty("required_capabilities").EnumerateArray().Any(item => item.GetString() == RenderPlanPlacementMigration.SizeTableCapability)
                || plan.GetProperty("targets").GetArrayLength() != 1
                || plan.GetProperty("targets")[0].GetProperty("asset_resource_id").GetString()
                    != plan.GetProperty("size_table").GetProperty("render_resource_id").GetString())) return false;
        if (!RenderPlanPlacementMigration.IsExplicit(plan) || output.Canvas != baseline.Canvas
            || output.LayerCount != baseline.LayerCount + (inserting ? 1 : 0)
            || output.Layers.Count != baseline.Layers.Count + (inserting ? 1 : 0)
            || output.Layers.Select(layer => layer.LayerId).Distinct().Count() != output.Layers.Count
            || output.Layers.Select(layer => Key(layer.Path)).Distinct(StringComparer.Ordinal).Count() != output.Layers.Count)
            return false;
        var targets = plan.GetProperty("targets").EnumerateArray().ToDictionary(target =>
            Key(target.GetProperty("layer_path").EnumerateArray().Select(item => item.GetInt32() - 1)), target => target);
        var resources = plan.GetProperty("resources").EnumerateArray().ToDictionary(
            item => item.GetProperty("resource_id").GetString()!, item => item);
        var superseded = plan.GetProperty("targets").EnumerateArray()
            .SelectMany(target => target.GetProperty("superseded_layers").EnumerateArray())
            .Select(old => Key(old.GetProperty("layer_path").EnumerateArray().Select(item => item.GetInt32() - 1)))
            .ToHashSet(StringComparer.Ordinal);
        var after = output.Layers.ToDictionary(layer => Key(layer.Path));
        var hiddenRoots = inserting ? insertion.GetProperty("hidden_layers").EnumerateArray()
            .Select(item => item.GetProperty("layer_path")[0].GetInt32() - 1).ToHashSet() : [];
        var anchor = inserting ? insertion.GetProperty("above_layer").GetProperty("layer_path")[0].GetInt32() - 1 : -1;
        if (inserting)
        {
            var oldIds = baseline.Layers.Select(layer => layer.LayerId).ToHashSet();
            var added = output.Layers.Where(layer => !oldIds.Contains(layer.LayerId)).ToArray();
            if (added.Length != 1) return false;
            var layer = added[0];
            if (layer.Path.Count != 1 || layer.Path[0] != anchor + 1
                || layer.Name != insertion.GetProperty("layer_name").GetString() || layer.Kind != "smartobject" || layer.IsGroup
                || !layer.Visible || !layer.EffectiveVisible || layer.Clipping || layer.HasMask || layer.HasVectorMask
                || layer.Opacity != 255 || layer.FillOpacity != 255 || layer.BlendMode is not ("norm" or "normal")
                || layer.ContentEvidence is not { Available: true, EffectsSha256: null, MaskSha256: null, VectorMaskSha256: null }
                || layer.SmartObject is not { Kind: "data", EmbeddedContentAvailable: true } smart
                || !HashEquals(smart.EmbeddedContentSha256, resources[plan.GetProperty("size_table").GetProperty("render_resource_id").GetString()!]
                    .GetProperty("sha256").GetString()) || !MatchesPlacement(smart.TransformBox, insertion.GetProperty("placement"))) return false;
        }
        foreach (var before in baseline.Layers)
        {
            var hidden = superseded.Contains(Key(before.Path));
            var path = before.Path.ToArray();
            if (inserting && path[0] > anchor) path[0]++;
            var hiddenRoot = hiddenRoots.Contains(before.Path[0]);
            var raster = targets.TryGetValue(Key(before.Path), out var knownTarget)
                && knownTarget.GetProperty("expected_layer").GetProperty("kind").GetString() == "pixel";
            if (!after.TryGetValue(Key(path), out var layer)
                || !SameStructure(before, layer, hidden || hiddenRoot && before.Path.Count == 1, hiddenRoot, anchor, raster)
                || raster && layer.LayerId != before.LayerId && baseline.Layers.Any(old => old.LayerId == layer.LayerId)
                || before.ContentEvidence is not { Available: true } || layer.ContentEvidence is not { Available: true }) return false;
            if (before.Kind == "type" && (before.ContentEvidence.TextStyleSha256 is not { Length: 64 }
                || layer.ContentEvidence.TextStyleSha256 is not { Length: 64 })) return false;
            if (before.Kind is "curves" or "exposure" or "colorbalance" or "shape" && (before.ContentEvidence.AdjustmentSha256 is not { Length: 64 }
                || layer.ContentEvidence.AdjustmentSha256 is not { Length: 64 })) return false;
            if (targets.TryGetValue(Key(before.Path), out var target))
            {
                var smart = layer.SmartObject;
                if (smart is not { Kind: "data", EmbeddedContentAvailable: true }
                    || !HashEquals(smart.EmbeddedContentSha256, resources[target.GetProperty("asset_resource_id").GetString()!]
                        .GetProperty("sha256").GetString()) || smart.TransformBox.Count != 8
                    || before.ContentEvidence.MaskSha256 != layer.ContentEvidence.MaskSha256
                    || before.ContentEvidence.VectorMaskSha256 != layer.ContentEvidence.VectorMaskSha256
                    || before.ContentEvidence.EffectsSha256 != layer.ContentEvidence.EffectsSha256) return false;
                var p = target.GetProperty("layout").GetProperty("placement");
                var x = p.GetProperty("left").GetDouble(); var y = p.GetProperty("top").GetDouble();
                var right = x + p.GetProperty("width").GetDouble(); var bottom = y + p.GetProperty("height").GetDouble();
                double[] expected = [x, y, right, y, right, bottom, x, bottom];
                if (smart.TransformBox.Where((value, i) => !double.IsFinite(value) || Math.Abs(value - expected[i]) > 0.1).Any()) return false;
            }
            else if (before.Name != layer.Name || !SameBounds(before, layer, hiddenRoot,
                before.IsGroup && targets.Keys.Any(key => key.StartsWith(Key(before.Path) + '/', StringComparison.Ordinal)))
                || before.ContentEvidence != layer.ContentEvidence || !SameSmartObject(before.SmartObject, layer.SmartObject)) return false;
        }
        return true;
    }

    private static bool SameBounds(PsdLayerInspection before, PsdLayerInspection after, bool explicitlyHiddenRoot,
        bool targetAncestor = false) =>
        before.Bounds == after.Bounds
        // Replaced descendant geometry changes the computed group union.
        // All original children retain their path/identity/content checks and
        // every non-target leaf still requires its exact original bounds.
        || targetAncestor && before.IsGroup && before.Kind == "group" && after.IsGroup && after.Kind == "group"
        // psd-tools computes group bbox from effectively visible descendants,
        // rather than a stored group transform. Hiding a declared root therefore
        // produces an empty bbox for it and its nested groups. Every original
        // descendant still passes the identity, local visibility, content and
        // leaf-bounds checks in the outer loop; a moved/deleted child is rejected.
        || explicitlyHiddenRoot && before.IsGroup && before.Kind == "group"
            && after.IsGroup && after.Kind == "group" && !after.EffectiveVisible
            && after.Bounds == new PsdLayerBounds(0, 0, 0, 0);

    private static bool MatchesPlacement(IReadOnlyList<double> box, JsonElement p)
    {
        var x = p.GetProperty("left").GetDouble(); var y = p.GetProperty("top").GetDouble();
        var right = x + p.GetProperty("width").GetDouble(); var bottom = y + p.GetProperty("height").GetDouble();
        double[] expected = [x, y, right, y, right, bottom, x, bottom];
        return box.Count == 8 && box.Select((value, i) => double.IsFinite(value) && Math.Abs(value - expected[i]) <= 0.1).All(value => value);
    }

    private static bool SameStructure(PsdLayerInspection a, PsdLayerInspection b, bool superseded, bool hiddenAncestor = false,
        int insertionAnchor = -1, bool rasterReplacement = false) =>
        (rasterReplacement ? a.Kind == "pixel" && b.Kind == "smartobject" && b.LayerId > 0
            : a.LayerId == b.LayerId && a.Kind == b.Kind) && a.IsGroup == b.IsGroup
        && (superseded ? !b.Visible && !b.EffectiveVisible : a.Visible == b.Visible
            && (hiddenAncestor ? !b.EffectiveVisible : a.EffectiveVisible == b.EffectiveVisible))
        && a.Clipping == b.Clipping && a.Opacity == b.Opacity
        && a.FillOpacity == b.FillOpacity && a.BlendMode == b.BlendMode && a.HasMask == b.HasMask
        && a.HasVectorMask == b.HasVectorMask && (a.ClippingBase is null ? b.ClippingBase is null
            : b.ClippingBase is not null && a.ClippingBase.LayerId == b.ClippingBase.LayerId
              && a.ClippingBase.Path.Select((part, i) => i == 0 && insertionAnchor >= 0 && part > insertionAnchor ? part + 1 : part)
                  .SequenceEqual(b.ClippingBase.Path));

    private static bool SameSmartObject(PsdSmartObjectInspection? a, PsdSmartObjectInspection? b) => a is null ? b is null
        : b is not null && a.Kind == "data" && b.Kind == "data" && a.EmbeddedContentAvailable && b.EmbeddedContentAvailable
          && HashEquals(a.EmbeddedContentSha256, b.EmbeddedContentSha256) && a.TransformBox.SequenceEqual(b.TransformBox)
          && a.UniqueId == b.UniqueId;

    private static string Key(IEnumerable<int> path) => string.Join('/', path);
    private static bool HashEquals(string? a, string? b) => a is not null && b is not null &&
        string.Equals(a.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase),
            b.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
}
