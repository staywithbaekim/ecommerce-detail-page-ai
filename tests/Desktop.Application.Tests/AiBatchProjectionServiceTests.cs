using System.Collections.Immutable;
using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using NSubstitute;

namespace Desktop.Application.Tests;

public sealed class AiBatchProjectionServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalReasonIsCapturedAfterLedgerReadAndOnlyForObservedTerminalRequests(bool terminal)
    {
        var fixture = new Fixture();
        var ledgerRead = false;
        var reader = Substitute.For<IAiAnalysisResultReader>();
        reader.LoadStyleResultsAsync(Arg.Any<IEnumerable<AiAnalysisRequestV1>>(), fixture.Request.StyleId,
            Arg.Any<CancellationToken>()).Returns(_ =>
            {
                ledgerRead = true;
                return Task.FromResult(new[] { new AiStyleAnalysisResultInput(fixture.Request,
                    fixture.Ledger with { Status = terminal ? "TerminalError" : "Analysing", ResultReference = null },
                    null, AiAnalysisResultReadFailure.None) });
            });
        var captures = 0;
        var projection = await new AiBatchProjectionService(reader).BuildAsync(fixture.Snapshot, default, () =>
        {
            Assert.True(ledgerRead);
            captures++;
            return ImmutableDictionary<string, string>.Empty
                .Add(fixture.Request.RequestId, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH")
                .Add("foreign_request", "AI_PROVIDER_REQUEST_TOO_LARGE");
        });
        Assert.Equal(1, captures);
        Assert.False(projection.CanEnterGeneration);
        var page = Assert.Single(Assert.Single(projection.Styles).Pages);
        if (terminal)
        {
            Assert.Equal("重新建立批次", projection.ActionText);
            Assert.Contains("结果引用", projection.StatusText, StringComparison.Ordinal);
            Assert.Equal("AI 返回引用不匹配，需要重新建立批次", page.StatusText);
            Assert.Equal("重新建立批次", page.ActionText);
        }
        else
        {
            Assert.Equal(AiBatchUiStatus.Analysing, projection.Status);
            Assert.Equal(AiPageUiStatus.Analysing, page.Status);
            Assert.DoesNotContain("引用", projection.StatusText, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("16 MiB", projection.StatusText, StringComparison.Ordinal);
        Assert.Empty(fixture.Snapshot.TerminalErrors);
        await reader.Received(1).LoadStyleResultsAsync(Arg.Any<IEnumerable<AiAnalysisRequestV1>>(),
            fixture.Request.StyleId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidatedResultProjectsSelectionAndPreviewWithoutWritingState()
    {
        var fixture = new Fixture();

        var projection = await fixture.ProjectAsync();

        Assert.Equal(AiBatchUiStatus.Ready, projection.Status);
        Assert.True(projection.CanEnterGeneration);
        Assert.Equal(1, projection.ReadyPageCount);
        var requirement = Assert.Single(Assert.Single(Assert.Single(projection.Styles).Pages).Requirements!);
        Assert.Equal("asset_front", requirement.SelectedAssetId);
        Assert.Equal(Path.Combine(fixture.Snapshot.Batch.InputRoot, "front.jpg"), requirement.SelectedPreviewPath);
        Assert.Equal(AiRequirementSelectionUiStatus.AiSelected, requirement.Status);
        Assert.All(fixture.Store.ReceivedCalls(), call => Assert.Contains(call.GetMethodInfo().Name,
            new[] { nameof(IAiAnalysisStore.FindRequestByFingerprintAsync), nameof(IAiAnalysisStore.LoadSessionAsync) }));
        Assert.All(fixture.Results.ReceivedCalls(), call =>
            Assert.Equal(nameof(IAiAnalysisResultStore.LoadAsync), call.GetMethodInfo().Name));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("result_reference")]
    [InlineData("ledger_reference")]
    public async Task UnusableDurableResultRequiresRecovery(string fault)
    {
        var fixture = new Fixture();
        switch (fault)
        {
            case "missing":
                fixture.Results.LoadAsync("results/first.json", Arg.Any<CancellationToken>())
                    .Returns((AiAnalysisResultV1?)null);
                break;
            case "unreadable":
                fixture.Results.LoadAsync("results/first.json", Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<AiAnalysisResultV1?>(new IOException("unavailable")));
                break;
            case "result_reference":
                fixture.SetResult(fixture.Result with { RequestId = "unrelated_request" });
                break;
            case "ledger_reference":
                fixture.SetLedger(fixture.Ledger with { StyleId = "unrelated_style" });
                break;
        }

        var projection = await fixture.ProjectAsync();

        Assert.Equal(AiBatchUiStatus.RecoveryRequired, projection.Status);
        Assert.False(projection.CanEnterGeneration);
        var page = Assert.Single(Assert.Single(projection.Styles).Pages);
        Assert.Equal(AiPageUiStatus.SystemError, page.Status);
        Assert.Empty(page.Candidates);
        Assert.Null(Assert.Single(page.Requirements!).SelectedAssetId);
        if (fault == "ledger_reference")
        {
            await fixture.Results.DidNotReceiveWithAnyArgs().LoadAsync(default!, default);
        }
    }

    [Fact]
    public async Task MissingRequiredMatchIsolatesPageAndKeepsReadySiblingExecutable()
    {
        var fixture = new Fixture(pageCount: 2);
        fixture.SetResult(fixture.Result with { RequirementMatches = [fixture.Result.RequirementMatches[0]] });

        var projection = await fixture.ProjectAsync();

        Assert.Equal(AiBatchUiStatus.ReviewRequired, projection.Status);
        Assert.True(projection.CanEnterGeneration);
        Assert.Equal(1, projection.ExecutablePageCount);
        Assert.Equal(1, projection.IsolatedPageCount);
        Assert.Collection(Assert.Single(projection.Styles).Pages,
            page => Assert.Equal(AiPageUiStatus.Ready, page.Status),
            page => Assert.Equal(AiPageUiStatus.BusinessError, page.Status));
    }

    [Fact]
    public async Task UnknownResultKeepsManualLockVisibleWithoutEnablingGeneration()
    {
        var fixture = new Fixture();
        fixture.SetLedger(fixture.Ledger with { Status = "AiResultUnknown", ResultReference = null });
        fixture.SetLockedSession(AiAnalysisSessionStatus.AiResultUnknown);

        var projection = await fixture.ProjectAsync();

        Assert.Equal(AiBatchUiStatus.RecoveryRequired, projection.Status);
        Assert.False(projection.CanEnterGeneration);
        var page = Assert.Single(Assert.Single(projection.Styles).Pages);
        Assert.Equal(AiPageUiStatus.SystemError, page.Status);
        Assert.True(Assert.Single(page.Requirements!).IsManualLocked);
        Assert.Equal("asset_front", Assert.Single(page.Requirements!).SelectedAssetId);
        await fixture.Results.DidNotReceiveWithAnyArgs().LoadAsync(default!, default);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.99)]
    public async Task ExplicitUnsafeEvidenceOnCalibratedTemplateIsolatesOnlyItsPage(double confidence)
    {
        var fixture = new Fixture(pageCount: 2);
        fixture.SetResult(fixture.Result with
        {
            RequirementMatches = [fixture.Result.RequirementMatches[0] with
                { Confidence = (decimal)confidence, EvidenceCodes = [AiTargetMatchEvidence.Unsafe] },
                fixture.Result.RequirementMatches[1]]
        });

        var projection = await fixture.ProjectAsync();

        Assert.True(projection.CanEnterGeneration);
        Assert.Equal(1, projection.ExecutablePageCount);
        Assert.Equal(1, projection.IsolatedPageCount);
        var pages = Assert.Single(projection.Styles).Pages;
        Assert.Equal(AiPageUiStatus.BusinessError, pages[0].Status);
        Assert.Equal(AiRequirementSelectionUiStatus.Unsafe, Assert.Single(pages[0].Requirements!).Status);
        Assert.False(pages[0].CanGenerate);
        Assert.Equal(AiPageUiStatus.Ready, pages[1].Status);

        fixture.SetLockedSession(AiAnalysisSessionStatus.ManualReview);
        var confirmed = await fixture.ProjectAsync();
        Assert.Equal(2, confirmed.ExecutablePageCount);
        Assert.True(Assert.Single(Assert.Single(confirmed.Styles).Pages[0].Requirements!).IsManualLocked);
    }

    [Fact]
    public async Task ManualModeOnlyEnablesGenerationAfterLocking()
    {
        var fixture = new Fixture(selectionMode: BatchSelectionMode.Manual);

        var unlocked = await fixture.ProjectAsync();
        fixture.SetLockedSession(AiAnalysisSessionStatus.ManualReview);
        var locked = await fixture.ProjectAsync();

        Assert.Equal(AiBatchUiStatus.ManualConfiguration, unlocked.Status);
        Assert.False(unlocked.CanEnterGeneration);
        Assert.True(locked.CanEnterGeneration);
        Assert.True(Assert.Single(Assert.Single(Assert.Single(locked.Styles).Pages).Requirements!).IsManualLocked);
    }

    [Fact]
    public async Task LocalRecoveryFailureOverridesPreviouslyValidatedResults()
    {
        var fixture = new Fixture();

        var projection = await fixture.ProjectAsync(fixture.Snapshot with
        {
            SchedulerRestoreError = "AI_CHECKPOINT_INCONSISTENT"
        });

        Assert.Equal(AiBatchUiStatus.RecoveryRequired, projection.Status);
        Assert.False(projection.CanEnterGeneration);
        Assert.Equal(AiPageUiStatus.SystemError, Assert.Single(Assert.Single(projection.Styles).Pages).Status);
    }

    [Fact]
    public async Task BudgetStopBlocksUnsettledWorkButPreservesCompletedResults()
    {
        var fixture = new Fixture();
        var snapshot = fixture.Snapshot with { BudgetHardStopped = true };
        var ready = await fixture.ProjectAsync(snapshot);
        fixture.SetLedger(fixture.Ledger with { Status = "Queued", ResultReference = null });

        var waiting = await fixture.ProjectAsync(snapshot);

        Assert.Equal(AiBatchUiStatus.Ready, ready.Status);
        Assert.True(ready.CanEnterGeneration);
        Assert.Equal(AiBatchUiStatus.BudgetStopped, waiting.Status);
        Assert.False(waiting.CanEnterGeneration);
    }

    [Fact]
    public async Task LegacyAliasesAreNormalizedOnlyInMemory()
    {
        var fixture = new Fixture();
        var aliased = fixture.Result with
        {
            RequirementMatches = [new("req_0", "img_0", 1, 0.95m, [AiTargetMatchEvidence.Exact])],
            SubjectGeometry = [new("img_0", 0, 0, 1, 1, 0.95m)]
        };
        fixture.SetResult(aliased);

        var projection = await fixture.ProjectAsync();

        Assert.True(projection.CanEnterGeneration);
        var candidate = Assert.Single(Assert.Single(Assert.Single(projection.Styles).Pages).Candidates);
        Assert.Equal("frame_0", candidate.RequirementId);
        Assert.Equal("asset_front", candidate.AssetId);
        Assert.Equal("img_0", Assert.Single(aliased.RequirementMatches).AssetId);
        await fixture.Results.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Fact]
    public async Task UnclosedRefinementIsRejectedBeforeLoadingItsResult()
    {
        var fixture = new Fixture();
        var child = fixture.Request with
        {
            RequestId = "unrelated_geometry_request",
            AnalysisKind = AiAnalysisKind.SelectedAssetGeometry
        };
        fixture.Store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(child), Arg.Any<CancellationToken>())
            .Returns(fixture.Ledger with { RequestId = child.RequestId, Fingerprint = AiAnalysisCacheKeyFactory.Create(child), ResultReference = "results/child.json" });
        var reader = new AiAnalysisResultReader(fixture.Store, fixture.Results);

        var inputs = await reader.LoadStyleResultsAsync([fixture.Request, child], child.StyleId, CancellationToken.None);

        Assert.Equal(AiAnalysisResultReadFailure.InvalidReference,
            Assert.Single(inputs, input => input.Request.RequestId == child.RequestId).Failure);
        await fixture.Results.DidNotReceive().LoadAsync("results/child.json", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingRecoveryFailure()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        fixture.Results.LoadAsync("results/first.json", cancellation.Token)
            .Returns(Task.FromCanceled<AiAnalysisResultV1?>(cancellation.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AiBatchProjectionService(new AiAnalysisResultReader(fixture.Store, fixture.Results))
                .BuildAsync(fixture.Snapshot, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData("Uploading", false)]
    [InlineData("Analysing", false)]
    [InlineData("Uploading", true)]
    [InlineData("Analysing", true)]
    public async Task RecoveredProviderWorkShowsUnknownWhileLiveWorkStillShowsProgress(string status, bool recovered)
    {
        var fixture = new Fixture();
        fixture.SetLedger(fixture.Ledger with { Status = status, ResultReference = null });
        var snapshot = fixture.Snapshot with
        {
            RecoveredInFlightRequestIds = recovered
                ? ImmutableHashSet.Create(fixture.Request.RequestId)
                : ImmutableHashSet<string>.Empty
        };

        var projection = await fixture.ProjectAsync(snapshot);

        Assert.Equal(recovered ? AiBatchUiStatus.RecoveryRequired : AiBatchUiStatus.Analysing, projection.Status);
        Assert.False(projection.CanEnterGeneration);
        var page = Assert.Single(Assert.Single(projection.Styles).Pages);
        Assert.Equal(recovered ? AiPageUiStatus.SystemError : AiPageUiStatus.Analysing, page.Status);
        if (recovered)
        {
            Assert.Contains("不会自动重发", projection.StatusText, StringComparison.Ordinal);
            Assert.Null(Assert.Single(page.Requirements!).SelectedAssetId);
        }
        Assert.All(fixture.Store.ReceivedCalls(), call => Assert.Contains(call.GetMethodInfo().Name,
            new[] { nameof(IAiAnalysisStore.FindRequestByFingerprintAsync), nameof(IAiAnalysisStore.LoadSessionAsync) }));
        await fixture.Results.DidNotReceiveWithAnyArgs().LoadAsync(default!, default);
    }

    [Fact]
    public async Task SettledResultWinsOverEarlierRecoveryMarker()
    {
        var fixture = new Fixture();
        var projection = await fixture.ProjectAsync(fixture.Snapshot with
        {
            RecoveredInFlightRequestIds = ImmutableHashSet.Create(fixture.Request.RequestId)
        });

        Assert.Equal(AiBatchUiStatus.Ready, projection.Status);
        Assert.True(projection.CanEnterGeneration);
    }

    [Theory]
    [InlineData(AiAnalysisKind.SelectedAssetGeometry)]
    [InlineData(AiAnalysisKind.SizeTableExtraction)]
    public async Task RecoveredRefinementUsesUnknownStateOnItsAffectedPage(string kind)
    {
        var fixture = new Fixture(pageCount: 2, includeDynamicSize: true);
        var child = fixture.Request with { RequestId = "recovered_child", AnalysisKind = kind };
        var childLedger = fixture.Ledger with { RequestId = child.RequestId, Status = "Analysing", ResultReference = null };
        var reader = Substitute.For<IAiAnalysisResultReader>();
        reader.LoadStyleResultsAsync(Arg.Any<IEnumerable<AiAnalysisRequestV1>>(), fixture.Request.StyleId, Arg.Any<CancellationToken>())
            .Returns(new AiStyleAnalysisResultInput[]
            {
                new(fixture.Request, fixture.Ledger, fixture.Result, AiAnalysisResultReadFailure.None),
                new(child, childLedger, null, AiAnalysisResultReadFailure.None)
            });
        var snapshot = fixture.Snapshot with
        {
            Requests = [fixture.Request, child],
            RecoveredInFlightRequestIds = ImmutableHashSet.Create(child.RequestId)
        };

        var projection = await new AiBatchProjectionService(reader).BuildAsync(snapshot, CancellationToken.None);

        var pages = Assert.Single(projection.Styles).Pages;
        var affected = pages.Single(page => page.PageId == (kind == AiAnalysisKind.SelectedAssetGeometry ? "page_0" : "page_1"));
        Assert.Equal(AiPageUiStatus.SystemError, affected.Status);
        Assert.Contains("结果未知", affected.StatusText, StringComparison.Ordinal);
        Assert.Contains(affected.WarningTexts, warning => warning.Contains("禁止自动重发", StringComparison.Ordinal));
        if (kind == AiAnalysisKind.SizeTableExtraction)
            Assert.Equal(AiPageUiStatus.Ready, pages.Single(page => page.PageId == "page_0").Status);
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(0.99)]
    public async Task SingleTrustedSizeSourceEnablesSizeOnlyBatchWithoutProductFrames(double confidence)
    {
        var fixture = new Fixture(includeDynamicSize: true);
        fixture.SetResult(fixture.Result with
        {
            SubjectGeometry = [],
            SizeTableCandidate = new("asset_front", (decimal)confidence, ["尺码", "胸围"], [["M", "96"]])
        });

        var projection = await fixture.ProjectAsync();

        var style = Assert.Single(projection.Styles);
        var page = Assert.Single(style.Pages);
        Assert.Single(fixture.Snapshot.Batch.Assets);
        Assert.Empty(fixture.Snapshot.TemplateRevision.FrameBindings);
        Assert.Empty(page.Requirements!);
        Assert.Equal(AiPageUiStatus.Ready, page.Status);
        Assert.False(page.RequiresManualConfiguration);
        Assert.True(page.CanGenerate);
        Assert.Empty(page.WarningTexts);
        Assert.Equal(AiSizeTableSourceStatus.AutoSelected, page.SizeTableSource!.Status);
        Assert.True(Assert.Single(page.SizeTableSource.Candidates).IsSelected);
        Assert.Equal(AiStyleUiStatus.Ready, style.Status);
        Assert.Equal(AiBatchUiStatus.Ready, projection.Status);
        Assert.True(projection.CanEnterGeneration);
        Assert.Equal(1, projection.ExecutablePageCount);
        Assert.Equal(0, projection.IsolatedPageCount);
        Assert.Contains("可生成 1/1 页", projection.ProgressText, StringComparison.Ordinal);
        await fixture.Results.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("low_confidence")]
    [InlineData("conflict")]
    public async Task UnreliableSizeSourceKeepsSizeOnlyBatchBlocked(string fault)
    {
        var fixture = new Fixture(includeDynamicSize: true);
        fixture.SetResult(fixture.Result with
        {
            SubjectGeometry = [],
            SizeTableCandidate = fault == "missing" ? null
                : new("asset_front", fault == "low_confidence" ? 0.79m : 0.99m,
                    ["尺码", "胸围"], [["M", "96"]]),
            Warnings = fault == "conflict" ? [new("size_source_conflict", "size_table", "conflicting sources")] : []
        });

        var projection = await fixture.ProjectAsync();

        var page = Assert.Single(Assert.Single(projection.Styles).Pages);
        Assert.Equal(AiPageUiStatus.BusinessError, page.Status);
        Assert.True(page.RequiresManualConfiguration);
        Assert.False(page.CanGenerate);
        Assert.False(projection.CanEnterGeneration);
        Assert.Equal(0, projection.ExecutablePageCount);
        Assert.Equal(1, projection.IsolatedPageCount);
        Assert.Empty(page.Requirements!);
    }

    private sealed class Fixture
    {
        public IAiAnalysisStore Store { get; } = Substitute.For<IAiAnalysisStore>();
        public IAiAnalysisResultStore Results { get; } = Substitute.For<IAiAnalysisResultStore>();
        public AiBatchProjectionSnapshot Snapshot { get; }
        public AiAnalysisRequestV1 Request { get; }
        public AiAnalysisResultV1 Result { get; }
        public AiRequestLedgerEntry Ledger { get; }

        public Fixture(int pageCount = 1, BatchSelectionMode selectionMode = BatchSelectionMode.Automatic,
            bool includeDynamicSize = false)
        {
            var now = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
            var pages = Enumerable.Range(0, pageCount).Select(index => TemplatePage.Create(
                $"page_{index}", "revision_projection", index, $"Page {index}",
                includeDynamicSize && index == pageCount - 1 ? TemplatePageContentType.DynamicSizeTable : TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct,
                null, true, "resource_projection")).ToArray();
            var bindings = pages.Where(page => page.ContentType == TemplatePageContentType.NormalComposition).Select((page, index) => FrameBinding.Create(
                $"frame_{index}", page.PageId, LayerPath.Create([index + 1]),
                ExpectedLayerEvidence.Create(index + 1, $"frame_{index}", true, false, false, false,
                    byte.MaxValue, "normal", LayerStructureSignature.Create("sha256:" + new string('a', 64))),
                [], SelectionRequirement.Create("Complete product view"),
                LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100)))).ToArray();
            var revision = TemplateRevision.Restore("revision_projection", "group_projection", 1,
                TemplateRevisionState.Committed, "templates/revision_projection/manifest.json",
                "sha256:" + new string('b', 64), now, pages, bindings);
            var template = BatchTemplateSnapshot.Create("snapshot_projection", "batch_projection", revision,
                pages.Select(page => page.PageId), now);
            var asset = SourceAssetMetadata.CreateReadable("asset_front", "style_projection", "front.jpg",
                "front.jpg", 128, new string('c', 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg");
            var batch = BatchDraft.Create("batch_projection", Path.Combine(Path.GetTempPath(), "projection-input"),
                    Path.Combine(Path.GetTempPath(), "projection-output"), template, selectionMode, false, now)
                .BeginScanning(now.AddTicks(1))
                .MarkScanReady(BatchInputType.SingleStyle,
                    [StyleDraft.Create("style_projection", "Product", "", 0, [asset.AssetId])], [asset],
                    "batches/batch_projection/asset-manifest/manifest.json", "sha256:" + new string('d', 64), now.AddTicks(2));
            Request = AiAnalysisInputProjector.Project(batch, revision, "style_projection",
                new AiAnalysisProjectionOptions("correlation_projection", "consent_projection",
                    "model/projection", "prompt/projection", "copy/projection"));
            Snapshot = new(batch, revision, "Projection templates", [Request],
                AiAnalysisSchedulingControlState.Running, null, false, null, true, null,
                ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty);
            Ledger = new(Request.RequestId, AiAnalysisCacheKeyFactory.Create(Request), batch.BatchId,
                "style_projection", "Validated", "results/first.json", now, now);
            Result = new(AiAnalysisProtocol.ResultSchemaVersion, "result", Request.RequestId, null, Request.ModelConfigVersion,
                bindings.Select(binding => new AiRequirementMatchV1(binding.FrameId, asset.AssetId, 1, 0.95m,
                    [AiTargetMatchEvidence.Exact])).ToArray(), [], [new(asset.AssetId, 0, 0, 1, 1, 0.95m)],
                null, [], new("usage_projection", 10, 10, 1, null, null));
            SetLedger(Ledger);
            SetResult(Result);
        }

        public void SetLedger(AiRequestLedgerEntry ledger) =>
            Store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(Request), Arg.Any<CancellationToken>())
                .Returns(ledger);

        public void SetResult(AiAnalysisResultV1 result) =>
            Results.LoadAsync("results/first.json", Arg.Any<CancellationToken>()).Returns(result);

        public void SetLockedSession(AiAnalysisSessionStatus status) =>
            Store.LoadSessionAsync($"session-{Request.RequestId}", Arg.Any<CancellationToken>())
                .Returns(AiAnalysisSession.Restore($"session-{Request.RequestId}", Request.BatchId, Request.StyleId,
                    Request.TemplateSnapshotId, "analysis_projection", status, 1,
                    [new("frame_0", "asset_front", 1)], Request.Requirements.Select(item => item.RequirementId),
                    Request.Assets.Select(item => item.AssetId), status != AiAnalysisSessionStatus.AiResultUnknown));

        public Task<AiBatchAnalysisProjection> ProjectAsync(AiBatchProjectionSnapshot? snapshot = null) =>
            new AiBatchProjectionService(new AiAnalysisResultReader(Store, Results))
                .BuildAsync(snapshot ?? Snapshot, CancellationToken.None);
    }
}
