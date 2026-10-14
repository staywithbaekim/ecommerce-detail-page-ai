using System.Text.Json.Nodes;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;

namespace Desktop.Application.Tests;

public sealed class RenderOutputEvidenceValidatorTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("raster", true)]
    [InlineData("raster_capability_missing", false)]
    [InlineData("raster_covered", false)]
    [InlineData("raster_decoration", false)]
    [InlineData("raster_id_collision", false)]
    [InlineData("raster_wrong_payload", false)]
    [InlineData("raster_group", true)]
    [InlineData("raster_group_leaf_moved", false)]
    [InlineData("raster_group_other_moved", false)]
    [InlineData("raster_group_content_changed", false)]
    [InlineData("raster_group_id_changed", false)]
    [InlineData("raster_dormant_still_covered", true)]
    [InlineData("raster_dormant_exposed", true)]
    [InlineData("raster_dormant_one_pixel_exposed", true)]
    [InlineData("raster_dormant_unknown", true)]
    [InlineData("raster_residual_unchanged", true)]
    [InlineData("raster_residual_grows", true)]
    [InlineData("raster_dormant_other_window", true)]
    [InlineData("raster_dormant_above_target", true)]
    [InlineData("raster_dormant_photo_pixels", true)]
    [InlineData("old_worker", false)]
    [InlineData("missing_layer", false)]
    [InlineData("pixels", false)]
    [InlineData("text", false)]
    [InlineData("text_style", false)]
    [InlineData("missing_text_style", false)]
    [InlineData("both_missing_text_style", false)]
    [InlineData("curves", true)]
    [InlineData("exposure", true)]
    [InlineData("colorbalance", true)]
    [InlineData("colorbalance_changed", false)]
    [InlineData("colorbalance_missing", false)]
    [InlineData("adjustment_changed", false)]
    [InlineData("adjustment_missing", false)]
    [InlineData("shape", true)]
    [InlineData("shape_changed", false)]
    [InlineData("shape_missing", false)]
    [InlineData("mask", false)]
    [InlineData("effects", false)]
    [InlineData("payload", false)]
    [InlineData("geometry", false)]
    [InlineData("linked", false)]
    [InlineData("canvas", false)]
    [InlineData("superseded", true)]
    [InlineData("superseded_already_hidden", false)]
    [InlineData("superseded_not_hidden", false)]
    [InlineData("superseded_payload", false)]
    [InlineData("superseded_geometry", false)]
    [InlineData("superseded_name", false)]
    [InlineData("superseded_mask", false)]
    [InlineData("superseded_other_hidden", false)]
    public void StructuralAndReplacementEvidenceAuthorizesPsdOutput(string mutation, bool expected)
    {
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
        plan["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.Capability);
        plan["targets"]![0]!["layout"]!["placement"] = JsonNode.Parse("""
            {"coordinate_space":"document_pixels","source_width":4000,"source_height":6000,
             "left":-5,"top":10,"width":800,"height":1200,"clipping":"preserve_template_masks_and_canvas"}
            """);
        var hash = new string('a', 64);
        var content = new PsdLayerContentEvidence(true, hash, null, null, null, null);
        var target = new PsdLayerInspection([1], 257, "product_frame", "smartobject", false, true, true, false, 255, 255, "norm",
            new(0, 0, 790, 1252), false, false, null, new("id", "source.jpg", "data", "jpg", "jpg", 128, false, []))
            { ContentEvidence = content };
        var fixedLayer = target with { Path = [0], LayerId = 258, Kind = "type", SmartObject = null,
            Name = "fixed", ContentEvidence = content with { TextSha256 = hash, TextStyleSha256 = hash } };
        var baseline = new PsdInspectionDocument(plan["resources"]![0]!["sha256"]!.GetValue<string>(), 1048576,
            new(790, 1252, 8, "RGB"), 2, 1, 0, 0, 0, plan["targets"]![0]!["expected_layer"]!["structure_signature"]!.GetValue<string>(),
            new(1, 1, 2), 1024, null, [fixedLayer, target]);
        var renderedTarget = target with { Name = "replacement", SmartObject = target.SmartObject! with {
            EmbeddedContentAvailable = true, EmbeddedContentSha256 = plan["resources"]![1]!["sha256"]!.GetValue<string>(),
            TransformBox = [-5, 10, 795, 10, 795, 1210, -5, 1210] } };
        var renderedFixed = fixedLayer;
        if (mutation.StartsWith("raster", StringComparison.Ordinal))
        {
            plan["targets"]![0]!["expected_layer"]!["kind"] = "pixel";
            if (mutation != "raster_capability_missing")
                plan["required_capabilities"]!.AsArray().Add(RenderTemplateEvidenceValidator.RasterReplacementCapability);
            target = target with { Kind = "pixel", SmartObject = null,
                PixelEvidence = new(true, 10000, mutation == "raster_covered" ? 0 : 9000,
                    mutation == "raster_covered" ? null : new(0, 0, 100, 100))
                { RasterImage = new(true, mutation == "raster_decoration" ? 2 : 500, .2) } };
            baseline = baseline with { SmartObjectCount = 0, Layers = [fixedLayer, target] };
            renderedTarget = renderedTarget with { LayerId = mutation == "raster_id_collision" ? fixedLayer.LayerId : 9000 };
            if (mutation == "raster_wrong_payload")
                renderedTarget = renderedTarget with { SmartObject = renderedTarget.SmartObject! with { EmbeddedContentSha256 = hash } };
        }
        if (mutation is "curves" or "exposure" or "adjustment_changed" or "adjustment_missing" or "shape" or "shape_changed" or "shape_missing"
            or "colorbalance" or "colorbalance_changed" or "colorbalance_missing")
        {
            fixedLayer = fixedLayer with { Kind = mutation.StartsWith("shape", StringComparison.Ordinal) ? "shape"
                : mutation.StartsWith("colorbalance", StringComparison.Ordinal) ? "colorbalance" : mutation == "exposure" ? "exposure" : "curves",
                ContentEvidence = content with { AdjustmentSha256 = mutation is "adjustment_missing" or "shape_missing" or "colorbalance_missing" ? null : hash } };
            baseline = baseline with { Layers = [fixedLayer, target] };
            renderedFixed = mutation is "adjustment_changed" or "shape_changed" or "colorbalance_changed"
                ? fixedLayer with { ContentEvidence = content with { AdjustmentSha256 = new string('b', 64) } }
                : fixedLayer;
        }
        if (mutation == "old_worker") renderedFixed = renderedFixed with { ContentEvidence = null };
        if (mutation == "pixels") renderedFixed = renderedFixed with { ContentEvidence = content with { PixelSha256 = new string('b', 64) } };
        if (mutation == "text") renderedFixed = renderedFixed with { ContentEvidence = content with { TextSha256 = new string('b', 64) } };
        if (mutation == "text_style") renderedFixed = renderedFixed with { ContentEvidence = fixedLayer.ContentEvidence! with { TextStyleSha256 = new string('b', 64) } };
        if (mutation is "missing_text_style" or "both_missing_text_style")
            renderedFixed = renderedFixed with { ContentEvidence = fixedLayer.ContentEvidence! with { TextStyleSha256 = null } };
        if (mutation == "both_missing_text_style") baseline = baseline with { Layers = [renderedFixed, target] };
        if (mutation == "mask") renderedTarget = renderedTarget with { ContentEvidence = content with { MaskSha256 = hash } };
        if (mutation == "effects") renderedTarget = renderedTarget with { ContentEvidence = content with { EffectsSha256 = hash } };
        if (mutation == "payload") renderedTarget = renderedTarget with { SmartObject = renderedTarget.SmartObject! with { EmbeddedContentSha256 = hash } };
        if (mutation == "geometry") renderedTarget = renderedTarget with { SmartObject = renderedTarget.SmartObject! with { TransformBox = [-4, 10, 795, 10, 795, 1210, -5, 1210] } };
        if (mutation == "linked") renderedTarget = renderedTarget with { SmartObject = renderedTarget.SmartObject! with { Kind = "external" } };
        var output = baseline with { Layers = [renderedFixed, renderedTarget] };
        if (mutation.StartsWith("raster_dormant", StringComparison.Ordinal)
            || mutation.StartsWith("raster_residual", StringComparison.Ordinal))
        {
            var clippingBase = new PsdClippingBaseInspection([0], 701, "window", "pixel", true, true,
                new(0, 0, 790, 1252), false, false, 255, "norm");
            target = target with { Path = [3], Clipping = true, ClippingBase = clippingBase };
            renderedTarget = renderedTarget with { Path = [3], Clipping = true, ClippingBase = clippingBase };
            plan["targets"]![0]!["layer_path"] = JsonNode.Parse("[4]");
            plan["targets"]![0]!["expected_layer"]!["clipping"] = true;
            var oldCount = mutation.StartsWith("raster_residual", StringComparison.Ordinal) ? 1 : 0;
            var old = renderedTarget with { Path = [2], LayerId = 702, Name = "covered old photo",
                SmartObject = renderedTarget.SmartObject! with { UniqueId = "old", EmbeddedContentSha256 = hash },
                PixelEvidence = new(true, 10000, oldCount, oldCount == 0 ? null : new(0, 0, 1, 1)) };
            if (mutation == "raster_dormant_other_window") old = old with { ClippingBase = clippingBase with { Path = [1], LayerId = 703 } };
            if (mutation == "raster_dormant_above_target") old = old with { Path = [4] };
            if (mutation == "raster_dormant_photo_pixels") old = old with { Kind = "pixel", SmartObject = null,
                PixelEvidence = old.PixelEvidence with { RasterImage = new(true, 500, .2) } };
            var count = mutation is "raster_dormant_still_covered" ? 0 : mutation is "raster_residual_unchanged" or "raster_dormant_one_pixel_exposed" ? 1 : 2351;
            var exposed = old with { PixelEvidence = old.PixelEvidence with { Available = mutation != "raster_dormant_unknown",
                CompositeContributionPixelCount = count, CompositeContributionBounds = count == 0 ? null : new(0, 0, 790, 1252) } };
            var baseLayer = fixedLayer with { Kind = "pixel", LayerId = 701, ContentEvidence = content };
            baseline = baseline with { Layers = [baseLayer, old, target], LayerCount = 3 };
            output = baseline with { Layers = [baseLayer, exposed, renderedTarget] };
        }
        if (mutation.StartsWith("raster_group", StringComparison.Ordinal))
        {
            plan["targets"]![0]!["layer_path"] = JsonNode.Parse("[2,1]");
            target = target with { Path = [1, 0] };
            renderedTarget = renderedTarget with { Path = [1, 0] };
            fixedLayer = fixedLayer with { Path = [1, 1] };
            renderedFixed = fixedLayer;
            var group = fixedLayer with { Path = [1], LayerId = 805, Kind = "group", IsGroup = true,
                ContentEvidence = new(true, null, null, null, null, null), Bounds = new(0, 0, 1000, 1400) };
            var other = group with { Path = [0], LayerId = 806 };
            var changedGroup = group with { Bounds = new(-5, 10, 800, 1400) };
            var changedOther = other;
            if (mutation == "raster_group_leaf_moved") renderedFixed = renderedFixed with { Bounds = new(1, 0, 790, 1252) };
            if (mutation == "raster_group_other_moved") changedOther = other with { Bounds = new(1, 0, 1000, 1400) };
            if (mutation == "raster_group_content_changed") changedGroup = changedGroup with { ContentEvidence = content };
            if (mutation == "raster_group_id_changed") changedGroup = changedGroup with { LayerId = 807 };
            baseline = baseline with { Layers = [other, group, target, fixedLayer], LayerCount = 4 };
            output = baseline with { Layers = [changedOther, changedGroup, renderedTarget, renderedFixed] };
        }
        if (mutation.StartsWith("superseded", StringComparison.Ordinal))
        {
            var visible = mutation != "superseded_already_hidden";
            plan["targets"]![0]!["superseded_layers"] = JsonNode.Parse("""
                [{"layer_path":[3],"layer_path_order":"psd_bottom_to_top","expected_kind":"smartObject",
                  "expected_visible":true,"expected_clipping":false,"expected_layer_id":259}]
                """);
            plan["targets"]![0]!["superseded_layers"]![0]!["expected_visible"] = visible;
            var old = target with { Path = [2], LayerId = 259, Name = "old product", Visible = visible, EffectiveVisible = visible,
                SmartObject = renderedTarget.SmartObject! with { UniqueId = "superseded", EmbeddedContentSha256 = hash } };
            var hidden = old with { Visible = false, EffectiveVisible = false };
            if (mutation == "superseded_not_hidden") hidden = old;
            if (mutation == "superseded_payload") hidden = hidden with { SmartObject = hidden.SmartObject! with { EmbeddedContentSha256 = new string('b', 64) } };
            if (mutation == "superseded_geometry") hidden = hidden with { SmartObject = hidden.SmartObject! with { TransformBox = [0, 0, 1, 0, 1, 1, 0, 1] } };
            if (mutation == "superseded_name") hidden = hidden with { Name = "changed" };
            if (mutation == "superseded_mask") hidden = hidden with { ContentEvidence = content with { MaskSha256 = hash } };
            if (mutation == "superseded_other_hidden") renderedFixed = renderedFixed with { Visible = false, EffectiveVisible = false };
            baseline = baseline with { LayerCount = 3, Layers = [fixedLayer, target, old] };
            output = output with { LayerCount = 3, Layers = [renderedFixed, renderedTarget, hidden] };
        }
        if (mutation == "missing_layer") output = output with { Layers = [renderedTarget] };
        if (mutation == "canvas") output = output with { Canvas = new(1, 1, 8, "RGB") };
        Assert.Equal(expected, RenderOutputEvidenceValidator.MatchesPsd(plan.ToJsonString(), baseline, output));
    }
}
