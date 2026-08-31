using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class AiBatchWorkflowRenderPlanIntegrationTests
{
    [Fact]
    public async Task PersistedAnalysisClosesIntoRenderPlanAndStopsBeforeUnavailablePhotoshop()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
        var fixture = Fixture(directory.Path, now);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await store.SaveGroupAsync(fixture.Group, CancellationToken.None);
        await store.CommitRevisionAsync(fixture.Revision, null, CancellationToken.None);
        await store.SaveImportedAsync("import_render_integration", fixture.Resource, CancellationToken.None);
        await store.SaveAsync(fixture.Batch, CancellationToken.None);

        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var resultStore = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
        var provider = new GeometryProvider();
        var schedulers = new TestSchedulerFactory(store, resultStore, provider);
        var plans = new AiRenderPlanFactory(
            new TemplateRenderPlanBuilder(new RenderPlanValidator(new JsonSchemaContractValidator())),
            new UnavailablePhotoshopAvailabilityPort());
        using var workflow = new AiBatchAnalysisWorkflow(
            store,
            store,
            store,
            resultStore,
            store,
            schedulers,
            store,
            store,
            plans);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);

        AiBatchAnalysisProjection? projection = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            projection = await workflow.RefreshAsync(fixture.Batch.BatchId, CancellationToken.None);
            if (projection?.Status == AiBatchUiStatus.Ready)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(projection);
        Assert.Equal(AiBatchUiStatus.Ready, projection!.Status);
        Assert.Equal(1, provider.Calls);

        var preparation = await workflow.PrepareRenderingAsync(
            fixture.Batch.BatchId,
            CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.WaitingForPhotoshop, preparation.Status);
        Assert.Equal("PHOTOSHOP_FORMAL_ADAPTER_NOT_AVAILABLE", preparation.ReasonCode);
        Assert.False(preparation.HasFormalOutput);
        var page = Assert.Single(preparation.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, page.Status);
        Assert.True(page.HasPlan);
        using var document = JsonDocument.Parse(page.RenderPlanJson!);
        Assert.Equal(page.PlanDigest, document.RootElement.GetProperty("plan_digest").GetString());
        Assert.Equal("asset_front", document.RootElement.GetProperty("targets")[0]
            .GetProperty("asset_resource_id").GetString());
        Assert.DoesNotContain(fixture.Batch.InputRoot, page.RenderPlanJson!, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.Batch.OutputRoot));
    }

    [Fact]
    public async Task LowConfidenceAiDefaultCannotCloseRenderPlanBeforeManualConfirmation()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 30, 0, TimeSpan.Zero);
        var fixture = Fixture(directory.Path, now);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await store.SaveGroupAsync(fixture.Group, CancellationToken.None);
        await store.CommitRevisionAsync(fixture.Revision, null, CancellationToken.None);
        await store.SaveImportedAsync("import_render_review", fixture.Resource, CancellationToken.None);
        await store.SaveAsync(fixture.Batch, CancellationToken.None);

        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var resultStore = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
        var provider = new GeometryProvider(0.40m);
        var schedulers = new TestSchedulerFactory(store, resultStore, provider);
        var plans = new AiRenderPlanFactory(
            new TemplateRenderPlanBuilder(new RenderPlanValidator(new JsonSchemaContractValidator())),
            new UnavailablePhotoshopAvailabilityPort());
        using var workflow = new AiBatchAnalysisWorkflow(
            store,
            store,
            store,
            resultStore,
            store,
            schedulers,
            store,
            store,
            plans);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);

        AiBatchAnalysisProjection? projection = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            projection = await workflow.RefreshAsync(fixture.Batch.BatchId, CancellationToken.None);
            if (projection?.Status == AiBatchUiStatus.ReviewRequired)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(projection);
        Assert.Equal(AiBatchUiStatus.ReviewRequired, projection!.Status);
        var pageProjection = Assert.Single(Assert.Single(projection.Styles).Pages);
        Assert.True(pageProjection.RequiresManualConfiguration);
        Assert.False(projection.CanEnterGeneration);

        var preparation = await workflow.PrepareRenderingAsync(
            fixture.Batch.BatchId,
            CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.UserActionRequired, preparation.Status);
        Assert.Equal("AI_RENDER_NO_EXECUTABLE_PAGE", preparation.ReasonCode);
        var page = Assert.Single(preparation.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.BusinessBlocked, page.Status);
        Assert.False(page.HasPlan);
        Assert.False(Directory.Exists(fixture.Batch.OutputRoot));
    }

    private static IntegrationFixture Fixture(string root, DateTimeOffset now)
    {
        var group = TemplateGroup.Create("group_render_integration", "渲染闭合集成模板", now);
        var page = TemplatePage.Create(
            "page_render_integration",
            "revision_render_integration",
            0,
            "商品主图",
            TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct,
            null,
            true,
            "resource_render_integration");
        var binding = FrameBinding.Create(
            "frame_render_integration",
            page.PageId,
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
        var draft = TemplateRevision.CreateDraft(
            "revision_render_integration",
            group.GroupId,
            1,
            now,
            [page],
            [binding]);
        var revision = Commit(draft);
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_render_integration",
            "batch_render_integration",
            revision,
            [page.PageId],
            now);
        var batch = BatchDraft.Create(
                "batch_render_integration",
                Path.Combine(root, "read-only-sources"),
                Path.Combine(root, "formal-output"),
                snapshot,
                BatchSelectionMode.Automatic,
                includeEditablePsd: false,
                now)
            .BeginScanning(now.AddTicks(1))
            .MarkScanReady(
                BatchInputType.SingleStyle,
                [StyleDraft.Create("style_render_integration", "款式 1", "", 0, ["asset_front"])],
                [SourceAssetMetadata.CreateReadable(
                    "asset_front",
                    "style_render_integration",
                    "front.jpg",
                    "front.jpg",
                    128,
                    new string('b', 64),
                    1000,
                    1200,
                    SourceImageFormat.Jpeg,
                    "image/jpeg")],
                "batches/batch_render_integration/asset-manifest/manifest.json",
                "sha256:" + new string('c', 64),
                now.AddTicks(2));
        var resource = new ImportedTemplateResource(
            "resource_render_integration",
            group.GroupId,
            revision.RevisionId,
            "templates/groups/group_render_integration/revisions/revision_render_integration/pages/page_render_integration/template.psd",
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
        return new IntegrationFixture(group, revision, batch, resource);
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
            "templates/groups/group_render_integration/revisions/revision_render_integration/revision-manifest.json",
            "sha256:" + new string('e', 64),
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }

    private sealed record IntegrationFixture(
        TemplateGroup Group,
        TemplateRevision Revision,
        BatchDraft Batch,
        ImportedTemplateResource Resource);

    private sealed class TestSchedulerFactory(
        SqliteDesktopStore store,
        IAiAnalysisResultStore results,
        IAiServicePort provider) : IAiAnalysisSchedulerFactory
    {
        public AiAnalysisScheduler Create(string batchId)
        {
            var queueOptions = new AiAnalysisQueueOptions(2, 1, 1);
            return new AiAnalysisScheduler(
                provider,
                store,
                new BoundedAiAnalysisWorkBuffer(queueOptions),
                new AiAnalysisBudgetPolicy(
                    4,
                    1000,
                    1000,
                    10m,
                    1m,
                    new AiAnalysisPricing(0.01m, 0.03m)),
                queueOptions: queueOptions,
                stateStore: store,
                stateKey: $"render-integration-{batchId}",
                resultStore: results,
                consentPort: store);
        }
    }

    private sealed class GeometryProvider(decimal matchConfidence = 0.95m) : IAiServicePort
    {
        public int Calls { get; private set; }

        public Task<AiAnalysisExecutionResult> AnalyzeAsync(
            AiAnalysisRequestV1 request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var assetId = Assert.Single(request.Assets).AssetId;
            var requirementId = Assert.Single(request.Requirements).RequirementId;
            var result = new AiAnalysisResultV1(
                AiAnalysisProtocol.ResultSchemaVersion,
                "result",
                request.RequestId,
                "provider-request-redacted",
                request.ModelConfigVersion,
                [new AiRequirementMatchV1(
                    requirementId,
                    assetId,
                    1,
                    matchConfidence,
                    ["front_view", "clear_subject"])],
                [],
                [new AiSubjectGeometryV1(assetId, 0.1m, 0.1m, 0.9m, 0.9m, 0.96m)],
                null,
                [],
                new AiUsageSummaryV1(
                    $"usage_{request.RequestId}",
                    10,
                    4,
                    1,
                    null,
                    null));
            return Task.FromResult(AiAnalysisExecutionResult.Completed(
                result,
                new AiProviderUsage(10, 4, 14, false),
                TimeSpan.FromMilliseconds(5)));
        }
    }
}
