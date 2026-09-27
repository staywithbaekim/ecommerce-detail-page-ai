using Desktop.Application.Ai;
using Desktop.Application.Inputs;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using NSubstitute;

namespace Desktop.Application.Tests;

public sealed class AiBatchCommandServiceTests
{
    [Fact]
    public async Task RepairLeaseKeepsAiCommandsOutsideTheResourcePreparationAndRegistrationWindow()
    {
        using var f = new Fixture();
        var now = DateTimeOffset.UtcNow;
        f.PersistedState = new([], AiAnalysisBudgetSnapshot.Empty, now, HistoricalRequests: [f.Request]);
        f.Store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(f.Request), Arg.Any<CancellationToken>())
            .Returns(new AiRequestLedgerEntry(f.Request.RequestId, AiAnalysisCacheKeyFactory.Create(f.Request),
                f.Batch.BatchId, f.Request.StyleId, "Validated", null, now, now));
        var batches = Substitute.For<IBatchRepository>();
        batches.GetAsync(f.Batch.BatchId, Arg.Any<CancellationToken>()).Returns(f.Batch);
        var templates = Substitute.For<ITemplateRepository>();
        templates.GetRevisionAsync(f.Revision.RevisionId, Arg.Any<CancellationToken>()).Returns(f.Revision);
        var factory = Substitute.For<IAiAnalysisSchedulerFactory>();
        factory.Create(f.Batch.BatchId).Returns(_ => new AiAnalysisScheduler(f.Provider, f.Store,
            new BoundedAiAnalysisWorkBuffer(AiAnalysisProductionPolicy.QueueV1), AiAnalysisProductionPolicy.BudgetV1,
            stateStore: f.State, stateKey: f.Batch.BatchId));
        using var workflow = new AiBatchAnalysisWorkflow(batches, templates, f.Store,
            Substitute.For<IAiAnalysisResultStore>(), f.State, factory, f.Consent, checkpointPort: f.Checkpoints);
        using var lease = await workflow.AcquireAsync(f.Batch.BatchId, default);
        using var cancellation = new CancellationTokenSource();
        var starting = workflow.StartAsync(f.Batch.BatchId, cancellation.Token);
        Assert.False(starting.IsCompleted);
        Assert.Empty(f.Consent.ReceivedCalls()); Assert.Empty(f.Copies.ReceivedCalls()); Assert.Empty(f.Provider.ReceivedCalls());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        lease.Dispose(); // Explicit release is idempotent; reacquisition proves no poisoned gate.
        using var next = await workflow.AcquireAsync(f.Batch.BatchId, default);
        Assert.Empty(f.Provider.ReceivedCalls());
    }

    [Theory]
    [InlineData("Validated", true)]
    [InlineData("ManualReview", true)]
    [InlineData("PlanClosed", true)]
    [InlineData("TerminalError", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Queued", false)]
    [InlineData("Uploading", false)]
    [InlineData("Analysing", false)]
    [InlineData("AiResultUnknown", false)]
    [InlineData("RetryableError", false)]
    [InlineData("unexpected", false)]
    public async Task RepairAdmissionRequiresEveryHistoricalRequestToBeSettled(string status, bool allowed)
    {
        using var f = new Fixture();
        var now = DateTimeOffset.UtcNow;
        f.PersistedState = new([], AiAnalysisBudgetSnapshot.Empty, now, HistoricalRequests: [f.Request]);
        f.Store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(f.Request), Arg.Any<CancellationToken>())
            .Returns(new AiRequestLedgerEntry(f.Request.RequestId, AiAnalysisCacheKeyFactory.Create(f.Request),
                f.Batch.BatchId, f.Request.StyleId, status, null, now, now));
        if (allowed) await f.Commands.RequireRepairIdleAsync(default);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => f.Commands.RequireRepairIdleAsync(default));
        Assert.Empty(f.Provider.ReceivedCalls());
        Assert.Empty(f.Copies.ReceivedCalls());
        Assert.Empty(f.Consent.ReceivedCalls());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("foreign")]
    [InlineData("pending")]
    [InlineData("missing-ledger")]
    [InlineData("recovery")]
    public async Task RepairAdmissionNeverTreatsMissingOrUnresolvedEvidenceAsIdle(string fault)
    {
        using var f = new Fixture();
        var now = DateTimeOffset.UtcNow;
        f.PersistedState = fault == "missing" ? null : new([], AiAnalysisBudgetSnapshot.Empty, now,
            HistoricalRequests: fault == "empty" ? [] : [fault == "foreign" ? f.Request with { BatchId = "other" } : f.Request]);
        if (fault == "pending") f.PersistedState = f.PersistedState! with
            { PendingItems = [new("pending", f.Request, AiAnalysisWorkStage.ProductJointUnderstanding, 0, now)] };
        if (fault != "missing-ledger") f.Store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(f.Request), Arg.Any<CancellationToken>())
            .Returns(new AiRequestLedgerEntry(f.Request.RequestId, AiAnalysisCacheKeyFactory.Create(f.Request),
                f.Batch.BatchId, f.Request.StyleId, fault == "pending" ? "Analysing" : "Validated", null, now, now));
        if (fault == "recovery") f.Checkpoints.RecoverAsync(f.Batch.BatchId, Arg.Any<CancellationToken>())
            .Returns(new AiAnalysisCheckpointResult(null, f.Batch.BatchId, AiAnalysisCheckpointOutcome.RecoveryRequired, "BROKEN"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Commands.RequireRepairIdleAsync(default));
        Assert.Empty(f.Provider.ReceivedCalls()); Assert.Empty(f.Copies.ReceivedCalls());
    }

    [Theory]
    [InlineData(false, "AI_UPLOAD_CONSENT_REQUIRED")]
    [InlineData(true, "AI_UPLOAD_CONSENT_REVOKED")]
    public async Task StartRequiresCurrentUploadConsentBeforeAdmission(bool revoked, string expected)
    {
        using var fixture = new Fixture();
        fixture.ConsentRecord = revoked
            ? fixture.ConsentRecord! with { State = AiUploadConsentState.Revoked }
            : null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Commands.StartAsync(CancellationToken.None));

        Assert.Equal(expected, error.Message);
        Assert.False(fixture.Commands.CaptureSnapshot().Started);
        Assert.Empty(fixture.Copies.ReceivedCalls());
        Assert.Empty(fixture.Provider.ReceivedCalls());
    }

    [Fact]
    public async Task UnscannedBatchBlocksBeforeConsentAndAdmission()
    {
        using var fixture = new Fixture(scanReady: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Commands.StartAsync(CancellationToken.None));

        Assert.Equal("AI_INPUT_BATCH_NOT_READY", error.Message);
        Assert.Empty(fixture.Consent.ReceivedCalls());
        Assert.Empty(fixture.Copies.ReceivedCalls());
    }

    [Fact]
    public async Task RecoveryFailureBlocksStartBeforeConsentButRefreshStillExposesReason()
    {
        using var fixture = new Fixture();
        fixture.Checkpoints.RecoverAsync(fixture.Batch.BatchId, Arg.Any<CancellationToken>())
            .Returns(new AiAnalysisCheckpointResult(null, fixture.Batch.BatchId,
                AiAnalysisCheckpointOutcome.RecoveryRequired, "AI_CHECKPOINT_TEST_FAILURE"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Commands.StartAsync(CancellationToken.None));
        await fixture.Commands.RefreshAsync(CancellationToken.None);

        Assert.Equal("AI_CHECKPOINT_TEST_FAILURE", error.Message);
        Assert.Equal(error.Message, fixture.Commands.CaptureSnapshot().SchedulerRestoreError);
        Assert.Empty(fixture.Consent.ReceivedCalls());
        Assert.Empty(fixture.Provider.ReceivedCalls());
        await fixture.Checkpoints.Received(1).RecoverAsync(fixture.Batch.BatchId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PauseOfflineResumeAndCancelKeepControlStateAndDoNotStartProvider()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        // A possibly submitted request remains recovery evidence, never ready
        // work. Cancelling the batch must preserve it without invoking AI.
        fixture.PersistedState = new(
            [new("pending_commands", fixture.Request, AiAnalysisWorkStage.ProductJointUnderstanding, 0, now)],
            AiAnalysisBudgetSnapshot.Empty, now, HistoricalRequests: [fixture.Request]);
        fixture.Store.FindRequestByFingerprintAsync(
                AiAnalysisCacheKeyFactory.Create(fixture.Request), Arg.Any<CancellationToken>())
            .Returns(new AiRequestLedgerEntry(fixture.Request.RequestId,
                AiAnalysisCacheKeyFactory.Create(fixture.Request), fixture.Batch.BatchId, "style_commands",
                "Analysing", null, now, now));
        await fixture.Commands.PauseAsync(CancellationToken.None);
        Assert.Equal(AiAnalysisSchedulingControlState.PauseRequested, fixture.Commands.CaptureSnapshot().ControlState);
        Assert.Equal("用户已暂停分析；可恢复继续。", fixture.PersistedState!.ControlReason);

        await fixture.Commands.WaitForNetworkAsync(CancellationToken.None);
        Assert.Equal(AiAnalysisSchedulingControlState.WaitingForNetwork, fixture.Commands.CaptureSnapshot().ControlState);
        Assert.Equal("网络暂不可用；恢复网络后可继续。", fixture.PersistedState!.ControlReason);

        await fixture.Commands.ResumeAsync(CancellationToken.None);
        Assert.Equal(AiAnalysisSchedulingControlState.Running, fixture.Commands.CaptureSnapshot().ControlState);
        await fixture.Commands.CancelAsync(CancellationToken.None);
        Assert.Equal(AiAnalysisSchedulingControlState.Cancelled, fixture.PersistedState!.ControlState);
        Assert.Single(fixture.PersistedState.PendingItems);
        Assert.Equal(fixture.Request.RequestId, Assert.Single(fixture.Commands.CaptureSnapshot().RecoveredInFlightRequestIds));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Commands.ResumeAsync(CancellationToken.None));
        Assert.Equal("AI_SCHEDULER_CANCELLED", error.Message);
        Assert.Empty(fixture.Provider.ReceivedCalls());
        await fixture.Checkpoints.Received(1).RecoverAsync(fixture.Batch.BatchId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshFiltersOtherBatchesAndReturnsSnapshotsUnaffectedByLaterRefresh()
    {
        using var fixture = new Fixture();
        await fixture.Commands.RefreshAsync(CancellationToken.None);
        var before = fixture.Commands.CaptureSnapshot();
        Assert.False(before.Started);
        Assert.StartsWith("consent-pending-", Assert.Single(before.Requests).ConsentId, StringComparison.Ordinal);
        var foreign = fixture.Request with { BatchId = "foreign", RequestId = "foreign_request" };
        fixture.PersistedState = new([], AiAnalysisBudgetSnapshot.Empty, DateTimeOffset.UtcNow,
            HistoricalRequests: [fixture.Request, foreign],
            TerminalErrors: new Dictionary<string, string>
            {
                [fixture.Request.RequestId] = "AI_CURRENT_ERROR",
                [foreign.RequestId] = "AI_FOREIGN_ERROR"
            });

        await fixture.Commands.RefreshAsync(CancellationToken.None);
        var after = fixture.Commands.CaptureSnapshot();

        Assert.True(after.Started);
        Assert.Equal(fixture.Request, Assert.Single(after.Requests));
        Assert.Equal("AI_CURRENT_ERROR", Assert.Single(after.TerminalErrors).Value);
        Assert.False(before.Started);
        Assert.Empty(before.TerminalErrors);
        Assert.NotEqual(Assert.Single(before.Requests).ConsentId, Assert.Single(after.Requests).ConsentId);
        Assert.Empty(fixture.Provider.ReceivedCalls());
    }

    [Theory]
    [InlineData(BatchSelectionMode.Manual, false)]
    [InlineData(BatchSelectionMode.Manual, true)]
    [InlineData(BatchSelectionMode.Automatic, true)]
    public async Task RefinementCannotUseAnAssetOutsideManualLocks(BatchSelectionMode mode, bool hasOtherLock)
    {
        using var fixture = new Fixture(selectionMode: mode);
        await fixture.Commands.RefreshAsync(CancellationToken.None);
        fixture.Store.LoadSessionAsync($"session-{fixture.Request.RequestId}", Arg.Any<CancellationToken>())
            .Returns(AiAnalysisSession.Restore($"session-{fixture.Request.RequestId}",
                fixture.Batch.BatchId, "style_commands", fixture.Request.TemplateSnapshotId, "analysis_commands",
                AiAnalysisSessionStatus.ManualReview, hasOtherLock ? 1 : 0,
                hasOtherLock ? [new("frame_commands", "asset_other", 1)] : [],
                ["frame_commands"], ["asset_front", "asset_other"], true));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Commands.AnalyzeSelectedAssetAsync("style_commands", "asset_front", false, CancellationToken.None));

        Assert.Equal("AI_REFINEMENT_SELECTION_NOT_LOCKED", error.Message);
        Assert.Empty(fixture.Provider.ReceivedCalls());
        Assert.DoesNotContain(fixture.Store.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IAiAnalysisStore.RecordRequestAsync));
    }

    [Fact]
    public async Task MissingSessionAndForeignSizeSourceKeepTheirExistingErrors()
    {
        using var fixture = new Fixture();
        await fixture.Commands.RefreshAsync(CancellationToken.None);

        var sessionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Commands.AnalyzeSelectedAssetAsync("style_commands", "asset_front", false, CancellationToken.None));
        var sourceError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Commands.SelectSizeTableSourceAsync("style_commands", "foreign_asset", CancellationToken.None));

        Assert.Equal("AI_SESSION_NOT_FOUND", sessionError.Message);
        Assert.Equal("AI_SIZE_TABLE_SOURCE_ASSET_INVALID", sourceError.Message);
        Assert.Empty(fixture.Provider.ReceivedCalls());
    }

    [Fact]
    public async Task RenderingRefreshDoesNotAddCheckpointRecovery()
    {
        using var fixture = new Fixture();
        await fixture.Commands.RefreshForRenderingAsync(CancellationToken.None);

        Assert.Single(fixture.Commands.CaptureRequests());
        Assert.Empty(fixture.Checkpoints.ReceivedCalls());
        Assert.Empty(fixture.Copies.ReceivedCalls());
        Assert.Empty(fixture.Provider.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartReturnsDuringCopyPreparationStartsAdmissionOnceAndDrainsOnStop(bool cancelBatch)
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Copies.PrepareAsync(Arg.Any<AiAnalysisCopyPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                    return null!;
                }
                finally
                {
                    cancelled.TrySetResult();
                }
            });

        await fixture.Commands.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Commands.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cancelled.Task.IsCompleted);
        Assert.True(fixture.Commands.CaptureSnapshot().Started);
        if (cancelBatch)
        {
            await fixture.Commands.CancelAsync(CancellationToken.None);
        }
        else
        {
            fixture.Shutdown.Cancel();
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Shutdown.Cancel();
        fixture.Commands.StopAdmission();
        fixture.Commands.WaitForBackgroundCompletion();

        Assert.Null(fixture.Commands.CaptureSnapshot().AdmissionError);
        await fixture.Copies.Received(1).PrepareAsync(
            Arg.Any<AiAnalysisCopyPreparationRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(fixture.Provider.ReceivedCalls());
        if (cancelBatch)
        {
            // No request has entered the scheduler yet; the existing cancel
            // contract stops admission without inventing a durable request.
            Assert.Equal(AiAnalysisSchedulingControlState.Running, fixture.Commands.CaptureSnapshot().ControlState);
            Assert.Empty(fixture.PersistedState?.PendingItems ?? []);
        }
    }

    [Theory]
    [InlineData(0, "AI_COPY_TEST_FAILURE")]
    [InlineData(1, "AI_COPY_TEST_FAILURE")]
    [InlineData(2, "AI_COPY_TEST_FAILURE")]
    [InlineData(3, "AI_ANALYSIS_ADMISSION_FAILED")]
    public async Task AdmissionErrorsRetainSafeCodesAndHideUnclassifiedExceptionText(int kind, string expected)
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception error = kind switch
        {
            0 => new InvalidOperationException("AI_COPY_TEST_FAILURE"),
            1 => new InvalidDataException("AI_COPY_TEST_FAILURE"),
            2 => new IOException("AI_COPY_TEST_FAILURE"),
            _ => new InvalidOperationException("private-local-path")
        };
        fixture.Copies.PrepareAsync(Arg.Any<AiAnalysisCopyPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                entered.TrySetResult();
                return Task.FromException<AiAnalysisCopyMetadata>(error);
            });

        await fixture.Commands.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Shutdown.Cancel();
        fixture.Commands.WaitForBackgroundCompletion();

        Assert.Equal(expected, fixture.Commands.CaptureSnapshot().AdmissionError);
        Assert.Empty(fixture.Provider.ReceivedCalls());
    }

    private sealed class Fixture : IDisposable
    {
        public IAiAnalysisStore Store { get; } = Substitute.For<IAiAnalysisStore>();
        public IAiAnalysisSchedulingStateStore State { get; } = Substitute.For<IAiAnalysisSchedulingStateStore>();
        public IAiAnalysisCheckpointPort Checkpoints { get; } = Substitute.For<IAiAnalysisCheckpointPort>();
        public IConsentPort Consent { get; } = Substitute.For<IConsentPort>();
        public IAiAnalysisCopyPreparer Copies { get; } = Substitute.For<IAiAnalysisCopyPreparer>();
        public IAiServicePort Provider { get; } = Substitute.For<IAiServicePort>();
        public CancellationTokenSource Shutdown { get; } = new();
        public AiAnalysisSchedulingState? PersistedState { get; set; }
        public AiUploadConsentRecord? ConsentRecord { get; set; }
        public BatchDraft Batch { get; }
        public TemplateRevision Revision { get; }
        public AiAnalysisRequestV1 Request { get; }
        public AiBatchCommandService Commands { get; }

        public Fixture(bool scanReady = true, BatchSelectionMode selectionMode = BatchSelectionMode.Automatic)
        {
            var now = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
            var page = TemplatePage.Create("page_commands", "revision_commands", 0, "Page",
                TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true,
                "resource_commands");
            var binding = FrameBinding.Create("frame_commands", page.PageId, LayerPath.Create([1]),
                ExpectedLayerEvidence.Create(1, "frame", true, false, false, false, byte.MaxValue, "normal",
                    LayerStructureSignature.Create("sha256:" + new string('a', 64))), [],
                SelectionRequirement.Create("front"), LayoutIntent.Create("subject_complete", 1, 0, 0,
                    TemplateRect.Create(0, 0, 100, 100)));
            var revision = TemplateRevision.Restore("revision_commands", "group_commands", 1,
                TemplateRevisionState.Committed, "templates/revision_commands/manifest.json",
                "sha256:" + new string('b', 64), now, [page], [binding]);
            Revision = revision;
            var template = BatchTemplateSnapshot.Create("snapshot_commands", "batch_commands", revision,
                [page.PageId], now);
            var asset = SourceAssetMetadata.CreateReadable("asset_front", "style_commands", "front.jpg",
                "front.jpg", 128, new string('c', 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg");
            var draft = BatchDraft.Create("batch_commands", Path.Combine(Path.GetTempPath(), "commands-input"),
                Path.Combine(Path.GetTempPath(), "commands-output"), template, selectionMode, false, now);
            var ready = draft.BeginScanning(now.AddTicks(1)).MarkScanReady(BatchInputType.SingleStyle,
                [StyleDraft.Create("style_commands", "Product", "", 0, [asset.AssetId])], [asset],
                "batches/batch_commands/asset-manifest/manifest.json", "sha256:" + new string('d', 64),
                now.AddTicks(2));
            Batch = scanReady ? ready : draft;
            Request = AiAnalysisInputProjector.Project(ready, revision, "style_commands",
                new AiAnalysisProjectionOptions("wpf-batch_commands", "consent_admitted",
                    AiAnalysisProductionPolicy.ModelConfigVersionV2, AiAnalysisProductionPolicy.PromptContractVersionV3,
                    AiAnalysisCopyProfiles.WicBoundedV4));
            ConsentRecord = new(AiConsentScopes.AnalysisCopy, Request.ConsentId, AiUploadConsentState.Granted,
                AiConsentPolicy.Version, now);
            Consent.GetConsentAsync(AiConsentScopes.AnalysisCopy, Arg.Any<CancellationToken>())
                .Returns(_ => ConsentRecord);
            State.LoadSchedulingStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => PersistedState);
            State.SaveSchedulingStateAsync(Arg.Any<string>(), Arg.Any<AiAnalysisSchedulingState>(),
                Arg.Any<CancellationToken>()).Returns(call =>
                {
                    PersistedState = call.Arg<AiAnalysisSchedulingState>();
                    return Task.CompletedTask;
                });
            Checkpoints.RecoverAsync(Batch.BatchId, Arg.Any<CancellationToken>())
                .Returns(new AiAnalysisCheckpointResult(null, Batch.BatchId,
                    AiAnalysisCheckpointOutcome.NothingToRecover, "none"));
            var scheduler = new AiAnalysisScheduler(Provider, Store,
                new BoundedAiAnalysisWorkBuffer(AiAnalysisProductionPolicy.QueueV1),
                AiAnalysisProductionPolicy.BudgetV1, stateStore: State, stateKey: Batch.BatchId,
                copyPreparer: Copies);
            Commands = new(Batch, revision, "Commands", scheduler, Store, State, Consent,
                Shutdown.Token, Checkpoints);
        }

        public void Dispose()
        {
            Shutdown.Cancel();
            Commands.StopAdmission();
            Commands.WaitForBackgroundCompletion();
            Commands.Dispose();
            Shutdown.Dispose();
        }
    }
}
