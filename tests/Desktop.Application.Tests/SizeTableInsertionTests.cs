using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Tests;

public sealed class SizeTableInsertionTests
{
    private readonly RenderPlanValidator _validator = new(new JsonSchemaContractValidator());

    internal static (string Plan, SizeTableInsertionTarget Target, PsdInspectionDocument Baseline) Fixture()
    {
        var f = SizeTableRenderTargetTests.Fixture();
        var root = f.Inspection.Layers[0] with { Path = [0], LayerId = 1, Name = "background", Kind = "pixel", SmartObject = null };
        var group = root with { Path = [1], LayerId = 2, Name = "old table", Kind = "group", IsGroup = true };
        var text = root with { Path = [1, 0], LayerId = 3, Name = "old cell", Kind = "type",
            ContentEvidence = root.ContentEvidence! with { TextSha256 = new string('a', 64), TextStyleSha256 = new string('b', 64) } };
        var header = root with { Path = [2], LayerId = 4, Name = "header" };
        var baseline = f.Inspection with { LayerCount = 4, Layers = [root, group, text, header] };
        var reference = new SizeTableLayerReference([2], 2, "group", "old table", baseline.StructureSignature);
        var insertion = new SizeTableInsertion("confirmed size table", reference, [reference],
            f.Target.Binding.LayoutIntent.SafeBounds, f.Target.Placement);
        return (f.Plan, new(f.Target.TemplateRevisionId, f.Target.TemplateSha256, f.Target.Binding.PageId, insertion), baseline);
    }

    private string Migrate(string plan, SizeTableInsertionTarget target, PsdInspectionDocument baseline) =>
        new RenderPlanPlacementMigration(_validator).MigrateSizeTableInsertion(plan, target, baseline,
            "plan_insert", "insert_attempt_01", "artifact_insert", DateTimeOffset.UnixEpoch);

    [Fact]
    public void LegacySizePlanNeedsExplicitAnchorAndRetainsOriginalTableAndResources()
    {
        var f = Fixture(); var migrated = Migrate(f.Plan, f.Target, f.Baseline);
        using var before = JsonDocument.Parse(f.Plan); using var after = JsonDocument.Parse(migrated);
        Assert.True(_validator.Validate(migrated).IsValid);
        Assert.True(RenderPlanPlacementMigration.IsExplicit(after.RootElement));
        Assert.True(RenderTemplateEvidenceValidator.Matches(migrated, f.Baseline));
        Assert.Empty(after.RootElement.GetProperty("targets").EnumerateArray());
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("resources"), after.RootElement.GetProperty("resources")));
        foreach (var field in before.RootElement.GetProperty("size_table").EnumerateObject())
            Assert.True(JsonElement.DeepEquals(field.Value, after.RootElement.GetProperty("size_table").GetProperty(field.Name)));
        Assert.Throws<InvalidDataException>(() => Migrate(migrated, f.Target, f.Baseline));
    }

    [Theory]
    [InlineData("missing_capability")]
    [InlineData("both_modes")]
    [InlineData("missing_insertion")]
    [InlineData("nested_anchor")]
    [InlineData("duplicate_hidden")]
    [InlineData("cropped")]
    [InlineData("stretched")]
    [InlineData("market_image")]
    [InlineData("ordinary")]
    public void InvalidInsertionSemanticsCannotReachExecution(string mutation)
    {
        var f = Fixture(); var node = JsonNode.Parse(Migrate(f.Plan, f.Target, f.Baseline))!;
        var insertion = node["size_table"]!["insertion"]!;
        switch (mutation)
        {
            case "missing_capability": node["required_capabilities"]!.AsArray().RemoveAt(node["required_capabilities"]!.AsArray().Count - 1); break;
            case "both_modes": node["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.SizeTableCapability); break;
            case "missing_insertion": node["size_table"]!.AsObject().Remove("insertion"); break;
            case "nested_anchor": insertion["above_layer"]!["layer_path"]!.AsArray().Add(1); break;
            case "duplicate_hidden": insertion["hidden_layers"]!.AsArray().Add(insertion["hidden_layers"]![0]!.DeepClone()); break;
            case "cropped": insertion["placement"]!["left"] = 0; break;
            case "stretched": insertion["placement"]!["width"] = 500; break;
            case "market_image": node["resources"]![1]!["media_type"] = "image/jpeg"; break;
            case "ordinary": node["document"]!["content_type"] = "ordinary_set"; break;
        }
        node["plan_digest"] = RenderPlanDigest.Compute(node.ToJsonString());
        Assert.False(_validator.Validate(node.ToJsonString()).IsValid);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("kind")]
    [InlineData("signature")]
    [InlineData("name_collision")]
    [InlineData("hidden_anchor")]
    [InlineData("clipped_next")]
    public void TemplateMismatchCannotAuthorizeInsertion(string mutation)
    {
        var f = Fixture(); var insertion = f.Target.Insertion;
        var anchor = insertion.AboveLayer;
        anchor = mutation switch
        {
            "id" => anchor with { LayerId = 99 }, "path" => anchor with { LayerPath = [1] },
            "kind" => anchor with { Kind = "pixel" }, "signature" => anchor with { StructureSignature = "sha256:" + new string('f', 64) },
            _ => anchor
        };
        insertion = insertion with { AboveLayer = anchor, LayerName = mutation == "name_collision" ? "header" : insertion.LayerName };
        var layers = f.Baseline.Layers.ToArray();
        if (mutation == "hidden_anchor") layers[1] = layers[1] with { Visible = false };
        if (mutation == "clipped_next") layers[3] = layers[3] with { Clipping = true };
        Assert.Throws<InvalidDataException>(() => Migrate(f.Plan, f.Target with { Insertion = insertion }, f.Baseline with { Layers = layers }));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("payload", false)]
    [InlineData("geometry", false)]
    [InlineData("extra_layer", false)]
    [InlineData("old_deleted", false)]
    [InlineData("old_visible", false)]
    [InlineData("child_local_visibility", false)]
    [InlineData("child_text", false)]
    [InlineData("fixed_moved", false)]
    [InlineData("wrong_stacking", false)]
    [InlineData("new_effect", false)]
    [InlineData("hidden_group_bbox", true)]
    [InlineData("hidden_group_nonempty_bbox", false)]
    [InlineData("hidden_child_moved", false)]
    [InlineData("hidden_child_empty_bbox", false)]
    [InlineData("hidden_group_content", false)]
    public void OutputAllowsExactlyOneEmbeddedLayerAndOnlyTheSpecifiedVisibilityChanges(string mutation, bool expected)
    {
        var f = Fixture(); var plan = Migrate(f.Plan, f.Target, f.Baseline);
        var layers = f.Baseline.Layers.Select(layer => layer with {
            Path = layer.Path.Select((part, i) => i == 0 && part > 1 ? part + 1 : part).ToArray(),
            Visible = layer.LayerId == 2 ? false : layer.Visible,
            EffectiveVisible = layer.Path[0] == 1 ? false : layer.EffectiveVisible }).ToList();
        using var json = JsonDocument.Parse(plan);
        var added = f.Baseline.Layers[0] with { Path = [2], LayerId = 10, Name = f.Target.Insertion.LayerName, Kind = "smartobject",
            SmartObject = new("new", "table.png", "data", "png", "png", 128, false, []) {
                EmbeddedContentAvailable = true, EmbeddedContentSha256 = json.RootElement.GetProperty("resources")[1].GetProperty("sha256").GetString(),
                TransformBox = [10, 20, 760, 20, 760, 320, 10, 320] } };
        if (mutation == "payload") added = added with { SmartObject = added.SmartObject! with { EmbeddedContentSha256 = new string('f', 64) } };
        if (mutation == "geometry") added = added with { SmartObject = added.SmartObject! with { TransformBox = [0, 20, 750, 20, 750, 320, 0, 320] } };
        if (mutation == "wrong_stacking") added = added with { Path = [5] };
        if (mutation == "new_effect") added = added with { ContentEvidence = added.ContentEvidence! with { EffectsSha256 = new string('a', 64) } };
        if (mutation == "old_visible") layers[1] = f.Baseline.Layers[1];
        if (mutation == "child_local_visibility") layers[2] = layers[2] with { Visible = false };
        if (mutation == "child_text") layers[2] = layers[2] with { ContentEvidence = layers[2].ContentEvidence! with { TextSha256 = new string('f', 64) } };
        if (mutation == "fixed_moved") layers[3] = layers[3] with { Bounds = new(1, 0, 790, 1252) };
        if (mutation.StartsWith("hidden_", StringComparison.Ordinal)) layers[1] = layers[1] with { Bounds = new(0, 0, 0, 0) };
        if (mutation == "hidden_group_nonempty_bbox") layers[1] = layers[1] with { Bounds = new(1, 0, 790, 1252) };
        if (mutation == "hidden_child_moved") layers[2] = layers[2] with { Bounds = new(1, 0, 790, 1252) };
        if (mutation == "hidden_child_empty_bbox") layers[2] = layers[2] with { Bounds = new(0, 0, 0, 0) };
        if (mutation == "hidden_group_content") layers[1] = layers[1] with {
            ContentEvidence = layers[1].ContentEvidence! with { EffectsSha256 = new string('e', 64) } };
        if (mutation == "old_deleted") layers.RemoveAt(1);
        if (mutation == "extra_layer") layers.Add(added with { LayerId = 11, Path = [6] });
        layers.Add(added);
        var output = f.Baseline with { Layers = layers, LayerCount = layers.Count };
        Assert.Equal(expected, RenderOutputEvidenceValidator.MatchesPsd(plan, f.Baseline, output));
    }
}
