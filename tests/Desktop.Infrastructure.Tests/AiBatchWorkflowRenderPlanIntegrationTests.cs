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

        var sessionId = $"session-ai-{fixture.Batch.BatchId}-style_render_integration";
        var session = await store.LoadSessionAsync(sessionId, CancellationToken.None);
        Assert.NotNull(session);
        var lockedSession = session!.LockSelection(
            "frame_render_integration",
            "asset_front",
            session.SelectionRevision);
        await store.SaveSessionAsync(
            lockedSession,
            CancellationToken.None,
            expectedSelectionRevision: session.SelectionRevision);
        var locked = await workflow.RefreshAsync(fixture.Batch.BatchId, CancellationToken.None);
        Assert.NotNull(locked);
        var lockedPage = Assert.Single(Assert.Single(locked!.Styles).Pages);
        Assert.Equal(AiPageUiStatus.Ready, lockedPage.Status);
        Assert.False(lockedPage.RequiresManualConfiguration);

        var confirmedPreparation = await workflow.PrepareRenderingAsync(
            fixture.Batch.BatchId,
            CancellationToken.None);
        Assert.Equal(AiRenderPreparationStatus.WaitingForPhotoshop, confirmedPreparation.Status);
        Assert.Equal(
            AiRenderPagePreparationStatus.ReadyToRender,
            Assert.Single(confirmedPreparation.Pages).Status);
    }

    [Fact]
    public async Task LowRankedCandidateDoesNotDowngradeHighConfidenceTopOne()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 45, 0, TimeSpan.Zero);
        var fixture = Fixture(directory.Path, now, includeSecondAsset: true);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await SeedAsync(store, fixture, "import_top_n");

        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var resultStore = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
        var provider = new ScenarioProvider(request => Result(
            request,
            matches:
            [
                new("frame_render_integration", "asset_front", 1, 0.96m, ["front_view"]),
                new("frame_render_integration", "asset_second", 2, 0.20m, ["detail_view"])
            ],
            geometry: [new("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.96m)],
            warnings: [new("low_confidence", "requirement", "lower ranked alternative")]));
        using var workflow = Workflow(store, resultStore, provider);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);
        var projection = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Status == AiBatchUiStatus.Ready);

        var page = Assert.Single(Assert.Single(projection.Styles).Pages);
        Assert.Equal(AiPageUiStatus.Ready, page.Status);
        Assert.False(page.RequiresManualConfiguration);
        Assert.Equal(2, page.Candidates.Count);
        Assert.Equal("asset_front", Assert.Single(page.Requirements!).SelectedAssetId);
        Assert.DoesNotContain(page.WarningTexts, text => text.Contains("置信度", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedAssetGeometryRefinementClosesOnlyTheCurrentSelectionAndRenderPlan()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 50, 0, TimeSpan.Zero);
        var fixture = Fixture(directory.Path, now);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await SeedAsync(store, fixture, "import_geometry_refinement");

        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var resultStore = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
        var provider = new ScenarioProvider(request => request.AnalysisKind switch
        {
            AiAnalysisKind.ProductJointUnderstanding => Result(
                request,
                matches: [new("frame_render_integration", "asset_front", 1, 0.96m, ["front_view"])],
                geometry: []),
            AiAnalysisKind.SelectedAssetGeometry => Result(
                request,
                geometry: [new("asset_front", 0.08m, 0.06m, 0.92m, 0.94m, 0.98m)]),
            _ => Result(request)
        });
        var plans = new AiRenderPlanFactory(
            new TemplateRenderPlanBuilder(new RenderPlanValidator(new JsonSchemaContractValidator())),
            new UnavailablePhotoshopAvailabilityPort());
        using var workflow = Workflow(store, resultStore, provider, store, plans);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);
        var awaitingGeometry = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Status == AiBatchUiStatus.ReviewRequired);
        var awaitingPage = Assert.Single(Assert.Single(awaitingGeometry.Styles).Pages);
        Assert.Equal(AiPageUiStatus.ReviewRequired, awaitingPage.Status);
        Assert.True(awaitingPage.RequiresManualConfiguration);
        Assert.Contains(awaitingPage.WarningTexts, text => text.Contains("主体边界", StringComparison.Ordinal));

        _ = await workflow.AnalyzeSelectedAssetAsync(
            fixture.Batch.BatchId,
            "style_render_integration",
            "asset_front",
            includeSizeTableSource: false,
            CancellationToken.None);
        var ready = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Status == AiBatchUiStatus.Ready);
        Assert.Equal(AiPageUiStatus.Ready, Assert.Single(Assert.Single(ready.Styles).Pages).Status);

        var preparation = await workflow.PrepareRenderingAsync(
            fixture.Batch.BatchId,
            CancellationToken.None);
        Assert.Equal(AiRenderPreparationStatus.WaitingForPhotoshop, preparation.Status);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, Assert.Single(preparation.Pages).Status);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task MissingAndSizeWarningsRemainOnTheirRelevantPages()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 55, 0, TimeSpan.Zero);
        var fixture = Fixture(
            directory.Path,
            now,
            includeSecondNormalPage: true,
            includeDynamicSizePage: true);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await SeedAsync(store, fixture, "import_warning_isolation");

        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var resultStore = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
        var provider = new ScenarioProvider(request => Result(
            request,
            matches: [new("frame_render_integration", "asset_front", 1, 0.96m, ["front_view"])],
            geometry: [new("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.96m)],
            warnings:
            [
                new("asset_missing", "requirement", "back view missing"),
                new("size_source_conflict", "size_table", "multiple size sources")
            ]));
        using var workflow = Workflow(store, resultStore, provider);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);
        var projection = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Styles.SelectMany(style => style.Pages).All(page =>
                page.Status is AiPageUiStatus.Ready or AiPageUiStatus.BusinessError));
        var pages = Assert.Single(projection.Styles).Pages.ToDictionary(page => page.PageId);

        Assert.Equal(AiPageUiStatus.Ready, pages["page_render_integration"].Status);
        Assert.Empty(pages["page_render_integration"].WarningTexts);
        Assert.Equal(AiPageUiStatus.BusinessError, pages["page_render_second"].Status);
        Assert.Contains(pages["page_render_second"].WarningTexts, text => text.Contains("缺少", StringComparison.Ordinal));
        Assert.DoesNotContain(pages["page_render_second"].WarningTexts, text => text.Contains("尺码来源", StringComparison.Ordinal));
        Assert.Equal(AiPageUiStatus.BusinessError, pages["page_render_size"].Status);
        Assert.Contains(pages["page_render_size"].WarningTexts, text => text.Contains("尺码来源", StringComparison.Ordinal));
        Assert.DoesNotContain(pages["page_render_size"].WarningTexts, text => text.Contains("商品素材", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DynamicSizePageAcceptsOneTrustedCandidateThenBlocksConflictingRefinement()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 58, 0, TimeSpan.Zero);
        var fixture = Fixture(
            directory.Path,
            now,
            includeSecondAsset: true,
            includeDynamicSizePage: true);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await SeedAsync(store, fixture, "import_size_candidates");

        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var resultStore = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
        var provider = new SizeConflictGateProvider();
        using var workflow = Workflow(store, resultStore, provider);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);
        var unique = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Styles.SelectMany(style => style.Pages)
                .Any(page => page.PageId == "page_render_size"
                    && page.Status == AiPageUiStatus.ReviewRequired));
        var uniquePages = Assert.Single(unique.Styles).Pages.ToDictionary(page => page.PageId);
        Assert.Equal(AiPageUiStatus.Ready, uniquePages["page_render_integration"].Status);
        Assert.True(uniquePages["page_render_size"].RequiresManualConfiguration);
        Assert.Contains(uniquePages["page_render_size"].WarningTexts, text => text.Contains("SIZE-01", StringComparison.Ordinal));

        _ = await workflow.AnalyzeSelectedAssetAsync(
            fixture.Batch.BatchId,
            "style_render_integration",
            "asset_second",
            includeSizeTableSource: true,
            CancellationToken.None);
        await provider.WaitUntilSizeStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var pending = await workflow.RefreshAsync(fixture.Batch.BatchId, CancellationToken.None);
        Assert.NotNull(pending);
        var pendingPages = Assert.Single(pending!.Styles).Pages.ToDictionary(page => page.PageId);
        Assert.Equal(AiPageUiStatus.Ready, pendingPages["page_render_integration"].Status);
        Assert.Equal(AiPageUiStatus.Analysing, pendingPages["page_render_size"].Status);
        Assert.DoesNotContain(pendingPages["page_render_size"].WarningTexts, text =>
            text.Contains("未找到", StringComparison.Ordinal));
        provider.ReleaseSize();
        var conflict = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => provider.Calls >= 3
                && candidate.Styles.SelectMany(style => style.Pages)
                    .Any(page => page.PageId == "page_render_size"
                        && page.Status == AiPageUiStatus.BusinessError));
        var conflictPages = Assert.Single(conflict.Styles).Pages.ToDictionary(page => page.PageId);
        Assert.Equal(AiPageUiStatus.Ready, conflictPages["page_render_integration"].Status);
        Assert.Equal(AiPageUiStatus.BusinessError, conflictPages["page_render_size"].Status);
        Assert.Contains(conflictPages["page_render_size"].WarningTexts, text => text.Contains("唯一来源", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("request", "结果引用异常")]
    [InlineData("model", "结果引用异常")]
    [InlineData("semantic", "结果文件不可用")]
    public async Task TypedFirstLayerResultMismatchFailsClosedAtPageBoundary(
        string mutation,
        string expectedStatusText)
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 59, 0, TimeSpan.Zero);
        var fixture = Fixture(directory.Path, now);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await SeedAsync(store, fixture, $"import_typed_{mutation}");

        var resultStore = new MutatingLoadResultStore(result => mutation switch
        {
            "request" => result with { RequestId = "ai_cross_referenced_request" },
            "model" => result with { ModelConfigVersion = "mismatched-model-v1" },
            "semantic" => result with
            {
                SubjectGeometry = [new("asset_front", 0.9m, 0.1m, 0.2m, 0.9m, 0.95m)]
            },
            _ => result
        });
        var provider = new GeometryProvider();
        using var workflow = Workflow(store, resultStore, provider);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);
        var projection = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Status == AiBatchUiStatus.RecoveryRequired);

        var page = Assert.Single(Assert.Single(projection.Styles).Pages);
        Assert.Equal(AiPageUiStatus.SystemError, page.Status);
        Assert.Contains(expectedStatusText, page.StatusText, StringComparison.Ordinal);
        Assert.False(page.CanGenerate);
        Assert.Equal(1, provider.Calls);
        // The projection can observe the committed ledger while the workflow
        // background task is still returning from its final persistence call.
        // Let that owned task quiesce before the test data root is disposed.
        await Task.Delay(100);
    }

    [Fact]
    public async Task CrossReferencedGeometryRefinementIsolatedToPagesUsingThatAsset()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 59, 30, TimeSpan.Zero);
        var fixture = Fixture(
            directory.Path,
            now,
            includeSecondAsset: true,
            includeSecondNormalPage: true);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await SeedAsync(store, fixture, "import_refinement_cross_reference");

        var resultStore = new MutatingLoadResultStore(result =>
            result.RequestId.Contains("-geometry-", StringComparison.Ordinal)
                ? result with { RequestId = "ai_crossed_geometry_result" }
                : result);
        var provider = new ScenarioProvider(request => request.AnalysisKind switch
        {
            AiAnalysisKind.ProductJointUnderstanding => Result(
                request,
                matches:
                [
                    new("frame_render_integration", "asset_front", 1, 0.96m, ["front_view"]),
                    new("frame_render_second", "asset_second", 1, 0.95m, ["back_view"])
                ],
                geometry:
                [
                    new("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.95m),
                    new("asset_second", 0.1m, 0.1m, 0.9m, 0.9m, 0.96m)
                ]),
            AiAnalysisKind.SelectedAssetGeometry => Result(
                request,
                geometry: [new("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.97m)]),
            _ => Result(request)
        });
        using var workflow = Workflow(store, resultStore, provider);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);
        var initial = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate =>
            {
                var pages = candidate.Styles.SelectMany(style => style.Pages).ToDictionary(page => page.PageId);
                return pages.GetValueOrDefault("page_render_integration")?.Status == AiPageUiStatus.Ready
                    && pages.GetValueOrDefault("page_render_second")?.Status == AiPageUiStatus.Ready;
            });
        Assert.Equal(2, Assert.Single(initial.Styles).Pages.Count);

        _ = await workflow.AnalyzeSelectedAssetAsync(
            fixture.Batch.BatchId,
            "style_render_integration",
            "asset_front",
            includeSizeTableSource: false,
            CancellationToken.None);
        var failed = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            _ => provider.Calls >= 2 && resultStore.MutatedLoads >= 1);
        var pages = Assert.Single(failed.Styles).Pages.ToDictionary(page => page.PageId);
        Assert.True(
            resultStore.MutatedLoads >= 1,
            $"mutated loads: {resultStore.MutatedLoads}; stored: {resultStore.StoredRequestIds}");
        Assert.Equal(AiPageUiStatus.SystemError, pages["page_render_integration"].Status);
        Assert.Contains("结果引用异常", pages["page_render_integration"].StatusText, StringComparison.Ordinal);
        Assert.Equal(AiPageUiStatus.Ready, pages["page_render_second"].Status);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task RefinementRequestMustRemainClosedOverItsFirstLayerParent()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 10, 59, 45, TimeSpan.Zero);
        var fixture = Fixture(directory.Path, now);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await SeedAsync(store, fixture, "import_refinement_parent_closure");

        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var resultStore = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
        var provider = new ScenarioProvider(request => request.AnalysisKind switch
        {
            AiAnalysisKind.ProductJointUnderstanding => Result(
                request,
                matches: [new("frame_render_integration", "asset_front", 1, 0.96m, ["front_view"])],
                geometry: []),
            AiAnalysisKind.SelectedAssetGeometry => Result(
                request,
                geometry: [new("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.97m)]),
            _ => Result(request)
        });
        using var workflow = Workflow(store, resultStore, provider);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);
        _ = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Status == AiBatchUiStatus.ReviewRequired);
        _ = await workflow.AnalyzeSelectedAssetAsync(
            fixture.Batch.BatchId,
            "style_render_integration",
            "asset_front",
            includeSizeTableSource: false,
            CancellationToken.None);
        _ = await WaitForProjectionAsync(
            workflow,
            fixture.Batch.BatchId,
            candidate => candidate.Status == AiBatchUiStatus.Ready && provider.Calls >= 2);

        var stateKey = $"render-integration-{fixture.Batch.BatchId}";
        var state = await store.LoadSchedulingStateAsync(stateKey, CancellationToken.None);
        Assert.NotNull(state);
        var histories = state!.HistoricalRequests!.Select(request =>
            request.AnalysisKind == AiAnalysisKind.SelectedAssetGeometry
                ? request with { CorrelationId = "rogue-parent-correlation" }
                : request).ToArray();
        Assert.Contains(histories, request =>
            request.AnalysisKind == AiAnalysisKind.SelectedAssetGeometry
            && request.CorrelationId == "rogue-parent-correlation");
        await store.SaveSchedulingStateAsync(
            stateKey,
            state with { HistoricalRequests = histories, UpdatedAt = state.UpdatedAt.AddTicks(1) },
            CancellationToken.None);

        var failed = await workflow.RefreshAsync(fixture.Batch.BatchId, CancellationToken.None);
        Assert.NotNull(failed);
        var page = Assert.Single(Assert.Single(failed!.Styles).Pages);
        Assert.Equal(AiPageUiStatus.SystemError, page.Status);
        Assert.Contains("结果引用异常", page.StatusText, StringComparison.Ordinal);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CorruptResultLoadIsolatedToPageSystemErrorInsteadOfEscapingRefresh()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 11, 0, 0, TimeSpan.Zero);
        var fixture = Fixture(directory.Path, now);
        var options = new DesktopDataOptions(directory.Path);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await store.SaveGroupAsync(fixture.Group, CancellationToken.None);
        await store.CommitRevisionAsync(fixture.Revision, null, CancellationToken.None);
        await store.SaveImportedAsync("import_result_corrupt", fixture.Resource, CancellationToken.None);
        await store.SaveAsync(fixture.Batch, CancellationToken.None);

        var results = new ThrowingLoadResultStore();
        var provider = new GeometryProvider();
        var schedulers = new TestSchedulerFactory(store, results, provider);
        using var workflow = new AiBatchAnalysisWorkflow(
            store,
            store,
            store,
            results,
            store,
            schedulers,
            store);

        await workflow.GrantUploadConsentAsync(CancellationToken.None);
        _ = await workflow.StartAsync(fixture.Batch.BatchId, CancellationToken.None);

        AiBatchAnalysisProjection? projection = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            projection = await workflow.RefreshAsync(fixture.Batch.BatchId, CancellationToken.None);
            var page = projection?.Styles.SingleOrDefault()?.Pages.SingleOrDefault();
            if (page?.Status == AiPageUiStatus.SystemError)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(projection);
        var pageProjection = Assert.Single(Assert.Single(projection!.Styles).Pages);
        Assert.Equal(AiPageUiStatus.SystemError, pageProjection.Status);
        Assert.Equal("结果文件不可用，需要恢复", pageProjection.StatusText);
        Assert.Equal("查询状态", pageProjection.ActionText);
        Assert.Contains("未启动生成", pageProjection.WarningTexts.Single(), StringComparison.Ordinal);
        Assert.False(pageProjection.CanGenerate);
        Assert.Equal(1, provider.Calls);
        await Task.Delay(100);
    }

    private static async Task SeedAsync(
        SqliteDesktopStore store,
        IntegrationFixture fixture,
        string importId)
    {
        await store.SaveGroupAsync(fixture.Group, CancellationToken.None);
        await store.CommitRevisionAsync(fixture.Revision, null, CancellationToken.None);
        await store.SaveImportedAsync(importId, fixture.Resource, CancellationToken.None);
        await store.SaveAsync(fixture.Batch, CancellationToken.None);
    }

    private static AiBatchAnalysisWorkflow Workflow(
        SqliteDesktopStore store,
        IAiAnalysisResultStore results,
        IAiServicePort provider,
        ITemplateResourceRepository? templateResources = null,
        IAiRenderPlanFactory? renderPlans = null) => new(
            store,
            store,
            store,
            results,
            store,
            new TestSchedulerFactory(store, results, provider),
            store,
            templateResources,
            renderPlans);

    private static async Task<AiBatchAnalysisProjection> WaitForProjectionAsync(
        AiBatchAnalysisWorkflow workflow,
        string batchId,
        Func<AiBatchAnalysisProjection, bool> predicate)
    {
        AiBatchAnalysisProjection? projection = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            projection = await workflow.RefreshAsync(batchId, CancellationToken.None);
            if (projection is not null && predicate(projection))
            {
                return projection;
            }

            await Task.Delay(10);
        }

        var pageStates = projection is null
            ? "none"
            : string.Join(", ", projection.Styles.SelectMany(style => style.Pages).Select(page =>
                $"{page.PageId}={page.Status}/{string.Join('|', page.Requirements?.Select(requirement => requirement.SelectedAssetId) ?? [])}/{string.Join('|', page.WarningTexts)}"));
        Assert.Fail($"Projection did not reach the expected state. Last: {projection?.Status}; pages: {pageStates}");
        throw new InvalidOperationException("TEST_PROJECTION_TIMEOUT");
    }

    private static AiAnalysisResultV1 Result(
        AiAnalysisRequestV1 request,
        IReadOnlyList<AiRequirementMatchV1>? matches = null,
        IReadOnlyList<AiSubjectGeometryV1>? geometry = null,
        AiSizeTableCandidateV1? sizeTable = null,
        IReadOnlyList<AiWarningV1>? warnings = null) => new(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            request.RequestId,
            "provider-request-redacted",
            request.ModelConfigVersion,
            matches ?? [],
            [],
            geometry ?? [],
            sizeTable,
            warnings ?? [],
            new AiUsageSummaryV1(
                $"usage_{request.RequestId}",
                10,
                4,
                1,
                null,
                null));

    private static IntegrationFixture Fixture(
        string root,
        DateTimeOffset now,
        bool includeSecondAsset = false,
        bool includeSecondNormalPage = false,
        bool includeDynamicSizePage = false)
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
        var pages = new List<TemplatePage> { page };
        var bindings = new List<FrameBinding> { binding };
        if (includeSecondNormalPage)
        {
            var secondPage = TemplatePage.Create(
                "page_render_second",
                "revision_render_integration",
                pages.Count,
                "商品背面",
                TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null,
                true,
                "resource_render_integration");
            pages.Add(secondPage);
            bindings.Add(FrameBinding.Create(
                "frame_render_second",
                secondPage.PageId,
                LayerPath.Create([3]),
                ExpectedLayerEvidence.Create(
                    257,
                    "registered_frame_second",
                    true,
                    false,
                    false,
                    false,
                    byte.MaxValue,
                    "normal",
                    LayerStructureSignature.Create("sha256:" + new string('a', 64))),
                [],
                SelectionRequirement.Create("需要清晰完整的商品背面。"),
                LayoutIntent.Create("back_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252))));
        }
        if (includeDynamicSizePage)
        {
            pages.Add(TemplatePage.Create(
                "page_render_size",
                "revision_render_integration",
                pages.Count,
                "动态尺码表",
                TemplatePageContentType.DynamicSizeTable,
                TemplateGenerationMode.OncePerProduct,
                null,
                true,
                "resource_render_integration"));
        }
        var draft = TemplateRevision.CreateDraft(
            "revision_render_integration",
            group.GroupId,
            1,
            now,
            pages,
            bindings);
        var revision = Commit(draft);
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_render_integration",
            "batch_render_integration",
            revision,
            pages.Select(item => item.PageId),
            now);
        var assetIds = includeSecondAsset
            ? new[] { "asset_front", "asset_second" }
            : ["asset_front"];
        var assets = new List<SourceAssetMetadata>
        {
            SourceAssetMetadata.CreateReadable(
                "asset_front",
                "style_render_integration",
                "front.jpg",
                "front.jpg",
                128,
                new string('b', 64),
                1000,
                1200,
                SourceImageFormat.Jpeg,
                "image/jpeg")
        };
        if (includeSecondAsset)
        {
            assets.Add(SourceAssetMetadata.CreateReadable(
                "asset_second",
                "style_render_integration",
                "second.jpg",
                "second.jpg",
                256,
                new string('f', 64),
                900,
                1100,
                SourceImageFormat.Jpeg,
                "image/jpeg"));
        }
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
                [StyleDraft.Create("style_render_integration", "款式 1", "", 0, assetIds)],
                assets,
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

    private sealed class ScenarioProvider(
        Func<AiAnalysisRequestV1, AiAnalysisResultV1> createResult) : IAiServicePort
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<AiAnalysisExecutionResult> AnalyzeAsync(
            AiAnalysisRequestV1 request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(AiAnalysisExecutionResult.Completed(
                createResult(request),
                new AiProviderUsage(10, 4, 14, false),
                TimeSpan.FromMilliseconds(5)));
        }
    }

    private sealed class SizeConflictGateProvider : IAiServicePort
    {
        private readonly TaskCompletionSource _sizeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSize = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task WaitUntilSizeStartedAsync() => _sizeStarted.Task;

        public void ReleaseSize() => _releaseSize.TrySetResult();

        public async Task<AiAnalysisExecutionResult> AnalyzeAsync(
            AiAnalysisRequestV1 request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            if (request.AnalysisKind == AiAnalysisKind.SizeTableExtraction)
            {
                _sizeStarted.TrySetResult();
                await _releaseSize.Task.WaitAsync(cancellationToken);
            }

            var result = request.AnalysisKind switch
            {
                AiAnalysisKind.ProductJointUnderstanding => Result(
                    request,
                    matches: [new("frame_render_integration", "asset_front", 1, 0.96m, ["front_view"])],
                    geometry: [new("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.96m)],
                    sizeTable: new("asset_front", 0.99m, ["尺码", "胸围"], [["M", "96"]])),
                AiAnalysisKind.SelectedAssetGeometry => Result(
                    request,
                    geometry: [new("asset_second", 0.1m, 0.1m, 0.9m, 0.9m, 0.95m)]),
                AiAnalysisKind.SizeTableExtraction => Result(
                    request,
                    sizeTable: new("asset_second", 0.98m, ["尺码", "腰围"], [["M", "72"]])),
                _ => Result(request)
            };
            return AiAnalysisExecutionResult.Completed(
                result,
                new AiProviderUsage(10, 4, 14, false),
                TimeSpan.FromMilliseconds(5));
        }
    }

    private sealed class MutatingLoadResultStore(
        Func<AiAnalysisResultV1, AiAnalysisResultV1> mutate) : IAiAnalysisResultStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AiAnalysisResultV1> _results =
            new(StringComparer.Ordinal);
        private int _mutatedLoads;

        public int MutatedLoads => Volatile.Read(ref _mutatedLoads);

        public string StoredRequestIds => string.Join(",", _results.Values.Select(result => result.RequestId));

        public Task SaveAsync(
            string resultReference,
            AiAnalysisResultV1 result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _results[resultReference] = result;
            return Task.CompletedTask;
        }

        public Task<AiAnalysisResultV1?> LoadAsync(
            string resultReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_results.TryGetValue(resultReference, out var result))
            {
                return Task.FromResult<AiAnalysisResultV1?>(null);
            }

            var projected = mutate(result);
            if (!ReferenceEquals(projected, result))
            {
                Interlocked.Increment(ref _mutatedLoads);
            }
            return Task.FromResult<AiAnalysisResultV1?>(projected);
        }
    }

    private sealed class ThrowingLoadResultStore : IAiAnalysisResultStore
    {
        public Task SaveAsync(
            string resultReference,
            AiAnalysisResultV1 result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<AiAnalysisResultV1?> LoadAsync(
            string resultReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("TEST_RESULT_CORRUPT");
        }
    }
}
