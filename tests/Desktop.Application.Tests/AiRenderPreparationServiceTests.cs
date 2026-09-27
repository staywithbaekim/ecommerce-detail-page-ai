using System.Collections.Immutable;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;
using NSubstitute;

namespace Desktop.Application.Tests;

public sealed class AiRenderPreparationServiceTests
{
    [Theory]
    [InlineData(AiBatchUiStatus.RecoveryRequired)]
    [InlineData(AiBatchUiStatus.Failed)]
    [InlineData(AiBatchUiStatus.Cancelled)]
    [InlineData(AiBatchUiStatus.BudgetStopped)]
    [InlineData(AiBatchUiStatus.Analysing)]
    [InlineData(AiBatchUiStatus.Waiting)]
    public async Task UnclosedAnalysisBlocksBeforeResourceReadsOrSizeWrites(AiBatchUiStatus status)
    {
        var fixture = new Fixture();
        var snapshot = await fixture.SnapshotAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            snapshot with { Projection = snapshot.Projection with { Status = status } }, CancellationToken.None));

        Assert.Equal("AI_RENDER_ANALYSIS_NOT_CLOSED", error.Message);
        Assert.Empty(fixture.Resources.ReceivedCalls());
        Assert.Empty(fixture.Tables.ReceivedCalls());
        Assert.Empty(fixture.Rasterizer.ReceivedCalls());
        Assert.Empty(fixture.Factory.ReceivedCalls());
    }

    [Fact]
    public async Task MissingTemplateResourceStopsBeforePreparingAnyStyle()
    {
        var fixture = new Fixture();
        fixture.Resources.GetByResourceIdAsync("resource_render", Arg.Any<CancellationToken>())
            .Returns((ImportedTemplateResource?)null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PrepareAsync());

        Assert.Equal("AI_RENDER_TEMPLATE_RESOURCE_NOT_FOUND", error.Message);
        Assert.Empty(fixture.Tables.ReceivedCalls());
        Assert.Empty(fixture.Factory.ReceivedCalls());
    }

    [Fact]
    public async Task ManualSelectionRevisionAndBestGeometrySurviveWhileBusinessRedPageIsIsolated()
    {
        var fixture = new Fixture();
        fixture.Lock("frame_front", "b_front", 3);
        var refined = fixture.Request with { RequestId = "refined_geometry", AnalysisKind = AiAnalysisKind.SelectedAssetGeometry };
        var bestGeometry = new AiSubjectGeometryV1("b_front", 0.1m, 0.2m, 0.8m, 0.9m, 0.99m);
        fixture.SetResults(
            fixture.Input(fixture.Result with
            {
                RequirementMatches = [new("frame_front", "a_front", 1, 0.95m, ["exact"])],
                SubjectGeometry = [new("b_front", 0, 0, 1, 1, 0.8m)]
            }),
            fixture.Input(fixture.Result with { SubjectGeometry = [bestGeometry] }, refined, 20));

        var result = await fixture.PrepareAsync();

        Assert.Same(fixture.FactoryResult, result);
        var request = Assert.IsType<AiRenderPlanPreparationRequest>(fixture.Captured);
        Assert.Same(fixture.Batch, request.Batch);
        Assert.Same(fixture.Revision, request.TemplateRevision);
        Assert.Single(request.TemplateResources);
        await fixture.Resources.Received(1).GetByResourceIdAsync("resource_render", Arg.Any<CancellationToken>());
        var selected = Assert.Single(Assert.Single(request.Styles).Selections);
        Assert.Equal("frame_front", selected.FrameId);
        Assert.Equal("b_front", selected.AssetId);
        Assert.Equal(3, selected.SelectionRevision);
        Assert.Equal(bestGeometry, selected.SubjectGeometry);
        Assert.Equal(fixture.Now.AddSeconds(20), request.CreatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimarySelectionsAndPerColorPlansRemainConsistent(bool bothComplete)
    {
        var fixture = new Fixture();
        var matches = fixture.Result.RequirementMatches.Concat(
            new[] { new AiRequirementMatchV1("frame_front", "b_front", 2, 0.9m, ["exact"]) }).ToList();
        if (bothComplete)
        {
            matches.Add(new("frame_back", "b_back", 2, 0.9m, ["exact"]));
        }
        fixture.SetResults(fixture.Input(fixture.Result with
        {
            VariantGroups =
            [
                new("variant_b", "color", "Example B", ["b_front", "b_back"], false, IsComplete: bothComplete),
                new("variant_a", "color", "Example A", ["a_front", "a_back"], true, IsComplete: true)
            ],
            RequirementMatches = matches
        }));

        await fixture.PrepareAsync();

        var style = Assert.Single(fixture.Captured!.Styles);
        Assert.All(style.Selections, selection =>
        {
            Assert.Equal("variant_a", selection.VariantId);
            Assert.StartsWith("a_", selection.AssetId);
        });
        var plans = Assert.IsAssignableFrom<IReadOnlyList<AiOutputVariantPlan>>(style.VariantPlans);
        Assert.Equal("variant_a", Assert.Single(plans, plan => plan.IsPrimary).VariantId);
        Assert.All(plans.Single(plan => plan.VariantId == "variant_b").RequirementAssets.Values,
            assetId => Assert.StartsWith("b_", assetId));
        Assert.Equal(bothComplete, plans.Single(plan => plan.VariantId == "variant_b").CanRender);
        Assert.Equal(bothComplete ? 2 : 1, plans.Single(plan => plan.VariantId == "variant_b").RequirementAssets.Count);
    }

    [Fact]
    public async Task SecondaryFrontUsesIncompleteOtherColorWithoutChangingPrimaryBack()
    {
        var fixture = new Fixture(secondaryFront: true);
        var secondaryGeometry = new AiSubjectGeometryV1("b_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.97m);
        fixture.SetResults(fixture.Input(fixture.Result with
        {
            VariantGroups =
            [
                new("variant_a", "color", "Example A", ["a_front", "a_back"], false, IsComplete: true),
                new("variant_b", "color", "Example B", ["b_front"], false, IsComplete: false)
            ],
            RequirementMatches = [.. fixture.Result.RequirementMatches, new("frame_front", "b_front", 2, 0.9m, ["exact"])],
            SubjectGeometry = [.. fixture.Result.SubjectGeometry, secondaryGeometry]
        }));

        await fixture.PrepareAsync();

        var selections = Assert.Single(fixture.Captured!.Styles).Selections;
        var front = Assert.Single(selections, selection => selection.FrameId == "frame_front");
        Assert.Equal("variant_b", front.VariantId);
        Assert.Equal("b_front", front.AssetId);
        Assert.Equal(secondaryGeometry, front.SubjectGeometry);
        var back = Assert.Single(selections, selection => selection.FrameId == "frame_back");
        Assert.Equal("variant_a", back.VariantId);
        Assert.Equal("a_back", back.AssetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitProductPageLockIsNotOverriddenByColorGrouping(bool perVariantFront)
    {
        var fixture = new Fixture(perVariantFront: perVariantFront);
        fixture.Lock("frame_front", "b_front", 3);
        var geometry = new AiSubjectGeometryV1("b_front", .2m, .1m, .8m, .9m, .99m);
        fixture.SetResults(fixture.Input(fixture.Result with
        {
            VariantGroups = [new("variant_a", "color", "Example A", ["a_front", "a_back"], true)],
            SubjectGeometry = [.. fixture.Result.SubjectGeometry, geometry]
        }));

        var snapshot = await fixture.SnapshotAsync();
        var pages = Assert.Single(snapshot.Projection.Styles).Pages;
        Assert.True(pages.Single(page => page.PageId == "page_back").CanGenerate);
        Assert.Equal(!perVariantFront, pages.Single(page => page.PageId == "page_front").CanGenerate);
        await fixture.Service.PrepareAsync(snapshot, default);

        var selections = Assert.Single(fixture.Captured!.Styles).Selections;
        var back = Assert.Single(selections, selection => selection.FrameId == "frame_back");
        Assert.Equal("a_back", back.AssetId);
        if (perVariantFront)
        {
            Assert.DoesNotContain(selections, selection => selection.FrameId == "frame_front");
            Assert.Contains(Assert.Single(fixture.Captured.Styles).VariantPlans!,
                plan => plan.ConflictCode == AiVariantPlanner.ManualSelectionConflictCode);
        }
        else
        {
            var front = Assert.Single(selections, selection => selection.FrameId == "frame_front");
            Assert.Equal("b_front", front.AssetId);
            Assert.Equal(3, front.SelectionRevision);
            Assert.Equal(geometry, front.SubjectGeometry);
            Assert.Null(front.SelectionConflictCode);
        }
    }

    [Fact]
    public async Task GeometryForNonPrimaryColorSurvivesPreparationAndUsesBestValidatedEvidence()
    {
        var fixture = new Fixture();
        var best = new AiSubjectGeometryV1("b_front", .2m, .1m, .7m, .9m, .99m);
        fixture.SetResults(fixture.Input(fixture.Result with {
            VariantGroups = [new("variant_a", "color", "A", ["a_front", "a_back"], true, IsComplete: true),
                new("variant_b", "color", "B", ["b_front", "b_back"], false, IsComplete: true)],
            RequirementMatches = [.. fixture.Result.RequirementMatches, new("frame_front", "b_front", 2, .9m, ["exact"]),
                new("frame_back", "b_back", 2, .9m, ["exact"])],
            SubjectGeometry = [.. fixture.Result.SubjectGeometry, new("b_front", 0, 0, 1, 1, .8m),
                new("b_back", .2m, .1m, .8m, .9m, .95m), new("size_1", 0, 0, 1, 1, 1)] }),
            fixture.Input(fixture.Result with { SubjectGeometry = [best] },
                fixture.Request with { RequestId = "secondary_geometry", AnalysisKind = AiAnalysisKind.SelectedAssetGeometry }, 20));
        await fixture.PrepareAsync();
        var style = Assert.Single(fixture.Captured!.Styles);
        Assert.All(style.Selections, selection => Assert.StartsWith("a_", selection.AssetId));
        Assert.NotNull(style.SubjectGeometryByAsset);
        Assert.Equal(4, style.SubjectGeometryByAsset.Count);
        Assert.Equal(best, style.SubjectGeometryByAsset["b_front"]);
        Assert.DoesNotContain("size_1", style.SubjectGeometryByAsset.Keys);
    }

    [Theory]
    [InlineData("missing", "SIZE_TABLE_SOURCE_MISSING")]
    [InlineData("unconfigured", "AI_SIZE_TABLE_RASTERIZER_PENDING")]
    [InlineData("conflict", "SIZE_TABLE_SOURCE_CONFLICT")]
    [InlineData("corrupt", "SIZE_TABLE_STATE_CORRUPT")]
    [InlineData("save_conflict", "SIZE_TABLE_REVISION_CONFLICT")]
    [InlineData("review", "SIZE_TABLE_REVIEW_REQUIRED")]
    public async Task SizeFailureKeepsItsReasonWithoutRasterizing(string fault, string reason)
    {
        var fixture = new Fixture(includeSize: true, configureSize: fault != "unconfigured");
        var candidate = fault == "missing" ? null : Fixture.Candidate("size_1", fault == "review" ? 0.5m : 0.99m);
        fixture.SetResults(fixture.Input(fixture.Result with
        {
            SizeTableCandidate = candidate,
            Warnings = fault == "conflict" ? [new("size_source_conflict", "size_table", "Conflicting sources")] : []
        }));
        if (fault == "corrupt")
        {
            fixture.Tables.LoadAsync(fixture.Batch.BatchId, fixture.Request.StyleId, "page_size", Arg.Any<CancellationToken>())
                .Returns(SizeTableLoadResult.RecoveryRequired("corrupt"));
        }
        if (fault == "save_conflict")
        {
            fixture.Tables.SaveAsync(Arg.Any<SizeTable>(), null, Arg.Any<CancellationToken>())
                .Returns(new SizeTableSaveResult(SizeTableSaveStatus.RevisionConflict, "SIZE_TABLE_REVISION_CONFLICT"));
        }

        await fixture.PrepareAsync();

        var style = Assert.Single(fixture.Captured!.Styles);
        Assert.Equal(reason, style.SizeTableReasonCode);
        Assert.Empty(style.PreparedSizeTablePages!);
        Assert.Empty(fixture.Rasterizer.ReceivedCalls());
        Assert.Equal(2, style.Selections.Count);
        if (fault is "missing" or "unconfigured" or "conflict")
        {
            Assert.Empty(fixture.Tables.ReceivedCalls());
        }
    }

    [Fact]
    public async Task PreparedSizeTableIsSavedOnceAndExistingEditsAreReusedUntilSourceChanges()
    {
        var fixture = new Fixture(includeSize: true);
        fixture.SetResults(fixture.Input(fixture.Result with { SizeTableCandidate = Fixture.Candidate("size_1") }));
        await fixture.PrepareAsync();
        var prepared = Assert.Single(Assert.Single(fixture.Captured!.Styles).PreparedSizeTablePages!);
        var table = prepared.Table;
        Assert.Equal("size_1", table.Source.AssetId);
        Assert.Equal(table.DataDigest, prepared.RasterizedResource.TableDataDigest);
        Assert.Equal("template_bbbbbbbbbbbbbbbb_v1", prepared.RasterizedResource.StyleVersion);
        await fixture.Tables.Received(1).SaveAsync(table, null, Arg.Any<CancellationToken>());
        var editedRows = table.Rows.Select(row => SizeTableRow.Create(row.RowId,
            row.Cells.Select(cell => cell.ColumnId == table.Columns[1].ColumnId
                ? SizeTableCell.Create(cell.ColumnId, "61")
                : cell)));
        var edited = table.Edit(table.Revision, table.Columns, editedRows, table.UpdatedAt.AddSeconds(1));
        Assert.NotEqual(table.DataDigest, edited.DataDigest);
        fixture.Tables.LoadAsync(fixture.Batch.BatchId, fixture.Request.StyleId, "page_size", Arg.Any<CancellationToken>())
            .Returns(SizeTableLoadResult.Loaded(edited));
        fixture.Tables.ClearReceivedCalls();
        fixture.Rasterizer.ClearReceivedCalls();

        await fixture.PrepareAsync();

        Assert.Same(edited, Assert.Single(Assert.Single(fixture.Captured!.Styles).PreparedSizeTablePages!).Table);
        await fixture.Tables.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
        await fixture.Rasterizer.Received(1).RasterizeAsync(
            Arg.Is<SizeTableRasterizationRequest>(request => ReferenceEquals(request.Table, edited)), Arg.Any<CancellationToken>());

        fixture.SetResults(fixture.Input(fixture.Result with { SizeTableCandidate = Fixture.Candidate("size_2") }));
        fixture.Rasterizer.ClearReceivedCalls();
        await fixture.PrepareAsync();

        Assert.Equal("SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED", Assert.Single(fixture.Captured!.Styles).SizeTableReasonCode);
        Assert.Empty(Assert.Single(fixture.Captured.Styles).PreparedSizeTablePages!);
        Assert.Empty(fixture.Rasterizer.ReceivedCalls());
        await fixture.Tables.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
    }

    [Fact]
    public async Task ExplicitSizeSourceLockResolvesProviderConflict()
    {
        var fixture = new Fixture(includeSize: true);
        fixture.Lock(AiAnalysisInputProjector.SizeTableRequirementId("page_size"), "size_2", 2);
        var refinement = fixture.Request with { RequestId = "size_refinement", AnalysisKind = AiAnalysisKind.SizeTableExtraction };
        fixture.SetResults(
            fixture.Input(fixture.Result with
            {
                SizeTableCandidate = Fixture.Candidate("size_1"),
                Warnings = [new("size_source_conflict", "size_table", "Conflicting sources")]
            }),
            fixture.Input(fixture.Result with { SizeTableCandidate = Fixture.Candidate("size_2") }, refinement));

        await fixture.PrepareAsync();

        var style = Assert.Single(fixture.Captured!.Styles);
        Assert.Null(style.SizeTableCandidate);
        Assert.Equal("SIZE_TABLE_VALID", style.SizeTableReasonCode);
        Assert.Equal("size_2", Assert.Single(style.PreparedSizeTablePages!).Table.Source.AssetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenSizeCalibrationFlowsToActualRasterPlacementAndStaleTemplateBlocks(bool stale)
    {
        var fixture = new Fixture(includeSize: true);
        fixture.SetResults(fixture.Input(fixture.Result with { SizeTableCandidate = Fixture.Candidate("size_1") }));
        var snapshot = await fixture.SnapshotAsync();
        var calibration = SizeTableCalibrationResolverTests.Calibration();
        var baseline = SizeTableInsertionTests.Fixture().Baseline;
        fixture.Resources.GetByResourceIdAsync("resource_render", Arg.Any<CancellationToken>()).Returns(
            new ImportedTemplateResource("resource_render", "group_render", "revision_render", "templates/resource_render.psd",
                baseline.SizeBytes, baseline.FileSha256, baseline.StructureSignature,
                stale ? baseline with { FileSha256 = new string('0', 64) } : baseline, fixture.Now));
        var revision = snapshot.TemplateRevision;
        var calibratedRevision = TemplateRevision.Restore(revision.RevisionId, revision.GroupId, revision.RevisionNo,
            revision.State, revision.ManifestRef, revision.RuleDigest, revision.CreatedAt,
            revision.Pages.Select(page => page.PageId == "page_size" ? page.WithSizeTableCalibration(calibration) : page), revision.FrameBindings);
        await fixture.Service.PrepareAsync(snapshot with { TemplateRevision = calibratedRevision }, default);
        var style = Assert.Single(fixture.Captured!.Styles);
        if (stale)
        {
            Assert.Equal("SIZE_TABLE_CALIBRATION_INVALID", style.SizeTableReasonCode);
            Assert.Empty(style.PreparedSizeTablePages!);
            Assert.Empty(fixture.Rasterizer.ReceivedCalls());
        }
        else
        {
            var prepared = Assert.Single(style.PreparedSizeTablePages!);
            Assert.StartsWith("calibrated_", prepared.RasterizedResource.StyleVersion);
            var insertion = Assert.IsType<SizeTableInsertionTarget>(prepared.CalibratedInsertion);
            Assert.Equal("page_size", insertion.PageId);
            Assert.Equal("revision_render", insertion.TemplateRevisionId);
            Assert.Equal(calibration.AboveLayer.LayerId, insertion.Insertion.AboveLayer.LayerId);
            Assert.Equal(1000, insertion.Insertion.Placement.SourceWidth);
            Assert.Equal(200, insertion.Insertion.Placement.SourceHeight);
            Assert.Equal(770m, insertion.Insertion.Placement.Width);
            Assert.Equal(154m, insertion.Insertion.Placement.Height);
            Assert.Equal(prepared.Table.DataDigest, prepared.RasterizedResource.TableDataDigest);
        }
    }

    [Fact]
    public async Task CancellationDuringResourceReadPropagatesWithoutCallingFactory()
    {
        var fixture = new Fixture();
        var snapshot = await fixture.SnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        fixture.Resources.GetByResourceIdAsync("resource_render", cancellation.Token)
            .Returns(Task.FromCanceled<ImportedTemplateResource?>(cancellation.Token));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.PrepareAsync(snapshot, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(fixture.Factory.ReceivedCalls());
        Assert.Empty(fixture.Tables.ReceivedCalls());
    }

    private sealed class Fixture
    {
        private static readonly string[] Views = ["front", "back"];
        private static readonly string[] AssetIds = ["a_front", "a_back", "b_front", "b_back", "size_1", "size_2"];
        public DateTimeOffset Now { get; } = new(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        public IAiAnalysisResultReader Reader { get; } = Substitute.For<IAiAnalysisResultReader>();
        public ITemplateResourceRepository Resources { get; } = Substitute.For<ITemplateResourceRepository>();
        public IAiRenderPlanFactory Factory { get; } = Substitute.For<IAiRenderPlanFactory>();
        public ISizeTableRepository Tables { get; } = Substitute.For<ISizeTableRepository>();
        public ISizeTableRasterizer Rasterizer { get; } = Substitute.For<ISizeTableRasterizer>();
        public AiRenderPreparationService Service { get; }
        public BatchDraft Batch { get; }
        public TemplateRevision Revision { get; }
        public AiAnalysisRequestV1 Request { get; }
        public AiAnalysisResultV1 Result { get; }
        public AiRenderPlanPreparationRequest? Captured { get; private set; }
        public AiRenderPlanPreparation FactoryResult { get; } = new(
            "batch_render", AiRenderPreparationStatus.WaitingForPhotoshop, "PHOTOSHOP_UNAVAILABLE", []);

        public Fixture(bool includeSize = false, bool configureSize = true, bool secondaryFront = false,
            bool perVariantFront = false)
        {
            var pages = new List<TemplatePage>
            {
                TemplatePage.Create("page_front", "revision_render", 0, "Front",
                    TemplatePageContentType.NormalComposition,
                    perVariantFront ? TemplateGenerationMode.OncePerVariant : TemplateGenerationMode.OncePerProduct,
                    null, true, "resource_render"),
                TemplatePage.Create("page_back", "revision_render", 1, "Back",
                    TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true, "resource_render")
            };
            var bindings = Views.Select((view, index) => FrameBinding.Create(
                $"frame_{view}", $"page_{view}", LayerPath.Create([index + 1]),
                ExpectedLayerEvidence.Create(index + 1, $"frame_{view}", true, false, false, false,
                    byte.MaxValue, "normal", LayerStructureSignature.Create("sha256:" + new string('a', 64))),
                [], SelectionRequirement.Create($"{(secondaryFront && view == "front" ? "secondary" : "primary")} {view}"),
                LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100)))).ToArray();
            if (includeSize)
            {
                pages.Add(TemplatePage.Create("page_size", "revision_render", 2, "Sizes",
                    TemplatePageContentType.DynamicSizeTable, TemplateGenerationMode.OncePerProduct, null, true, "resource_render"));
            }
            Revision = TemplateRevision.Restore("revision_render", "group_render", 1, TemplateRevisionState.Committed,
                "templates/revision_render/manifest.json", "sha256:" + new string('b', 64), Now, pages, bindings);
            var template = BatchTemplateSnapshot.Create("snapshot_render", "batch_render", Revision,
                pages.Select(page => page.PageId), Now);
            var assets = AssetIds
                .Select((id, index) => SourceAssetMetadata.CreateReadable(id, "style_render", $"{id}.jpg",
                    $"{id}.jpg", 128, new string((char)('a' + index), 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg"))
                .ToArray();
            Batch = BatchDraft.Create("batch_render", Path.Combine(Path.GetTempPath(), "render-input"),
                    Path.Combine(Path.GetTempPath(), "render-output"), template, BatchSelectionMode.Automatic, false, Now)
                .BeginScanning(Now.AddTicks(1))
                .MarkScanReady(BatchInputType.SingleStyle,
                    [StyleDraft.Create("style_render", "Product", "", 0, assets.Select(asset => asset.AssetId))], assets,
                    "batches/batch_render/asset-manifest/manifest.json", "sha256:" + new string('d', 64), Now.AddTicks(2));
            Request = AiAnalysisInputProjector.Project(Batch, Revision, "style_render",
                new("correlation_render", "consent_render", "model/render", "prompt/render", "copy/render"));
            Result = new(AiAnalysisProtocol.ResultSchemaVersion, "result", Request.RequestId, null, Request.ModelConfigVersion,
                [new("frame_front", "a_front", 1, 0.95m, ["exact"]), new("frame_back", "a_back", 1, 0.95m, ["exact"])],
                [], [new("a_front", 0, 0, 1, 1, 0.95m), new("a_back", 0, 0, 1, 1, 0.95m)],
                null, [], new("usage_render", 10, 10, 1, null, null));
            SetResults(Input(Result));
            var inspection = new PsdInspectionDocument(new string('a', 64), 128, new(1000, 1200, 8, "RGB"),
                0, 0, 0, 0, 0, "sha256:" + new string('a', 64), new(0, 0, 0), 0, null, []);
            Resources.GetByResourceIdAsync("resource_render", Arg.Any<CancellationToken>()).Returns(
                new ImportedTemplateResource("resource_render", "group_render", "revision_render",
                    "templates/resource_render.psd", 128, new string('a', 64), inspection.StructureSignature, inspection, Now));
            Factory.PrepareAsync(Arg.Any<AiRenderPlanPreparationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Captured = call.Arg<AiRenderPlanPreparationRequest>();
                return FactoryResult;
            });
            Tables.LoadAsync(Batch.BatchId, Request.StyleId, "page_size", Arg.Any<CancellationToken>())
                .Returns(SizeTableLoadResult.NotFound());
            Tables.SaveAsync(Arg.Any<SizeTable>(), null, Arg.Any<CancellationToken>())
                .Returns(new SizeTableSaveResult(SizeTableSaveStatus.Saved));
            Rasterizer.RasterizeAsync(Arg.Any<SizeTableRasterizationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var request = call.Arg<SizeTableRasterizationRequest>();
                return new SizeTableRasterizedResource("raster", "dynamic_size_table", "image/png", new string('a', 64),
                    128, 1000, 200, request.Table.DataDigest, request.Style.StyleVersion, request.Style.LayoutVersion, "raster/table.png");
            });
            Service = new(Reader, Resources, Factory, configureSize ? Tables : null, configureSize ? Rasterizer : null);
        }

        public static AiSizeTableCandidateV1 Candidate(string assetId, decimal confidence = 0.99m) =>
            new(assetId, confidence, ["Size", "Length"], [["M", "60"]]);

        public AiStyleAnalysisResultInput Input(AiAnalysisResultV1 result, AiAnalysisRequestV1? request = null, int seconds = 10)
        {
            request ??= Request;
            return new(request, new(request.RequestId, AiAnalysisCacheKeyFactory.Create(request), Batch.BatchId,
                request.StyleId, "Validated", $"results/{request.RequestId}.json", Now, Now.AddSeconds(seconds)),
                result, AiAnalysisResultReadFailure.None);
        }

        public void SetResults(params AiStyleAnalysisResultInput[] inputs) =>
            Reader.LoadStyleResultsAsync(Arg.Any<IEnumerable<AiAnalysisRequestV1>>(), Request.StyleId, Arg.Any<CancellationToken>())
                .Returns(inputs);

        public void Lock(string requirementId, string assetId, long revision) =>
            Reader.LoadSessionAsync($"session-{Request.RequestId}", Arg.Any<CancellationToken>()).Returns(
                AiAnalysisSession.Restore($"session-{Request.RequestId}", Batch.BatchId, Request.StyleId,
                    Request.TemplateSnapshotId, "analysis_render", AiAnalysisSessionStatus.ManualReview, revision,
                    [new(requirementId, assetId, revision)], Request.Requirements.Select(item => item.RequirementId),
                    Request.Assets.Select(item => item.AssetId), true));

        public async Task<AiRenderPreparationSnapshot> SnapshotAsync()
        {
            var projection = await new AiBatchProjectionService(Reader).BuildAsync(
                new(Batch, Revision, "Templates", [Request], AiAnalysisSchedulingControlState.Running, null,
                    false, null, true, null, ImmutableDictionary<string, string>.Empty,
                    ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty), CancellationToken.None);
            return new(Batch, Revision, [Request], projection);
        }

        public async Task<AiRenderPlanPreparation> PrepareAsync() =>
            await Service.PrepareAsync(await SnapshotAsync(), CancellationToken.None);
    }
}
