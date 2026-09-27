using System.Text.Json.Nodes;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;

namespace Desktop.Application.Tests;

public sealed class RenderOutputEvidenceValidatorTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("old_worker", false)]
    [InlineData("missing_layer", false)]
    [InlineData("pixels", false)]
    [InlineData("text", false)]
    [InlineData("text_style", false)]
    [InlineData("missing_text_style", false)]
    [InlineData("both_missing_text_style", false)]
    [InlineData("curves", true)]
    [InlineData("exposure", true)]
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
    public void OnlyIndependentMatchingContentsAuthorizePsdOutput(string mutation, bool expected)
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
        if (mutation is "curves" or "exposure" or "adjustment_changed" or "adjustment_missing" or "shape" or "shape_changed" or "shape_missing")
        {
            fixedLayer = fixedLayer with { Kind = mutation.StartsWith("shape", StringComparison.Ordinal) ? "shape" : mutation == "exposure" ? "exposure" : "curves",
                ContentEvidence = content with { AdjustmentSha256 = mutation is "adjustment_missing" or "shape_missing" ? null : hash } };
            baseline = baseline with { Layers = [fixedLayer, target] };
            renderedFixed = mutation is "adjustment_changed" or "shape_changed"
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
