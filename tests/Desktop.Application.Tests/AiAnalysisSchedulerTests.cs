using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class AiAnalysisSchedulerTests
{
    [Fact]
    public void CacheKeyIsStableForAssetOrderAndChangesWithContractInputs()
    {
        var request = Request();
        var reordered = request with { Assets = request.Assets.Reverse().ToArray() };

        Assert.Equal(
            AiAnalysisCacheKeyFactory.Create(request),
            AiAnalysisCacheKeyFactory.Create(reordered));
        Assert.StartsWith("sha256:", AiAnalysisCacheKeyFactory.Create(request), StringComparison.Ordinal);
        Assert.Equal(71, AiAnalysisCacheKeyFactory.Create(request).Length);
        Assert.NotEqual(
            AiAnalysisCacheKeyFactory.Create(request),
            AiAnalysisCacheKeyFactory.Create(request with { PromptContractVersion = "prompt/v2" }));
    }

    [Fact]
    public async Task BoundedBufferRejectsDuplicatesAndOverflow()
    {
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(1, 2));
        var first = new AiAnalysisWorkItem("work-1", Request(), AiAnalysisWorkStage.ProductJointUnderstanding, 0, DateTimeOffset.UtcNow);
        var second = first with { WorkItemId = "work-2" };

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        Assert.Equal(1, queue.Count);

        var read = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(first.WorkItemId, read.WorkItemId);
        queue.Complete(read);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task SuccessfulWorkPersistsUsageCacheAndValidatedState()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"),
            new AiProviderUsage(10, 20, 30, false),
            TimeSpan.FromSeconds(1)));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
        var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));

        var enqueued = await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var processed = await scheduler.ProcessNextAsync(CancellationToken.None);
        var session = await store.LoadSessionAsync("session-request_01", CancellationToken.None);
        var ledger = await store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.Enqueued, enqueued.Disposition);
        Assert.Equal(AiAnalysisWorkDisposition.Completed, processed.Disposition);
        Assert.Equal(AiAnalysisSessionStatus.Validated, session!.Status);
        Assert.Equal("Validated", ledger!.Status);
        Assert.NotNull(await store.FindCacheReferenceAsync(AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0.0005m, scheduler.Budget.EstimatedCost);
    }

    [Fact]
    public async Task RetryableFailureIsRequeuedAndUnknownIsNotRetried()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(
            _ => AiAnalysisExecutionResult.Failure(
                AiProviderOutcome.RetryableError,
                new ContractError("RATE", true, "provider", "稍后重试"),
                TimeSpan.Zero),
            _ => AiAnalysisExecutionResult.Failure(
                AiProviderOutcome.AiResultUnknown,
                new ContractError("UNKNOWN", false, "provider", "请人工确认"),
                TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
        var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));

        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var retry = await scheduler.ProcessNextAsync(CancellationToken.None);
        var duplicateWhileRetryPending = await scheduler.EnqueueAsync(
            Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var unknown = await scheduler.ProcessNextAsync(CancellationToken.None);
        var session = await store.LoadSessionAsync("session-request_01", CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.RetryScheduled, retry.Disposition);
        Assert.Equal(AiAnalysisWorkDisposition.Duplicate, duplicateWhileRetryPending.Disposition);
        Assert.Equal(AiAnalysisWorkDisposition.ResultUnknown, unknown.Disposition);
        Assert.Equal(AiAnalysisSessionStatus.AiResultUnknown, session!.Status);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ObservedUsageIsRecordedForNonSuccessfulProviderOutcome()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => new AiAnalysisExecutionResult(
            AiProviderOutcome.RetryableError,
            null,
            new AiProviderUsage(4, 2, 6, false),
            new ContractError("RATE", true, "provider", "稍后重试"),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
        using var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));

        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var result = await scheduler.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.RetryScheduled, result.Disposition);
        var usage = Assert.Single(await store.LoadUsageAsync("request_01", CancellationToken.None));
        Assert.Equal(4, usage.InputUnits);
        Assert.Equal(2, usage.OutputUnits);
        Assert.Equal(0, usage.RetryCount);
    }

    [Fact]
    public async Task ExistingCacheIsReturnedWithoutChargingAnotherProviderRequest()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"),
            new AiProviderUsage(10, 20, 30, false),
            TimeSpan.Zero));
        var policy = new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m));

        using (var firstQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2)))
        {
            var firstScheduler = new AiAnalysisScheduler(provider, store, firstQueue, policy);
            await firstScheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
            _ = await firstScheduler.ProcessNextAsync(CancellationToken.None);
        }

        using var secondQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
        var secondScheduler = new AiAnalysisScheduler(provider, store, secondQueue, policy);
        var equivalentRequest = Request() with { RequestId = "request_02" };

        var result = await secondScheduler.EnqueueAsync(
            equivalentRequest,
            AiAnalysisWorkStage.ProductJointUnderstanding,
            CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.CacheHit, result.Disposition);
        Assert.Equal("results/request_01", result.ResultReference);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, secondQueue.Count);
    }

    [Fact]
    public async Task SuccessfulWorkPersistsResultBeforeValidatedAndCacheReuseReadsIt()
    {
        var store = new MemoryStore();
        var results = new RecordingResultStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(Result("request_01"), new AiProviderUsage(1, 1, 2, false), TimeSpan.Zero));
        var policy = new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(provider, store, queue, policy, resultStore: results);
        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.Completed, (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);
        Assert.NotNull(await results.LoadAsync("results/request_01", CancellationToken.None));
        var equivalent = Request() with { RequestId = "request_02" };
        var hit = await scheduler.EnqueueAsync(equivalent, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.CacheHit, hit.Disposition);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CacheHitWithMissingResultIsInvalidatedAndRequeued()
    {
        var store = new MemoryStore();
        var results = new RecordingResultStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"), new AiProviderUsage(1, 1, 2, false), TimeSpan.Zero));
        var policy = new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(provider, store, queue, policy, resultStore: results);
        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.Completed, (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);
        results.Remove("results/request_01");
        var equivalent = Request() with { RequestId = "request_02" };
        var requeued = await scheduler.EnqueueAsync(equivalent, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.TerminalError, requeued.Disposition);
        Assert.Equal("AI_CACHE_RESULT_INVALID", requeued.ErrorCode);
        Assert.Null(await results.LoadAsync("results/request_01", CancellationToken.None));
    }

    [Fact]
    public async Task SchedulerPersistsEnqueueAndTerminalBoundariesThroughCheckpointPort()
    {
        var store = new MemoryStore();
        var checkpoints = new RecordingCheckpointPort(store);
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Failure(
            AiProviderOutcome.AiResultUnknown,
            new ContractError("UNKNOWN", false, "provider", "请人工确认"),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            checkpointPort: checkpoints);
        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        await scheduler.ProcessNextAsync(CancellationToken.None);
        Assert.True(checkpoints.Commands.Count >= 2);
        Assert.Contains(checkpoints.Commands, command => command.Graphs.Any(graph => graph.Request.RequestId == "request_01"));
        Assert.All(checkpoints.Commands, command => Assert.Contains("request_01", command.Manifest.RequestIds));
    }

    [Fact]
    public async Task FormalCheckpointFlowWithoutResultStoreCannotMarkProviderSuccessValidated()
    {
        var store = new MemoryStore();
        var checkpoints = new RecordingCheckpointPort(store);
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"),
            new AiProviderUsage(1, 1, 2, false),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            checkpointPort: checkpoints);

        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var processed = await scheduler.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.TerminalError, processed.Disposition);
        Assert.Equal("AI_RESULT_STORE_REQUIRED", processed.ErrorCode);
        Assert.Equal("TerminalError", (await store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None))!.Status);
        Assert.DoesNotContain(checkpoints.Commands, command =>
            command.Graphs.Any(graph => graph.Request.Status == "Validated"));
    }

    [Fact]
    public async Task FormalCacheHitCommitsOriginUsageCacheAndValidatedStateInOneGraph()
    {
        var store = new MemoryStore();
        var results = new RecordingResultStore();
        var cache = await SeedReusableCacheAsync(store, results, Request());
        var checkpoints = new RecordingCheckpointPort(store);
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            new SequenceProvider(_ => throw new InvalidOperationException("CACHE_MUST_NOT_CALL_PROVIDER")),
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            resultStore: results,
            checkpointPort: checkpoints);

        var hit = await scheduler.EnqueueAsync(
            Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.CacheHit, hit.Disposition);
        var final = checkpoints.Commands.Last();
        var graph = Assert.Single(final.Graphs);
        Assert.Equal("Validated", graph.Request.Status);
        Assert.Equal(AiAnalysisSessionStatus.Validated, graph.Session.Status);
        Assert.Equal(cache.UsageEventId, graph.Usage!.UsageEventId);
        Assert.Equal(cache, graph.Cache);
        Assert.Single(await store.LoadUsageAsync("request_01", CancellationToken.None));
    }

    [Fact]
    public async Task FormalCacheHitCheckpointFailureLeavesAnalysingWithoutPartialUsageOrValidation()
    {
        var store = new MemoryStore();
        var results = new RecordingResultStore();
        _ = await SeedReusableCacheAsync(store, results, Request());
        var checkpoints = new RecordingCheckpointPort(
            store,
            command => command.Graphs.Any(graph => graph.Request.Status == "Validated"));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            new SequenceProvider(_ => throw new InvalidOperationException("CACHE_MUST_NOT_CALL_PROVIDER")),
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            resultStore: results,
            checkpointPort: checkpoints);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.EnqueueAsync(
            Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None));

        Assert.Equal("TEST_CHECKPOINT_FAILURE", exception.Message);
        Assert.Equal("Analysing", (await store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None))!.Status);
        Assert.Equal(AiAnalysisSessionStatus.Analysing,
            (await store.LoadSessionAsync("session-request_01", CancellationToken.None))!.Status);
        Assert.Single(await store.LoadUsageAsync("request_01", CancellationToken.None));
    }

    [Fact]
    public async Task FormalDifferentRequestFingerprintHitReturnsCacheOwnerWithoutCreatingNewGraphOrUsage()
    {
        var store = new MemoryStore();
        var results = new RecordingResultStore();
        var checkpoints = new RecordingCheckpointPort(store);
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"), new AiProviderUsage(1, 1, 2, false), TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            resultStore: results,
            checkpointPort: checkpoints);
        await scheduler.EnqueueAsync(
            Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.Completed,
            (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);
        var commandsBeforeHit = checkpoints.Commands.Count;
        var equivalent = Request() with { RequestId = "request_02" };

        var hit = await scheduler.EnqueueAsync(
            equivalent, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.CacheHit, hit.Disposition);
        Assert.Equal("request_01", hit.RequestId);
        Assert.Equal(commandsBeforeHit, checkpoints.Commands.Count);
        Assert.Null(await store.LoadSessionAsync("session-request_02", CancellationToken.None));
        Assert.Empty(await store.LoadUsageAsync("request_02", CancellationToken.None));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task RepeatedReadyBatchReleasesFreshPreparedLeasesWhenWorkIsAlreadyPending()
    {
        var store = new MemoryStore();
        var copies = new RecordingCopyPreparer();
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            new SequenceProvider(_ => AiAnalysisExecutionResult.Failure(
                AiProviderOutcome.AiResultUnknown,
                new ContractError("UNKNOWN", false, "provider", "请人工确认"),
                TimeSpan.Zero)),
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            copyPreparer: copies,
            copyLeaseManager: copies);
        var now = DateTimeOffset.UtcNow;
        var revision = TestReadyRevision(now);
        var snapshot = BatchTemplateSnapshot.Create("snapshot_batch", "batch_multi", revision, ["page_01"], now);
        var batch = BatchDraft.Create("batch_multi", @"C:\input", @"C:\output", snapshot, BatchSelectionMode.Automatic, false, now)
            .BeginScanning(now.AddSeconds(1))
            .MarkScanReady(
                BatchInputType.SingleStyle,
                [StyleDraft.Create("style_a", "SKU-A", "", 0, ["asset_a"])],
                [SourceAssetMetadata.CreateReadable("asset_a", "style_a", "1.jpg", "1.jpg", 10, new string('a', 64), 100, 100, SourceImageFormat.Jpeg, "image/jpeg")],
                "batches/batch_multi/asset-manifest/manifest.json",
                "sha256:" + new string('f', 64),
                now.AddSeconds(2));
        var options = new AiAnalysisProjectionOptions(
            "corr_batch", "consent_batch", "model/proposed", "prompt/v1", "copy/v1");

        var first = await scheduler.EnqueueBatchAsync(batch, revision, options, CancellationToken.None);
        var duplicate = await scheduler.EnqueueBatchAsync(batch, revision, options, CancellationToken.None);

        Assert.Equal(1, first.EnqueuedCount);
        Assert.Equal(1, duplicate.DuplicateCount);
        Assert.Equal(2, copies.PrepareCalls);
        var lease = Assert.Single(copies.Leases("analysis-copy-asset_a"));
        Assert.StartsWith("lease-work-", lease, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialCopyPreparationFailureReleasesEveryLeasePreparedBeforeFailure()
    {
        var store = new MemoryStore();
        var copies = new RecordingCopyPreparer(failOnPrepareCall: 2);
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            new SequenceProvider(_ => throw new InvalidOperationException("PROVIDER_NOT_EXPECTED")),
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            copyPreparer: copies,
            copyLeaseManager: copies);
        var now = DateTimeOffset.UtcNow;
        var (batch, revision) = ReadyBatch(
            now,
            SourceAssetMetadata.CreateReadable("asset_a", "style_a", "1.jpg", "1.jpg", 10, new string('a', 64), 100, 100, SourceImageFormat.Jpeg, "image/jpeg"),
            SourceAssetMetadata.CreateReadable("asset_b", "style_a", "2.jpg", "2.jpg", 10, new string('b', 64), 100, 100, SourceImageFormat.Jpeg, "image/jpeg"));

        var exception = await Assert.ThrowsAsync<IOException>(() => scheduler.EnqueueBatchAsync(
            batch,
            revision,
            new AiAnalysisProjectionOptions("corr_batch", "consent_batch", "model/proposed", "prompt/v1", "copy/v1"),
            CancellationToken.None));

        Assert.Equal("TEST_COPY_PREPARE_FAILURE", exception.Message);
        Assert.Empty(copies.Leases("analysis-copy-asset_a"));
        Assert.Empty(copies.Leases("analysis-copy-asset_b"));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task CancelledSchedulerReleasesFreshBatchPreparationLeaseBeforeReturning()
    {
        var store = new MemoryStore();
        var copies = new RecordingCopyPreparer();
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            new SequenceProvider(_ => throw new InvalidOperationException("PROVIDER_NOT_EXPECTED")),
            store,
            queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            copyPreparer: copies,
            copyLeaseManager: copies);
        Assert.Equal(0, await scheduler.CancelAsync(null, CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        var (batch, revision) = ReadyBatch(
            now,
            SourceAssetMetadata.CreateReadable("asset_a", "style_a", "1.jpg", "1.jpg", 10, new string('a', 64), 100, 100, SourceImageFormat.Jpeg, "image/jpeg"));

        var result = await scheduler.EnqueueBatchAsync(
            batch,
            revision,
            new AiAnalysisProjectionOptions("corr_batch", "consent_batch", "model/proposed", "prompt/v1", "copy/v1"),
            CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.Cancelled, Assert.Single(result.StyleResults).Disposition);
        Assert.Empty(copies.Leases("analysis-copy-asset_a"));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task FormalSchedulerRejectsMixedBatchWithoutPoisoningSeparatelyKeyedBatchSchedulers()
    {
        var store = new MemoryStore();
        var checkpoints = new RecordingCheckpointPort(store);
        using var firstQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var first = new AiAnalysisScheduler(
            new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
                Result("request_01"), new AiProviderUsage(1, 1, 2, false), TimeSpan.Zero)),
            store,
            firstQueue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            stateKey: "batch-batch_01",
            resultStore: new RecordingResultStore(),
            checkpointPort: checkpoints);
        var secondRequest = Request() with
        {
            RequestId = "request_02",
            BatchId = "batch_02",
            StyleId = "style_02",
            RequirementsDigest = "sha256:" + new string('d', 64)
        };

        Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
            (await first.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => first.EnqueueAsync(
            secondRequest, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None));
        Assert.Equal("AI_CHECKPOINT_BATCH_SCOPE_INVALID", conflict.Message);
        Assert.Equal(1, firstQueue.Count);

        using var secondQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var second = new AiAnalysisScheduler(
            new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
                Result("request_02") with { ModelConfigVersion = secondRequest.ModelConfigVersion },
                new AiProviderUsage(1, 1, 2, false),
                TimeSpan.Zero)),
            store,
            secondQueue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            stateKey: "batch-batch_02",
            resultStore: new RecordingResultStore(),
            checkpointPort: checkpoints);
        Assert.Equal(AiAnalysisWorkDisposition.Enqueued,
            (await second.EnqueueAsync(secondRequest, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None)).Disposition);
        Assert.Equal(1, secondQueue.Count);
        Assert.All(checkpoints.Commands, command =>
            Assert.All(command.Graphs, graph => Assert.Equal(command.Manifest.BatchId, graph.Request.BatchId)));
    }

    [Fact]
    public async Task CacheInvalidationIsExplicitAndAllowsFreshWork()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"),
            new AiProviderUsage(10, 20, 30, false),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));

        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var key = AiAnalysisCacheKeyFactory.Create(Request());
        Assert.Equal(AiAnalysisWorkDisposition.Completed, (await scheduler.ProcessNextAsync(CancellationToken.None)).Disposition);
        Assert.True(await scheduler.InvalidateCacheAsync(key, CancellationToken.None));
        Assert.Null(await store.FindCacheReferenceAsync(key, CancellationToken.None));
        Assert.False(await scheduler.InvalidateCacheAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task RefinementOnlyQueuesCallerConfirmedAssetsAndOptionalSizeSource()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Failure(
            AiProviderOutcome.AiResultUnknown,
            new ContractError("UNKNOWN", false, "provider", "请人工确认"),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(4, 1));
        var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));

        var queued = await scheduler.EnqueueRefinementAsync(
            Request(),
            ["asset_02"],
            includeSizeTableSource: true,
            CancellationToken.None);

        Assert.Equal(2, queued.Count);
        Assert.All(queued, result => Assert.Equal(AiAnalysisWorkDisposition.Enqueued, result.Disposition));
        var first = await queue.DequeueAsync(CancellationToken.None);
        queue.Complete(first);
        var second = await queue.DequeueAsync(CancellationToken.None);
        queue.Complete(second);
        Assert.Equal(AiAnalysisKind.SelectedAssetGeometry, first.Request.AnalysisKind);
        Assert.Equal(AiAnalysisKind.SizeTableExtraction, second.Request.AnalysisKind);
        Assert.All(new[] { first, second }, item => Assert.Equal("asset_02", Assert.Single(item.Request.Assets).AssetId));
    }

    [Fact]
    public async Task HardBudgetStopsBeforeProviderCallAfterRequestLimit()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"),
            new AiProviderUsage(1, 1, 2, false),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(1, 1));
        var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(1, 1, 1, 0.01m, 0.005m, new AiAnalysisPricing(1m, 1m)));

        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var completed = await scheduler.ProcessNextAsync(CancellationToken.None);
        var secondRequest = Request() with
        {
            RequestId = "request_02",
            RequirementsDigest = "sha256:" + new string('d', 64)
        };
        await scheduler.EnqueueAsync(secondRequest, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var result = await scheduler.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.Completed, completed.Disposition);
        Assert.Equal(AiAnalysisWorkDisposition.BudgetStopped, result.Disposition);
        Assert.Equal("AI_BUDGET_HARD_STOP", result.ErrorCode);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task PendingWorkAndBudgetSnapshotRestoreAfterRestart()
    {
        var store = new MemoryStore();
        var schedulingState = new MemorySchedulingStateStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Failure(
            AiProviderOutcome.RetryableError,
            new ContractError("RATE", true, "provider", "稍后重试"),
            TimeSpan.Zero));
        var policy = new AiAnalysisBudgetPolicy(
            4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m));

        using (var firstQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2)))
        using (var firstScheduler = new AiAnalysisScheduler(
                   provider, store, firstQueue, policy,
                   stateStore: schedulingState))
        {
            await firstScheduler.EnqueueAsync(
                Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
            var retry = await firstScheduler.ProcessNextAsync(CancellationToken.None);
            Assert.Equal(AiAnalysisWorkDisposition.RetryScheduled, retry.Disposition);
            Assert.Equal(1, firstScheduler.Budget.RequestCount);
        }

        using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
        using var reopenedScheduler = new AiAnalysisScheduler(
            provider, store, reopenedQueue, policy, stateStore: schedulingState);
        var restored = await reopenedScheduler.RestoreAsync(CancellationToken.None);

        Assert.Equal(1, restored);
        Assert.Equal(1, reopenedQueue.Count);
        Assert.Equal(1, reopenedScheduler.Budget.RequestCount);
        Assert.Single((await schedulingState.LoadSchedulingStateAsync(
            "ai-s3-default", CancellationToken.None))!.PendingItems);
    }

    [Fact]
    public async Task ReadyBatchProducerQueuesOneJointRequestPerStyleAndPreservesOrder()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Failure(
            AiProviderOutcome.AiResultUnknown,
            new ContractError("UNKNOWN", false, "provider", "请人工确认"),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(4, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider,
            store,
            queue,
            new AiAnalysisBudgetPolicy(4, 1000, 1000, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));
        var now = DateTimeOffset.UtcNow;
        var revision = TestReadyRevision(now);
        var snapshot = BatchTemplateSnapshot.Create("snapshot_batch", "batch_multi", revision, ["page_01"], now);
        var batch = BatchDraft.Create("batch_multi", @"C:\\input", @"C:\\output", snapshot, BatchSelectionMode.Automatic, false, now)
            .BeginScanning(now.AddSeconds(1))
            .MarkScanReady(
                BatchInputType.StyleDirectoryBatch,
                [
                    StyleDraft.Create("style_b", "SKU-B", "b", 1, ["asset_b"]),
                    StyleDraft.Create("style_a", "SKU-A", "a", 0, ["asset_a"])
                ],
                [
                    SourceAssetMetadata.CreateReadable("asset_b", "style_b", "b/2.jpg", "2.jpg", 10, new string('b', 64), 100, 100, SourceImageFormat.Jpeg, "image/jpeg"),
                    SourceAssetMetadata.CreateReadable("asset_a", "style_a", "a/1.jpg", "1.jpg", 10, new string('a', 64), 100, 100, SourceImageFormat.Jpeg, "image/jpeg")
                ],
                "batches/batch_multi/asset-manifest/manifest.json",
                "sha256:" + new string('f', 64),
                now.AddSeconds(2));

        var result = await scheduler.EnqueueBatchAsync(
            batch,
            revision,
            new AiAnalysisProjectionOptions("corr_batch", "consent_batch", "model/proposed", "prompt/v1", "copy/v1"),
            CancellationToken.None);

        Assert.Equal("batch_multi", result.BatchId);
        Assert.Equal(2, result.EnqueuedCount);
        Assert.Equal(2, queue.Count);
        var first = await queue.DequeueAsync(CancellationToken.None);
        queue.Complete(first);
        var second = await queue.DequeueAsync(CancellationToken.None);
        queue.Complete(second);
        Assert.Equal("style_a", first.Request.StyleId);
        Assert.Equal("style_b", second.Request.StyleId);
        Assert.All(new[] { first, second }, item => Assert.Equal(AiAnalysisKind.ProductJointUnderstanding, item.Request.AnalysisKind));
    }

    [Fact]
    public async Task PauseWaitsWithoutProviderCallAndResumeRequeuesPendingWork()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(
            Result("request_01"), new AiProviderUsage(1, 1, 2, false), TimeSpan.Zero));
        var stateStore = new MemorySchedulingStateStore();
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            stateStore: stateStore);

        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        await scheduler.RequestPauseAsync("用户请求暂停", CancellationToken.None);
        var paused = await scheduler.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.Paused, paused.Disposition);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(AiAnalysisSchedulingControlState.Paused, scheduler.ControlState);
        await scheduler.ResumeAsync(CancellationToken.None);
        Assert.Equal(1, queue.Count);
        var completed = await scheduler.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.Completed, completed.Disposition);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CancelAsyncCancelsOnlyPendingLedgerAndPersistsTerminalControl()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(Result("request_01"), null, TimeSpan.Zero));
        var stateStore = new MemorySchedulingStateStore();
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            stateStore: stateStore);
        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var cancelled = await scheduler.CancelAsync("batch_01", CancellationToken.None);
        Assert.Equal(1, cancelled);
        Assert.Equal(AiAnalysisSchedulingControlState.Cancelled, scheduler.ControlState);
        var ledger = await store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None);
        Assert.Equal("Cancelled", ledger!.Status);
        var persisted = await stateStore.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None);
        Assert.Equal(AiAnalysisSchedulingControlState.Cancelled, persisted!.ControlState);
        Assert.Empty(persisted.PendingItems);
    }

    [Fact]
    public async Task RestoreCancelRequestedCompletesQueuedCancellationWithoutProviderCall()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(Result("request_01"), null, TimeSpan.Zero));
        var stateStore = new MemorySchedulingStateStore();
        var now = DateTimeOffset.UtcNow;
        using (var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1)))
        using (var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            timeProvider: new FixedTimeProvider(now),
            stateStore: stateStore))
        {
            await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        }

        var persisted = await stateStore.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None);
        Assert.NotNull(persisted);
        await stateStore.SaveSchedulingStateAsync(
            "ai-s3-default",
            persisted! with
            {
                ControlState = AiAnalysisSchedulingControlState.CancelRequested,
                ControlReason = "AI_CANCEL_REQUESTED",
                UpdatedAt = now.AddSeconds(1)
            },
            CancellationToken.None);

        using var reopenedQueue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var reopened = new AiAnalysisScheduler(
            provider, store, reopenedQueue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)),
            timeProvider: new FixedTimeProvider(now.AddSeconds(2)),
            stateStore: stateStore);

        var restored = await reopened.RestoreAsync(CancellationToken.None);

        Assert.Equal(0, restored);
        Assert.Equal(0, reopenedQueue.Count);
        Assert.Equal(0, provider.Calls);
        var ledger = await store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None);
        Assert.Equal("Cancelled", ledger!.Status);
        var after = await stateStore.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None);
        Assert.NotNull(after);
        Assert.Empty(after!.PendingItems);
    }

    [Fact]
    public async Task WaitingForNetworkBlocksNewWorkWithoutChangingLedger()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Completed(Result("request_01"), null, TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));
        await scheduler.WaitForNetworkAsync("网络暂不可用", CancellationToken.None);
        var result = await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        Assert.Equal(AiAnalysisWorkDisposition.WaitingForNetwork, result.Disposition);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("Queued", (await store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ProviderExceptionIsPersistedAsUnknownInsteadOfEscapingOrRetrying()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => throw new InvalidOperationException("TEST_PROVIDER_CRASH"));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(2, 2));
        using var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(2, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));

        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        var result = await scheduler.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(AiAnalysisWorkDisposition.ResultUnknown, result.Disposition);
        Assert.Equal("AI_PROVIDER_EXCEPTION_UNKNOWN", result.ErrorCode);
        Assert.Equal(AiAnalysisSessionStatus.AiResultUnknown,
            (await store.LoadSessionAsync("session-request_01", CancellationToken.None))!.Status);
        Assert.Equal("AiResultUnknown",
            (await store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(Request()), CancellationToken.None))!.Status);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task BatchScopedCancellationCannotAffectOtherPendingBatch()
    {
        var store = new MemoryStore();
        var provider = new SequenceProvider(_ => AiAnalysisExecutionResult.Failure(
            AiProviderOutcome.AiResultUnknown,
            new ContractError("UNKNOWN", false, "provider", "请人工确认"),
            TimeSpan.Zero));
        using var queue = new BoundedAiAnalysisWorkBuffer(new AiAnalysisQueueOptions(4, 1));
        using var scheduler = new AiAnalysisScheduler(
            provider, store, queue,
            new AiAnalysisBudgetPolicy(4, 100, 100, 10m, 1m, new AiAnalysisPricing(0.01m, 0.02m)));

        var other = Request() with
        {
            RequestId = "request_02",
            BatchId = "batch_02",
            StyleId = "style_02",
            RequirementsDigest = "sha256:" + new string('d', 64)
        };
        await scheduler.EnqueueAsync(Request(), AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);
        await scheduler.EnqueueAsync(other, AiAnalysisWorkStage.ProductJointUnderstanding, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.CancelAsync("batch_01", CancellationToken.None));

        Assert.Equal("AI_SCHEDULER_BATCH_SCOPE_CONFLICT", exception.Message);
        Assert.Equal(AiAnalysisSchedulingControlState.Running, scheduler.ControlState);
        Assert.Equal(2, queue.Count);
    }

    private static TemplateRevision TestReadyRevision(DateTimeOffset now)
    {
        var page = TemplatePage.Create("page_01", "revision_batch", 0, "页面", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, "resource_01");
        var binding = FrameBinding.Create(
            "frame_01", "page_01", LayerPath.Create([1]),
            ExpectedLayerEvidence.Create(1, "frame", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('c', 64))), [],
            SelectionRequirement.Create("商品图", [SelectionCriterion.Create("asset_id", "asset_a")]),
            LayoutIntent.Create("主体完整", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100)));
        return TemplateRevision.Restore("revision_batch", "group_batch", 1, TemplateRevisionState.Committed,
            "templates/revision_batch/manifest.json", "sha256:" + new string('d', 64), now, [page], [binding]);
    }

    private static (BatchDraft Batch, TemplateRevision Revision) ReadyBatch(
        DateTimeOffset now,
        params SourceAssetMetadata[] assets)
    {
        var revision = TestReadyRevision(now);
        var snapshot = BatchTemplateSnapshot.Create("snapshot_batch", "batch_multi", revision, ["page_01"], now);
        var style = StyleDraft.Create(
            "style_a", "SKU-A", "", 0, assets.Select(asset => asset.AssetId));
        var batch = BatchDraft.Create(
                "batch_multi", @"C:\input", @"C:\output", snapshot, BatchSelectionMode.Automatic, false, now)
            .BeginScanning(now.AddSeconds(1))
            .MarkScanReady(
                BatchInputType.SingleStyle,
                [style],
                assets,
                "batches/batch_multi/asset-manifest/manifest.json",
                "sha256:" + new string('f', 64),
                now.AddSeconds(2));
        return (batch, revision);
    }

    private static async Task<AiCacheReference> SeedReusableCacheAsync(
        MemoryStore store,
        RecordingResultStore results,
        AiAnalysisRequestV1 request)
    {
        var resultReference = $"results/{request.RequestId}";
        await results.SaveAsync(resultReference, Result(request.RequestId), CancellationToken.None);
        var recordedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var usage = new AiUsageEventRecord(
            $"usage-origin-{request.RequestId}",
            request.RequestId,
            request.ModelConfigVersion,
            1,
            1,
            false,
            0,
            null,
            null,
            recordedAt);
        await store.RecordUsageAsync(usage, CancellationToken.None);
        var cache = new AiCacheReference(
            AiAnalysisCacheKeyFactory.Create(request),
            resultReference,
            request.ConsentId,
            request.ModelConfigVersion,
            usage.UsageEventId,
            recordedAt.AddTicks(1));
        await store.SaveCacheReferenceAsync(cache, CancellationToken.None);
        return cache;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

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
        "compression/v1",
        "consent_01",
        "sha256:" + new string('a', 64),
        [
            new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copy_01"),
            new AiAssetInputV1("asset_02", "sha256:" + new string('c', 64), "image/jpeg", 10, 10, "copy_02")
        ],
        [new AiRequirementInputV1("frame_01", "商品图", "居中", true)]);

    private static AiAnalysisResultV1 Result(string requestId) => new(
        AiAnalysisProtocol.ResultSchemaVersion,
        "result",
        requestId,
        null,
        "model/proposed",
        [],
        [],
        [],
        null,
        [],
        new AiUsageSummaryV1("usage_" + requestId, 10, 20, 1, null, null));

    private sealed class SequenceProvider(params Func<AiAnalysisRequestV1, AiAnalysisExecutionResult>[] steps) : IAiServicePort
    {
        private readonly Func<AiAnalysisRequestV1, AiAnalysisExecutionResult>[] _steps = steps;
        public int Calls { get; private set; }
        public List<AiAnalysisRequestV1> Seen { get; } = [];

        public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Seen.Add(request);
            var index = Math.Min(Calls++, _steps.Length - 1);
            return Task.FromResult(_steps[index](request));
        }
    }

    private sealed class MemoryStore : IAiAnalysisStore
    {
        private readonly Dictionary<string, AiRequestLedgerEntry> _ledgers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AiAnalysisSession> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AiCacheReference> _caches = new(StringComparer.Ordinal);
        private readonly List<AiUsageEventRecord> _usage = [];

        public Task<AiRequestLedgerEntry?> FindRequestByFingerprintAsync(string fingerprint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_ledgers.Values.SingleOrDefault(entry => entry.Fingerprint == fingerprint));
        }

        public Task<AiRequestLedgerEntry> RecordRequestAsync(AiRequestLedgerEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ledgers.TryGetValue(entry.RequestId, out var existing))
            {
                return Task.FromResult(existing);
            }

            var byFingerprint = _ledgers.Values.SingleOrDefault(item => item.Fingerprint == entry.Fingerprint);
            if (byFingerprint is not null)
            {
                return Task.FromResult(byFingerprint);
            }

            _ledgers.Add(entry.RequestId, entry);
            return Task.FromResult(entry);
        }

        public Task SaveSessionAsync(AiAnalysisSession session, CancellationToken cancellationToken, long? expectedSelectionRevision = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessions.TryGetValue(session.SessionId, out var existing)
                && expectedSelectionRevision is not null
                && existing.SelectionRevision != expectedSelectionRevision)
            {
                throw new InvalidOperationException("CAS");
            }

            _sessions[session.SessionId] = session;
            return Task.CompletedTask;
        }

        public Task<AiRequestLedgerEntry> UpdateRequestStatusAsync(string requestId, string expectedStatus, string nextStatus, string? resultReference, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = _ledgers[requestId];
            if (existing.Status != expectedStatus)
            {
                throw new InvalidOperationException("STATUS");
            }

            var next = existing with { Status = nextStatus, ResultReference = resultReference, UpdatedAt = updatedAt };
            _ledgers[requestId] = next;
            return Task.FromResult(next);
        }

        public Task<AiAnalysisSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task SaveCacheReferenceAsync(AiCacheReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _caches[reference.CacheKey] = reference;
            return Task.CompletedTask;
        }

        public Task<bool> InvalidateCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_caches.Remove(cacheKey));
        }

        public Task<AiCacheReference?> FindCacheReferenceAsync(string cacheKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _caches.TryGetValue(cacheKey, out var cache);
            return Task.FromResult(cache);
        }

        public Task RecordUsageAsync(AiUsageEventRecord usage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _usage.Add(usage);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiUsageEventRecord>> LoadUsageAsync(string requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AiUsageEventRecord>>(_usage.Where(item => item.RequestId == requestId).ToArray());
        }

        public Task<AiUsageEventRecord?> FindUsageEventAsync(string usageEventId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AiUsageEventRecord?>(_usage.SingleOrDefault(item => item.UsageEventId == usageEventId));
        }
    }

    private sealed class MemorySchedulingStateStore : IAiAnalysisSchedulingStateStore
    {
        private readonly Dictionary<string, AiAnalysisSchedulingState> _states = new(StringComparer.Ordinal);

        public Task<AiAnalysisSchedulingState?> LoadSchedulingStateAsync(
            string stateKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _states.TryGetValue(stateKey, out var state);
            return Task.FromResult(state);
        }

        public Task SaveSchedulingStateAsync(
            string stateKey,
            AiAnalysisSchedulingState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _states[stateKey] = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCopyPreparer(int? failOnPrepareCall = null) : IAiAnalysisCopyPreparer, IAiAnalysisCopyLeaseManager
    {
        private readonly Dictionary<string, HashSet<string>> _leases = new(StringComparer.Ordinal);
        private int _leaseSequence;

        public int PrepareCalls { get; private set; }

        public Task<AiAnalysisCopyMetadata> PrepareAsync(
            AiAnalysisCopyPreparationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            if (PrepareCalls == failOnPrepareCall)
            {
                throw new IOException("TEST_COPY_PREPARE_FAILURE");
            }
            var leaseId = $"lease-prepared-{++_leaseSequence}";
            if (!_leases.TryGetValue(request.AnalysisCopyReference, out var leases))
            {
                leases = new HashSet<string>(StringComparer.Ordinal);
                _leases.Add(request.AnalysisCopyReference, leases);
            }
            leases.Add(leaseId);
            return Task.FromResult(new AiAnalysisCopyMetadata(
                request.AnalysisCopyReference,
                "sha256:" + request.SourceAsset.Sha256.ToLowerInvariant(),
                request.SourceAsset.Length,
                request.SourceAsset.MimeType,
                request.SourceAsset.PixelWidth,
                request.SourceAsset.PixelHeight,
                request.CompressionProfileVersion,
                leaseId,
                leases.Order(StringComparer.Ordinal).ToArray()));
        }

        public Task AcquireAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_leases.TryGetValue(analysisCopyReference, out var leases))
            {
                leases = new HashSet<string>(StringComparer.Ordinal);
                _leases.Add(analysisCopyReference, leases);
            }
            leases.Add(leaseId);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_leases.TryGetValue(analysisCopyReference, out var leases)) leases.Remove(leaseId);
            return Task.CompletedTask;
        }

        public HashSet<string> Leases(string analysisCopyReference) =>
            _leases.TryGetValue(analysisCopyReference, out var leases) ? leases : [];
    }

    private sealed class RecordingCheckpointPort(
        MemoryStore store,
        Func<AiAnalysisCheckpointCommand, bool>? shouldFail = null) : IAiAnalysisCheckpointPort
    {
        public List<AiAnalysisCheckpointCommand> Commands { get; } = [];

        public async Task<AiAnalysisCheckpointResult> CommitAsync(AiAnalysisCheckpointCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (shouldFail?.Invoke(command) == true)
            {
                throw new InvalidOperationException("TEST_CHECKPOINT_FAILURE");
            }
            foreach (var graph in command.Graphs)
            {
                var existing = await store.FindRequestByFingerprintAsync(graph.Request.Fingerprint, cancellationToken);
                if (existing is null)
                {
                    await store.RecordRequestAsync(graph.Request, cancellationToken);
                }
                else if (!Equals(existing, graph.Request))
                {
                    await store.UpdateRequestStatusAsync(
                        graph.Request.RequestId,
                        graph.ExpectedRequestStatus,
                        graph.Request.Status,
                        graph.Request.ResultReference,
                        graph.Request.UpdatedAt,
                        cancellationToken);
                }

                await store.SaveSessionAsync(graph.Session, cancellationToken, graph.ExpectedSelectionRevision);
                if (graph.Usage is not null
                    && await store.FindUsageEventAsync(graph.Usage.UsageEventId, cancellationToken) is null)
                {
                    await store.RecordUsageAsync(graph.Usage, cancellationToken);
                }
                if (graph.Cache is not null) await store.SaveCacheReferenceAsync(graph.Cache, cancellationToken);
            }

            return new AiAnalysisCheckpointResult(
                command.CheckpointId,
                command.Manifest.BatchId,
                AiAnalysisCheckpointOutcome.Committed,
                "TEST_COMMITTED");
        }

        public Task<AiAnalysisCheckpointResult> RecoverAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult(new AiAnalysisCheckpointResult(null, batchId, AiAnalysisCheckpointOutcome.NothingToRecover, "TEST_NONE"));
    }

    private sealed class RecordingResultStore : IAiAnalysisResultStore
    {
        private readonly Dictionary<string, AiAnalysisResultV1> _results = new(StringComparer.Ordinal);
        public Task SaveAsync(string resultReference, AiAnalysisResultV1 result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _results[resultReference] = result;
            return Task.CompletedTask;
        }
        public Task<AiAnalysisResultV1?> LoadAsync(string resultReference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _results.TryGetValue(resultReference, out var result);
            return Task.FromResult(result);
        }

        public void Remove(string resultReference) => _results.Remove(resultReference);
    }
}
