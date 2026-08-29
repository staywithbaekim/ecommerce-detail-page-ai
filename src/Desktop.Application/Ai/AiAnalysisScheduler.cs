using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;

namespace Desktop.Application.Ai;

/// <summary>
/// Bounded S3 producer/consumer orchestration. Provider details remain behind
/// IAiServicePort and all business progress is persisted through IAiAnalysisStore.
/// </summary>
public sealed class AiAnalysisScheduler : IDisposable
{
    private readonly IAiServicePort _provider;
    private readonly IAiAnalysisStore _store;
    private readonly IAiAnalysisWorkBuffer _queue;
    private readonly AiAnalysisQueueOptions _queueOptions;
    private readonly AiAnalysisBudgetPolicy _budgetPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IAiAnalysisSchedulingStateStore? _stateStore;
    private readonly string _stateKey;
    private readonly object _budgetGate = new();
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, AiAnalysisWorkItem> _pendingItems = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _statePersistenceGate = new(1, 1);
    private AiAnalysisBudgetSnapshot _budget = AiAnalysisBudgetSnapshot.Empty;

    public AiAnalysisScheduler(
        IAiServicePort provider,
        IAiAnalysisStore store,
        IAiAnalysisWorkBuffer queue,
        AiAnalysisBudgetPolicy budgetPolicy,
        TimeProvider? timeProvider = null,
        AiAnalysisQueueOptions? queueOptions = null,
        IAiAnalysisSchedulingStateStore? stateStore = null,
        string stateKey = "ai-s3-default")
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _budgetPolicy = budgetPolicy ?? throw new ArgumentNullException(nameof(budgetPolicy));
        _budgetPolicy.Validate();
        _queueOptions = queueOptions ?? new AiAnalysisQueueOptions();
        _queueOptions.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stateStore = stateStore;
        if (string.IsNullOrWhiteSpace(stateKey)
            || stateKey.Length > 128
            || stateKey.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("AI 调度状态键无效。", nameof(stateKey));
        }

        _stateKey = stateKey;
    }

    public AiAnalysisBudgetSnapshot Budget
    {
        get
        {
            lock (_budgetGate)
            {
                return _budget;
            }
        }
    }

    public Task<bool> InvalidateCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken) =>
        _store.InvalidateCacheReferenceAsync(cacheKey, cancellationToken);

    /// <summary>
    /// Restores durable pending work and the local budget snapshot after a
    /// process restart. In-flight provider work is never guessed as success:
    /// only queued/retryable ledger entries are re-enqueued.
    /// </summary>
    public async Task<int> RestoreAsync(CancellationToken cancellationToken)
    {
        if (_stateStore is null)
        {
            return 0;
        }

        var state = await _stateStore.LoadSchedulingStateAsync(_stateKey, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return 0;
        }

        ValidateBudgetSnapshot(state.Budget);
        lock (_budgetGate)
        {
            _budget = NormalizeBudgetSnapshot(state.Budget);
        }

        var restored = 0;
        foreach (var item in state.PendingItems ?? [])
        {
            ValidateWorkItem(item);
            var fingerprint = AiAnalysisCacheKeyFactory.Create(item.Request);
            var ledger = await _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false);
            if (ledger is null)
            {
                throw new InvalidOperationException("AI_SCHEDULER_PENDING_LEDGER_MISSING");
            }

            if (ledger.Status is "Validated" or "ManualReview" or "PlanClosed"
                or "Cancelled" or "TerminalError" or "AiResultUnknown")
            {
                lock (_pendingGate)
                {
                    _pendingItems.Remove(item.WorkItemId);
                }

                continue;
            }

            if (ledger.Status is not ("Queued" or "RetryableError"))
            {
                // Uploading/Analysing may have reached the provider before a
                // crash. Retrying could duplicate a billable request; retain
                // the item as unresolved recovery evidence without requeueing.
                lock (_pendingGate)
                {
                    _pendingItems[item.WorkItemId] = item;
                }

                continue;
            }

            lock (_pendingGate)
            {
                if (_pendingItems.ContainsKey(item.WorkItemId))
                {
                    continue;
                }

                _pendingItems[item.WorkItemId] = item;
            }

            if (!_queue.TryEnqueue(item))
            {
                throw new InvalidOperationException("AI_SCHEDULER_RESTORE_QUEUE_FULL");
            }

            restored++;
        }

        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        return restored;
    }

    public async Task<AiAnalysisScheduleResult> EnqueueAsync(
        AiAnalysisRequestV1 request,
        AiAnalysisWorkStage stage,
        CancellationToken cancellationToken)
    {
        ValidateStage(request, stage);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var fingerprint = AiAnalysisCacheKeyFactory.Create(request);
        AiRequestLedgerEntry existing;
        try
        {
            existing = await _store.RecordRequestAsync(
                new AiRequestLedgerEntry(
                    request.RequestId,
                    fingerprint,
                    request.BatchId,
                    request.StyleId,
                    AiAnalysisSessionStatus.Queued.ToString(),
                    null,
                    now,
                    now),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
            when (exception.Message is "AI_REQUEST_INITIAL_STATE_CONFLICT"
                or "AI_REQUEST_INITIAL_STATE_INVALID")
        {
            existing = await _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
        }

        if (!string.Equals(existing.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            var existingCache = await _store.FindCacheReferenceAsync(fingerprint, cancellationToken).ConfigureAwait(false);
            if (existingCache is not null)
            {
                return new(AiAnalysisWorkDisposition.CacheHit, existing.RequestId, existingCache.ResultReference, null, 0);
            }

            return new(AiAnalysisWorkDisposition.Duplicate, existing.RequestId, existing.ResultReference, null, 0);
        }

        if (existing.Status is "Validated" or "ManualReview" or "PlanClosed"
            or "AiResultUnknown" or "Cancelled" or "TerminalError")
        {
            RemovePendingForRequest(request.RequestId, stage);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.AlreadySettled, existing.RequestId, existing.ResultReference, null, 0);
        }

        var sessionId = SessionId(request);
        if (await _store.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            await _store.SaveSessionAsync(
                AiAnalysisSession.Create(
                    sessionId,
                    request.BatchId,
                    request.StyleId,
                    request.TemplateSnapshotId,
                    $"analysis-{request.RequestId}",
                    request.Requirements.Select(requirement => requirement.RequirementId),
                    request.Assets.Select(asset => asset.AssetId)),
                cancellationToken).ConfigureAwait(false);
        }

        var cache = await _store.FindCacheReferenceAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        if (cache is not null && await IsCacheReusableAsync(cache, request, cancellationToken).ConfigureAwait(false))
        {
            await TransitionToAsync(request, AiAnalysisSessionStatus.Uploading, cancellationToken).ConfigureAwait(false);
            await TransitionToAsync(request, AiAnalysisSessionStatus.Analysing, cancellationToken).ConfigureAwait(false);
            await RecordCacheUsageAsync(request, cache, cancellationToken).ConfigureAwait(false);
            await CompleteValidatedAsync(request, cache.ResultReference, cancellationToken).ConfigureAwait(false);
            RemovePendingForRequest(request.RequestId, stage);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.CacheHit, request.RequestId, cache.ResultReference, null, 0);
        }

        var item = new AiAnalysisWorkItem(
            $"{request.RequestId}-{stage.ToString().ToLowerInvariant()}",
            request,
            stage,
            0,
            now);
        if (_queue.IsActive(item.WorkItemId))
        {
            return new(AiAnalysisWorkDisposition.Duplicate, request.RequestId, null, null, 0);
        }

        lock (_pendingGate)
        {
            if (_pendingItems.ContainsKey(item.WorkItemId)
                || _pendingItems.Values.Any(pending =>
                    string.Equals(pending.Request.RequestId, request.RequestId, StringComparison.Ordinal)
                    && pending.Stage == stage))
            {
                return new(AiAnalysisWorkDisposition.Duplicate, request.RequestId, null, null, 0);
            }

            _pendingItems[item.WorkItemId] = item;
        }

        try
        {
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_pendingGate)
            {
                _pendingItems.Remove(item.WorkItemId);
            }

            throw;
        }

        if (!_queue.TryEnqueue(item))
        {
            lock (_pendingGate)
            {
                _pendingItems.Remove(item.WorkItemId);
            }

            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.QueueFull, request.RequestId, null, "AI_QUEUE_FULL", 0);
        }

        return new(AiAnalysisWorkDisposition.Enqueued, request.RequestId, null, null, 0);
    }

    /// <summary>
    /// Enqueues second-layer work only for caller-confirmed assets. The
    /// selected set is validated against the first-layer request, so an
    /// unselected candidate can never be uploaded by this method.
    /// </summary>
    public async Task<IReadOnlyList<AiAnalysisScheduleResult>> EnqueueRefinementAsync(
        AiAnalysisRequestV1 firstLayerRequest,
        IReadOnlyCollection<string> selectedAssetIds,
        bool includeSizeTableSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firstLayerRequest);
        ArgumentNullException.ThrowIfNull(selectedAssetIds);
        if (firstLayerRequest.AnalysisKind != AiAnalysisKind.ProductJointUnderstanding
            || selectedAssetIds.Count == 0
            || selectedAssetIds.Any(assetId => !firstLayerRequest.Assets.Any(asset => string.Equals(asset.AssetId, assetId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("AI_REFINEMENT_SELECTION_INVALID");
        }

        var selected = firstLayerRequest.Assets
            .Where(asset => selectedAssetIds.Contains(asset.AssetId, StringComparer.Ordinal))
            .OrderBy(asset => asset.AssetId, StringComparer.Ordinal)
            .ToArray();
        var results = new List<AiAnalysisScheduleResult>();
        var geometryRequest = firstLayerRequest with
        {
            RequestId = $"{firstLayerRequest.RequestId}-geometry",
            AnalysisKind = AiAnalysisKind.SelectedAssetGeometry,
            Assets = selected
        };
        results.Add(await EnqueueAsync(
            geometryRequest,
            AiAnalysisWorkStage.SelectedAssetGeometry,
            cancellationToken).ConfigureAwait(false));

        if (includeSizeTableSource)
        {
            var sizeRequest = firstLayerRequest with
            {
                RequestId = $"{firstLayerRequest.RequestId}-size",
                AnalysisKind = AiAnalysisKind.SizeTableExtraction,
                Assets = selected
            };
            results.Add(await EnqueueAsync(
                sizeRequest,
                AiAnalysisWorkStage.SizeTableExtraction,
                cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<AiAnalysisScheduleResult> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var item = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
            if (result.Disposition != AiAnalysisWorkDisposition.RetryScheduled
                && result.Disposition != AiAnalysisWorkDisposition.BudgetStopped
                && result.Disposition != AiAnalysisWorkDisposition.QueueFull)
            {
                lock (_pendingGate)
                {
                    _pendingItems.Remove(item.WorkItemId);
                }

                await PersistStateAsync(
                    cancellationToken.IsCancellationRequested
                        ? CancellationToken.None
                        : cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _queue.Complete(item);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<AiAnalysisScheduleResult> ProcessAsync(
        AiAnalysisWorkItem item,
        CancellationToken cancellationToken)
    {
        var fingerprint = AiAnalysisCacheKeyFactory.Create(item.Request);
        var ledger = await _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        if (ledger is null || !string.Equals(ledger.RequestId, item.Request.RequestId, StringComparison.Ordinal))
        {
            return Failure(item, AiAnalysisWorkDisposition.TerminalError, "AI_REQUEST_LEDGER_MISSING");
        }

        if (ledger.Status is "Validated" or "ManualReview" or "PlanClosed"
            or "Cancelled" or "TerminalError" or "AiResultUnknown")
        {
            return new(AiAnalysisWorkDisposition.AlreadySettled, item.Request.RequestId, ledger.ResultReference, null, item.Attempt);
        }

        if (ledger.Status is not ("Queued" or "RetryableError"))
        {
            return Failure(item, AiAnalysisWorkDisposition.ResultUnknown, "AI_REQUEST_IN_FLIGHT");
        }

        var reservation = await TryReserveRequestBudgetAsync(cancellationToken).ConfigureAwait(false);
        if (!reservation.Allowed)
        {
            return Failure(item, AiAnalysisWorkDisposition.BudgetStopped, reservation.ErrorCode);
        }

        await TransitionToAsync(item.Request, AiAnalysisSessionStatus.Uploading, cancellationToken).ConfigureAwait(false);
        await TransitionToAsync(item.Request, AiAnalysisSessionStatus.Analysing, cancellationToken).ConfigureAwait(false);

        AiAnalysisExecutionResult execution;
        try
        {
            execution = await _provider.AnalyzeAsync(item.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.Cancelled, "AI_REQUEST_CANCELLED", AiAnalysisSessionStatus.Cancelled, CancellationToken.None).ConfigureAwait(false);
        }

        if (execution.Usage is not null)
        {
            await AddUsageAsync(execution.Usage, cancellationToken).ConfigureAwait(false);
        }

        if (execution.Outcome == AiProviderOutcome.Completed && execution.Result is not null)
        {
            return await CompleteResultAsync(item, execution, cancellationToken).ConfigureAwait(false);
        }

        if (execution.Outcome == AiProviderOutcome.RetryableError)
        {
            if (item.Attempt + 1 < _queueOptions.MaxAttempts)
            {
                return await ScheduleRetryAsync(item, cancellationToken).ConfigureAwait(false);
            }

            return await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.TerminalError, "AI_PROVIDER_RETRY_EXHAUSTED", AiAnalysisSessionStatus.TerminalError, cancellationToken).ConfigureAwait(false);
        }

        return execution.Outcome switch
        {
            AiProviderOutcome.AiResultUnknown => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.ResultUnknown, "AI_PROVIDER_RESULT_UNKNOWN", AiAnalysisSessionStatus.AiResultUnknown, cancellationToken).ConfigureAwait(false),
            AiProviderOutcome.Cancelled => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.Cancelled, "AI_REQUEST_CANCELLED", AiAnalysisSessionStatus.Cancelled, cancellationToken).ConfigureAwait(false),
            _ => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.TerminalError, execution.Error?.Code ?? "AI_PROVIDER_ERROR", AiAnalysisSessionStatus.TerminalError, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AiAnalysisScheduleResult> CompleteResultAsync(
        AiAnalysisWorkItem item,
        AiAnalysisExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var resultReference = $"results/{item.Request.RequestId}";
        if (execution.Usage is not null)
        {
            await _store.RecordUsageAsync(
                new AiUsageEventRecord(
                    $"usage-{item.Request.RequestId}-{item.Attempt}",
                    item.Request.RequestId,
                    item.Request.ModelConfigVersion,
                    execution.Usage.InputTokens,
                    execution.Usage.OutputTokens,
                    execution.Usage.CacheHit,
                    item.Attempt,
                    null,
                    null,
                    _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }

        await CompleteValidatedAsync(item.Request, resultReference, cancellationToken).ConfigureAwait(false);
        if (execution.Usage is not null)
        {
            await _store.SaveCacheReferenceAsync(
                new AiCacheReference(
                    AiAnalysisCacheKeyFactory.Create(item.Request),
                    resultReference,
                    item.Request.ConsentId,
                    item.Request.ModelConfigVersion,
                    $"usage-{item.Request.RequestId}-{item.Attempt}",
                    NextTimestamp(_timeProvider.GetUtcNow())),
                cancellationToken).ConfigureAwait(false);
        }
        return new(AiAnalysisWorkDisposition.Completed, item.Request.RequestId, resultReference, null, item.Attempt);
    }

    private async Task CompleteValidatedAsync(
        AiAnalysisRequestV1 request,
        string resultReference,
        CancellationToken cancellationToken)
    {
        var ledger = await _store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(request),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
        if (ledger.Status != AiAnalysisSessionStatus.Analysing.ToString())
        {
            throw new InvalidOperationException("AI_REQUEST_STATUS_CONFLICT");
        }

        await _store.UpdateRequestStatusAsync(
            request.RequestId,
            AiAnalysisSessionStatus.Analysing.ToString(),
            AiAnalysisSessionStatus.Validated.ToString(),
            resultReference,
            NextTimestamp(ledger.UpdatedAt),
            cancellationToken).ConfigureAwait(false);
        await TransitionSessionOnlyAsync(request, AiAnalysisSessionStatus.Validated, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordCacheUsageAsync(
        AiAnalysisRequestV1 request,
        AiCacheReference cache,
        CancellationToken cancellationToken)
    {
        await _store.RecordUsageAsync(
            new AiUsageEventRecord(
                $"usage-cache-{request.RequestId}",
                request.RequestId,
                request.ModelConfigVersion,
                0,
                0,
                true,
                0,
                null,
                null,
                NextTimestamp(cache.CreatedAt)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AiAnalysisScheduleResult> ScheduleRetryAsync(
        AiAnalysisWorkItem item,
        CancellationToken cancellationToken)
    {
        await TransitionToAsync(item.Request, AiAnalysisSessionStatus.RetryableError, cancellationToken).ConfigureAwait(false);
        await TransitionToAsync(item.Request, AiAnalysisSessionStatus.Queued, cancellationToken).ConfigureAwait(false);
        var retry = item with
        {
            WorkItemId = $"{item.Request.RequestId}-{item.Stage.ToString().ToLowerInvariant()}-{item.Attempt + 1}",
            Attempt = item.Attempt + 1,
            EnqueuedAt = _timeProvider.GetUtcNow()
        };

        lock (_pendingGate)
        {
            _pendingItems.Remove(item.WorkItemId);
            _pendingItems[retry.WorkItemId] = retry;
        }

        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        if (!_queue.TryEnqueue(retry))
        {
            // The retry remains durably queued for the next restore attempt;
            // jumping from Queued directly to TerminalError is not a legal
            // domain transition.
            return Failure(item, AiAnalysisWorkDisposition.QueueFull, "AI_QUEUE_FULL_RETRY");
        }

        return new(AiAnalysisWorkDisposition.RetryScheduled, item.Request.RequestId, null, null, retry.Attempt);
    }

    private async Task<AiAnalysisScheduleResult> CompleteTerminalAsync(
        AiAnalysisWorkItem item,
        AiAnalysisWorkDisposition disposition,
        string errorCode,
        AiAnalysisSessionStatus status,
        CancellationToken cancellationToken)
    {
        await TransitionToAsync(item.Request, status, cancellationToken).ConfigureAwait(false);
        return Failure(item, disposition, errorCode);
    }

    private async Task TransitionToAsync(
        AiAnalysisRequestV1 request,
        AiAnalysisSessionStatus next,
        CancellationToken cancellationToken)
    {
        var ledger = await _store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(request),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
        await TransitionSessionOnlyAsync(request, next, cancellationToken).ConfigureAwait(false);
        var resultReference = next is AiAnalysisSessionStatus.Validated or AiAnalysisSessionStatus.ManualReview or AiAnalysisSessionStatus.PlanClosed
            ? ledger.ResultReference
            : null;
        await _store.UpdateRequestStatusAsync(
            request.RequestId,
            ledger.Status,
            next.ToString(),
            resultReference,
            NextTimestamp(ledger.UpdatedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionSessionOnlyAsync(
        AiAnalysisRequestV1 request,
        AiAnalysisSessionStatus next,
        CancellationToken cancellationToken)
    {
        var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
        if (session.Status == next)
        {
            return;
        }

        await _store.SaveSessionAsync(
            session.AdvanceTo(next),
            cancellationToken,
            session.SelectionRevision).ConfigureAwait(false);
    }

    private async Task<bool> IsCacheReusableAsync(
        AiCacheReference cache,
        AiAnalysisRequestV1 request,
        CancellationToken cancellationToken)
    {
        var usage = await _store.FindUsageEventAsync(cache.UsageEventId, cancellationToken).ConfigureAwait(false);
        return usage is not null
            && string.Equals(usage.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(usage.RequestId);
    }

    private async Task<(bool Allowed, string ErrorCode)> TryReserveRequestBudgetAsync(
        CancellationToken cancellationToken)
    {
        AiAnalysisBudgetSnapshot previous;
        lock (_budgetGate)
        {
            if (_budget.HardStopped
                || _budget.RequestCount >= _budgetPolicy.MaxRequests
                || _budget.InputTokens >= _budgetPolicy.MaxInputTokens
                || _budget.OutputTokens >= _budgetPolicy.MaxOutputTokens
                || _budget.EstimatedCost >= _budgetPolicy.MaxEstimatedCost)
            {
                return (false, "AI_BUDGET_HARD_STOP");
            }

            previous = _budget;
            _budget = _budget with { RequestCount = _budget.RequestCount + 1 };
        }

        try
        {
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            return (true, string.Empty);
        }
        catch
        {
            lock (_budgetGate)
            {
                _budget = previous;
            }

            return (false, "AI_BUDGET_STATE_PERSIST_FAILED");
        }
    }

    private async Task AddUsageAsync(
        AiProviderUsage usage,
        CancellationToken cancellationToken)
    {
        AiAnalysisBudgetSnapshot next;
        lock (_budgetGate)
        {
            var estimatedCost = _budgetPolicy.Pricing.Estimate(usage);
            next = _budget with
            {
                InputTokens = _budget.InputTokens + usage.InputTokens,
                OutputTokens = _budget.OutputTokens + usage.OutputTokens,
                EstimatedCost = _budget.EstimatedCost + estimatedCost
            };
            _budget = next with
            {
                WarningReached = next.EstimatedCost >= _budgetPolicy.WarningThreshold,
                HardStopped = next.EstimatedCost >= _budgetPolicy.MaxEstimatedCost
                    || next.InputTokens >= _budgetPolicy.MaxInputTokens
                    || next.OutputTokens >= _budgetPolicy.MaxOutputTokens
                    || next.RequestCount >= _budgetPolicy.MaxRequests
            };
        }

        try
        {
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Provider usage has already been observed. Fail closed so a
            // restart cannot issue further requests with an unrecorded budget.
            lock (_budgetGate)
            {
                _budget = next with { HardStopped = true };
            }
        }
    }

    private static AiAnalysisScheduleResult Failure(
        AiAnalysisWorkItem item,
        AiAnalysisWorkDisposition disposition,
        string errorCode) => new(disposition, item.Request.RequestId, null, errorCode, item.Attempt);

    private static DateTimeOffset NextTimestamp(DateTimeOffset value) => value.AddTicks(1);

    private void RemovePendingForRequest(string requestId, AiAnalysisWorkStage stage)
    {
        lock (_pendingGate)
        {
            foreach (var key in _pendingItems
                         .Where(pair => string.Equals(pair.Value.Request.RequestId, requestId, StringComparison.Ordinal)
                             && pair.Value.Stage == stage)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _pendingItems.Remove(key);
            }
        }
    }

    private async Task PersistStateAsync(CancellationToken cancellationToken)
    {
        if (_stateStore is null)
        {
            return;
        }

        AiAnalysisWorkItem[] pending;
        lock (_pendingGate)
        {
            pending = _pendingItems.Values
                .OrderBy(item => item.WorkItemId, StringComparer.Ordinal)
                .ToArray();
        }

        AiAnalysisBudgetSnapshot budget;
        lock (_budgetGate)
        {
            budget = _budget;
        }

        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stateStore.SaveSchedulingStateAsync(
                _stateKey,
                new AiAnalysisSchedulingState(pending, budget, _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _statePersistenceGate.Release();
        }
    }

    private static void ValidateWorkItem(AiAnalysisWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.WorkItemId)
            || item.WorkItemId.Length > 256
            || item.WorkItemId.Any(character => character is '\\' or '/' or '\0'))
        {
            throw new InvalidOperationException("AI_SCHEDULER_WORK_ITEM_INVALID");
        }

        if (item.Attempt < 0 || item.EnqueuedAt == default)
        {
            throw new InvalidOperationException("AI_SCHEDULER_WORK_ITEM_INVALID");
        }

        ValidateStage(item.Request, item.Stage);
        if (item.Request.Assets.Any(asset =>
                string.IsNullOrWhiteSpace(asset.AnalysisCopyReference)
                || Path.IsPathFullyQualified(asset.AnalysisCopyReference)
                || asset.AnalysisCopyReference.Contains('\\')
                || asset.AnalysisCopyReference.Contains(':')))
        {
            throw new InvalidOperationException("AI_SCHEDULER_SOURCE_PATH_INVALID");
        }
    }

    private static void ValidateBudgetSnapshot(AiAnalysisBudgetSnapshot snapshot)
    {
        if (snapshot.RequestCount < 0
            || snapshot.InputTokens < 0
            || snapshot.OutputTokens < 0
            || snapshot.EstimatedCost < 0m)
        {
            throw new InvalidDataException("AI_SCHEDULER_BUDGET_CORRUPT");
        }
    }

    private AiAnalysisBudgetSnapshot NormalizeBudgetSnapshot(AiAnalysisBudgetSnapshot snapshot)
    {
        var hardStopped = snapshot.HardStopped
            || snapshot.RequestCount >= _budgetPolicy.MaxRequests
            || snapshot.InputTokens >= _budgetPolicy.MaxInputTokens
            || snapshot.OutputTokens >= _budgetPolicy.MaxOutputTokens
            || snapshot.EstimatedCost >= _budgetPolicy.MaxEstimatedCost;
        return snapshot with
        {
            WarningReached = snapshot.WarningReached
                || snapshot.EstimatedCost >= _budgetPolicy.WarningThreshold,
            HardStopped = hardStopped
        };
    }

    private static string SessionId(AiAnalysisRequestV1 request) => $"session-{request.RequestId}";

    private static void ValidateStage(AiAnalysisRequestV1 request, AiAnalysisWorkStage stage)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expectedKind = stage switch
        {
            AiAnalysisWorkStage.ProductJointUnderstanding => AiAnalysisKind.ProductJointUnderstanding,
            AiAnalysisWorkStage.SelectedAssetGeometry => AiAnalysisKind.SelectedAssetGeometry,
            AiAnalysisWorkStage.SizeTableExtraction => AiAnalysisKind.SizeTableExtraction,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        if (!string.Equals(request.AnalysisKind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_ANALYSIS_STAGE_KIND_MISMATCH");
        }
    }

    public void Dispose() => _statePersistenceGate.Dispose();
}
