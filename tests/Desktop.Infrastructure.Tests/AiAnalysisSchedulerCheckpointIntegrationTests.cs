using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Ai;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class AiAnalysisSchedulerCheckpointIntegrationTests
{
    [Fact]
    public async Task SelectionLockPersistsAcrossRefreshAndSchedulerRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-selection-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DesktopDataOptions(root);
        var request = Request() with
        {
            Assets =
            [
                Request().Assets[0],
                Request().Assets[0] with
                {
                    AssetId = "asset_02",
                    ContentHash = "sha256:" + new string('c', 64),
                    AnalysisCopyReference = "copy_02"
                }
            ]
        };
        try
        {
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                var files = new AtomicFileStore(new LocalAppPathPolicy(root));
                using var coordinator = new AiAnalysisCheckpointCoordinator(
                    store, new AiAnalysisStateManifestStore(files), TimeProvider.System);
                using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
                var provider = new SuccessfulProvider();
                using var scheduler = Scheduler(store, files, coordinator, queue, provider);

                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(request, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
                Assert.Equal(AiAnalysisWorkDisposition.Completed,
                    (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);
                Assert.Equal(1, provider.Calls);

                var locked = await scheduler.LockSelectionAsync(
                    request, "frame_01", "asset_02", CancellationToken.None);
                var persisted = await store.LoadSessionAsync("session-request_01", CancellationToken.None);
                var ledger = await store.FindRequestByFingerprintAsync(
                    AiAnalysisCacheKeyFactory.Create(request), CancellationToken.None);

                Assert.Equal(AiAnalysisSessionStatus.ManualReview, locked.Status);
                Assert.Equal(1, locked.SelectionRevision);
                Assert.Equal("asset_02", locked.LockedSelections["frame_01"].AssetId);
                Assert.Equal(AiAnalysisSessionStatus.ManualReview, persisted!.Status);
                Assert.Equal(1, persisted.SelectionRevision);
                Assert.Equal("asset_02", persisted.LockedSelections["frame_01"].AssetId);
                Assert.Equal("ManualReview", ledger!.Status);
                Assert.Equal(1, provider.Calls);
            }

            await using var reopened = new SqliteDesktopStore(options);
            await reopened.InitializeAsync();
            var reopenedFiles = new AtomicFileStore(new LocalAppPathPolicy(root));
            using var reopenedCoordinator = new AiAnalysisCheckpointCoordinator(
                reopened, new AiAnalysisStateManifestStore(reopenedFiles), TimeProvider.System);
            using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
            using var reopenedScheduler = Scheduler(reopened, reopenedFiles, reopenedCoordinator, reopenedQueue);

            Assert.Equal(0, await reopenedScheduler.RestoreAsync(CancellationToken.None));
            var recovered = await reopened.LoadSessionAsync("session-request_01", CancellationToken.None);
            Assert.Equal(AiAnalysisSessionStatus.ManualReview, recovered!.Status);
            Assert.Equal(1, recovered.SelectionRevision);
            Assert.Equal("asset_02", recovered.LockedSelections["frame_01"].AssetId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentRequestsAdvanceOneFullCheckpointGraphAndRestartClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-concurrent-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DesktopDataOptions(root);
        var first = Request(1);
        var second = Request(2);
        try
        {
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                var files = new AtomicFileStore(new LocalAppPathPolicy(root));
                var manifests = new AiAnalysisStateManifestStore(files);
                using var coordinator = new AiAnalysisCheckpointCoordinator(store, manifests, TimeProvider.System);
                using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1, 2));
                using var provider = new BlockingProvider(2);
                using var scheduler = new AiAnalysisScheduler(
                    provider,
                    store,
                    queue,
                    new AiAnalysisBudgetPolicy(10, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.03m)),
                    queueOptions: new AiAnalysisQueueOptions(2, 1, 2),
                    stateStore: store,
                    stateKey: "batch-batch_01",
                    resultStore: new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator()),
                    checkpointPort: coordinator);

                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(first, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(second, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);

                var processing = new[]
                {
                    scheduler.ProcessNextAsync(CancellationToken.None),
                    scheduler.ProcessNextAsync(CancellationToken.None)
                };
                await provider.WaitUntilEnteredAsync().WaitAsync(TimeSpan.FromSeconds(10));
                provider.Release();
                var completed = await Task.WhenAll(processing).WaitAsync(TimeSpan.FromSeconds(20));

                Assert.All(completed, result => Assert.Equal(AiAnalysisWorkDisposition.Completed, result.Disposition));
                Assert.Equal("Validated", (await store.FindRequestByFingerprintAsync(
                    AiAnalysisCacheKeyFactory.Create(first), CancellationToken.None))!.Status);
                Assert.Equal("Validated", (await store.FindRequestByFingerprintAsync(
                    AiAnalysisCacheKeyFactory.Create(second), CancellationToken.None))!.Status);
                var inspection = await manifests.InspectAsync("batch_01", CancellationToken.None);
                Assert.True(inspection!.IsCommitted);
                Assert.Equal(2, inspection.CommittedManifest!.RequestIds.Count);
                Assert.Empty((await store.LoadSchedulingStateAsync("batch-batch_01", CancellationToken.None))!.PendingItems);
            }

            await using var reopened = new SqliteDesktopStore(options);
            await reopened.InitializeAsync();
            var reopenedFiles = new AtomicFileStore(new LocalAppPathPolicy(root));
            using var reopenedCoordinator = new AiAnalysisCheckpointCoordinator(
                reopened,
                new AiAnalysisStateManifestStore(reopenedFiles),
                TimeProvider.System);
            using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1, 2));
            using var reopenedScheduler = new AiAnalysisScheduler(
                new SuccessfulProvider(),
                reopened,
                reopenedQueue,
                new AiAnalysisBudgetPolicy(10, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.03m)),
                queueOptions: new AiAnalysisQueueOptions(2, 1, 2),
                stateStore: reopened,
                stateKey: "batch-batch_01",
                resultStore: new FileAiAnalysisResultStore(reopenedFiles, new JsonSchemaContractValidator()),
                checkpointPort: reopenedCoordinator);

            Assert.Equal(0, await reopenedScheduler.RestoreAsync(CancellationToken.None));
            Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
                (await reopenedCoordinator.RecoverAsync("batch_01", CancellationToken.None)).Outcome);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RetryAttemptReplacementSurvivesCrashBeforeQueuedTransitionWithoutThirdHttpCall()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-retry-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DesktopDataOptions(root);
        var firstProvider = new RetryableProvider();
        try
        {
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                var files = new AtomicFileStore(new LocalAppPathPolicy(root));
                using var coordinator = new AiAnalysisCheckpointCoordinator(
                    store, new AiAnalysisStateManifestStore(files), TimeProvider.System);
                var failQueuedRetry = new FailQueuedRetryCheckpointPort(coordinator);
                using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
                using var scheduler = new AiAnalysisScheduler(
                    firstProvider,
                    store,
                    queue,
                    new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.03m)),
                    queueOptions: new AiAnalysisQueueOptions(2, 2),
                    stateStore: store,
                    stateKey: "batch-batch_01",
                    resultStore: new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator()),
                    checkpointPort: failQueuedRetry);
                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    scheduler.ProcessNextAsync(CancellationToken.None));

                Assert.Equal("TEST_CRASH_BEFORE_RETRY_QUEUED", exception.Message);
                Assert.Equal(1, firstProvider.Calls);
                Assert.Equal("RetryableError", (await store.FindRequestByFingerprintAsync(
                    AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None))!.Status);
                var persisted = (await store.LoadSchedulingStateAsync(
                    "batch-batch_01", CancellationToken.None))!;
                Assert.Equal(1, persisted.Budget.RequestCount);
                Assert.Equal(1, Assert.Single(persisted.PendingItems).Attempt);
            }

            await using var reopened = new SqliteDesktopStore(options);
            await reopened.InitializeAsync();
            var reopenedFiles = new AtomicFileStore(new LocalAppPathPolicy(root));
            using var reopenedCoordinator = new AiAnalysisCheckpointCoordinator(
                reopened,
                new AiAnalysisStateManifestStore(reopenedFiles),
                TimeProvider.System);
            var reopenedProvider = new SuccessfulProvider();
            using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
            using var reopenedScheduler = new AiAnalysisScheduler(
                reopenedProvider,
                reopened,
                reopenedQueue,
                new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.03m)),
                queueOptions: new AiAnalysisQueueOptions(2, 2),
                stateStore: reopened,
                stateKey: "batch-batch_01",
                resultStore: new FileAiAnalysisResultStore(reopenedFiles, new JsonSchemaContractValidator()),
                checkpointPort: reopenedCoordinator);

            Assert.Equal(1, await reopenedScheduler.RestoreAsync(CancellationToken.None));
            var completed = await reopenedScheduler.ProcessNextAsync(CancellationToken.None);
            Assert.Equal(AiAnalysisWorkDisposition.Completed, completed.Disposition);
            Assert.Equal(1, completed.Attempt);
            Assert.Equal(2, firstProvider.Calls + reopenedProvider.Calls);
            Assert.Equal(2, reopenedScheduler.Budget.RequestCount);
            Assert.Empty((await reopened.LoadSchedulingStateAsync(
                "batch-batch_01", CancellationToken.None))!.PendingItems);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BufferWaterlineKeepsTwoHundredDurableItemsRecoverableAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-durable-backlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DesktopDataOptions(root);
        var queueOptions = new AiAnalysisQueueOptions(32, 1, 8);
        var budget = new AiAnalysisBudgetPolicy(400, 10_000, 10_000, 100m, 10m, new AiAnalysisPricing(0.01m, 0.03m));
        try
        {
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                using var queue = new BoundedAiAnalysisWorkBuffer(queueOptions);
                using var provider = new BlockingProvider(8);
                using var scheduler = new AiAnalysisScheduler(
                    provider,
                    store,
                    queue,
                    budget,
                    queueOptions: queueOptions,
                    stateStore: store,
                    stateKey: "batch-batch_01");

                for (var index = 1; index <= 200; index++)
                {
                    Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                        (await scheduler.EnqueueAsync(Request(index), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
                }

                Assert.Equal(32, queue.Count);
                Assert.Equal(200, (await store.LoadSchedulingStateAsync("batch-batch_01", CancellationToken.None))!.PendingItems.Count);

                var firstWave = Enumerable.Range(0, 8)
                    .Select(_ => scheduler.ProcessNextAsync(CancellationToken.None))
                    .ToArray();
                await provider.WaitUntilEnteredAsync().WaitAsync(TimeSpan.FromSeconds(10));
                provider.Release();
                Assert.All(await Task.WhenAll(firstWave).WaitAsync(TimeSpan.FromSeconds(20)),
                    result => Assert.Equal(AiAnalysisWorkDisposition.Completed, result.Disposition));
                Assert.Equal(192, (await store.LoadSchedulingStateAsync("batch-batch_01", CancellationToken.None))!.PendingItems.Count);
            }

            await using var reopened = new SqliteDesktopStore(options);
            await reopened.InitializeAsync();
            using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(queueOptions);
            var reopenedProvider = new SuccessfulProvider();
            using var reopenedScheduler = new AiAnalysisScheduler(
                reopenedProvider,
                reopened,
                reopenedQueue,
                budget,
                queueOptions: queueOptions,
                stateStore: reopened,
                stateKey: "batch-batch_01");

            Assert.Equal(192, await reopenedScheduler.RestoreAsync(CancellationToken.None));
            Assert.Equal(32, reopenedQueue.Count);
            var remaining = Enumerable.Range(0, 192)
                .Select(_ => reopenedScheduler.ProcessNextAsync(CancellationToken.None))
                .ToArray();
            Assert.All(await Task.WhenAll(remaining).WaitAsync(TimeSpan.FromSeconds(60)),
                result => Assert.Equal(AiAnalysisWorkDisposition.Completed, result.Disposition));
            Assert.Equal(192, reopenedProvider.Calls);
            Assert.Empty((await reopened.LoadSchedulingStateAsync("batch-batch_01", CancellationToken.None))!.PendingItems);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SchedulerCommittedCheckpointRemainsRecoverableAcrossV10ToV11Upgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-scheduler-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DesktopDataOptions(root);
        try
        {
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                var policy = new LocalAppPathPolicy(root);
                var files = new AtomicFileStore(policy);
                var manifests = new AiAnalysisStateManifestStore(files);
                using var coordinator = new AiAnalysisCheckpointCoordinator(store, manifests, TimeProvider.System);
                var results = new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator());
                using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
                using var scheduler = new AiAnalysisScheduler(
                    new SuccessfulProvider(),
                    store,
                    queue,
                    new AiAnalysisBudgetPolicy(2, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.03m)),
                    stateStore: store,
                    stateKey: "batch-batch_01",
                    resultStore: results,
                    checkpointPort: coordinator);

                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
                Assert.Equal(AiAnalysisWorkDisposition.Completed,
                    (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);
                Assert.Equal("Validated", (await store.FindRequestByFingerprintAsync(
                    AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None))!.Status);
                Assert.NotNull(await results.LoadAsync("results/request_01", CancellationToken.None));
                Assert.Empty((await store.LoadSchedulingStateAsync("batch-batch_01", CancellationToken.None))!.PendingItems);
                Assert.True((await manifests.InspectAsync("batch_01", CancellationToken.None))!.IsCommitted);
            }

            await using (var downgrade = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = options.DatabasePath,
                    Pooling = false
                }.ToString()))
            {
                await downgrade.OpenAsync();
                await using var command = downgrade.CreateCommand();
                command.CommandText = "DELETE FROM schema_migrations WHERE version=11; ALTER TABLE ai_cache_references DROP COLUMN is_reusable;";
                await command.ExecuteNonQueryAsync();
            }

            await using var reopened = new SqliteDesktopStore(options);
            await reopened.InitializeAsync();
            var reopenedFiles = new AtomicFileStore(new LocalAppPathPolicy(root));
            using var reopenedCoordinator = new AiAnalysisCheckpointCoordinator(
                reopened,
                new AiAnalysisStateManifestStore(reopenedFiles),
                TimeProvider.System);
            using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
            using var reopenedScheduler = new AiAnalysisScheduler(
                new SuccessfulProvider(),
                reopened,
                reopenedQueue,
                new AiAnalysisBudgetPolicy(2, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.03m)),
                stateStore: reopened,
                stateKey: "batch-batch_01",
                resultStore: new FileAiAnalysisResultStore(reopenedFiles, new JsonSchemaContractValidator()),
                checkpointPort: reopenedCoordinator);
            Assert.Equal(0, await reopenedScheduler.RestoreAsync(CancellationToken.None));
            Assert.Equal(0, reopenedQueue.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CacheInvalidationSurvivesRestartAndNewFingerprintCommitsWithoutDroppingTombstoneHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-cache-invalidation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DesktopDataOptions(root);
        var firstRequest = Request();
        var firstKey = AiAnalysisCacheKeyFactory.Create(firstRequest);
        try
        {
            await using (var store = new SqliteDesktopStore(options))
            {
                await store.InitializeAsync();
                var files = new AtomicFileStore(new LocalAppPathPolicy(root));
                using var coordinator = new AiAnalysisCheckpointCoordinator(
                    store, new AiAnalysisStateManifestStore(files), TimeProvider.System);
                using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
                using var scheduler = Scheduler(store, files, coordinator, queue);
                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(firstRequest, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
                Assert.Equal(AiAnalysisWorkDisposition.Completed,
                    (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);

                Assert.True(await scheduler.InvalidateCacheAsync(firstKey, CancellationToken.None));
                var tombstone = await store.FindCacheReferenceAsync(firstKey, CancellationToken.None);
                Assert.NotNull(tombstone);
                Assert.False(tombstone!.IsReusable);
                Assert.NotNull(await store.FindUsageEventAsync(tombstone.UsageEventId, CancellationToken.None));
            }

            await using (var reopened = new SqliteDesktopStore(options))
            {
                await reopened.InitializeAsync();
                var files = new AtomicFileStore(new LocalAppPathPolicy(root));
                var manifests = new AiAnalysisStateManifestStore(files);
                using var coordinator = new AiAnalysisCheckpointCoordinator(reopened, manifests, TimeProvider.System);
                using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
                using var scheduler = Scheduler(reopened, files, coordinator, queue);
                Assert.Equal(0, await scheduler.RestoreAsync(CancellationToken.None));
                Assert.False((await reopened.FindCacheReferenceAsync(firstKey, CancellationToken.None))!.IsReusable);

                var sameFingerprint = await scheduler.EnqueueAsync(
                    firstRequest with { RequestId = "request_same_fingerprint" },
                    AiAnalysisWorkStage.ProductJointUnderstanding,
                    CancellationToken.None);
                Assert.Equal(AiAnalysisWorkDisposition.TerminalError, sameFingerprint.Disposition);
                Assert.Equal("AI_CACHE_INVALIDATED_REANALYSIS_VERSION_REQUIRED", sameFingerprint.ErrorCode);
                Assert.Equal(0, queue.Count);

                var nextRequest = firstRequest with
                {
                    RequestId = "request_02",
                    RequirementsDigest = "sha256:" + new string('c', 64)
                };
                var nextKey = AiAnalysisCacheKeyFactory.Create(nextRequest);
                Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
                    (await scheduler.EnqueueAsync(nextRequest, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
                Assert.Equal(AiAnalysisWorkDisposition.Completed,
                    (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);

                var inspection = await manifests.InspectAsync("batch_01", CancellationToken.None);
                Assert.NotNull(inspection);
                Assert.Contains(firstKey, inspection!.CommittedManifest!.CacheKeys);
                Assert.Contains(nextKey, inspection.CommittedManifest.CacheKeys);
                Assert.False((await reopened.FindCacheReferenceAsync(firstKey, CancellationToken.None))!.IsReusable);
                Assert.True((await reopened.FindCacheReferenceAsync(nextKey, CancellationToken.None))!.IsReusable);
                Assert.Equal(AiAnalysisCheckpointOutcome.Committed,
                    (await coordinator.RecoverAsync("batch_01", CancellationToken.None)).Outcome);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static AiAnalysisScheduler Scheduler(
        SqliteDesktopStore store,
        AtomicFileStore files,
        AiAnalysisCheckpointCoordinator coordinator,
        BoundedAiAnalysisWorkBuffer queue,
        IAiServicePort? provider = null) => new(
        provider ?? new SuccessfulProvider(),
        store,
        queue,
        new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.03m)),
        stateStore: store,
        stateKey: "batch-batch_01",
        resultStore: new FileAiAnalysisResultStore(files, new JsonSchemaContractValidator()),
        checkpointPort: coordinator);

    private static AiAnalysisRequestV1 Request() => new(
        AiAnalysisProtocol.RequestSchemaVersion,
        "request",
        "request_01",
        "correlation_01",
        "product_01",
        "batch_01",
        "style_01",
        "snapshot_01",
        "revision_01",
        AiAnalysisKind.ProductJointUnderstanding,
        "model/proposed",
        "prompt/v1",
        AiAnalysisProtocol.ResultSchemaVersion,
        AiAnalysisCopyProfiles.BytePreservingProvisionalV1,
        "consent_01",
        "sha256:" + new string('a', 64),
        [new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copy_01")],
        [new AiRequirementInputV1("frame_01", "商品图", "居中", true)]);

    private static AiAnalysisRequestV1 Request(int index)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .ToLowerInvariant();
        return Request() with
        {
            RequestId = $"request_{index:D3}",
            ProductId = $"product_{index:D3}",
            StyleId = $"style_{index:D3}",
            RequirementsDigest = "sha256:" + digest,
            Assets =
            [
                Request().Assets[0] with
                {
                    AssetId = $"asset_{index:D3}",
                    ContentHash = "sha256:" + digest,
                    AnalysisCopyReference = $"copy_{index:D3}"
                }
            ]
        };
    }

    private sealed class SuccessfulProvider : IAiServicePort
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken cancellationToken) =>
            Task.FromResult(Complete(request, Interlocked.Increment(ref _calls)));
    }

    private sealed class RetryableProvider : IAiServicePort
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<AiAnalysisExecutionResult> AnalyzeAsync(
            AiAnalysisRequestV1 request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(AiAnalysisExecutionResult.Failure(
                AiProviderOutcome.RetryableError,
                new ContractError("TEST_RETRYABLE", true, "test", "稍后重试"),
                TimeSpan.Zero));
        }
    }

    private sealed class FailQueuedRetryCheckpointPort(IAiAnalysisCheckpointPort inner) : IAiAnalysisCheckpointPort
    {
        private int _failed;

        public Task<AiAnalysisCheckpointResult> CommitAsync(
            AiAnalysisCheckpointCommand command,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _failed) == 0
                && command.Graphs.Any(graph =>
                    graph.ExpectedRequestStatus == "RetryableError"
                    && graph.Request.Status == "Queued")
                && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new InvalidOperationException("TEST_CRASH_BEFORE_RETRY_QUEUED");
            }

            return inner.CommitAsync(command, cancellationToken);
        }

        public Task<AiAnalysisCheckpointResult> RecoverAsync(
            string batchId,
            CancellationToken cancellationToken) => inner.RecoverAsync(batchId, cancellationToken);
    }

    private sealed class BlockingProvider(int expectedCalls) : IAiServicePort, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task WaitUntilEnteredAsync() => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<AiAnalysisExecutionResult> AnalyzeAsync(
            AiAnalysisRequestV1 request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == expectedCalls) _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return Complete(request, _calls);
        }

        public void Dispose()
        {
            _entered.TrySetCanceled();
            _release.TrySetCanceled();
        }
    }

    private static AiAnalysisExecutionResult Complete(AiAnalysisRequestV1 request, int sequence) =>
        AiAnalysisExecutionResult.Completed(
                new AiAnalysisResultV1(
                    AiAnalysisProtocol.ResultSchemaVersion,
                    "result",
                    request.RequestId,
                    null,
                    request.ModelConfigVersion,
                    [], [], [], null, [],
                    new AiUsageSummaryV1($"usage_{request.RequestId}_{sequence}", 1, 1, 1, null, null)),
                new AiProviderUsage(1, 1, 2, false),
                TimeSpan.Zero);
}
