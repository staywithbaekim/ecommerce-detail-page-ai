using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateRenderPlanBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RasterBindingRequiresNegotiatedCapabilityAndExplicitRendererNeutralPlacement(bool covered)
    {
        var page = TemplatePage.Create("page_render", "revision_render", 0, "photo", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, "resource_template");
        var old = Binding();
        var pixel = FrameBinding.Create(old.FrameId, old.PageId, old.LayerPath,
            ExpectedLayerEvidence.Create(257, "registered_frame", true, false, false, false, 255, "normal",
                old.ExpectedLayer.StructureSignature, TemplateLayerKind.Pixel),
            covered ? [SupersededLayerEvidence.Create(LayerPath.Create([3]), false, 259)] : [], old.SelectionRequirement, old.LayoutIntent);
        var revision = Commit(TemplateRevision.CreateDraft("revision_render", "group_render", 1,
            DateTimeOffset.UtcNow, [page], [pixel]));
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        var builder = new TemplateRenderPlanBuilder(validator);
        var request = new TemplateRenderPlanRequest("plan_raster", "raster_page_01", "corr_raster", "batch_raster",
            "product_raster", "artifact_raster", "01-detail.jpg", false, null, DateTimeOffset.UtcNow,
            [new("asset_front", new string('c', 64), 32, "image/jpeg", 4000, 6000)], RequireAbsolutePlacement: true);
        var json = builder.Build(revision, page, Resource(), request);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("pixel", parsed.RootElement.GetProperty("targets")[0].GetProperty("expected_layer").GetProperty("kind").GetString());
        Assert.Contains(parsed.RootElement.GetProperty("required_capabilities").EnumerateArray(),
            item => item.GetString() == RenderTemplateEvidenceValidator.RasterReplacementCapability);
        Assert.Empty(parsed.RootElement.GetProperty("targets")[0].GetProperty("superseded_layers").EnumerateArray());
        Assert.True(validator.Validate(json).IsValid);
        Assert.DoesNotContain(parsed.RootElement.GetProperty("required_capabilities").EnumerateArray(),
            item => item.GetString() == RenderTemplateEvidenceValidator.CoveredRasterStackCapability);
        var coverageMutation = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var coverageCapabilities = coverageMutation["required_capabilities"]!.AsArray();
        coverageCapabilities.Add(RenderTemplateEvidenceValidator.CoveredRasterStackCapability);
        Assert.False(new JsonSchemaContractValidator().Validate(ContractDocumentKind.RenderPlan, coverageMutation.ToJsonString()).IsValid);
        Assert.Throws<TemplateRuleViolationException>(() => builder.Build(revision, page, Resource(),
            request with { RequireAbsolutePlacement = false }));
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var capabilities = invalid["required_capabilities"]!.AsArray();
        capabilities.Remove(capabilities.Single(item => item!.GetValue<string>() == RenderTemplateEvidenceValidator.RasterReplacementCapability));
        Assert.False(validator.Validate(invalid.ToJsonString()).IsValid);
    }

    [Fact]
    public void CalibratedBindingBuildsSchemaValidRendererNeutralTarget()
    {
        var page = TemplatePage.Create(
            "page_render",
            "revision_render",
            0,
            "主图页",
            TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct,
            null,
            true,
            "resource_template");
        var draft = TemplateRevision.CreateDraft(
            "revision_render",
            "group_render",
            1,
            DateTimeOffset.UtcNow,
            [page],
            [Binding()]);
        var revision = Commit(draft);
        var builder = new TemplateRenderPlanBuilder(
            new RenderPlanValidator(new JsonSchemaContractValidator()));
        var request = new TemplateRenderPlanRequest(
            "plan_render",
            "batch_render_page_01",
            "corr_render",
            "batch_render",
            "product_render",
            "artifact_render",
            "01-detail.jpg",
            false,
            null,
            new DateTimeOffset(2026, 8, 28, 5, 0, 0, TimeSpan.Zero),
            [new TemplateRenderSource("asset_front", new string('c', 64), 32)]);

        var json = builder.Build(revision, page, Resource(), request);
        using var document = JsonDocument.Parse(json);
        var target = document.RootElement.GetProperty("targets")[0];

        Assert.Equal("psd_bottom_to_top", target.GetProperty("layer_path_order").GetString());
        Assert.Equal(257, target.GetProperty("expected_layer").GetProperty("layer_id").GetInt32());
        Assert.Equal("asset_front", target.GetProperty("asset_resource_id").GetString());
        Assert.False(json.Contains("batchPlay", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("photoshop_command", StringComparison.OrdinalIgnoreCase));

        var missing = Assert.Throws<TemplateRuleViolationException>(() =>
            builder.Build(revision, page, Resource(), request with { Sources = [] }));
        Assert.Equal("TPL_RENDER_SOURCE_MISSING", missing.Code);
    }

    [Fact]
    public void LearnedBindingRequiresExplicitTargetBatchSelection()
    {
        var page = TemplatePage.Create(
            "page_render",
            "revision_render",
            0,
            "主图页",
            TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct,
            null,
            true,
            "resource_template");
        var learned = FrameBinding.Create(
            "frame_render",
            "page_render",
            LayerPath.Create([2]),
            ExpectedLayerEvidence.Create(
                257,
                "registered_frame",
                true,
                false,
                false,
                false,
                byte.MaxValue,
                "normal",
                LayerStructureSignature.Create("sha256:" + new string('b', 64))),
            [],
            SelectionRequirement.Create(
                "请选择目标款商品图。",
                [
                    SelectionCriterion.Create("asset_id", "asset_reference"),
                    SelectionCriterion.Create("reference_only", "true")
                ]),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));
        var revision = Commit(TemplateRevision.CreateDraft(
            "revision_render",
            "group_render",
            1,
            DateTimeOffset.UtcNow,
            [page],
            [learned]));
        var builder = new TemplateRenderPlanBuilder(
            new RenderPlanValidator(new JsonSchemaContractValidator()));
        var geometry = new TemplateRenderSubjectGeometry(
            "asset_target", 0m, 0m, 1m, 1m, 1m);
        var request = new TemplateRenderPlanRequest(
            "plan_learned",
            "batch_learned_page_01",
            "corr_learned",
            "batch_learned",
            "product_learned",
            "artifact_learned",
            "01-detail.jpg",
            false,
            null,
            new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
            [new TemplateRenderSource("asset_reference", new string('c', 64), 32)]);

        var missing = Assert.Throws<TemplateRuleViolationException>(() =>
            builder.Build(revision, page, Resource(), request));
        Assert.Equal("TPL_RENDER_SELECTIONS_INCOMPLETE", missing.Code);

        var json = builder.Build(
            revision,
            page,
            Resource(),
            request with
            {
                Sources = [new TemplateRenderSource("asset_target", new string('e', 64), 48)],
                ConfirmedSelections = [new TemplateRenderSelection("frame_render", "asset_target", geometry)]
            });
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            "asset_target",
            document.RootElement.GetProperty("targets")[0].GetProperty("asset_resource_id").GetString());
        Assert.DoesNotContain("asset_reference", json, StringComparison.Ordinal);
    }

    private static FrameBinding Binding() => FrameBinding.Create(
        "frame_render",
        "page_render",
        LayerPath.Create([2]),
        ExpectedLayerEvidence.Create(
            257,
            "registered_frame",
            true,
            false,
            false,
            false,
            byte.MaxValue,
            "normal",
            LayerStructureSignature.Create("sha256:" + new string('b', 64))),
        [],
        SelectionRequirement.Create(
            "Choose one source.",
            [SelectionCriterion.Create("asset_id", "asset_front")]),
        LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));

    private static ImportedTemplateResource Resource() => new(
        "resource_template",
        "group_render",
        "revision_render",
        "templates/groups/group_render/revisions/revision_render/pages/page_render/template.psd",
        64,
        new string('a', 64),
        "sha256:" + new string('b', 64),
        new PsdInspectionDocument(
            new string('a', 64),
            64,
            new PsdCanvasInspection(790, 1252, 8, "RGB"),
            1,
            1,
            0,
            0,
            0,
            "sha256:" + new string('b', 64),
            new PsdInspectionTiming(1, 1, 2),
            1024,
            null,
            []),
        DateTimeOffset.UtcNow);

    private static TemplateRevision Commit(TemplateRevision revision)
    {
        var importing = revision.AdvanceTo(
            TemplateRevisionState.ImportingCopy,
            new RevisionTransitionEvidence(SourceReadable: true));
        var inspecting = importing.AdvanceTo(
            TemplateRevisionState.StructureInspecting,
            new RevisionTransitionEvidence(CopyMatchesSource: true));
        var binding = inspecting.AdvanceTo(
            TemplateRevisionState.FrameBinding,
            new RevisionTransitionEvidence(StructureReadable: true));
        var calibrating = binding.AdvanceTo(
            TemplateRevisionState.Calibrating,
            new RevisionTransitionEvidence(RegisteredFramesHaveEvidence: true));
        var review = calibrating.AdvanceTo(
            TemplateRevisionState.AwaitingReview,
            new RevisionTransitionEvidence(CalibrationComplete: true));
        var committing = review.AdvanceTo(
            TemplateRevisionState.Committing,
            new RevisionTransitionEvidence(ConfigurationValid: true));
        return committing.Commit(
            "templates/groups/group_render/revisions/revision_render/revision-manifest.json",
            "sha256:" + new string('d', 64),
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }
}
