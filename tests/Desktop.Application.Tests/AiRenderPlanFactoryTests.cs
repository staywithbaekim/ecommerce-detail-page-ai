using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class AiRenderPlanFactoryTests
{
    [Fact]
    public async Task FrozenSnapshotSelectionAndGeometryBuildPlanButUnavailablePhotoshopKeepsWaiting()
    {
        var fixture = Fixture();
        var availability = new TestAvailability(false);
        var factory = Factory(availability);

        var result = await factory.PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [Selection()])],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.WaitingForPhotoshop, result.Status);
        Assert.Equal("PHOTOSHOP_TEST_UNAVAILABLE", result.ReasonCode);
        Assert.False(result.HasFormalOutput);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, page.Status);
        Assert.True(page.HasPlan);
        Assert.Equal(1, availability.Calls);

        using var document = JsonDocument.Parse(page.RenderPlanJson!);
        var root = document.RootElement;
        Assert.Equal(page.PlanDigest, root.GetProperty("plan_digest").GetString());
        Assert.Equal("asset_front", root.GetProperty("targets")[0]
            .GetProperty("asset_resource_id").GetString());
        var source = root.GetProperty("resources").EnumerateArray().Single(resource =>
            string.Equals(
                resource.GetProperty("resource_id").GetString(),
                "asset_front",
                StringComparison.Ordinal));
        Assert.Equal("high_resolution_render_source", source.GetProperty("role").GetString());
        Assert.Equal(new string('b', 64), source.GetProperty("sha256").GetString());
        Assert.Equal(128, source.GetProperty("byte_length").GetInt64());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.DoesNotContain(fixture.Batch.InputRoot, page.RenderPlanJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("analysis-copy", page.RenderPlanJson!, StringComparison.OrdinalIgnoreCase);
        Assert.True(new RenderPlanValidator(new JsonSchemaContractValidator())
            .Validate(page.RenderPlanJson!).IsValid);
    }

    [Fact]
    public async Task MissingSelectionIsBusinessIsolatedAndDoesNotProbePhotoshop()
    {
        var fixture = Fixture();
        var availability = new TestAvailability(true);
        var result = await Factory(availability).PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [])],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.UserActionRequired, result.Status);
        Assert.Equal("AI_RENDER_NO_EXECUTABLE_PAGE", result.ReasonCode);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.BusinessBlocked, page.Status);
        Assert.Equal("AI_RENDER_REQUIRED_SELECTION_MISSING", page.ReasonCode);
        Assert.False(page.HasPlan);
        Assert.Equal(0, availability.Calls);
    }

    [Fact]
    public async Task SelectedAssetWithoutGeometryUsesCalibratedLayoutFallback()
    {
        var fixture = Fixture();
        var availability = new TestAvailability(true);
        var selection = Selection() with { SubjectGeometry = null };
        var result = await Factory(availability).PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [selection])],
            fixture.Now), CancellationToken.None);

        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, page.Status);
        Assert.Equal("AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", page.ReasonCode);
        Assert.True(page.HasPlan);
        Assert.Equal(1, availability.Calls);
    }

    [Fact]
    public async Task ChangedRevisionCannotReplaceOldBatchSnapshot()
    {
        var fixture = Fixture();
        var changed = TemplateRevision.Restore(
            fixture.Revision.RevisionId,
            fixture.Revision.GroupId,
            fixture.Revision.RevisionNo,
            fixture.Revision.State,
            fixture.Revision.ManifestRef,
            "sha256:" + new string('f', 64),
            fixture.Revision.CreatedAt,
            fixture.Revision.Pages,
            fixture.Revision.FrameBindings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(new TestAvailability(true))
            .PrepareAsync(new AiRenderPlanPreparationRequest(
                fixture.Batch,
                changed,
                [fixture.Resource],
                [new AiRenderStyleInput("style_01", [Selection()])],
                fixture.Now), CancellationToken.None));

        Assert.Equal("AI_RENDER_SNAPSHOT_MISMATCH", error.Message);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("revision")]
    [InlineData("length")]
    [InlineData("file-digest")]
    [InlineData("structure")]
    public async Task TemplateResourceMustCloseOverFrozenPageAndInspection(string mutation)
    {
        var fixture = Fixture();
        var resource = mutation switch
        {
            "group" => fixture.Resource with { GroupId = "group_other" },
            "revision" => fixture.Resource with { DraftId = "revision_other" },
            "length" => fixture.Resource with
            {
                Inspection = fixture.Resource.Inspection with { SizeBytes = fixture.Resource.Length + 1 }
            },
            "file-digest" => fixture.Resource with
            {
                Inspection = fixture.Resource.Inspection with { FileSha256 = new string('f', 64) }
            },
            "structure" => fixture.Resource with
            {
                StructureSignature = "sha256:" + new string('f', 64)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(new TestAvailability(true))
            .PrepareAsync(new AiRenderPlanPreparationRequest(
                fixture.Batch,
                fixture.Revision,
                [resource],
                [new AiRenderStyleInput("style_01", [Selection()])],
                fixture.Now), CancellationToken.None));

        Assert.Equal("AI_RENDER_TEMPLATE_RESOURCE_OWNERSHIP_MISMATCH", error.Message);
    }

    [Fact]
    public async Task DynamicSizeCandidateMustReferenceAnAssetOwnedByTheStyle()
    {
        var fixture = DynamicSizeFixture();
        var foreignCandidate = new AiSizeTableCandidateV1(
            "asset_foreign",
            0.99m,
            ["尺码", "胸围"],
            [["M", "96"]]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(new TestAvailability(true))
            .PrepareAsync(new AiRenderPlanPreparationRequest(
                fixture.Batch,
                fixture.Revision,
                [fixture.Resource],
                [new AiRenderStyleInput("style_01", [], foreignCandidate)],
                fixture.Now), CancellationToken.None));

        Assert.Equal("AI_RENDER_SIZE_TABLE_SOURCE_MISMATCH", error.Message);
    }

    [Fact]
    public async Task ValidDynamicSizeCandidateStopsBeforeSizeRasterizerAndPhotoshop()
    {
        var fixture = DynamicSizeFixture();
        var availability = new TestAvailability(true);
        var candidate = new AiSizeTableCandidateV1(
            "asset_front",
            0.99m,
            ["尺码", "胸围"],
            [["M", "96"]]);

        var result = await Factory(availability).PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [], candidate)],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.UserActionRequired, result.Status);
        Assert.Equal("AI_RENDER_NO_EXECUTABLE_PAGE", result.ReasonCode);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.AwaitingSizeTableRasterization, page.Status);
        Assert.Equal("AI_SIZE_TABLE_RASTERIZER_PENDING", page.ReasonCode);
        Assert.False(page.HasPlan);
        Assert.Equal(0, availability.Calls);
    }

    [Fact]
    public async Task PreparedDynamicSizeResourceBuildsRendererNeutralPlan()
    {
        var fixture = DynamicSizeFixture();
        var availability = new TestAvailability(true);
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        var table = ReadySizeTable(fixture.Now);
        var resource = new SizeTableRasterizedResource(
            "size_raster_0123456789abcdef01234567",
            "deterministic_size_table",
            "image/png",
            new string('c', 64),
            4096,
            1000,
            420,
            table.DataDigest,
            "gold_v1",
            "dynamic_grid_v1",
            "batches/batch_01/size-tables/style_01/page_01/resources/private.png");
        var factory = new AiRenderPlanFactory(
            new TemplateRenderPlanBuilder(validator),
            availability,
            new SizeTableRenderPlanBuilder(validator));

        var result = await factory.PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput(
                "style_01",
                [],
                null,
                [new AiPreparedSizeTablePage("page_01", table, resource)])],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.ReadyToRender, result.Status);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, page.Status);
        Assert.True(page.HasPlan);
        Assert.Equal(1, availability.Calls);
        using var document = JsonDocument.Parse(page.RenderPlanJson!);
        var root = document.RootElement;
        Assert.Equal("dynamic_size_table", root.GetProperty("document").GetProperty("content_type").GetString());
        Assert.Empty(root.GetProperty("targets").EnumerateArray());
        Assert.Equal("/", root.GetProperty("size_table").GetProperty("rows")[0]
            .GetProperty("cells").GetProperty("col_002").GetString());
        Assert.Equal("deterministic_size_table", root.GetProperty("resources")[1]
            .GetProperty("role").GetString());
        Assert.DoesNotContain(resource.OwnedRelativePath, page.RenderPlanJson!, StringComparison.Ordinal);
        Assert.True(validator.Validate(page.RenderPlanJson!).IsValid);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("orphan-resource")]
    [InlineData("wrong-role")]
    [InlineData("safe-bounds")]
    public void SemanticValidatorRejectsTamperedDigestOrResourceClosure(string mutation)
    {
        var fixture = Fixture();
        var builder = new TemplateRenderPlanBuilder(
            new RenderPlanValidator(new JsonSchemaContractValidator()));
        var json = builder.Build(
            fixture.Revision,
            fixture.Revision.Pages[0],
            fixture.Resource,
            new TemplateRenderPlanRequest(
                "plan_valid",
                "render/batch_01/style_01/page_01/revision_01",
                "corr_valid",
                "batch_01",
                "style_01",
                "artifact_valid",
                "001-01.jpg",
                false,
                null,
                fixture.Now,
                [new TemplateRenderSource("asset_front", new string('b', 64), 128)],
                [new TemplateRenderSelection(
                    "frame_01",
                    "asset_front",
                    new TemplateRenderSubjectGeometry("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.95m))]));
        var root = JsonNode.Parse(json)!.AsObject();
        switch (mutation)
        {
            case "digest":
                root["plan_digest"] = "sha256:" + new string('f', 64);
                break;
            case "orphan-resource":
                root["resources"]!.AsArray().Add(JsonNode.Parse("""
                    {"resource_id":"asset_orphan","role":"high_resolution_render_source","media_type":"image/jpeg","sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","byte_length":64}
                    """));
                break;
            case "wrong-role":
                root["resources"]![1]!["role"] = "template_copy";
                break;
            case "safe-bounds":
                root["targets"]![0]!["layout"]!["safe_bounds"]!["left"] = 700;
                root["targets"]![0]!["layout"]!["safe_bounds"]!["right"] = 600;
                break;
        }

        var validation = new RenderPlanValidator(new JsonSchemaContractValidator())
            .Validate(root.ToJsonString());

        Assert.False(validation.IsValid);
        Assert.StartsWith("RENDER_PLAN_", Assert.Single(validation.Errors).Code, StringComparison.Ordinal);
    }

    private static AiRenderPlanFactory Factory(TestAvailability availability) =>
        new AiRenderPlanFactory(
            new TemplateRenderPlanBuilder(new RenderPlanValidator(new JsonSchemaContractValidator())),
            availability);

    private static AiConfirmedRenderSelection Selection() => new(
        "frame_01",
        "asset_front",
        new AiSubjectGeometryV1("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.95m),
        1);

    private static RenderFixture DynamicSizeFixture() => Fixture(dynamicSize: true);

    private static SizeTable ReadySizeTable(DateTimeOffset now)
    {
        var candidate = SizeTableCandidate.Create(
            "candidate_01",
            SizeTableSource.Create("asset_front", new string('b', 64)),
            0.99m,
            ["尺码", "袖长"],
            [["L", "/"], ["XL", "/"]]);
        return SizeTable.FromCandidate(
            "size_01", "batch_01", "style_01", "page_01", candidate, now);
    }

    private static RenderFixture Fixture(bool dynamicSize = false)
    {
        var now = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        var page = TemplatePage.Create(
            "page_01",
            "revision_01",
            0,
            "详情页",
            dynamicSize
                ? TemplatePageContentType.DynamicSizeTable
                : TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct,
            null,
            true,
            "resource_template");
        var binding = FrameBinding.Create(
            "frame_01",
            "page_01",
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
                LayerStructureSignature.Create("sha256:" + new string('a', 64))),
            [],
            SelectionRequirement.Create("商品主体清晰完整，构图适合当前图片框。"),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));
        IReadOnlyList<FrameBinding> bindings = dynamicSize ? [] : [binding];
        var revision = Commit(TemplateRevision.CreateDraft(
            "revision_01",
            "group_01",
            1,
            now,
            [page],
            bindings));
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_01",
            "batch_01",
            revision,
            [page.PageId],
            now);
        var batch = BatchDraft.Create(
                "batch_01",
                @"C:\readonly\source",
                @"D:\formal-output",
                snapshot,
                BatchSelectionMode.Automatic,
                includeEditablePsd: false,
                now)
            .BeginScanning(now.AddTicks(1))
            .MarkScanReady(
                BatchInputType.SingleStyle,
                [StyleDraft.Create("style_01", "款式 1", "", 0, ["asset_front"])],
                [SourceAssetMetadata.CreateReadable(
                    "asset_front",
                    "style_01",
                    "front.jpg",
                    "front.jpg",
                    128,
                    new string('b', 64),
                    1000,
                    1200,
                    SourceImageFormat.Jpeg,
                    "image/jpeg")],
                "batches/batch_01/asset-manifest/manifest.json",
                "sha256:" + new string('c', 64),
                now.AddTicks(2));
        var resource = new ImportedTemplateResource(
            "resource_template",
            "group_01",
            "revision_01",
            "templates/groups/group_01/revisions/revision_01/pages/page_01/template.psd",
            64,
            new string('d', 64),
            "sha256:" + new string('a', 64),
            new PsdInspectionDocument(
                new string('d', 64),
                64,
                new PsdCanvasInspection(790, 1252, 8, "RGB"),
                1,
                1,
                0,
                0,
                0,
                "sha256:" + new string('a', 64),
                new PsdInspectionTiming(1, 1, 2),
                1024,
                null,
                []),
            now);
        return new RenderFixture(now, revision, batch, resource);
    }

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
            "templates/groups/group_01/revisions/revision_01/revision-manifest.json",
            "sha256:" + new string('e', 64),
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }

    private sealed record RenderFixture(
        DateTimeOffset Now,
        TemplateRevision Revision,
        BatchDraft Batch,
        ImportedTemplateResource Resource);

    private sealed class TestAvailability(bool available) : IPhotoshopAvailabilityPort
    {
        public int Calls { get; private set; }

        public Task<PhotoshopAvailability> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new PhotoshopAvailability(
                available,
                available ? null : "PHOTOSHOP_TEST_UNAVAILABLE"));
        }
    }
}
