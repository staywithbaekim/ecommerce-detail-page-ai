using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

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
    private readonly IAiAnalysisResultStore? _resultStore;
    private readonly IAiAnalysisCopyPreparer? _copyPreparer;
    private readonly IAiAnalysisCopyLeaseManager? _copyLeaseManager;
    private readonly IAiAnalysisCheckpointPort? _checkpointPort;
    private readonly bool _strictResultPersistence;
    private readonly string _stateKey;
    private readonly object _budgetGate = new();
    private readonly object _pendingGate = new();
    private readonly object _timestampGate = new();
    private readonly Dictionary<string, AiAnalysisWorkItem> _pendingItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _automaticRefillSuppressedItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiAnalysisRequestV1> _historicalRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _statePersistenceGate = new(1, 1);
    private readonly SemaphoreSlim _processingGate;
    private AiAnalysisBudgetSnapshot _budget = AiAnalysisBudgetSnapshot.Empty;
    private AiAnalysisSchedulingControlState _controlState = AiAnalysisSchedulingControlState.Running;
    private string? _controlReason;
    private DateTimeOffset _lastPersistenceTimestamp;

    public AiAnalysisScheduler(
        IAiServicePort provider,
        IAiAnalysisStore store,
        IAiAnalysisWorkBuffer queue,
        AiAnalysisBudgetPolicy budgetPolicy,
        TimeProvider? timeProvider = null,
        AiAnalysisQueueOptions? queueOptions = null,
        IAiAnalysisSchedulingStateStore? stateStore = null,
        string stateKey = "ai-s3-default",
        IAiAnalysisResultStore? resultStore = null,
        IAiAnalysisCopyPreparer? copyPreparer = null,
        IAiAnalysisCopyLeaseManager? copyLeaseManager = null,
        IAiAnalysisCheckpointPort? checkpointPort = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _budgetPolicy = budgetPolicy ?? throw new ArgumentNullException(nameof(budgetPolicy));
        _budgetPolicy.Validate();
        _queueOptions = queueOptions ?? new AiAnalysisQueueOptions();
        _queueOptions.Validate();
        _processingGate = new SemaphoreSlim(
            _queueOptions.MaxConcurrentRequests,
            _queueOptions.MaxConcurrentRequests);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stateStore = stateStore;
        _resultStore = resultStore;
        _copyPreparer = copyPreparer;
        _copyLeaseManager = copyLeaseManager ?? copyPreparer as IAiAnalysisCopyLeaseManager;
        _checkpointPort = checkpointPort;
        _strictResultPersistence = copyPreparer is not null || copyLeaseManager is not null || checkpointPort is not null;
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

    public AiAnalysisSchedulingControlState ControlState
    {
        get
        {
            lock (_pendingGate)
            {
                return _controlState;
            }
        }
    }

    public string? ControlReason
    {
        get
        {
            lock (_pendingGate)
            {
                return _controlReason;
            }
        }
    }

    /// <summary>
    /// Consumes the ready INPUT-01 producer artifacts and creates one first-layer
    /// joint-understanding request per style.  The projector remains the only
    /// code that maps the existing BatchDraft/manifest metadata into the AI
    /// contract; this method never creates a second input model or scans files.
    /// </summary>
    public async Task<AiAnalysisBatchScheduleResult> EnqueueBatchAsync(
        BatchDraft batch,
        TemplateRevision templateRevision,
        AiAnalysisProjectionOptions projectionOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(templateRevision);
        ArgumentNullException.ThrowIfNull(projectionOptions);
        if (batch.ScanStatus != BatchScanStatus.ScanReady
            || batch.InputType is null
            || batch.Styles.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(batch.ManifestReference)
            || string.IsNullOrWhiteSpace(batch.ManifestDigest))
        {
            throw new InvalidOperationException("AI_INPUT_BATCH_NOT_READY");
        }

        var results = new List<AiAnalysisScheduleResult>(batch.Styles.Length);
        foreach (var style in batch.Styles.OrderBy(style => style.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = AiAnalysisInputProjector.Project(
                batch,
                templateRevision,
                style.StyleId,
                projectionOptions with { AnalysisKind = AiAnalysisKind.ProductJointUnderstanding });
            if (request.Assets.Count > _queueOptions.MaxAssetsPerRequest)
            {
                results.Add(new(
                    AiAnalysisWorkDisposition.TerminalError,
                    request.RequestId,
                    null,
                    "AI_ANALYSIS_ASSET_LIMIT_EXCEEDED",
                    0));
                continue;
            }
            if (_copyPreparer is not null)
            {
                request = await PrepareCopiesAsync(batch, style, request, projectionOptions.CompressionProfileVersion, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                results.Add(await EnqueueAsync(
                    request,
                    AiAnalysisWorkStage.ProductJointUnderstanding,
                    cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                await ReleasePreparedLeaseIdsAsync(request, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        return new(
            batch.BatchId,
            results,
            results.Count(result => result.Disposition == AiAnalysisWorkDisposition.Enqueued),
            results.Count(result => result.Disposition == AiAnalysisWorkDisposition.Duplicate),
            results.Count(result => result.Disposition == AiAnalysisWorkDisposition.CacheHit),
            results.Count(result => result.Disposition == AiAnalysisWorkDisposition.AlreadySettled));
    }

    private async Task<AiAnalysisRequestV1> PrepareCopiesAsync(
        BatchDraft batch,
        StyleDraft style,
        AiAnalysisRequestV1 request,
        string compressionProfileVersion,
        CancellationToken cancellationToken)
    {
        var sourceById = batch.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        var prepared = new List<AiAssetInputV1>(request.Assets.Count);
        try
        {
            foreach (var asset in request.Assets)
            {
                if (!sourceById.TryGetValue(asset.AssetId, out var source)
                    || !string.Equals(source.StyleId, style.StyleId, StringComparison.Ordinal))
                    throw new InvalidOperationException("AI_INPUT_ASSET_NOT_FOUND");
                var metadata = await _copyPreparer!.PrepareAsync(
                    new AiAnalysisCopyPreparationRequest(asset.AnalysisCopyReference, batch.InputRoot, source, compressionProfileVersion),
                    cancellationToken).ConfigureAwait(false);
                prepared.Add(asset with
                {
                    AnalysisCopyDigest = metadata.Digest,
                    AnalysisCopyLength = metadata.ByteLength,
                    AnalysisCopyMimeType = metadata.MimeType,
                    AnalysisCopyCompressionProfile = metadata.CompressionProfileVersion,
                    AnalysisCopyLeaseId = metadata.LeaseId
                });
            }
            return request with { Assets = prepared };
        }
        catch
        {
            await ReleasePreparedLeaseIdsAsync(
                request with { Assets = prepared }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RequestPauseAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        SetControlState(AiAnalysisSchedulingControlState.PauseRequested, reason);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        lock (_pendingGate)
        {
            if (_controlState == AiAnalysisSchedulingControlState.Cancelled)
            {
                throw new InvalidOperationException("AI_SCHEDULER_CANCELLED");
            }

            _controlState = AiAnalysisSchedulingControlState.Running;
            _controlReason = null;
            _automaticRefillSuppressedItems.Clear();
        }
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        await RefillQueueFromPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels only work that has not reached the provider.  In-flight or
    /// unknown provider outcomes remain auditable and are never silently
    /// retried; a later explicit recovery command may cancel them.
    /// </summary>
    public async Task<int> CancelPendingAsync(
        string? batchId,
        CancellationToken cancellationToken)
    {
        if (batchId is not null && (string.IsNullOrWhiteSpace(batchId) || batchId.Length > 128))
        {
            throw new ArgumentException("AI 批次标识无效。", nameof(batchId));
        }

        AiAnalysisWorkItem[] pending;
        lock (_pendingGate)
        {
            pending = _pendingItems.Values
                .Where(item => batchId is null || string.Equals(item.Request.BatchId, batchId, StringComparison.Ordinal))
                .ToArray();
        }

        var cancelled = 0;
        foreach (var item in pending)
        {
            if (!await TryCancelPendingItemAsync(item, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            lock (_pendingGate)
            {
                _pendingItems.Remove(item.WorkItemId);
                _automaticRefillSuppressedItems.Remove(item.WorkItemId);
            }

            await ReleaseRequestLeasesAsync(item.Request, item.Stage, cancellationToken).ConfigureAwait(false);

            cancelled++;
        }

        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    private async Task<bool> TryCancelPendingItemAsync(
        AiAnalysisWorkItem item,
        CancellationToken cancellationToken)
    {
        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await _store.FindRequestByFingerprintAsync(
                AiAnalysisCacheKeyFactory.Create(item.Request),
                cancellationToken).ConfigureAwait(false);
            // Uploading is still before IAiServicePort.AnalyzeAsync in this
            // scheduler. Analysing may already have crossed HTTP and must stay
            // as unresolved evidence instead of being guessed cancelled.
            if (ledger is null || ledger.Status is not ("Queued" or "RetryableError" or "Uploading"))
            {
                return false;
            }

            var session = await _store.LoadSessionAsync(
                SessionId(item.Request), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
            if (!session.CanAdvanceTo(AiAnalysisSessionStatus.Cancelled))
            {
                return false;
            }

            if (_checkpointPort is not null)
            {
                var usageRecords = await _store.LoadUsageAsync(
                    item.Request.RequestId, cancellationToken).ConfigureAwait(false);
                await CommitGraphCoreAsync(
                    item.Request,
                    ledger with
                    {
                        Status = AiAnalysisSessionStatus.Cancelled.ToString(),
                        ResultReference = null,
                        UpdatedAt = NextTimestamp(ledger.UpdatedAt)
                    },
                    ledger.Status,
                    session.AdvanceTo(AiAnalysisSessionStatus.Cancelled),
                    session.Status,
                    session.SelectionRevision,
                    usageRecords.Count == 0 ? null : usageRecords[^1],
                    await _store.FindCacheReferenceAsync(
                        AiAnalysisCacheKeyFactory.Create(item.Request), cancellationToken).ConfigureAwait(false),
                    null,
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            await _store.SaveSessionAsync(
                session.AdvanceTo(AiAnalysisSessionStatus.Cancelled),
                cancellationToken,
                session.SelectionRevision).ConfigureAwait(false);
            await _store.UpdateRequestStatusAsync(
                item.Request.RequestId,
                ledger.Status,
                AiAnalysisSessionStatus.Cancelled.ToString(),
                null,
                NextTimestamp(ledger.UpdatedAt),
                cancellationToken).ConfigureAwait(false);
            ApplyPendingMutation(
                item.Request.RequestId,
                AiAnalysisSessionStatus.Cancelled.ToString(),
                null,
                null);
            return true;
        }
        finally
        {
            _statePersistenceGate.Release();
        }
    }

    public async Task<int> CancelAsync(
        string? batchId,
        CancellationToken cancellationToken)
    {
        if (batchId is not null && (string.IsNullOrWhiteSpace(batchId) || batchId.Length > 128))
        {
            throw new ArgumentException("AI 批次标识无效。", nameof(batchId));
        }

        // The persisted control state is scheduler-wide.  A batch-scoped
        // cancellation must therefore not silently pause/cancel unrelated
        // batches that happen to share the same scheduler state key.
        if (batchId is not null)
        {
            lock (_pendingGate)
            {
                var hasMatching = _pendingItems.Values.Any(item =>
                    string.Equals(item.Request.BatchId, batchId, StringComparison.Ordinal));
                var hasOther = _pendingItems.Values.Any(item =>
                    !string.Equals(item.Request.BatchId, batchId, StringComparison.Ordinal));
                if (!hasMatching)
                {
                    return 0;
                }

                if (hasOther)
                {
                    throw new InvalidOperationException("AI_SCHEDULER_BATCH_SCOPE_CONFLICT");
                }
            }
        }

        SetControlState(AiAnalysisSchedulingControlState.CancelRequested, "AI_CANCEL_REQUESTED");
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        var cancelled = await CancelPendingAsync(batchId, cancellationToken).ConfigureAwait(false);
        SetControlState(AiAnalysisSchedulingControlState.Cancelled, "AI_CANCELLED_BY_USER");
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    /// <summary>
    /// Records a network wait without changing a request ledger.  This is used
    /// when connectivity is known to be unavailable before a request starts;
    /// an in-flight response-unknown outcome remains AiResultUnknown instead.
    /// </summary>
    public async Task WaitForNetworkAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        SetControlState(AiAnalysisSchedulingControlState.WaitingForNetwork, reason);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InvalidateCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var cache = await _store.FindCacheReferenceAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        return cache is not null
            && await InvalidateCacheReferenceSafelyAsync(cache, cancellationToken).ConfigureAwait(false);
    }

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

        if (_checkpointPort is not null)
        {
            var batchIds = (state.HistoricalRequests ?? state.PendingItems.Select(item => item.Request).ToArray())
                .Select(request => request.BatchId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var batchId in batchIds)
            {
                var recovered = await _checkpointPort.RecoverAsync(batchId, cancellationToken).ConfigureAwait(false);
                if (recovered.Outcome == AiAnalysisCheckpointOutcome.RecoveryRequired)
                {
                    throw new InvalidOperationException(recovered.ReasonCode);
                }
            }
        }

        ValidateBudgetSnapshot(state.Budget);
        lock (_budgetGate)
        {
            _budget = NormalizeBudgetSnapshot(state.Budget);
        }
        if (!Enum.IsDefined(state.ControlState))
        {
            throw new InvalidDataException("AI_SCHEDULER_CONTROL_STATE_CORRUPT");
        }

        lock (_pendingGate)
        {
            _controlState = state.ControlState;
            _controlReason = string.IsNullOrWhiteSpace(state.ControlReason) ? null : state.ControlReason;
            _automaticRefillSuppressedItems.Clear();
            _historicalRequests.Clear();
            foreach (var historical in state.HistoricalRequests ?? [])
            {
                _historicalRequests[historical.RequestId] = historical;
            }
        }
        lock (_timestampGate) _lastPersistenceTimestamp = state.UpdatedAt;

        await RetryTerminalLeaseCleanupAsync(
            state.HistoricalRequests ?? [],
            cancellationToken).ConfigureAwait(false);

        var restored = 0;
        var retryableItems = new List<AiAnalysisWorkItem>();
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
                await ReleaseRequestLeasesAsync(item.Request, item.Stage, cancellationToken).ConfigureAwait(false);
                lock (_pendingGate)
                {
                    _pendingItems.Remove(item.WorkItemId);
                }

                continue;
            }

            if (state.ControlState is AiAnalysisSchedulingControlState.CancelRequested
                or AiAnalysisSchedulingControlState.Cancelled)
            {
                // A crash can occur after cancellation is durably requested but
                // before a queued item's ledger transition. Complete that
                // explicit transition on restore and never re-enqueue it. An
                // in-flight item remains as auditable recovery evidence; it is
                // not guessed successful or silently discarded.
                if (ledger.Status is "Queued" or "RetryableError")
                {
                    await TransitionToAsync(item.Request, AiAnalysisSessionStatus.Cancelled, cancellationToken)
                        .ConfigureAwait(false);
                    lock (_pendingGate)
                    {
                        _pendingItems.Remove(item.WorkItemId);
                        _automaticRefillSuppressedItems.Remove(item.WorkItemId);
                    }
                    await ReleaseRequestLeasesAsync(
                        item.Request, item.Stage, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    lock (_pendingGate)
                    {
                        _pendingItems[item.WorkItemId] = item;
                    }
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

            await AcquireRequestLeasesAsync(item.Request, item.Stage, cancellationToken).ConfigureAwait(false);

            if (ledger.Status == "RetryableError")
            {
                // ScheduleRetry checkpoints the incremented attempt together
                // with RetryableError. Complete the domain-required hop back
                // to Queued only after every restored pending item has been
                // installed, so the full-batch checkpoint remains closed.
                retryableItems.Add(item);
                continue;
            }

            if (state.ControlState is AiAnalysisSchedulingControlState.Paused
                or AiAnalysisSchedulingControlState.PauseRequested
                or AiAnalysisSchedulingControlState.WaitingForNetwork
                or AiAnalysisSchedulingControlState.Cancelled
                || state.Budget.HardStopped)
            {
                if (state.ControlState == AiAnalysisSchedulingControlState.Cancelled)
                {
                    lock (_pendingGate)
                    {
                        _pendingItems.Remove(item.WorkItemId);
                    }
                }

                continue;
            }

            _queue.TryEnqueue(item);
            restored++;
        }

        foreach (var item in retryableItems)
        {
            await TransitionToAsync(
                item.Request, AiAnalysisSessionStatus.Queued, cancellationToken).ConfigureAwait(false);
            if (state.ControlState is AiAnalysisSchedulingControlState.Paused
                or AiAnalysisSchedulingControlState.PauseRequested
                or AiAnalysisSchedulingControlState.WaitingForNetwork
                or AiAnalysisSchedulingControlState.Cancelled
                || state.Budget.HardStopped)
            {
                continue;
            }

            _queue.TryEnqueue(item);
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
        if (request.Assets.Count > _queueOptions.MaxAssetsPerRequest)
        {
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            return new(
                AiAnalysisWorkDisposition.TerminalError,
                request.RequestId,
                null,
                "AI_ANALYSIS_ASSET_LIMIT_EXCEEDED",
                0);
        }
        var controlState = ControlState;
        if (controlState == AiAnalysisSchedulingControlState.Cancelled)
        {
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.Cancelled, request.RequestId, null, "AI_SCHEDULER_CANCELLED", 0);
        }
        var fingerprint = AiAnalysisCacheKeyFactory.Create(request);
        var batchScopeConflict = false;
        var requestIdConflict = false;
        var logicalRequestLimitExceeded = false;
        lock (_pendingGate)
        {
            batchScopeConflict = _checkpointPort is not null
                && _historicalRequests.Values.Any(existingRequest =>
                    !string.Equals(existingRequest.BatchId, request.BatchId, StringComparison.Ordinal));
            requestIdConflict = _historicalRequests.TryGetValue(request.RequestId, out var existingRequest)
                && !string.Equals(
                    AiAnalysisCacheKeyFactory.Create(existingRequest),
                    fingerprint,
                    StringComparison.Ordinal);
            logicalRequestLimitExceeded = existingRequest is null
                && _historicalRequests.Values.Count(existing =>
                    string.Equals(existing.BatchId, request.BatchId, StringComparison.Ordinal)
                    && string.Equals(existing.StyleId, request.StyleId, StringComparison.Ordinal))
                >= _queueOptions.MaxLogicalRequestsPerStyle;
            if (!batchScopeConflict && !requestIdConflict && !logicalRequestLimitExceeded)
            {
                _historicalRequests[request.RequestId] = request;
            }
        }
        if (batchScopeConflict)
        {
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("AI_CHECKPOINT_BATCH_SCOPE_INVALID");
        }
        if (requestIdConflict || logicalRequestLimitExceeded)
        {
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            return new(
                AiAnalysisWorkDisposition.TerminalError,
                request.RequestId,
                null,
                requestIdConflict
                    ? "AI_ANALYSIS_REQUEST_ID_CONFLICT"
                    : "AI_ANALYSIS_REQUEST_LIMIT_EXCEEDED",
                0);
        }
        var now = _timeProvider.GetUtcNow();
        AiRequestLedgerEntry existing;
        var found = await _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        if (found is null && _checkpointPort is not null)
        {
            var initialLedger = new AiRequestLedgerEntry(
                request.RequestId,
                fingerprint,
                request.BatchId,
                request.StyleId,
                AiAnalysisSessionStatus.Queued.ToString(),
                null,
                now,
                now);
            var initialSession = AiAnalysisSession.Create(
                SessionId(request),
                request.BatchId,
                request.StyleId,
                request.TemplateSnapshotId,
                $"analysis-{request.RequestId}",
                request.Requirements.Select(requirement => requirement.RequirementId),
                request.Assets.Select(asset => asset.AssetId));
            await CommitGraphAsync(
                request,
                initialLedger,
                AiAnalysisSessionStatus.Queued.ToString(),
                initialSession,
                AiAnalysisSessionStatus.Queued,
                0,
                null,
                null,
                cancellationToken,
                new AiAnalysisWorkItem(
                    $"{request.RequestId}-{stage.ToString().ToLowerInvariant()}",
                    request,
                    stage,
                    0,
                    now)).ConfigureAwait(false);
            existing = await _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
        }
        else if (found is not null)
        {
            existing = found;
        }
        else
        {
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
        }

        if (!string.Equals(existing.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            lock (_pendingGate)
            {
                _historicalRequests.Remove(request.RequestId);
            }
            var existingCache = await _store.FindCacheReferenceAsync(fingerprint, cancellationToken).ConfigureAwait(false);
            if (existingCache is not null)
            {
                if (await IsCacheReusableAsync(existingCache, request, cancellationToken).ConfigureAwait(false))
                {
                    await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
                    return new(AiAnalysisWorkDisposition.CacheHit, existing.RequestId, existingCache.ResultReference, null, 0);
                }
                var invalidationError = existingCache.IsReusable
                    ? "AI_CACHE_RESULT_INVALID"
                    : "AI_CACHE_INVALIDATED_REANALYSIS_VERSION_REQUIRED";
                await InvalidateCacheReferenceSafelyAsync(existingCache, cancellationToken).ConfigureAwait(false);
                await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
                return new(
                    AiAnalysisWorkDisposition.TerminalError,
                    existing.RequestId,
                    null,
                    invalidationError,
                    0);
            }

            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.Duplicate, existing.RequestId, existing.ResultReference, null, 0);
        }

        if (existing.Status is "Validated" or "ManualReview" or "PlanClosed"
            or "AiResultUnknown" or "Cancelled" or "TerminalError")
        {
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            RemovePendingForRequest(request.RequestId, stage);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.AlreadySettled, existing.RequestId, existing.ResultReference, null, 0);
        }

        var sessionId = SessionId(request);
        if (await _store.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            if (_checkpointPort is not null)
            {
                await ReleasePreparedLeaseIdsAsync(request, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("AI_CHECKPOINT_SESSION_MISSING");
            }

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
        if (cache is not null)
        {
            if (await IsCacheReusableAsync(cache, request, cancellationToken).ConfigureAwait(false))
            {
                await AcquireRequestLeasesAsync(request, stage, cancellationToken).ConfigureAwait(false);
                await TransitionToAsync(request, AiAnalysisSessionStatus.Uploading, cancellationToken).ConfigureAwait(false);
                await TransitionToAsync(request, AiAnalysisSessionStatus.Analysing, cancellationToken).ConfigureAwait(false);
                await CompleteCacheHitAsync(request, cache, cancellationToken).ConfigureAwait(false);
                if (stage != AiAnalysisWorkStage.ProductJointUnderstanding)
                {
                    await ReleaseRequestLeasesAsync(request, stage, cancellationToken).ConfigureAwait(false);
                }
                RemovePendingForRequest(request.RequestId, stage);
                await PersistStateAsync(cancellationToken).ConfigureAwait(false);
                return new(AiAnalysisWorkDisposition.CacheHit, request.RequestId, cache.ResultReference, null, 0);
            }

            var invalidationError = cache.IsReusable
                ? "AI_CACHE_RESULT_INVALID"
                : "AI_CACHE_INVALIDATED_REANALYSIS_VERSION_REQUIRED";
            // Missing, corrupt, or model-mismatched result references become
            // durable tombstones. The same fingerprint remains fail-closed;
            // only a changed analysis input/configuration can create new work.
            await InvalidateCacheReferenceSafelyAsync(cache, cancellationToken).ConfigureAwait(false);
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            return new(
                AiAnalysisWorkDisposition.TerminalError,
                request.RequestId,
                null,
                invalidationError,
                0);
        }

        var item = new AiAnalysisWorkItem(
            $"{request.RequestId}-{stage.ToString().ToLowerInvariant()}",
            request,
            stage,
            0,
            now);
        if (_queue.IsActive(item.WorkItemId))
        {
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.Duplicate, request.RequestId, null, null, 0);
        }

        var pendingDuplicate = false;
        lock (_pendingGate)
        {
            pendingDuplicate = _pendingItems.ContainsKey(item.WorkItemId)
                || _pendingItems.Values.Any(pending =>
                    string.Equals(pending.Request.RequestId, request.RequestId, StringComparison.Ordinal)
                    && pending.Stage == stage);
            if (!pendingDuplicate) _pendingItems[item.WorkItemId] = item;
        }
        if (pendingDuplicate)
        {
            await ReleasePreparedLeaseIdsAsync(request, cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.Duplicate, request.RequestId, null, null, 0);
        }

        try
        {
            await AcquireRequestLeasesAsync(request, stage, cancellationToken).ConfigureAwait(false);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_pendingGate)
            {
                _pendingItems.Remove(item.WorkItemId);
            }

            await ReleaseRequestLeasesAsync(request, stage, CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        if (ControlState is AiAnalysisSchedulingControlState.Paused
            or AiAnalysisSchedulingControlState.PauseRequested
            or AiAnalysisSchedulingControlState.WaitingForNetwork
            or AiAnalysisSchedulingControlState.CancelRequested)
        {
            return new(
                ControlState == AiAnalysisSchedulingControlState.WaitingForNetwork
                    ? AiAnalysisWorkDisposition.WaitingForNetwork
                    : ControlState == AiAnalysisSchedulingControlState.CancelRequested
                        ? AiAnalysisWorkDisposition.Cancelled
                        : AiAnalysisWorkDisposition.Paused,
                request.RequestId,
                null,
                ControlReason,
                item.Attempt);
        }

        // Capacity is an in-memory work waterline, not a batch admission
        // limit.  The full pending set is already durable; items beyond the
        // bounded channel remain in that backlog and are promoted as workers
        // complete.  Never release their lease or report an unrecoverable
        // QueueFull merely because the current process has 32 ready items.
        _queue.TryEnqueue(item);

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

        // Ownership moves from the completed first-layer result to its
        // selected refinements only after every child has acquired its own
        // durable lease.  Unselected candidates therefore cannot be uploaded.
        await ReleaseRequestLeasesAsync(
            firstLayerRequest,
            AiAnalysisWorkStage.ProductJointUnderstanding,
            cancellationToken).ConfigureAwait(false);

        return results;
    }

    public async Task<AiAnalysisScheduleResult> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await _processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var controlState = ControlState;
                if (controlState is AiAnalysisSchedulingControlState.Paused
                    or AiAnalysisSchedulingControlState.PauseRequested
                    or AiAnalysisSchedulingControlState.WaitingForNetwork
                    or AiAnalysisSchedulingControlState.CancelRequested)
                {
                    if (controlState == AiAnalysisSchedulingControlState.PauseRequested)
                    {
                        lock (_pendingGate)
                        {
                            _controlState = AiAnalysisSchedulingControlState.Paused;
                        }

                        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (controlState == AiAnalysisSchedulingControlState.CancelRequested)
                    {
                        var ledger = await _store.FindRequestByFingerprintAsync(
                            AiAnalysisCacheKeyFactory.Create(item.Request),
                            cancellationToken).ConfigureAwait(false);
                        if (ledger?.Status is "Queued" or "RetryableError")
                        {
                            if (await TryCancelPendingItemAsync(item, cancellationToken).ConfigureAwait(false))
                            {
                                await ReleaseRequestLeasesAsync(
                                    item.Request, item.Stage, cancellationToken).ConfigureAwait(false);
                            }

                            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
                        }

                        return new(
                            AiAnalysisWorkDisposition.Cancelled,
                            item.Request.RequestId,
                            null,
                            "AI_CANCEL_REQUESTED",
                            item.Attempt);
                    }

                    return new(
                        controlState == AiAnalysisSchedulingControlState.WaitingForNetwork
                            ? AiAnalysisWorkDisposition.WaitingForNetwork
                            : AiAnalysisWorkDisposition.Paused,
                        item.Request.RequestId,
                        null,
                        ControlReason,
                        item.Attempt);
                }

                var result = await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
                if (result.Disposition == AiAnalysisWorkDisposition.BudgetStopped)
                {
                    lock (_pendingGate)
                    {
                        _automaticRefillSuppressedItems.Add(item.WorkItemId);
                    }
                }
                if (result.Disposition != AiAnalysisWorkDisposition.RetryScheduled
                    && result.Disposition != AiAnalysisWorkDisposition.BudgetStopped
                    && result.Disposition != AiAnalysisWorkDisposition.QueueFull
                    && result.Disposition != AiAnalysisWorkDisposition.Paused
                    && result.Disposition != AiAnalysisWorkDisposition.WaitingForNetwork)
                {
                    lock (_pendingGate)
                    {
                        _pendingItems.Remove(item.WorkItemId);
                        _automaticRefillSuppressedItems.Remove(item.WorkItemId);
                    }

                    if (item.Stage != AiAnalysisWorkStage.ProductJointUnderstanding
                        || result.Disposition != AiAnalysisWorkDisposition.Completed)
                    {
                        await ReleaseRequestLeasesAsync(item.Request, item.Stage,
                            cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
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
                await RefillQueueFromPendingAsync(
                    cancellationToken.IsCancellationRequested
                        ? CancellationToken.None
                        : cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _processingGate.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var workers = Enumerable.Range(0, _queueOptions.MaxConcurrentRequests)
            .Select(_ => RunWorkerAsync(cancellationToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
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

        if (ledger.Status == "RetryableError")
        {
            // A refill may observe the durable retry checkpoint before the
            // normal RetryableError -> Queued hop completes. Normalize only
            // when a worker actually takes the item, and do it before budget
            // reservation or provider execution. A failed normalization stays
            // fail-closed without consuming another attempt.
            await TransitionToAsync(
                item.Request, AiAnalysisSessionStatus.Queued, cancellationToken).ConfigureAwait(false);
        }

        var reservation = await TryReserveRequestBudgetAsync(cancellationToken).ConfigureAwait(false);
        if (!reservation.Allowed)
        {
            return Failure(item, AiAnalysisWorkDisposition.BudgetStopped, reservation.ErrorCode);
        }

        try
        {
            await TransitionToAsync(item.Request, AiAnalysisSessionStatus.Uploading, cancellationToken).ConfigureAwait(false);
            if (ControlState is AiAnalysisSchedulingControlState.CancelRequested
                or AiAnalysisSchedulingControlState.Cancelled)
            {
                await TryCancelPendingItemAsync(item, cancellationToken).ConfigureAwait(false);
                return Failure(item, AiAnalysisWorkDisposition.Cancelled, "AI_CANCEL_REQUESTED");
            }
            await TransitionToAsync(item.Request, AiAnalysisSessionStatus.Analysing, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (
            ControlState is AiAnalysisSchedulingControlState.CancelRequested or AiAnalysisSchedulingControlState.Cancelled
            && exception.Message is "AI_SESSION_TERMINAL" or "AI_SESSION_TRANSITION_INVALID"
                or "AI_CHECKPOINT_REQUEST_CAS_CONFLICT" or "AI_CHECKPOINT_SESSION_CAS_CONFLICT")
        {
            return Failure(item, AiAnalysisWorkDisposition.Cancelled, "AI_CANCEL_REQUESTED");
        }

        AiAnalysisExecutionResult execution;
        try
        {
            execution = await _provider.AnalyzeAsync(item.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.Cancelled, "AI_REQUEST_CANCELLED", AiAnalysisSessionStatus.Cancelled, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An adapter exception after the request boundary leaves the
            // provider outcome unknown.  Fail closed and persist that fact;
            // never turn an unclassified exception into a retry or success.
            return await CompleteTerminalAsync(
                item,
                AiAnalysisWorkDisposition.ResultUnknown,
                "AI_PROVIDER_EXCEPTION_UNKNOWN",
                AiAnalysisSessionStatus.AiResultUnknown,
                CancellationToken.None).ConfigureAwait(false);
        }

        // Once the provider has returned, cancellation of the caller must not
        // discard an observed result or leave the ledger in Analysing.  The
        // state transition is itself the durable boundary and is therefore
        // completed with a non-cancelled token.
        var persistenceToken = CancellationToken.None;

        if (execution.Usage is not null)
        {
            await AddUsageAsync(execution.Usage, persistenceToken).ConfigureAwait(false);
            if (execution.Outcome != AiProviderOutcome.Completed || execution.Result is null)
            {
                await RecordObservedUsageAsync(item, execution.Usage, persistenceToken).ConfigureAwait(false);
            }
        }

        if (execution.Outcome == AiProviderOutcome.Completed && execution.Result is not null)
        {
            try
            {
                return await CompleteResultAsync(item, execution, persistenceToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                return await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.TerminalError, "AI_RESULT_PERSISTENCE_FAILED", AiAnalysisSessionStatus.TerminalError, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (execution.Outcome == AiProviderOutcome.RetryableError)
        {
            if (item.Attempt + 1 < _queueOptions.MaxAttempts)
            {
                return await ScheduleRetryAsync(item, persistenceToken).ConfigureAwait(false);
            }

            return await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.TerminalError, "AI_PROVIDER_RETRY_EXHAUSTED", AiAnalysisSessionStatus.TerminalError, persistenceToken).ConfigureAwait(false);
        }

        if (string.Equals(execution.Error?.Code, "AI_PROVIDER_NETWORK_UNAVAILABLE", StringComparison.Ordinal))
        {
            SetControlState(AiAnalysisSchedulingControlState.WaitingForNetwork, execution.Error?.UserAction);
            await PersistStateAsync(persistenceToken).ConfigureAwait(false);
            return new(
                AiAnalysisWorkDisposition.WaitingForNetwork,
                item.Request.RequestId,
                null,
                execution.Error?.Code,
                item.Attempt);
        }

        return execution.Outcome switch
        {
            AiProviderOutcome.AiResultUnknown => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.ResultUnknown, "AI_PROVIDER_RESULT_UNKNOWN", AiAnalysisSessionStatus.AiResultUnknown, persistenceToken).ConfigureAwait(false),
            AiProviderOutcome.Cancelled => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.Cancelled, "AI_REQUEST_CANCELLED", AiAnalysisSessionStatus.Cancelled, persistenceToken).ConfigureAwait(false),
            _ => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.TerminalError, execution.Error?.Code ?? "AI_PROVIDER_ERROR", AiAnalysisSessionStatus.TerminalError, persistenceToken).ConfigureAwait(false)
        };
    }

    private async Task<AiAnalysisScheduleResult> CompleteResultAsync(
        AiAnalysisWorkItem item,
        AiAnalysisExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var resultReference = $"results/{item.Request.RequestId}";
        if (!string.Equals(execution.Result!.RequestId, item.Request.RequestId, StringComparison.Ordinal)
            || !string.Equals(execution.Result.ModelConfigVersion, item.Request.ModelConfigVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_RESULT_REFERENCE_MISMATCH");
        }
        if (_resultStore is null)
        {
            if (_strictResultPersistence)
            {
                return await CompleteTerminalAsync(
                    item,
                    AiAnalysisWorkDisposition.TerminalError,
                    "AI_RESULT_STORE_REQUIRED",
                    AiAnalysisSessionStatus.TerminalError,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _resultStore.SaveAsync(resultReference, execution.Result!, cancellationToken).ConfigureAwait(false);
        }
        var usageRecord = execution.Usage is null
            ? null
            : new AiUsageEventRecord(
                $"usage-{item.Request.RequestId}-{item.Attempt}",
                item.Request.RequestId,
                item.Request.ModelConfigVersion,
                execution.Usage.InputTokens,
                execution.Usage.OutputTokens,
                execution.Usage.CacheHit,
                item.Attempt,
                null,
                null,
                _timeProvider.GetUtcNow());
        var cacheRecord = usageRecord is null
            ? null
            : new AiCacheReference(
                AiAnalysisCacheKeyFactory.Create(item.Request),
                resultReference,
                item.Request.ConsentId,
                item.Request.ModelConfigVersion,
                usageRecord.UsageEventId,
                NextTimestamp(usageRecord.RecordedAt));
        if (_checkpointPort is not null)
        {
            var ledger = await _store.FindRequestByFingerprintAsync(
                AiAnalysisCacheKeyFactory.Create(item.Request), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
            var session = await _store.LoadSessionAsync(SessionId(item.Request), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
            await CommitGraphAsync(
                item.Request,
                ledger with
                {
                    Status = AiAnalysisSessionStatus.Validated.ToString(),
                    ResultReference = resultReference,
                    UpdatedAt = NextTimestamp(ledger.UpdatedAt)
                },
                ledger.Status,
                session.AdvanceTo(AiAnalysisSessionStatus.Validated),
                session.Status,
                session.SelectionRevision,
                usageRecord,
                cacheRecord,
                cancellationToken).ConfigureAwait(false);
            return new(AiAnalysisWorkDisposition.Completed, item.Request.RequestId, resultReference, null, item.Attempt);
        }

        if (usageRecord is not null)
        {
            await _store.RecordUsageAsync(usageRecord, cancellationToken).ConfigureAwait(false);
        }

        await CompleteValidatedAsync(item.Request, resultReference, cancellationToken).ConfigureAwait(false);
        if (cacheRecord is not null)
        {
            await _store.SaveCacheReferenceAsync(cacheRecord, cancellationToken).ConfigureAwait(false);
        }
        return new(AiAnalysisWorkDisposition.Completed, item.Request.RequestId, resultReference, null, item.Attempt);
    }

    private async Task RecordObservedUsageAsync(
        AiAnalysisWorkItem item,
        AiProviderUsage usage,
        CancellationToken cancellationToken)
    {
        var usageRecord = new AiUsageEventRecord(
            $"usage-{item.Request.RequestId}-{item.Attempt}",
            item.Request.RequestId,
            item.Request.ModelConfigVersion,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheHit,
            item.Attempt,
            null,
            null,
            _timeProvider.GetUtcNow());
        if (_checkpointPort is null)
        {
            await _store.RecordUsageAsync(usageRecord, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ledger = await _store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(item.Request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
        var session = await _store.LoadSessionAsync(SessionId(item.Request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
        await CommitGraphAsync(
            item.Request,
            ledger,
            ledger.Status,
            session,
            session.Status,
            session.SelectionRevision,
            usageRecord,
            null,
            cancellationToken).ConfigureAwait(false);
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

    private async Task CompleteCacheHitAsync(
        AiAnalysisRequestV1 request,
        AiCacheReference cache,
        CancellationToken cancellationToken)
    {
        // A different request ID with the same fingerprint is handled earlier
        // as an idempotent reference to the cache owner and never reaches this
        // method.  A graph committed here therefore must retain same-request
        // usage ownership; provider usage from another request is never
        // attached to the current request.
        if (_checkpointPort is null)
        {
            await RecordCacheUsageAsync(request, cache, cancellationToken).ConfigureAwait(false);
            await CompleteValidatedAsync(request, cache.ResultReference, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ledger = await _store.FindRequestByFingerprintAsync(
            AiAnalysisCacheKeyFactory.Create(request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
        var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
        var cacheOriginUsage = await _store.FindUsageEventAsync(
            cache.UsageEventId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_CACHE_USAGE_NOT_FOUND");
        if (ledger.Status != AiAnalysisSessionStatus.Analysing.ToString()
            || session.Status != AiAnalysisSessionStatus.Analysing
            || !string.Equals(cacheOriginUsage.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(cacheOriginUsage.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
            || !string.Equals(cache.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_CACHE_CHECKPOINT_CLOSURE_INVALID");
        }

        await CommitGraphAsync(
            request,
            ledger with
            {
                Status = AiAnalysisSessionStatus.Validated.ToString(),
                ResultReference = cache.ResultReference,
                UpdatedAt = NextTimestamp(ledger.UpdatedAt)
            },
            ledger.Status,
            session.AdvanceTo(AiAnalysisSessionStatus.Validated),
            session.Status,
            session.SelectionRevision,
            cacheOriginUsage,
            cache,
            cancellationToken).ConfigureAwait(false);
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
        var retry = item with
        {
            WorkItemId = $"{item.Request.RequestId}-{item.Stage.ToString().ToLowerInvariant()}-{item.Attempt + 1}",
            Attempt = item.Attempt + 1,
            EnqueuedAt = _timeProvider.GetUtcNow()
        };

        // The attempt increment and RetryableError ledger transition are one
        // durable checkpoint.  A crash before the following Queued transition
        // therefore restores attempt 1 and can never issue a third HTTP call.
        await TransitionToAsync(
            item.Request,
            AiAnalysisSessionStatus.RetryableError,
            cancellationToken,
            item.WorkItemId,
            retry).ConfigureAwait(false);
        await TransitionToAsync(item.Request, AiAnalysisSessionStatus.Queued, cancellationToken).ConfigureAwait(false);

        // The logical request/stage token is stable across attempts.  Acquire
        // is idempotent, so the copy remains present while the retry is queued.
        await AcquireRequestLeasesAsync(retry.Request, retry.Stage, cancellationToken).ConfigureAwait(false);

        if (_checkpointPort is null)
        {
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        }
        // A full ready buffer leaves the retry in the durable backlog.  It is
        // promoted after a worker frees capacity or after restart.
        _queue.TryEnqueue(retry);

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
        CancellationToken cancellationToken,
        string? replacedPendingWorkItemId = null,
        AiAnalysisWorkItem? replacementPending = null)
    {
        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await _store.FindRequestByFingerprintAsync(
                AiAnalysisCacheKeyFactory.Create(request),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
            var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
            if (_checkpointPort is not null)
            {
                var checkpointResultReference = next is AiAnalysisSessionStatus.Validated or AiAnalysisSessionStatus.ManualReview or AiAnalysisSessionStatus.PlanClosed
                    ? ledger.ResultReference
                    : null;
                var usageRecords = await _store.LoadUsageAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
                await CommitGraphCoreAsync(
                    request,
                    ledger with
                    {
                        Status = next.ToString(),
                        ResultReference = checkpointResultReference,
                        UpdatedAt = NextTimestamp(ledger.UpdatedAt)
                    },
                    ledger.Status,
                    session.Status == next ? session : session.AdvanceTo(next),
                    session.Status,
                    session.SelectionRevision,
                    usageRecords.Count == 0 ? null : usageRecords[^1],
                    await _store.FindCacheReferenceAsync(AiAnalysisCacheKeyFactory.Create(request), cancellationToken).ConfigureAwait(false),
                    null,
                    replacedPendingWorkItemId,
                    replacementPending,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

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

            ApplyPendingMutation(request.RequestId, next.ToString(), replacedPendingWorkItemId, replacementPending);
        }
        finally
        {
            _statePersistenceGate.Release();
        }
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
        if (!cache.IsReusable)
        {
            return false;
        }

        var usage = await _store.FindUsageEventAsync(cache.UsageEventId, cancellationToken).ConfigureAwait(false);
        if (usage is null
            || !string.Equals(cache.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
            || !string.Equals(usage.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(usage.RequestId)
            || string.IsNullOrWhiteSpace(cache.ResultReference))
        {
            return false;
        }

        if (_resultStore is null)
        {
            // Legacy in-memory scheduler tests can still exercise cache
            // identity; a formal scheduler with owned copies/checkpoints must
            // provide a durable result store (see CompleteResultAsync).
            return !_strictResultPersistence;
        }

        try
        {
            var result = await _resultStore.LoadAsync(cache.ResultReference, cancellationToken).ConfigureAwait(false);
            return result is not null
                && string.Equals(result.SchemaVersion, AiAnalysisProtocol.ResultSchemaVersion, StringComparison.Ordinal)
                && string.Equals(result.RequestId, usage.RequestId, StringComparison.Ordinal)
                && string.Equals(result.ModelConfigVersion, cache.ModelConfigVersion, StringComparison.Ordinal)
                && string.Equals(cache.ResultReference, $"results/{result.RequestId}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return false;
        }
    }

    private async Task<bool> InvalidateCacheReferenceSafelyAsync(
        AiCacheReference cache,
        CancellationToken cancellationToken)
    {
        if (!cache.IsReusable)
        {
            return false;
        }

        if (_checkpointPort is null)
        {
            return await _store.InvalidateCacheReferenceAsync(cache.CacheKey, cancellationToken).ConfigureAwait(false);
        }

        var ledger = await _store.FindRequestByFingerprintAsync(cache.CacheKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_CACHE_INVALIDATION_REQUEST_MISSING");
        var usage = await _store.FindUsageEventAsync(cache.UsageEventId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_CACHE_INVALIDATION_USAGE_MISSING");
        AiAnalysisRequestV1? request;
        lock (_pendingGate)
        {
            _historicalRequests.TryGetValue(ledger.RequestId, out request);
        }

        if (request is null
            || !string.Equals(AiAnalysisCacheKeyFactory.Create(request), cache.CacheKey, StringComparison.Ordinal)
            || !string.Equals(usage.RequestId, ledger.RequestId, StringComparison.Ordinal)
            || !string.Equals(usage.ModelConfigVersion, cache.ModelConfigVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_CACHE_INVALIDATION_GRAPH_MISSING");
        }

        var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_CHECKPOINT_SESSION_MISSING");
        await CommitGraphAsync(
            request,
            ledger,
            ledger.Status,
            session,
            session.Status,
            session.SelectionRevision,
            usage,
            cache with { IsReusable = false },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<(bool Allowed, string ErrorCode)> TryReserveRequestBudgetAsync(
        CancellationToken cancellationToken)
    {
        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
                // Reservation and its durable snapshot share the same gate.
                // No other worker can observe/persist the tentative count,
                // and no HTTP boundary is crossed until this succeeds.
                await PersistStateCoreAsync(cancellationToken).ConfigureAwait(false);
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
        finally
        {
            _statePersistenceGate.Release();
        }
    }

    private async Task AddUsageAsync(
        AiProviderUsage usage,
        CancellationToken cancellationToken)
    {
        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_budgetGate)
            {
                var estimatedCost = _budgetPolicy.Pricing.Estimate(usage);
                var next = _budget with
                {
                    InputTokens = checked(_budget.InputTokens + usage.InputTokens),
                    OutputTokens = checked(_budget.OutputTokens + usage.OutputTokens),
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
                await PersistStateCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Usage was observed after the provider boundary and therefore
                // must never be rolled back.  Retain the complete monotonic
                // aggregate in memory and fail closed; a later in-flight usage
                // commit persists the full aggregate rather than a stale copy.
                lock (_budgetGate)
                {
                    _budget = _budget with { HardStopped = true };
                }

                try
                {
                    // A transient first save failure must not leave the
                    // observed aggregate only in memory.  Retry the complete
                    // monotonic snapshot once with the durable hard-stop set.
                    await PersistStateCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Storage remains unavailable.  The in-process hard stop
                    // is retained and no subsequent request may cross HTTP.
                }
            }
        }
        finally
        {
            _statePersistenceGate.Release();
        }
    }

    private static AiAnalysisScheduleResult Failure(
        AiAnalysisWorkItem item,
        AiAnalysisWorkDisposition disposition,
        string errorCode) => new(disposition, item.Request.RequestId, null, errorCode, item.Attempt);

    private static DateTimeOffset NextTimestamp(DateTimeOffset value) => value.AddTicks(1);

    private async Task AcquireRequestLeasesAsync(
        AiAnalysisRequestV1 request,
        AiAnalysisWorkStage stage,
        CancellationToken cancellationToken)
    {
        if (_copyLeaseManager is null) return;
        foreach (var group in request.Assets
                     .Where(asset => !string.IsNullOrWhiteSpace(asset.AnalysisCopyLeaseId))
                     .GroupBy(asset => asset.AnalysisCopyReference, StringComparer.Ordinal))
        {
            var token = LeaseToken(request.RequestId, stage, group.Key);
            await _copyLeaseManager.AcquireAsync(group.Key, token, cancellationToken).ConfigureAwait(false);
            foreach (var original in group.Select(asset => asset.AnalysisCopyLeaseId!).Distinct(StringComparer.Ordinal))
            {
                if (!string.Equals(original, token, StringComparison.Ordinal))
                {
                    await _copyLeaseManager.ReleaseAsync(group.Key, original, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ReleaseRequestLeasesAsync(
        AiAnalysisRequestV1 request,
        AiAnalysisWorkStage stage,
        CancellationToken cancellationToken)
    {
        if (_copyLeaseManager is null) return;
        foreach (var reference in request.Assets
                     .Select(asset => asset.AnalysisCopyReference)
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                await _copyLeaseManager.ReleaseAsync(reference, LeaseToken(request.RequestId, stage, reference), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                // Durable sidecars remain as recovery evidence; a later
                // restart can retry cleanup without changing request state.
            }
        }
    }

    private async Task RetryTerminalLeaseCleanupAsync(
        IReadOnlyList<AiAnalysisRequestV1> historicalRequests,
        CancellationToken cancellationToken)
    {
        if (_copyLeaseManager is null || historicalRequests.Count == 0)
        {
            return;
        }

        foreach (var request in historicalRequests)
        {
            var ledger = await _store.FindRequestByFingerprintAsync(
                AiAnalysisCacheKeyFactory.Create(request), cancellationToken).ConfigureAwait(false);
            if (ledger is null || !ShouldRetryTerminalLeaseCleanup(request, ledger.Status, historicalRequests))
            {
                continue;
            }

            await ReleaseRequestLeasesAsync(
                request,
                StageForAnalysisKind(request.AnalysisKind),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetryTerminalLeaseCleanup(
        AiAnalysisRequestV1 request,
        string status,
        IReadOnlyList<AiAnalysisRequestV1> historicalRequests)
    {
        if (status is not ("Validated" or "ManualReview" or "PlanClosed"
            or "AiResultUnknown" or "Cancelled" or "TerminalError"))
        {
            return false;
        }

        if (request.AnalysisKind != AiAnalysisKind.ProductJointUnderstanding
            || status is "PlanClosed" or "AiResultUnknown" or "Cancelled" or "TerminalError")
        {
            return true;
        }

        // A successful first-layer result retains its lease until at least one
        // durable refinement request exists.  EnqueueRefinementAsync persists
        // the child before releasing this token, so restart can distinguish a
        // legitimate future refinement from a failed cleanup attempt.
        return historicalRequests.Any(candidate =>
            string.Equals(candidate.BatchId, request.BatchId, StringComparison.Ordinal)
            && string.Equals(candidate.StyleId, request.StyleId, StringComparison.Ordinal)
            && candidate.RequestId.StartsWith(request.RequestId + "-", StringComparison.Ordinal)
            && candidate.AnalysisKind is AiAnalysisKind.SelectedAssetGeometry or AiAnalysisKind.SizeTableExtraction);
    }

    private static AiAnalysisWorkStage StageForAnalysisKind(string analysisKind) => analysisKind switch
    {
        AiAnalysisKind.ProductJointUnderstanding => AiAnalysisWorkStage.ProductJointUnderstanding,
        AiAnalysisKind.SelectedAssetGeometry => AiAnalysisWorkStage.SelectedAssetGeometry,
        AiAnalysisKind.SizeTableExtraction => AiAnalysisWorkStage.SizeTableExtraction,
        _ => throw new InvalidDataException("AI_ANALYSIS_KIND_INVALID")
    };

    private async Task ReleasePreparedLeaseIdsAsync(
        AiAnalysisRequestV1 request,
        CancellationToken cancellationToken)
    {
        if (_copyLeaseManager is null) return;
        foreach (var asset in request.Assets
                     .Where(asset => !string.IsNullOrWhiteSpace(asset.AnalysisCopyLeaseId))
                     .GroupBy(asset => new { asset.AnalysisCopyReference, asset.AnalysisCopyLeaseId }))
        {
            try
            {
                await _copyLeaseManager.ReleaseAsync(
                    asset.Key.AnalysisCopyReference,
                    asset.Key.AnalysisCopyLeaseId!,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                // Sidecar remains durable recovery evidence.
            }
        }
    }

    private static string LeaseToken(string requestId, AiAnalysisWorkStage stage, string reference)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{requestId}|{stage}|{reference}"));
        return "lease-work-" + Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }

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
        if (_stateStore is null && _checkpointPort is null)
        {
            return;
        }

        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _statePersistenceGate.Release();
        }
    }

    private async Task PersistStateCoreAsync(CancellationToken cancellationToken)
    {
        if (_stateStore is null && _checkpointPort is null)
        {
            return;
        }

        // Every caller reaches this method while holding the persistence gate.
        // Snapshot construction, full-graph reads and the durable commit are a
        // single scheduler-level critical section.
        AiAnalysisWorkItem[] pending;
        AiAnalysisBudgetSnapshot budget;
        AiAnalysisSchedulingControlState controlState;
        string? controlReason;
        AiAnalysisRequestV1[] historicalRequests;
        lock (_pendingGate)
        {
            pending = _pendingItems.Values
                .OrderBy(item => item.WorkItemId, StringComparer.Ordinal)
                .ToArray();
            controlState = _controlState;
            controlReason = _controlReason;
            historicalRequests = _historicalRequests.Values
                .OrderBy(request => request.RequestId, StringComparer.Ordinal)
                .ToArray();
        }
        lock (_budgetGate)
        {
            budget = _budget;
        }

        var state = new AiAnalysisSchedulingState(
            pending,
            budget,
            NextPersistenceTimestamp(),
            controlState,
            controlReason,
            historicalRequests);
        if (_checkpointPort is not null)
        {
            var command = await BuildCheckpointCommandAsync(state, cancellationToken).ConfigureAwait(false);
            if (command is not null)
            {
                var outcome = await CommitCheckpointWithRecoveryAsync(command, cancellationToken).ConfigureAwait(false);
                if (outcome.Outcome is AiAnalysisCheckpointOutcome.RecoveryRequired)
                {
                    throw new InvalidOperationException(outcome.ReasonCode);
                }
            }
            else if (_stateStore is not null)
            {
                await _stateStore.SaveSchedulingStateAsync(_stateKey, state, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _stateStore!.SaveSchedulingStateAsync(_stateKey, state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AiAnalysisCheckpointCommand?> BuildCheckpointCommandAsync(
        AiAnalysisSchedulingState state,
        CancellationToken cancellationToken,
        AiAnalysisCheckpointGraph? targetGraph = null)
    {
        var requests = (state.HistoricalRequests ?? [])
            .GroupBy(request => request.RequestId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(request => request.RequestId, StringComparer.Ordinal)
            .ToArray();
        if (requests.Length == 0) return null;
        var batchIds = requests.Select(request => request.BatchId).Distinct(StringComparer.Ordinal).ToArray();
        if (batchIds.Length != 1)
        {
            // A scheduler state key may temporarily contain multiple batches;
            // the checkpoint protocol is intentionally batch-scoped and must
            // not merge unrelated graphs.
            throw new InvalidOperationException("AI_CHECKPOINT_BATCH_SCOPE_INVALID");
        }

        var graphs = new List<AiAnalysisCheckpointGraph>(requests.Length);
        var cacheKeys = new List<string>();
        foreach (var request in requests)
        {
            if (targetGraph is not null
                && string.Equals(targetGraph.Request.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                if (targetGraph.Cache is not null) cacheKeys.Add(targetGraph.Cache.CacheKey);
                graphs.Add(targetGraph);
                continue;
            }

            var fingerprint = AiAnalysisCacheKeyFactory.Create(request);
            var ledger = await _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_CHECKPOINT_REQUEST_MISSING");
            var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_CHECKPOINT_SESSION_MISSING");
            var usage = (await _store.LoadUsageAsync(request.RequestId, cancellationToken).ConfigureAwait(false))
                .OrderBy(item => item.RecordedAt)
                .LastOrDefault();
            var cache = await _store.FindCacheReferenceAsync(fingerprint, cancellationToken).ConfigureAwait(false);
            if (cache is not null) cacheKeys.Add(cache.CacheKey);
            graphs.Add(new AiAnalysisCheckpointGraph(
                ledger,
                ledger.Status,
                session,
                session.Status,
                session.SelectionRevision,
                usage,
                cache));
        }

        var now = graphs.Select(graph => graph.Request.UpdatedAt)
            .Concat(graphs.Where(graph => graph.Usage is not null).Select(graph => graph.Usage!.RecordedAt))
            .Concat(graphs.Where(graph => graph.Cache is not null).Select(graph => graph.Cache!.CreatedAt))
            .Append(state.UpdatedAt)
            .Max()
            .ToUniversalTime();
        var schedulerDigest = AiAnalysisCheckpointStateDigest.Compute(state);
        var graphIdentity = string.Join("|", graphs.Select(graph =>
            $"{graph.Request.RequestId}:{graph.Request.Status}:{graph.Request.UpdatedAt.UtcTicks}:{graph.Session.Status}:{graph.Session.SelectionRevision}:{graph.Usage?.UsageEventId}:{graph.Cache?.CacheKey}"));
        var graphDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(schedulerDigest + "|" + graphIdentity))).ToLowerInvariant();
        var checkpointId = $"checkpoint-{batchIds[0]}-{graphDigest[..16]}";
        var manifest = new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version,
            checkpointId,
            batchIds[0],
            graphs.Select(graph => graph.Session.SessionId).ToArray(),
            graphs.Select(graph => graph.Request.RequestId).ToArray(),
            cacheKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            now,
            schedulerDigest);
        return new AiAnalysisCheckpointCommand(
            checkpointId,
            manifest,
            graphs,
            now,
            _stateKey,
            state);
    }

    private async Task CommitGraphAsync(
        AiAnalysisRequestV1 request,
        AiRequestLedgerEntry ledger,
        string expectedRequestStatus,
        AiAnalysisSession session,
        AiAnalysisSessionStatus expectedSessionStatus,
        long expectedSelectionRevision,
        AiUsageEventRecord? usage,
        AiCacheReference? cache,
        CancellationToken cancellationToken,
        AiAnalysisWorkItem? requiredPending = null)
    {
        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CommitGraphCoreAsync(
                request,
                ledger,
                expectedRequestStatus,
                session,
                expectedSessionStatus,
                expectedSelectionRevision,
                usage,
                cache,
                requiredPending,
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _statePersistenceGate.Release();
        }
    }

    private async Task CommitGraphCoreAsync(
        AiAnalysisRequestV1 request,
        AiRequestLedgerEntry ledger,
        string expectedRequestStatus,
        AiAnalysisSession session,
        AiAnalysisSessionStatus expectedSessionStatus,
        long expectedSelectionRevision,
        AiUsageEventRecord? usage,
        AiCacheReference? cache,
        AiAnalysisWorkItem? requiredPending,
        string? replacedPendingWorkItemId,
        AiAnalysisWorkItem? replacementPending,
        CancellationToken cancellationToken)
    {
        if (_checkpointPort is null) throw new InvalidOperationException("AI_CHECKPOINT_PORT_REQUIRED");
        AiAnalysisWorkItem[] pending;
        AiAnalysisRequestV1[] history;
        AiAnalysisSchedulingControlState controlState;
        string? controlReason;
        lock (_pendingGate)
        {
            pending = _pendingItems.Values.OrderBy(item => item.WorkItemId, StringComparer.Ordinal).ToArray();
            history = _historicalRequests.Values.OrderBy(item => item.RequestId, StringComparer.Ordinal).ToArray();
            controlState = _controlState;
            controlReason = _controlReason;
        }
        if (requiredPending is not null && pending.All(item =>
                !string.Equals(item.WorkItemId, requiredPending.WorkItemId, StringComparison.Ordinal)))
        {
            pending = pending.Append(requiredPending)
                .OrderBy(item => item.WorkItemId, StringComparer.Ordinal)
                .ToArray();
        }
        if (replacedPendingWorkItemId is not null)
        {
            pending = pending
                .Where(item => !string.Equals(item.WorkItemId, replacedPendingWorkItemId, StringComparison.Ordinal))
                .ToArray();
        }
        if (replacementPending is not null && pending.All(item =>
                !string.Equals(item.WorkItemId, replacementPending.WorkItemId, StringComparison.Ordinal)))
        {
            pending = pending.Append(replacementPending)
                .OrderBy(item => item.WorkItemId, StringComparer.Ordinal)
                .ToArray();
        }
        if (ledger.Status is not ("Queued" or "RetryableError" or "Uploading" or "Analysing"))
        {
            pending = pending
                .Where(item => !string.Equals(item.Request.RequestId, ledger.RequestId, StringComparison.Ordinal))
                .ToArray();
        }

        AiAnalysisBudgetSnapshot budget;
        lock (_budgetGate) budget = _budget;
        var state = new AiAnalysisSchedulingState(
            pending,
            budget,
            NextPersistenceTimestamp(),
            controlState,
            controlReason,
            history);
        var target = new AiAnalysisCheckpointGraph(
            ledger,
            expectedRequestStatus,
            session,
            expectedSessionStatus,
            expectedSelectionRevision,
            usage,
            cache);
        var command = await BuildCheckpointCommandAsync(state, cancellationToken, target).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_CHECKPOINT_BATCH_SCOPE_INVALID");
        var result = await CommitCheckpointWithRecoveryAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == AiAnalysisCheckpointOutcome.RecoveryRequired)
        {
            throw new InvalidOperationException(result.ReasonCode);
        }

        // The durable scheduler snapshot and its in-memory mirror change while
        // the same persistence gate is still held. A second worker can never
        // observe a terminal graph paired with the old pending item.
        ApplyPendingMutation(
            ledger.RequestId,
            ledger.Status,
            replacedPendingWorkItemId,
            replacementPending);
    }

    private void ApplyPendingMutation(
        string requestId,
        string requestStatus,
        string? replacedPendingWorkItemId,
        AiAnalysisWorkItem? replacementPending)
    {
        lock (_pendingGate)
        {
            if (replacedPendingWorkItemId is not null)
            {
                _pendingItems.Remove(replacedPendingWorkItemId);
                _automaticRefillSuppressedItems.Remove(replacedPendingWorkItemId);
            }
            if (replacementPending is not null)
            {
                _pendingItems[replacementPending.WorkItemId] = replacementPending;
            }
            if (requestStatus is not ("Queued" or "RetryableError" or "Uploading" or "Analysing"))
            {
                foreach (var key in _pendingItems
                             .Where(pair => string.Equals(
                                 pair.Value.Request.RequestId, requestId, StringComparison.Ordinal))
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _pendingItems.Remove(key);
                    _automaticRefillSuppressedItems.Remove(key);
                }
            }
        }
    }

    private async Task RefillQueueFromPendingAsync(CancellationToken cancellationToken)
    {
        if (ControlState != AiAnalysisSchedulingControlState.Running)
        {
            return;
        }
        lock (_budgetGate)
        {
            if (_budget.HardStopped)
            {
                return;
            }
        }

        AiAnalysisWorkItem[] backlog;
        lock (_pendingGate)
        {
            backlog = _pendingItems.Values
                .Where(item => !_automaticRefillSuppressedItems.Contains(item.WorkItemId))
                .OrderBy(item => item.EnqueuedAt)
                .ThenBy(item => item.WorkItemId, StringComparer.Ordinal)
                .ToArray();
        }

        foreach (var item in backlog)
        {
            if (_queue.IsActive(item.WorkItemId))
            {
                continue;
            }

            var ledger = await _store.FindRequestByFingerprintAsync(
                AiAnalysisCacheKeyFactory.Create(item.Request),
                cancellationToken).ConfigureAwait(false);
            if (ledger?.Status is not ("Queued" or "RetryableError")
                || !string.Equals(ledger.RequestId, item.Request.RequestId, StringComparison.Ordinal))
            {
                // Uploading/Analysing are retained as unresolved evidence. A
                // terminal stale item is likewise never re-executed here.
                continue;
            }

            if (ControlState != AiAnalysisSchedulingControlState.Running)
            {
                return;
            }

            if (!_queue.TryEnqueue(item))
            {
                break;
            }
        }
    }

    private async Task<AiAnalysisCheckpointResult> CommitCheckpointWithRecoveryAsync(
        AiAnalysisCheckpointCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _checkpointPort!.CommitAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message == "AI_CHECKPOINT_BATCH_PENDING")
        {
            var recovery = await _checkpointPort!.RecoverAsync(command.Manifest.BatchId, cancellationToken).ConfigureAwait(false);
            if (recovery.Outcome == AiAnalysisCheckpointOutcome.RecoveryRequired)
            {
                return recovery;
            }

            return await _checkpointPort.CommitAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private DateTimeOffset NextPersistenceTimestamp()
    {
        lock (_timestampGate)
        {
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            if (now <= _lastPersistenceTimestamp)
            {
                now = _lastPersistenceTimestamp.AddTicks(1);
            }

            _lastPersistenceTimestamp = now;
            return now;
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

    private void SetControlState(AiAnalysisSchedulingControlState state, string? reason)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (reason is not null && (reason.Length > 512 || reason.Contains('\0')))
        {
            throw new ArgumentException("AI 调度控制原因无效。", nameof(reason));
        }

        lock (_pendingGate)
        {
            if (_controlState == AiAnalysisSchedulingControlState.Cancelled
                && state != AiAnalysisSchedulingControlState.Cancelled)
            {
                throw new InvalidOperationException("AI_SCHEDULER_CANCELLED");
            }

            _controlState = state;
            _controlReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
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

    public void Dispose()
    {
        _processingGate.Dispose();
        _statePersistenceGate.Dispose();
    }
}
