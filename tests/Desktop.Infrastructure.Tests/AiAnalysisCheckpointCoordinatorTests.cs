using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Application.Files;
using Desktop.Domain.Ai;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class AiAnalysisCheckpointCoordinatorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CommitsFullBatchGraphAndRecoversAfterRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        AiAnalysisCheckpointCommand command;

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var first = await SeedQueuedAsync(store, "batch_01", "a", 0);
            var second = await SeedQueuedAsync(store, "batch_01", "b", 10);
            command = Command("checkpoint_01", [Graph(first), Graph(second)]);
            var coordinator = new AiAnalysisCheckpointCoordinator(
                store, new AiAnalysisStateManifestStore(files), TimeProvider.System);

            var result = await coordinator.CommitAsync(command, CancellationToken.None);

            Assert.True(result.Outcome == AiAnalysisCheckpointOutcome.Committed, result.ReasonCode);
            Assert.Equal("AI_CHECKPOINT_COMMITTED", result.ReasonCode);
            var journal = await store.FindCheckpointAsync("checkpoint_01", CancellationToken.None);
            Assert.Equal(AiAnalysisCheckpointJournalState.Committed, journal!.State);
            Assert.Equal(2, journal.Items.Count);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restarted = new AiAnalysisCheckpointCoordinator(
            reopened,
            new AiAnalysisStateManifestStore(new AtomicFileStore(new LocalAppPathPolicy(directory.Path))),
            TimeProvider.System);

        var recovered = await restarted.RecoverAsync("batch_01", CancellationToken.None);
        var duplicate = await restarted.CommitAsync(command, CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, recovered.Outcome);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, duplicate.Outcome);
    }

    [Theory]
    [InlineData(AiAnalysisSessionStatus.AiResultUnknown)]
    [InlineData(AiAnalysisSessionStatus.TerminalError)]
    [InlineData(AiAnalysisSessionStatus.Cancelled)]
    public async Task FailureCheckpointAllowsUsageAndCacheToBeAbsent(AiAnalysisSessionStatus status)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var current = await SeedAnalysingAsync(store, "batch_01", "failure", 0);
        var desired = Advance(current, status, 3);
        var command = Command("checkpoint_failure", [Graph(current, desired)]);
        var coordinator = Coordinator(store, directory.Path);

        var result = await coordinator.CommitAsync(command, CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, result.Outcome);
        Assert.Equal(status, (await store.LoadSessionAsync(desired.Session.SessionId, CancellationToken.None))!.Status);
        Assert.Empty(await store.LoadUsageAsync(desired.Request.RequestId, CancellationToken.None));
    }

    [Fact]
    public async Task ValidatedCheckpointMayOmitUsageAndCache()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var current = await SeedAnalysingAsync(store, "batch_01", "validated", 0);
        var desired = Advance(current, AiAnalysisSessionStatus.Validated, 3);
        var command = Command("checkpoint_validated", [Graph(current, desired)]);

        var result = await Coordinator(store, directory.Path).CommitAsync(command, CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, result.Outcome);
        Assert.Empty(command.Manifest.CacheKeys);
    }

    [Fact]
    public async Task UsageMayExistWithoutCache()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var current = await SeedAnalysingAsync(store, "batch_01", "usage", 0);
        var desired = Advance(current, AiAnalysisSessionStatus.TerminalError, 3);
        var usage = Usage(desired.Request.RequestId, "usage_01", Origin.AddSeconds(4));
        var command = Command("checkpoint_usage", [Graph(current, desired, usage)]);

        var result = await Coordinator(store, directory.Path).CommitAsync(command, CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, result.Outcome);
        Assert.Single(await store.LoadUsageAsync(desired.Request.RequestId, CancellationToken.None));
        Assert.Empty(command.Manifest.CacheKeys);
    }

    [Fact]
    public async Task ValidatedCheckpointClosesUsageCacheAndResultReferences()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var current = await SeedAnalysingAsync(store, "batch_01", "cache", 0);
        var desired = Advance(current, AiAnalysisSessionStatus.Validated, 3);
        var usage = Usage(desired.Request.RequestId, "usage_01", Origin.AddSeconds(4));
        var cache = Cache(usage, desired.Request.ResultReference!, Origin.AddSeconds(5));
        var command = Command("checkpoint_cache", [Graph(current, desired, usage, cache)]);

        var result = await Coordinator(store, directory.Path).CommitAsync(command, CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, result.Outcome);
        Assert.Equal(cache, await store.FindCacheReferenceAsync(cache.CacheKey, CancellationToken.None));
    }

    [Fact]
    public async Task SchedulerStateIsCommittedWithCheckpointAndRestoredAsOneGraph()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        AiAnalysisCheckpointCommand command;
        AiAnalysisSchedulingState schedulerState;
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var now = Origin.AddSeconds(2);
            var seeded = await SeedSchedulerQueuedAsync(store, "batch_01", "scheduled", now);
            var request = SchedulerRequest(seeded.Request.RequestId, seeded.Request.BatchId, seeded.Request.StyleId, now);
            var item = new AiAnalysisWorkItem(
                $"{request.RequestId}-productjointunderstanding",
                request,
                AiAnalysisWorkStage.ProductJointUnderstanding,
                1,
                now);
            schedulerState = new AiAnalysisSchedulingState(
                [item],
                new AiAnalysisBudgetSnapshot(1, 100, 10, 0.01m, false, false),
                now);
            command = CommandWithScheduler(
                "checkpoint_scheduler",
                [Graph(seeded)],
                "ai-s3-default",
                schedulerState);

            var result = await Coordinator(store, directory.Path)
                .CommitAsync(command, CancellationToken.None);
            Assert.Equal(AiAnalysisCheckpointOutcome.Committed, result.Outcome);

            var journal = await store.FindCheckpointAsync(command.CheckpointId, CancellationToken.None);
            Assert.Equal("ai-s3-default", journal!.SchedulerStateKey);
            Assert.Equal(
                AiAnalysisCheckpointRules.ComputeSchedulerStateDigest(schedulerState),
                journal.SchedulerStateDigest);
            var persisted = await store.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None);
            Assert.Equal(schedulerState.Budget, persisted!.Budget);
            Assert.Single(persisted.PendingItems);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var recovered = await Coordinator(reopened, directory.Path)
            .RecoverAsync("batch_01", CancellationToken.None);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, recovered.Outcome);
        var restored = await reopened.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None);
        Assert.Equal(schedulerState.Budget, restored!.Budget);
        Assert.Equal(schedulerState.PendingItems.Single().WorkItemId, restored.PendingItems.Single().WorkItemId);
    }

    [Fact]
    public async Task SchedulerCheckpointRejectsQueuedGraphOmittedFromPendingSnapshot()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var seeded = await SeedQueuedAsync(store, "batch_01", "scheduler_omitted", 0);
        var now = Origin.AddSeconds(2);
        var state = new AiAnalysisSchedulingState(
            [],
            new AiAnalysisBudgetSnapshot(0, 0, 0, 0m, false, false),
            now);
        var command = CommandWithScheduler(
            "checkpoint_scheduler_omitted",
            [Graph(seeded)],
            "ai-s3-default",
            state);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Coordinator(store, directory.Path).CommitAsync(command, CancellationToken.None));
        Assert.Equal("AI_CHECKPOINT_SCHEDULER_PENDING_OMITTED", exception.Message);
        Assert.Null(await store.FindCheckpointAsync(command.CheckpointId, CancellationToken.None));
        Assert.Null(await store.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None));
    }

    [Fact]
    public async Task SchedulerSnapshotRollsBackWithCheckpointTransactionOnJournalFailure()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var now = Origin.AddSeconds(2);
        var seeded = await SeedSchedulerQueuedAsync(store, "batch_01", "scheduler_failure", now);
        var request = SchedulerRequest(seeded.Request.RequestId, seeded.Request.BatchId, seeded.Request.StyleId, now);
        var schedulerState = new AiAnalysisSchedulingState(
            [new AiAnalysisWorkItem(
                $"{request.RequestId}-productjointunderstanding",
                request,
                AiAnalysisWorkStage.ProductJointUnderstanding,
                0,
                now)],
            new AiAnalysisBudgetSnapshot(1, 1, 1, 0.01m, false, false),
            now);
        var command = CommandWithScheduler("checkpoint_scheduler_failure", [Graph(seeded)], "ai-s3-default", schedulerState);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_scheduler_checkpoint BEFORE INSERT ON ai_analysis_checkpoint_journal BEGIN SELECT RAISE(ABORT, 'TEST_SCHEDULER_CHECKPOINT_FAILURE'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            Coordinator(store, directory.Path).CommitAsync(command, CancellationToken.None));
        Assert.Null(await store.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None));
        Assert.Null(await store.FindCheckpointAsync(command.CheckpointId, CancellationToken.None));
    }

    [Fact]
    public async Task NewCheckpointCannotOmitPriorSchedulerOrRollBackBudget()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var now = Origin.AddSeconds(2);
        var seeded = await SeedSchedulerQueuedAsync(store, "batch_01", "scheduler_history", now);
        var item = new AiAnalysisWorkItem(
            $"{seeded.Request.RequestId}-productjointunderstanding",
            SchedulerRequest(seeded.Request.RequestId, seeded.Request.BatchId, seeded.Request.StyleId, now),
            AiAnalysisWorkStage.ProductJointUnderstanding,
            0,
            now);
        var firstState = new AiAnalysisSchedulingState(
            [item],
            new AiAnalysisBudgetSnapshot(2, 100, 20, 0.20m, true, false),
            now);
        var coordinator = Coordinator(store, directory.Path);
        var first = CommandWithScheduler(
            "checkpoint_scheduler_history",
            [Graph(seeded)],
            "ai-s3-default",
            firstState);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(first, CancellationToken.None)).Outcome);

        var rolledBackBudget = firstState with
        {
            Budget = firstState.Budget with { RequestCount = 1, InputTokens = 10, EstimatedCost = 0.01m },
            UpdatedAt = firstState.UpdatedAt.AddSeconds(1)
        };
        var stale = CommandWithScheduler(
            "checkpoint_scheduler_budget_rollback",
            [Graph(seeded)],
            "ai-s3-default",
            rolledBackBudget) with
        {
            CreatedAt = first.CreatedAt.AddSeconds(1)
        };
        stale = stale with { Manifest = stale.Manifest with { CreatedAt = stale.CreatedAt } };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(stale, CancellationToken.None));
        Assert.Equal("AI_CHECKPOINT_PRIOR_SCHEDULER_BUDGET_ROLLBACK", exception.Message);

        var omitted = Command("checkpoint_scheduler_omitted_history", [Graph(seeded)]) with
        {
            CreatedAt = first.CreatedAt.AddSeconds(2)
        };
        omitted = omitted with { Manifest = omitted.Manifest with { CreatedAt = omitted.CreatedAt } };
        var omittedException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(omitted, CancellationToken.None));
        Assert.Equal("AI_CHECKPOINT_PRIOR_SCHEDULER_OMITTED", omittedException.Message);
        Assert.Equal("checkpoint_scheduler_history",
            (await store.FindLatestCommittedCheckpointAsync("batch_01", CancellationToken.None))!.CheckpointId);
    }

    [Fact]
    public async Task RejectsCacheWithoutUsageAndManifestMissingHistoricalGraph()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var first = await SeedQueuedAsync(store, "batch_01", "a", 0);
        var second = await SeedQueuedAsync(store, "batch_01", "b", 10);
        var orphanCache = new AiCacheReference(
            Digest('c'), "results/result.json", "consent_01", "model_v1", "usage_01", Origin.AddSeconds(2));
        var cacheWithoutUsage = Command(
            "checkpoint_bad_cache",
            [Graph(first, first, null, orphanCache)]);
        var full = Command("checkpoint_full", [Graph(first), Graph(second)]);
        var missingHistorical = full with
        {
            Manifest = full.Manifest with
            {
                SessionIds = [first.Session.SessionId],
                RequestIds = [first.Request.RequestId]
            }
        };
        var coordinator = Coordinator(store, directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(cacheWithoutUsage, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(missingHistorical, CancellationToken.None));
        Assert.Null(await store.FindCheckpointAsync("checkpoint_bad_cache", CancellationToken.None));
        Assert.Null(await store.FindCheckpointAsync("checkpoint_full", CancellationToken.None));
    }

    [Theory]
    [InlineData("result")]
    [InlineData("model")]
    [InlineData("time")]
    public async Task RejectsCacheClosureMismatch(string mismatch)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var current = await SeedAnalysingAsync(store, "batch_01", "closure", 0);
        var desired = Advance(current, AiAnalysisSessionStatus.Validated, 3);
        var usage = Usage(desired.Request.RequestId, "usage_01", Origin.AddSeconds(4));
        var cache = Cache(usage, desired.Request.ResultReference!, Origin.AddSeconds(5));
        cache = mismatch switch
        {
            "result" => cache with { ResultReference = "results/other.json" },
            "model" => cache with { ModelConfigVersion = "model_v2" },
            _ => cache with { CreatedAt = usage.RecordedAt.AddSeconds(-1) }
        };
        var command = Command("checkpoint_bad_closure", [Graph(current, desired, usage, cache)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Coordinator(store, directory.Path).CommitAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task SqliteGraphAndPendingJournalRollBackTogetherOnSecondGraphConflict()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var first = await SeedQueuedAsync(store, "batch_01", "a", 0);
        var second = await SeedQueuedAsync(store, "batch_01", "b", 10);
        var firstDesired = Advance(first, AiAnalysisSessionStatus.Cancelled, 1);
        var secondDesired = Advance(second, AiAnalysisSessionStatus.Cancelled, 11);
        var command = Command("checkpoint_tx_failure", [
            Graph(first, firstDesired),
            new AiAnalysisCheckpointGraph(
                secondDesired.Request,
                AiAnalysisSessionStatus.Uploading.ToString(),
                secondDesired.Session,
                AiAnalysisSessionStatus.Uploading,
                0,
                null,
                null)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Coordinator(store, directory.Path).CommitAsync(command, CancellationToken.None));

        Assert.Equal(AiAnalysisSessionStatus.Queued,
            (await store.LoadSessionAsync(first.Session.SessionId, CancellationToken.None))!.Status);
        Assert.Null(await store.FindCheckpointAsync("checkpoint_tx_failure", CancellationToken.None));
        Assert.Null(await new AiAnalysisStateManifestStore(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)))
            .InspectAsync("batch_01", CancellationToken.None));
    }

    [Fact]
    public async Task CheckpointCannotSkipManualReviewWhileAddingSelectionLock()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var analysing = await SeedAnalysingAsync(store, "batch_01", "selection", 0);
        var resultReference = $"results/{analysing.Request.RequestId}.json";
        var validatedRequest = await store.UpdateRequestStatusAsync(
            analysing.Request.RequestId,
            analysing.Request.Status,
            AiAnalysisSessionStatus.Validated.ToString(),
            resultReference,
            Origin.AddSeconds(3),
            CancellationToken.None);
        var validatedSession = analysing.Session.AdvanceTo(AiAnalysisSessionStatus.Validated);
        await store.SaveSessionAsync(
            validatedSession,
            CancellationToken.None,
            analysing.Session.SelectionRevision);
        var current = new GraphState(validatedRequest, validatedSession);
        var forbiddenSession = AiAnalysisSession.Restore(
            validatedSession.SessionId,
            validatedSession.BatchId,
            validatedSession.StyleId,
            validatedSession.TemplateSnapshotId,
            validatedSession.AnalysisKey,
            AiAnalysisSessionStatus.PlanClosed,
            1,
            [new AiLockedSelection("requirement_selection", "asset_selection", 1)],
            validatedSession.AllowedRequirementIds,
            validatedSession.AllowedAssetIds,
            true);
        var forbidden = new GraphState(
            validatedRequest with
            {
                Status = AiAnalysisSessionStatus.PlanClosed.ToString(),
                UpdatedAt = Origin.AddSeconds(4)
            },
            forbiddenSession);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Coordinator(store, directory.Path).CommitAsync(
                Command("checkpoint_skip_review", [Graph(current, forbidden)]),
                CancellationToken.None));

        Assert.Equal("AI_CHECKPOINT_SESSION_SELECTION_TRANSITION_INVALID", exception.Message);
        Assert.Equal(AiAnalysisSessionStatus.Validated,
            (await store.LoadSessionAsync(validatedSession.SessionId, CancellationToken.None))!.Status);
        Assert.Null(await store.FindCheckpointAsync("checkpoint_skip_review", CancellationToken.None));
    }

    [Fact]
    public async Task SqliteJournalWriteFailureRollsBackGraphAndOwnedStage()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var current = await SeedAnalysingAsync(store, "batch_01", "sqlite", 0);
        var desired = Advance(current, AiAnalysisSessionStatus.TerminalError, 3);
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_checkpoint_insert BEFORE INSERT ON ai_analysis_checkpoint_journal BEGIN SELECT RAISE(ABORT, 'TEST_CHECKPOINT_FAILURE'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            Coordinator(store, directory.Path).CommitAsync(
                Command("checkpoint_sqlite_failure", [Graph(current, desired)]),
                CancellationToken.None));

        Assert.Equal(AiAnalysisSessionStatus.Analysing,
            (await store.LoadSessionAsync(current.Session.SessionId, CancellationToken.None))!.Status);
        Assert.Null(await store.FindCheckpointAsync("checkpoint_sqlite_failure", CancellationToken.None));
        Assert.Null(await new AiAnalysisStateManifestStore(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)))
            .InspectAsync("batch_01", CancellationToken.None));
    }

    [Fact]
    public async Task PendingWithStagedManifestResumesAfterRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var seeded = await SeedQueuedAsync(store, "batch_01", "staged", 0);
            var manifests = new AiAnalysisStateManifestStore(
                new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));
            var failing = new ThrowingManifestStore(manifests, ThrowPoint.BeforeCommit);
            var result = await new AiAnalysisCheckpointCoordinator(store, failing, TimeProvider.System)
                .CommitAsync(Command("checkpoint_staged", [Graph(seeded)]), CancellationToken.None);
            Assert.Equal(AiAnalysisCheckpointOutcome.RecoveryRequired, result.Outcome);
            Assert.Equal(AiAnalysisCheckpointJournalState.Pending,
                (await store.FindCheckpointAsync("checkpoint_staged", CancellationToken.None))!.State);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var recovered = await Coordinator(reopened, directory.Path)
            .RecoverAsync("batch_01", CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, recovered.Outcome);
    }

    [Fact]
    public async Task PendingWithCommittedManifestFinalizesAfterRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var seeded = await SeedQueuedAsync(store, "batch_01", "pending", 0);
            var failingDatabase = new ThrowingCheckpointDatabase(store, ThrowPoint.BeforeFinalize);
            var result = await new AiAnalysisCheckpointCoordinator(
                failingDatabase,
                new AiAnalysisStateManifestStore(new AtomicFileStore(new LocalAppPathPolicy(directory.Path))),
                TimeProvider.System)
                .CommitAsync(Command("checkpoint_pending", [Graph(seeded)]), CancellationToken.None);
            Assert.Equal(AiAnalysisCheckpointOutcome.RecoveryRequired, result.Outcome);
            Assert.Equal(AiAnalysisCheckpointJournalState.Pending,
                (await store.FindCheckpointAsync("checkpoint_pending", CancellationToken.None))!.State);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var recovered = await Coordinator(reopened, directory.Path)
            .RecoverAsync("batch_01", CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, recovered.Outcome);
    }

    [Fact]
    public async Task ManifestWrittenThenExceptionAndDatabaseWrittenThenExceptionAreReconciled()
    {
        using var firstDirectory = new TestDirectory();
        await using (var store = new SqliteDesktopStore(new DesktopDataOptions(firstDirectory.Path)))
        {
            await store.InitializeAsync();
            var seeded = await SeedQueuedAsync(store, "batch_01", "marker", 0);
            var inner = new AiAnalysisStateManifestStore(
                new AtomicFileStore(new LocalAppPathPolicy(firstDirectory.Path)));
            var result = await new AiAnalysisCheckpointCoordinator(
                store,
                new ThrowingManifestStore(inner, ThrowPoint.AfterCommit),
                TimeProvider.System)
                .CommitAsync(Command("checkpoint_marker_unknown", [Graph(seeded)]), CancellationToken.None);
            Assert.Equal(AiAnalysisCheckpointOutcome.Committed, result.Outcome);
        }

        using var secondDirectory = new TestDirectory();
        await using var secondStore = new SqliteDesktopStore(new DesktopDataOptions(secondDirectory.Path));
        await secondStore.InitializeAsync();
        var secondSeed = await SeedQueuedAsync(secondStore, "batch_02", "database", 0);
        var databaseUnknown = new ThrowingCheckpointDatabase(secondStore, ThrowPoint.AfterPrepare);
        var databaseResult = await new AiAnalysisCheckpointCoordinator(
            databaseUnknown,
            new AiAnalysisStateManifestStore(new AtomicFileStore(new LocalAppPathPolicy(secondDirectory.Path))),
            TimeProvider.System)
            .CommitAsync(Command("checkpoint_database_unknown", [Graph(secondSeed)]), CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, databaseResult.Outcome);

        using var thirdDirectory = new TestDirectory();
        await using var thirdStore = new SqliteDesktopStore(new DesktopDataOptions(thirdDirectory.Path));
        await thirdStore.InitializeAsync();
        var thirdSeed = await SeedQueuedAsync(thirdStore, "batch_03", "finalize", 0);
        var finalizeUnknown = new ThrowingCheckpointDatabase(thirdStore, ThrowPoint.AfterFinalize);
        var finalizeResult = await new AiAnalysisCheckpointCoordinator(
            finalizeUnknown,
            new AiAnalysisStateManifestStore(new AtomicFileStore(new LocalAppPathPolicy(thirdDirectory.Path))),
            TimeProvider.System)
            .CommitAsync(Command("checkpoint_finalize_unknown", [Graph(thirdSeed)]), CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed, finalizeResult.Outcome);
        Assert.Equal("AI_CHECKPOINT_COMMITTED_AFTER_DATABASE_UNKNOWN", finalizeResult.ReasonCode);
    }

    [Fact]
    public async Task DirectCommitRejectsCommittedManifestWithoutJournalAndPreservesMarker()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var seeded = await SeedQueuedAsync(store, "batch_01", "manifest_only", 0);
        var manifests = new AiAnalysisStateManifestStore(files);
        var manifestOnly = Command("checkpoint_manifest_only", [Graph(seeded)]).Manifest;
        var staged = await manifests.StageAsync(manifestOnly, CancellationToken.None);
        await manifests.CommitAsync(staged, CancellationToken.None);
        var markerPath = "batches/batch_01/ai-analysis/state/committed.marker";
        var markerBefore = await files.ReadAsync(markerPath, CancellationToken.None);
        var next = Command("checkpoint_next", [Graph(seeded)]) with
        {
            CreatedAt = manifestOnly.CreatedAt.AddSeconds(1)
        };
        next = next with { Manifest = next.Manifest with { CreatedAt = next.CreatedAt } };

        var result = await new AiAnalysisCheckpointCoordinator(store, manifests, TimeProvider.System)
            .CommitAsync(next, CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("AI_CHECKPOINT_COMMITTED_MANIFEST_WITHOUT_JOURNAL", result.ReasonCode);
        Assert.Equal(markerBefore, await files.ReadAsync(markerPath, CancellationToken.None));
        Assert.Null(await store.FindCheckpointAsync("checkpoint_next", CancellationToken.None));
    }

    [Fact]
    public async Task DirectCommitRejectsCommittedJournalWithMissingManifestAndPreservesMarker()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var seeded = await SeedQueuedAsync(store, "batch_01", "journal_only", 0);
        var coordinator = new AiAnalysisCheckpointCoordinator(
            store, new AiAnalysisStateManifestStore(files), TimeProvider.System);
        var first = Command("checkpoint_first", [Graph(seeded)]);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(first, CancellationToken.None)).Outcome);
        var inspection = await new AiAnalysisStateManifestStore(files)
            .InspectAsync("batch_01", CancellationToken.None);
        var markerPath = "batches/batch_01/ai-analysis/state/committed.marker";
        var markerBefore = await files.ReadAsync(markerPath, CancellationToken.None);
        Assert.True(await files.DeleteAsync(inspection!.CommittedManifestReference!, CancellationToken.None));
        var next = Command("checkpoint_next", [Graph(seeded)]) with
        {
            CreatedAt = first.CreatedAt.AddSeconds(1)
        };
        next = next with { Manifest = next.Manifest with { CreatedAt = next.CreatedAt } };

        var result = await coordinator.CommitAsync(next, CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("AI_CHECKPOINT_MANIFEST_INSPECTION_FAILED", result.ReasonCode);
        Assert.Equal(markerBefore, await files.ReadAsync(markerPath, CancellationToken.None));
        Assert.Null(await store.FindCheckpointAsync("checkpoint_next", CancellationToken.None));
    }

    [Fact]
    public async Task OrphanStageRollsBackAndCommittedManifestMismatchRequiresRecovery()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var manifests = new AiAnalysisStateManifestStore(files);
        await manifests.StageAsync(new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version,
            "orphan_01",
            "batch_orphan",
            ["session_orphan"],
            ["request_orphan"],
            [],
            Origin), CancellationToken.None);
        var coordinator = new AiAnalysisCheckpointCoordinator(store, manifests, TimeProvider.System);

        var orphan = await coordinator.RecoverAsync("batch_orphan", CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.RolledBackOrphan, orphan.Outcome);
        Assert.Null(await manifests.InspectAsync("batch_orphan", CancellationToken.None));

        var seeded = await SeedQueuedAsync(store, "batch_01", "mismatch", 0);
        var command = Command("checkpoint_mismatch", [Graph(seeded)]);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(command, CancellationToken.None)).Outcome);
        var inspection = await manifests.InspectAsync("batch_01", CancellationToken.None);
        Assert.True(await files.DeleteAsync(inspection!.CommittedManifestReference!, CancellationToken.None));

        var mismatch = await coordinator.RecoverAsync("batch_01", CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.RecoveryRequired, mismatch.Outcome);
    }

    [Fact]
    public async Task CorruptJournalIsReportedAsRecoveryRequired()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var seeded = await SeedQueuedAsync(store, "batch_01", "corrupt", 0);
        var coordinator = Coordinator(store, directory.Path);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(
                Command("checkpoint_corrupt", [Graph(seeded)]),
                CancellationToken.None)).Outcome);
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE ai_analysis_checkpoint_journal SET payload_digest='not-a-digest' WHERE checkpoint_id='checkpoint_corrupt';";
            await corrupt.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.FindCheckpointAsync("checkpoint_corrupt", CancellationToken.None));
        var recovered = await coordinator.RecoverAsync("batch_01", CancellationToken.None);

        Assert.Equal(AiAnalysisCheckpointOutcome.RecoveryRequired, recovered.Outcome);
        Assert.Equal("AI_CHECKPOINT_JOURNAL_READ_FAILED", recovered.ReasonCode);
    }

    [Fact]
    public async Task SameCheckpointPayloadIsIdempotentButDifferentPayloadConflicts()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var seeded = await SeedQueuedAsync(store, "batch_01", "same", 0);
        var command = Command("checkpoint_same", [Graph(seeded)]);
        var coordinator = Coordinator(store, directory.Path);

        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(command, CancellationToken.None)).Outcome);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(command, CancellationToken.None)).Outcome);

        var conflicting = command with { CreatedAt = command.CreatedAt.AddSeconds(1) };
        conflicting = conflicting with { Manifest = conflicting.Manifest with { CreatedAt = conflicting.CreatedAt } };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(conflicting, CancellationToken.None));
    }

    [Fact]
    public async Task NewerCheckpointIsolatedAndStaleOrForgedCasCannotReplaceIt()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var seeded = await SeedQueuedAsync(store, "batch_01", "version", 0);
        var coordinator = Coordinator(store, directory.Path);
        var first = Command("checkpoint_first", [Graph(seeded)]);
        var second = Command("checkpoint_second", [Graph(seeded)]) with
        {
            CreatedAt = first.CreatedAt.AddSeconds(2)
        };
        second = second with { Manifest = second.Manifest with { CreatedAt = second.CreatedAt } };
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(second, CancellationToken.None)).Outcome);

        var stale = Command("checkpoint_stale", [Graph(seeded)]) with
        {
            CreatedAt = first.CreatedAt.AddSeconds(1)
        };
        stale = stale with { Manifest = stale.Manifest with { CreatedAt = stale.CreatedAt } };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(stale, CancellationToken.None));

        var forgedGraph = new AiAnalysisCheckpointGraph(
            seeded.Request,
            AiAnalysisSessionStatus.Uploading.ToString(),
            seeded.Session,
            AiAnalysisSessionStatus.Uploading,
            0,
            null,
            null);
        var forged = Command("checkpoint_forged", [forgedGraph]) with
        {
            CreatedAt = second.CreatedAt.AddSeconds(1)
        };
        forged = forged with { Manifest = forged.Manifest with { CreatedAt = forged.CreatedAt } };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(forged, CancellationToken.None));

        var inspection = await new AiAnalysisStateManifestStore(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)))
            .InspectAsync("batch_01", CancellationToken.None);
        Assert.Equal("checkpoint_second", inspection!.CommittedManifest!.ManifestId);
        Assert.Null(await store.FindCheckpointAsync("checkpoint_stale", CancellationToken.None));
        Assert.Null(await store.FindCheckpointAsync("checkpoint_forged", CancellationToken.None));
    }

    [Fact]
    public async Task NewFullBatchCheckpointCannotOmitPreviouslyCommittedGraph()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var historical = await SeedQueuedAsync(store, "batch_01", "c_historical", 0);
        var added = await SeedQueuedAsync(store, "batch_01", "d_added", 10);
        var coordinator = Coordinator(store, directory.Path);
        var first = Command("checkpoint_history", [Graph(historical), Graph(added)]);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(first, CancellationToken.None)).Outcome);
        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        const string markerPath = "batches/batch_01/ai-analysis/state/committed.marker";
        var markerBefore = await files.ReadAsync(markerPath, CancellationToken.None);

        var omitted = Command("checkpoint_omitted", [Graph(historical)]) with
        {
            CreatedAt = first.CreatedAt.AddSeconds(20)
        };
        omitted = omitted with { Manifest = omitted.Manifest with { CreatedAt = omitted.CreatedAt } };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(omitted, CancellationToken.None));

        Assert.Equal("AI_CHECKPOINT_PRIOR_MANIFEST_REFERENCE_OMITTED", exception.Message);
        var inspection = await new AiAnalysisStateManifestStore(
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)))
            .InspectAsync("batch_01", CancellationToken.None);
        Assert.Equal("checkpoint_history", inspection!.CommittedManifest!.ManifestId);
        Assert.Equal(2, inspection.CommittedManifest.RequestIds.Count);
        Assert.Equal(markerBefore, await files.ReadAsync(markerPath, CancellationToken.None));
        Assert.NotNull(await store.LoadSessionAsync(added.Session.SessionId, CancellationToken.None));
        Assert.Equal(AiAnalysisCheckpointJournalState.Committed,
            (await store.FindCheckpointAsync("checkpoint_history", CancellationToken.None))!.State);
        Assert.Null(await store.FindCheckpointAsync("checkpoint_omitted", CancellationToken.None));

        var complete = Command("checkpoint_complete", [Graph(historical), Graph(added)]) with
        {
            CreatedAt = omitted.CreatedAt.AddSeconds(1)
        };
        complete = complete with { Manifest = complete.Manifest with { CreatedAt = complete.CreatedAt } };
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(complete, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ActiveCacheReferenceCannotBeSilentlyRemovedByNextCheckpoint()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var current = await SeedAnalysingAsync(store, "batch_01", "cache_history", 0);
        var desired = Advance(current, AiAnalysisSessionStatus.Validated, 3);
        var usage = Usage(desired.Request.RequestId, "usage_history", Origin.AddSeconds(4));
        var cache = Cache(usage, desired.Request.ResultReference!, Origin.AddSeconds(5));
        var first = Command("checkpoint_cache_history", [Graph(current, desired, usage, cache)]);
        var coordinator = Coordinator(store, directory.Path);
        Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
            (await coordinator.CommitAsync(first, CancellationToken.None)).Outcome);
        var markerFiles = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        const string markerPath = "batches/batch_01/ai-analysis/state/committed.marker";
        var markerBefore = await markerFiles.ReadAsync(markerPath, CancellationToken.None);
        var persisted = new GraphState(desired.Request, desired.Session);
        var withoutCache = Command(
            "checkpoint_cache_removed",
            [Graph(persisted, persisted, usage, null)]) with
        {
            CreatedAt = first.CreatedAt.AddSeconds(1)
        };
        withoutCache = withoutCache with
        {
            Manifest = withoutCache.Manifest with { CreatedAt = withoutCache.CreatedAt }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(withoutCache, CancellationToken.None));

        Assert.Equal("AI_CHECKPOINT_PRIOR_MANIFEST_REFERENCE_OMITTED", exception.Message);
        Assert.Equal(markerBefore, await markerFiles.ReadAsync(markerPath, CancellationToken.None));
        Assert.Null(await store.FindCheckpointAsync("checkpoint_cache_removed", CancellationToken.None));
    }

    private static AiAnalysisCheckpointCoordinator Coordinator(SqliteDesktopStore store, string root) => new(
        store,
        new AiAnalysisStateManifestStore(new AtomicFileStore(new LocalAppPathPolicy(root))),
        TimeProvider.System);

    private static async Task<GraphState> SeedQueuedAsync(
        SqliteDesktopStore store,
        string batchId,
        string suffix,
        int seconds)
    {
        var time = Origin.AddSeconds(seconds);
        var request = new AiRequestLedgerEntry(
            $"request_{suffix}", Digest(suffix[0]), batchId, $"style_{suffix}",
            AiAnalysisSessionStatus.Queued.ToString(), null, time, time);
        var session = AiAnalysisSession.Create(
            $"session_{suffix}", batchId, $"style_{suffix}", $"snapshot_{suffix}", $"analysis_{suffix}",
            [$"requirement_{suffix}"], [$"asset_{suffix}"]);
        await store.RecordRequestAsync(request, CancellationToken.None);
        await store.SaveSessionAsync(session, CancellationToken.None, 0);
        return new GraphState(request, session);
    }

    private static async Task<GraphState> SeedSchedulerQueuedAsync(
        SqliteDesktopStore store,
        string batchId,
        string suffix,
        DateTimeOffset time)
    {
        var request = SchedulerRequest($"request_{suffix}", batchId, $"style_{suffix}", time);
        var ledger = new AiRequestLedgerEntry(
            request.RequestId,
            AiAnalysisCacheKeyFactory.Create(request),
            batchId,
            $"style_{suffix}",
            AiAnalysisSessionStatus.Queued.ToString(),
            null,
            time,
            time);
        var session = AiAnalysisSession.Create(
            $"session_{suffix}", batchId, $"style_{suffix}", "snapshot_scheduled", $"analysis_{suffix}",
            request.Requirements.Select(requirement => requirement.RequirementId),
            request.Assets.Select(asset => asset.AssetId));
        await store.RecordRequestAsync(ledger, CancellationToken.None);
        await store.SaveSessionAsync(session, CancellationToken.None, 0);
        return new GraphState(ledger, session);
    }

    private static async Task<GraphState> SeedAnalysingAsync(
        SqliteDesktopStore store,
        string batchId,
        string suffix,
        int seconds)
    {
        var state = await SeedQueuedAsync(store, batchId, suffix, seconds);
        foreach (var status in new[] { AiAnalysisSessionStatus.Uploading, AiAnalysisSessionStatus.Analysing })
        {
            var updatedAt = state.Request.UpdatedAt.AddSeconds(1);
            var request = await store.UpdateRequestStatusAsync(
                state.Request.RequestId,
                state.Request.Status,
                status.ToString(),
                null,
                updatedAt,
                CancellationToken.None);
            var session = state.Session.AdvanceTo(status);
            await store.SaveSessionAsync(session, CancellationToken.None, state.Session.SelectionRevision);
            state = new GraphState(request, session);
        }

        return state;
    }

    private static GraphState Advance(GraphState current, AiAnalysisSessionStatus status, int seconds)
    {
        var resultReference = status is AiAnalysisSessionStatus.Validated
            or AiAnalysisSessionStatus.ManualReview
            or AiAnalysisSessionStatus.PlanClosed
            ? $"results/{current.Request.RequestId}.json"
            : null;
        return new GraphState(
            current.Request with
            {
                Status = status.ToString(),
                ResultReference = resultReference,
                UpdatedAt = Origin.AddSeconds(seconds)
            },
            current.Session.AdvanceTo(status));
    }

    private static AiAnalysisCheckpointGraph Graph(GraphState state) => Graph(state, state);

    private static AiAnalysisCheckpointGraph Graph(
        GraphState current,
        GraphState? desired = null,
        AiUsageEventRecord? usage = null,
        AiCacheReference? cache = null) => new(
        (desired ?? current).Request,
        current.Request.Status,
        (desired ?? current).Session,
        current.Session.Status,
        current.Session.SelectionRevision,
        usage,
        cache);

    private static AiAnalysisCheckpointCommand Command(
        string checkpointId,
        IReadOnlyList<AiAnalysisCheckpointGraph> graphs)
    {
        var createdAt = graphs
            .SelectMany(graph => new[]
            {
                graph.Request.UpdatedAt,
                graph.Usage?.RecordedAt ?? Origin,
                graph.Cache?.CreatedAt ?? Origin
            })
            .Max()
            .AddSeconds(1);
        var batchId = graphs[0].Request.BatchId;
        var manifest = new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version,
            checkpointId,
            batchId,
            graphs.Select(graph => graph.Session.SessionId).ToArray(),
            graphs.Select(graph => graph.Request.RequestId).ToArray(),
            graphs.Where(graph => graph.Cache is not null).Select(graph => graph.Cache!.CacheKey).ToArray(),
            createdAt);
        return new AiAnalysisCheckpointCommand(checkpointId, manifest, graphs, createdAt);
    }

    private static AiAnalysisCheckpointCommand CommandWithScheduler(
        string checkpointId,
        IReadOnlyList<AiAnalysisCheckpointGraph> graphs,
        string schedulerStateKey,
        AiAnalysisSchedulingState schedulerState)
    {
        var createdAt = graphs
            .SelectMany(graph => new[]
            {
                graph.Request.UpdatedAt,
                graph.Usage?.RecordedAt ?? Origin,
                graph.Cache?.CreatedAt ?? Origin,
                schedulerState.UpdatedAt
            })
            .Max()
            .AddSeconds(1);
        var batchId = graphs[0].Request.BatchId;
        var schedulerDigest = AiAnalysisCheckpointRules.ComputeSchedulerStateDigest(schedulerState);
        var manifest = new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version,
            checkpointId,
            batchId,
            graphs.Select(graph => graph.Session.SessionId).ToArray(),
            graphs.Select(graph => graph.Request.RequestId).ToArray(),
            graphs.Where(graph => graph.Cache is not null).Select(graph => graph.Cache!.CacheKey).ToArray(),
            createdAt,
            schedulerDigest);
        return new AiAnalysisCheckpointCommand(
            checkpointId,
            manifest,
            graphs,
            createdAt,
            schedulerStateKey,
            schedulerState);
    }

    private static AiAnalysisRequestV1 SchedulerRequest(string requestId, string batchId, string styleId, DateTimeOffset now) =>
        new(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            requestId,
            $"correlation-{requestId}",
            "product_01",
            batchId,
            styleId,
            "snapshot_01",
            "revision_01",
            AiAnalysisKind.ProductJointUnderstanding,
            "model/v1",
            "prompt/v1",
            AiAnalysisProtocol.ResultSchemaVersion,
            "compression/v1",
            "consent_01",
            Digest('a'),
            [new AiAssetInputV1("asset_scheduled", Digest('b'), "image/jpeg", 10, 10, "copies/asset_scheduled")],
            [new AiRequirementInputV1("requirement_scheduled", "subject", "center", true)]);

    private static AiUsageEventRecord Usage(string requestId, string usageId, DateTimeOffset recordedAt) => new(
        usageId, requestId, "model_v1", 10, 20, false, 0, null, null, recordedAt);

    private static AiCacheReference Cache(
        AiUsageEventRecord usage,
        string resultReference,
        DateTimeOffset createdAt) => new(
        Digest('c'), resultReference, "consent_01", usage.ModelConfigVersion, usage.UsageEventId, createdAt);

    private static string Digest(char value) => "sha256:" + new string(value is >= 'a' and <= 'f' ? value : 'a', 64);


    private sealed record GraphState(AiRequestLedgerEntry Request, AiAnalysisSession Session);

    private enum ThrowPoint
    {
        BeforeCommit,
        AfterCommit,
        AfterPrepare,
        BeforeFinalize,
        AfterFinalize
    }

    private sealed class ThrowingManifestStore(
        IAiAnalysisStateManifestStore inner,
        ThrowPoint point) : IAiAnalysisStateManifestStore
    {
        public Task<StagedAiAnalysisStateManifest> StageAsync(
            AiAnalysisStateManifestV1 manifest,
            CancellationToken cancellationToken) => inner.StageAsync(manifest, cancellationToken);

        public async Task CommitAsync(
            StagedAiAnalysisStateManifest stagedManifest,
            CancellationToken cancellationToken)
        {
            if (point == ThrowPoint.BeforeCommit)
            {
                throw new IOException("TEST_BEFORE_MANIFEST_COMMIT");
            }

            await inner.CommitAsync(stagedManifest, cancellationToken);
            if (point == ThrowPoint.AfterCommit)
            {
                throw new IOException("TEST_AFTER_MANIFEST_COMMIT");
            }
        }

        public Task RollbackAsync(
            StagedAiAnalysisStateManifest stagedManifest,
            CancellationToken cancellationToken) => inner.RollbackAsync(stagedManifest, cancellationToken);

        public Task<AiAnalysisStateManifestInspection?> InspectAsync(
            string batchId,
            CancellationToken cancellationToken) => inner.InspectAsync(batchId, cancellationToken);
    }

    private sealed class ThrowingCheckpointDatabase(
        IAiAnalysisCheckpointDatabase inner,
        ThrowPoint point) : IAiAnalysisCheckpointDatabase
    {
        public Task<AiAnalysisCheckpointJournal?> FindCheckpointAsync(string checkpointId, CancellationToken cancellationToken) =>
            inner.FindCheckpointAsync(checkpointId, cancellationToken);

        public Task<AiAnalysisCheckpointJournal?> FindPendingCheckpointAsync(string batchId, CancellationToken cancellationToken) =>
            inner.FindPendingCheckpointAsync(batchId, cancellationToken);

        public Task<AiAnalysisCheckpointJournal?> FindLatestCommittedCheckpointAsync(string batchId, CancellationToken cancellationToken) =>
            inner.FindLatestCommittedCheckpointAsync(batchId, cancellationToken);

        public async Task<AiAnalysisCheckpointJournal> PrepareCheckpointAsync(
            AiAnalysisCheckpointCommand command,
            StagedAiAnalysisStateManifest stagedManifest,
            string payloadDigest,
            CancellationToken cancellationToken)
        {
            var journal = await inner.PrepareCheckpointAsync(command, stagedManifest, payloadDigest, cancellationToken);
            if (point == ThrowPoint.AfterPrepare)
            {
                throw new IOException("TEST_AFTER_DATABASE_PREPARE");
            }

            return journal;
        }

        public Task ValidateCheckpointGraphAsync(
            AiAnalysisCheckpointJournal journal,
            AiAnalysisStateManifestV1 manifest,
            CancellationToken cancellationToken) => inner.ValidateCheckpointGraphAsync(journal, manifest, cancellationToken);

        public async Task<AiAnalysisCheckpointJournal> MarkCheckpointCommittedAsync(
            AiAnalysisCheckpointJournal journal,
            DateTimeOffset committedAt,
            CancellationToken cancellationToken)
        {
            if (point == ThrowPoint.BeforeFinalize)
            {
                throw new IOException("TEST_BEFORE_DATABASE_FINALIZE");
            }

            var committed = await inner.MarkCheckpointCommittedAsync(journal, committedAt, cancellationToken);
            if (point == ThrowPoint.AfterFinalize)
            {
                throw new IOException("TEST_AFTER_DATABASE_FINALIZE");
            }

            return committed;
        }
    }
}
