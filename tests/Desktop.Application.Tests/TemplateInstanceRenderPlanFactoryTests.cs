using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateInstanceRenderPlanFactoryTests
{
    [Fact]
    public void ValidFrozenInstanceProducesDeterministicSchemaValidPlanWithInstanceIdentity()
    {
        var fixture = Fixture();
        var factory = Factory();

        var first = factory.Build(new(fixture.Snapshot, fixture.Instance.PageInstanceId,
            fixture.Revision, fixture.Resource, [fixture.Asset]));
        var second = factory.Build(new(fixture.Snapshot, fixture.Instance.PageInstanceId,
            fixture.Revision, fixture.Resource, [fixture.Asset]));

        Assert.Equal(first.RenderPlanJson, second.RenderPlanJson);
        Assert.Equal(first.PlanDigest, second.PlanDigest);
        Assert.Equal(first.PageInstanceId, fixture.Instance.PageInstanceId);
        Assert.True(new RenderPlanValidator(new JsonSchemaContractValidator()).Validate(first.RenderPlanJson).IsValid);
        using var document = JsonDocument.Parse(first.RenderPlanJson);
        var identity = document.RootElement.GetProperty("document").GetProperty("instance_identity");
        Assert.Equal(fixture.Instance.PageInstanceId, identity.GetProperty("page_instance_id").GetString());
        Assert.Equal(fixture.Instance.RecipeId, identity.GetProperty("recipe_id").GetString());
        Assert.Equal(fixture.Instance.Frames[0].AssetId,
            document.RootElement.GetProperty("targets")[0].GetProperty("asset_resource_id").GetString());
        Assert.DoesNotContain(DateTimeOffset.UtcNow.Year.ToString(CultureInfo.InvariantCulture), first.RenderPlanJson, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedOrEmptyInstanceFailsClosed()
    {
        var fixture = Fixture(missingSelection: true);
        var blocked = fixture.Snapshot;
        Assert.Throws<TemplateRuleViolationException>(() => Factory().Build(new(
            blocked, fixture.Instance.PageInstanceId, fixture.Revision, fixture.Resource, [fixture.Asset])));

        var empty = Fixture(emptyExpansion: true).Snapshot;
        Assert.Throws<TemplateRuleViolationException>(() => Factory().Build(new(
            empty, fixture.Instance.PageInstanceId, fixture.Revision, fixture.Resource, [fixture.Asset])));
    }

    [Fact]
    public void DifferentInstanceIdentityCannotReusePlanIdentityOrAssetBinding()
    {
        var first = Fixture();
        var second = Fixture(pageId: "page_back", assetId: "asset_back");
        var factory = Factory();
        var firstPlan = factory.Build(new(first.Snapshot, first.Instance.PageInstanceId,
            first.Revision, first.Resource, [first.Asset]));
        var secondPlan = factory.Build(new(second.Snapshot, second.Instance.PageInstanceId,
            second.Revision, second.Resource, [second.Asset]));

        Assert.NotEqual(firstPlan.PlanId, secondPlan.PlanId);
        Assert.NotEqual(firstPlan.PlanDigest, secondPlan.PlanDigest);
        using var json = JsonDocument.Parse(secondPlan.RenderPlanJson);
        Assert.Equal("asset_back", json.RootElement.GetProperty("targets")[0]
            .GetProperty("asset_resource_id").GetString());
        Assert.DoesNotContain("asset_front", secondPlan.RenderPlanJson, StringComparison.Ordinal);
    }

    private static TemplateInstanceRenderPlanFactory Factory() =>
        new(new RenderPlanValidator(new JsonSchemaContractValidator()));

    private static FixtureData Fixture(string pageId = "page_front", string assetId = "asset_front",
        bool missingSelection = false, bool emptyExpansion = false)
    {
        var revisionId = "revision_instance";
        var page = TemplatePage.Create(pageId, revisionId, 0, "主图",
            TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct,
            null, true, "resource_template");
        var binding = FrameBinding.Create("frame_front", pageId, LayerPath.Create([2]),
            ExpectedLayerEvidence.Create(257, "registered_frame", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('a', 64))), [],
            SelectionRequirement.Create("商品图", [SelectionCriterion.Create("asset_id", assetId)]),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));
        var rules = new TemplateRuleSet(1, [new("branch", new(1, 1),
            [new("recipe", pageId, TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
                [new("slot", TemplateRuleColorSource.Primary)], [new("frame_front", "slot")])])]);
        if (emptyExpansion)
        {
            rules = new TemplateRuleSet(1, [new("branch", new(1, 1),
                [new("recipe", pageId, TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.Secondary,
                    [new("slot", TemplateRuleColorSource.Allocated)], [new("frame_front", "slot")])])]);
        }
        var draft = TemplateRevision.CreateDraft(revisionId, "group", 1, DateTimeOffset.UnixEpoch,
            [page], [binding], new(1, rules, []));
        var revision = TemplateRevision.Restore(revisionId, "group", 1, TemplateRevisionState.Committed,
            "templates/revision.json", TemplateRuleCodec.BusinessDigest(draft), DateTimeOffset.UnixEpoch,
            draft.Pages, draft.FrameBindings, draft.RuleWorkspace);
        var asset = SourceAssetMetadata.CreateReadable(assetId, "style", assetId + ".jpg", assetId + ".jpg",
            100, new string('b', 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg");
        var request = new TemplateInstanceProjectionRequest(
            new("batch", "style", "snapshot", revisionId, revision.RuleDigest!), revision,
            new(1, new(["color_a"], "color_a", true), [asset],
                [new("color_a", missingSelection ? [] : [assetId])],
                missingSelection ? [] : [new("frame_front", "color_a", assetId)], []));
        var projection = TemplateInstanceProjector.Project(request);
        if (projection.Status == TemplateInstanceProjectionStatus.Rejected)
            throw new InvalidOperationException(JsonSerializer.Serialize(projection.Diagnostics));
        var snapshot = TemplateInstanceProjectionSnapshotCodec.Freeze(request, projection);
        var resource = new ImportedTemplateResource("resource_template", "group", revisionId,
            "templates/resource.psd", 64, new string('c', 64), "sha256:" + new string('d', 64),
            new PsdInspectionDocument(new string('c', 64), 64,
                new PsdCanvasInspection(790, 1252, 8, "RGB"), 1, 1, 0, 0, 0,
                "sha256:" + new string('d', 64), new PsdInspectionTiming(1, 1, 1), 1024, null, []),
            DateTimeOffset.UnixEpoch);
        var instance = snapshot.Instances.SingleOrDefault();
        return new(snapshot, instance ?? new TemplateInstanceProjection(
            "instance_" + new string('0', 64), "batch", "style", "snapshot", revisionId,
            revision.RuleDigest!, snapshot.InputFactsDigest, pageId, "resource_template", "branch", "recipe", 0, 0,
            [], [], TemplateInstancePreflightStatus.BusinessBlocked, [new("TPL_INSTANCE_EMPTY")]), revision, resource, asset);
    }

    private sealed record FixtureData(TemplateInstanceProjectionSnapshot Snapshot,
        TemplateInstanceProjection Instance, TemplateRevision Revision,
        ImportedTemplateResource Resource, SourceAssetMetadata Asset);
}
