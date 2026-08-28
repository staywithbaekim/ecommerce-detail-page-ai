using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateRenderPlanBuilderTests
{
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
