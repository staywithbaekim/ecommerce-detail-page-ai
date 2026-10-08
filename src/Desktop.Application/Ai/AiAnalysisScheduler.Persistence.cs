using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

public sealed partial class AiAnalysisScheduler
{
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
        KeyValuePair<string, string>[] terminalErrors;
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
            terminalErrors = _terminalErrors
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
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
            historicalRequests,
            terminalErrors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
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
        KeyValuePair<string, string>[] terminalErrors;
        lock (_pendingGate)
        {
            pending = _pendingItems.Values.OrderBy(item => item.WorkItemId, StringComparer.Ordinal).ToArray();
            history = _historicalRequests.Values.OrderBy(item => item.RequestId, StringComparer.Ordinal).ToArray();
            controlState = _controlState;
            controlReason = _controlReason;
            terminalErrors = _terminalErrors
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
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
        if (ledger.Status is not (AiLedgerStatusPolicy.Queued or AiLedgerStatusPolicy.RetryableError or AiLedgerStatusPolicy.Uploading or AiLedgerStatusPolicy.Analysing))
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
            history,
            terminalErrors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
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
            if (requestStatus is not (AiLedgerStatusPolicy.Queued or AiLedgerStatusPolicy.RetryableError or AiLedgerStatusPolicy.Uploading or AiLedgerStatusPolicy.Analysing))
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
            if (ledger?.Status is not (AiLedgerStatusPolicy.Queued or AiLedgerStatusPolicy.RetryableError)
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


}
