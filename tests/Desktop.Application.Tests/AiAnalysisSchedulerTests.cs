using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Ai;

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
}
