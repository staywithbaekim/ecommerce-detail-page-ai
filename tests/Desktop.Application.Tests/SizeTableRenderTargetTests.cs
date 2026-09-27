using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class SizeTableRenderTargetTests
{
    private readonly RenderPlanValidator _validator = new(new JsonSchemaContractValidator());

    internal static (string Plan, SizeTableRenderTarget Target, PsdInspectionDocument Inspection) Fixture()
    {
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
        var old = plan["targets"]![0]!;
        var expected = old["expected_layer"]!;
        var signature = expected["structure_signature"]!.GetValue<string>();
        var target = new SizeTableRenderTarget(plan["template_revision_id"]!.GetValue<string>(),
            plan["resources"]![0]!["sha256"]!.GetValue<string>(),
            FrameBinding.Create("size_target", plan["document"]!["page_id"]!.GetValue<string>(), LayerPath.Create([2]),
                ExpectedLayerEvidence.Create(257, "product_frame", true, false, false, false, 255, "normal", LayerStructureSignature.Create(signature)),
                [], SelectionRequirement.Create("显式尺码表目标"), LayoutIntent.Create("size_table_complete", 1, 0, 0, TemplateRect.Create(10, 20, 780, 1200))),
            new(1000, 400, 10, 20, 750, 300));
        plan["document"]!["content_type"] = "dynamic_size_table";
        plan["targets"] = new JsonArray();
        plan["resources"]![1]!["role"] = "deterministic_size_table";
        plan["resources"]![1]!["media_type"] = "image/png";
        plan["size_table"] = new JsonObject
        {
            ["size_table_id"] = "size_01", ["grid_digest"] = "sha256:" + plan["resources"]![1]!["sha256"]!.GetValue<string>(),
            ["style_version"] = "style_v1", ["layout_version"] = "grid_v1", ["embed_as"] = "smart_object",
            ["render_resource_id"] = plan["resources"]![1]!["resource_id"]!.GetValue<string>(),
            ["structured_data_digest"] = "sha256:" + new string('c', 64),
            ["columns"] = JsonNode.Parse("""[{"column_id":"col_1","label":"尺码","index":0}]"""),
            ["rows"] = JsonNode.Parse("""[{"row_id":"row_1","index":0,"cells":{"col_1":"L"}}]""")
        };
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        var layer = new PsdLayerInspection([1], 257, "product_frame", "smartobject", false, true, true, false, 255, 255, "norm",
            new(0, 0, 790, 1252), false, false, null, new("embedded", "old.png", "data", "png", "png", 128, false, []))
            { ContentEvidence = new(true, new string('a', 64), null, null, null, null) };
        var inspection = new PsdInspectionDocument(target.TemplateSha256, plan["resources"]![0]!["byte_length"]!.GetValue<long>(),
            new(790, 1252, 8, "RGB"), 1, 1, 0, 0, 0, signature, new(1, 1, 2), 1024, null, [layer]);
        return (plan.ToJsonString(), target, inspection);
    }

    [Fact]
    public void EmptyLegacySizePlanNeedsExplicitTemplateEvidenceAndNewAttempt()
    {
        var f = Fixture();
        Assert.True(_validator.Validate(f.Plan).IsValid);
        using var before = JsonDocument.Parse(f.Plan);
        Assert.False(RenderPlanPlacementMigration.IsExplicit(before.RootElement));
        var json = new RenderPlanPlacementMigration(_validator).MigrateSizeTableTarget(f.Plan, f.Target, f.Inspection,
            "plan_size_bound", "size_bound_attempt", "artifact_size_bound", DateTimeOffset.UnixEpoch);
        Assert.True(_validator.Validate(json).IsValid);
        using var after = JsonDocument.Parse(json);
        Assert.True(RenderPlanPlacementMigration.IsExplicit(after.RootElement));
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("size_table"), after.RootElement.GetProperty("size_table")));
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("resources"), after.RootElement.GetProperty("resources")));
        Assert.Equal(0, before.RootElement.GetProperty("targets").GetArrayLength());
        Assert.NotEqual(before.RootElement.GetProperty("plan_digest").GetString(), after.RootElement.GetProperty("plan_digest").GetString());
        Assert.Throws<InvalidDataException>(() => new RenderPlanPlacementMigration(_validator).MigrateSizeTableTarget(json,
            f.Target, f.Inspection, "plan_new", "new_attempt", "artifact_new", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("missing_capability")]
    [InlineData("product_resource")]
    [InlineData("market_jpeg")]
    [InlineData("grid_digest")]
    [InlineData("second_target")]
    [InlineData("cropped_table")]
    [InlineData("mask")]
    [InlineData("ordinary_page")]
    public void UnsafeSizeTargetIsRejectedBeforePhotoshop(string mutation)
    {
        var f = Fixture();
        var json = new RenderPlanPlacementMigration(_validator).MigrateSizeTableTarget(f.Plan, f.Target, f.Inspection,
            "plan_size_bound", "size_bound_attempt", "artifact_size_bound", DateTimeOffset.UnixEpoch);
        var plan = JsonNode.Parse(json)!;
        switch (mutation)
        {
            case "missing_capability": plan["required_capabilities"]!.AsArray().RemoveAt(4); break;
            case "product_resource": plan["resources"]![1]!["role"] = "high_resolution_render_source"; break;
            case "market_jpeg": plan["resources"]![1]!["media_type"] = "image/jpeg"; break;
            case "grid_digest": plan["size_table"]!["grid_digest"] = "sha256:" + new string('f', 64); break;
            case "second_target": plan["targets"]!.AsArray().Add(plan["targets"]![0]!.DeepClone()); break;
            case "cropped_table": plan["targets"]![0]!["layout"]!["placement"]!["left"] = 0; break;
            case "mask": plan["targets"]![0]!["expected_layer"]!["user_mask"] = true; break;
            case "ordinary_page": plan["document"]!["content_type"] = "ordinary_set"; plan["size_table"] = null; break;
        }
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        Assert.False(_validator.Validate(plan.ToJsonString()).IsValid);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("page")]
    [InlineData("source_hash")]
    [InlineData("inspection")]
    [InlineData("reuse_plan")]
    [InlineData("reuse_key")]
    [InlineData("reuse_artifact")]
    [InlineData("effects")]
    [InlineData("old_worker")]
    public void BindingCannotBeReusedAcrossTemplatesOrAttempts(string mutation)
    {
        var f = Fixture();
        var target = f.Target;
        var inspection = f.Inspection;
        var original = JsonNode.Parse(f.Plan)!;
        if (mutation == "revision") target = target with { TemplateRevisionId = "another_revision" };
        if (mutation == "source_hash") target = target with { TemplateSha256 = new string('e', 64) };
        if (mutation == "inspection") inspection = inspection with { Layers = [inspection.Layers[0] with { LayerId = 1 }] };
        if (mutation == "effects") inspection = inspection with { Layers = [inspection.Layers[0] with {
            ContentEvidence = inspection.Layers[0].ContentEvidence! with { EffectsSha256 = new string('a', 64) } }] };
        if (mutation == "old_worker") inspection = inspection with { Layers = [inspection.Layers[0] with { ContentEvidence = null }] };
        if (mutation == "page") target = target with { Binding = FrameBinding.Create(target.Binding.FrameId, "another_page",
            target.Binding.LayerPath, target.Binding.ExpectedLayer, [], target.Binding.SelectionRequirement, target.Binding.LayoutIntent) };
        Assert.Throws<InvalidDataException>(() => new RenderPlanPlacementMigration(_validator).MigrateSizeTableTarget(f.Plan, target, inspection,
            mutation == "reuse_plan" ? original["plan_id"]!.GetValue<string>() : "plan_new",
            mutation == "reuse_key" ? original["idempotency_key"]!.GetValue<string>() : "new_attempt",
            mutation == "reuse_artifact" ? original["output"]!["artifact_set_id"]!.GetValue<string>() : "artifact_new", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SizeOutputRequiresTheExactEmbeddedRasterAndPreservedStructure(bool corruptRaster)
    {
        var f = Fixture();
        var json = new RenderPlanPlacementMigration(_validator).MigrateSizeTableTarget(f.Plan, f.Target, f.Inspection,
            "plan_size_bound", "size_bound_attempt", "artifact_size_bound", DateTimeOffset.UnixEpoch);
        using var plan = JsonDocument.Parse(json);
        var before = f.Inspection.Layers[0];
        var after = before with { Name = "deterministic_table", SmartObject = before.SmartObject! with
        {
            EmbeddedContentAvailable = true,
            EmbeddedContentSha256 = corruptRaster ? new string('f', 64) : plan.RootElement.GetProperty("resources")[1].GetProperty("sha256").GetString(),
            TransformBox = [10, 20, 760, 20, 760, 320, 10, 320]
        } };
        Assert.Equal(!corruptRaster, RenderOutputEvidenceValidator.MatchesPsd(json, f.Inspection, f.Inspection with { Layers = [after] }));
    }
}
