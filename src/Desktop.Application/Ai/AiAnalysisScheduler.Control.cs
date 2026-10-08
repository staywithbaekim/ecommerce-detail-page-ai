using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

public sealed partial class AiAnalysisScheduler
{
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
            if (ledger is null || ledger.Status is not (AiLedgerStatusPolicy.Queued or AiLedgerStatusPolicy.RetryableError or AiLedgerStatusPolicy.Uploading))
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

            // Recovery may finish a pending journal that atomically changes
            // the request graph and scheduler snapshot. Never continue from
            // the pre-recovery read: doing so can resurrect an old budget,
            // control state or pending attempt and duplicate a reservation.
            state = await _stateStore.LoadSchedulingStateAsync(
                _stateKey,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_SCHEDULER_STATE_MISSING_AFTER_RECOVERY");
        }

        ValidateBudgetSnapshot(state.Budget);
        lock (_budgetGate)
        {
            _budgetLedger.Restore(state.Budget);
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
            _terminalErrors.Clear();
            _recoveredInFlightRequestIds = ImmutableHashSet<string>.Empty;
            foreach (var historical in state.HistoricalRequests ?? [])
            {
                _historicalRequests[historical.RequestId] = historical;
            }
            foreach (var pair in state.TerminalErrors ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                _terminalErrors[pair.Key] = pair.Value;
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

            if (ledger.Status is AiLedgerStatusPolicy.Validated or AiLedgerStatusPolicy.ManualReview or AiLedgerStatusPolicy.PlanClosed
                or AiLedgerStatusPolicy.Cancelled or AiLedgerStatusPolicy.TerminalError or AiLedgerStatusPolicy.AiResultUnknown)
            {
                await ReleaseRequestLeasesAsync(item.Request, item.Stage, cancellationToken).ConfigureAwait(false);
                lock (_pendingGate)
                {
                    _pendingItems.Remove(item.WorkItemId);
                }

                continue;
            }

            if (AiLedgerStatusPolicy.IsInFlight(ledger.Status))
            {
                // Preserve the ledger and checkpoint evidence. The projection
                // must distinguish this abandoned work from a live provider call.
                lock (_pendingGate)
                    _recoveredInFlightRequestIds = _recoveredInFlightRequestIds.Add(item.Request.RequestId);
            }

            if (state.ControlState is AiAnalysisSchedulingControlState.CancelRequested
                or AiAnalysisSchedulingControlState.Cancelled)
            {
                // A crash can occur after cancellation is durably requested but
                // before a queued item's ledger transition. Complete that
                // explicit transition on restore and never re-enqueue it. An
                // in-flight item remains as auditable recovery evidence; it is
                // not guessed successful or silently discarded.
                if (AiLedgerStatusPolicy.IsRunnable(ledger.Status))
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

            if (!AiLedgerStatusPolicy.IsRunnable(ledger.Status))
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

            if (ledger.Status == AiLedgerStatusPolicy.RetryableError)
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


}
