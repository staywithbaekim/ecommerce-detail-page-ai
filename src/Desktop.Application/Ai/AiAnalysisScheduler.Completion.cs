using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

public sealed partial class AiAnalysisScheduler
{
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

        // Validate the typed result before it can cross any persistence
        // boundary.  The formal result store performs the same check when it
        // serializes, but the scheduler also supports a legacy in-memory path
        // without a result store; keeping this guard here ensures malformed
        // provider output can never advance the ledger/session or create a
        // reusable cache entry on that path.
        AiAnalysisResultSemanticValidator.Validate(execution.Result!);

        if (_resultStore is null)
        {
            if (_strictResultPersistence)
            {
                if (execution.Usage is not null)
                {
                    await RecordObservedUsageAsync(item, execution.Usage, cancellationToken)
                        .ConfigureAwait(false);
                }
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
                _timeProvider.GetUtcNow(),
                execution.Usage.CachedInputTokens);
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
            _timeProvider.GetUtcNow(),
            usage.CachedInputTokens);
        if (await _store.FindUsageEventAsync(usageRecord.UsageEventId, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            return;
        }
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
        CancellationToken cancellationToken,
        bool releaseUnusedRequestReservation = false)
    {
        await TransitionToAsync(
            item.Request,
            status,
            cancellationToken,
            releaseUnusedRequestReservation: releaseUnusedRequestReservation,
            terminalErrorCode: status is AiAnalysisSessionStatus.TerminalError
                or AiAnalysisSessionStatus.AiResultUnknown
                ? errorCode
                : null).ConfigureAwait(false);
        return Failure(item, disposition, errorCode);
    }

    private async Task TransitionToAsync(
        AiAnalysisRequestV1 request,
        AiAnalysisSessionStatus next,
        CancellationToken cancellationToken,
        string? replacedPendingWorkItemId = null,
        AiAnalysisWorkItem? replacementPending = null,
        bool releaseUnusedRequestReservation = false,
        string? terminalErrorCode = null)
    {
        await _statePersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AiAnalysisBudgetSnapshot? budgetBeforeRelease = null;
        string? terminalErrorBefore = null;
        bool hadTerminalError = false;
        try
        {
            var ledger = await _store.FindRequestByFingerprintAsync(
                AiAnalysisCacheKeyFactory.Create(request),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_REQUEST_LEDGER_MISSING");
            var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
            lock (_pendingGate)
            {
                hadTerminalError = _terminalErrors.TryGetValue(request.RequestId, out terminalErrorBefore);
                if (next is AiAnalysisSessionStatus.TerminalError
                    or AiAnalysisSessionStatus.AiResultUnknown)
                {
                    if (string.IsNullOrWhiteSpace(terminalErrorCode))
                    {
                        throw new InvalidOperationException("AI_TERMINAL_ERROR_CODE_MISSING");
                    }

                    _terminalErrors[request.RequestId] = terminalErrorCode;
                }
                else
                {
                    _terminalErrors.Remove(request.RequestId);
                }
            }
            if (releaseUnusedRequestReservation)
            {
                lock (_budgetGate)
                {
                    if (_budget.RequestCount <= 0)
                    {
                        throw new InvalidOperationException("AI_BUDGET_RESERVATION_MISSING");
                    }

                    budgetBeforeRelease = _budget;
                    var requestCount = _budget.RequestCount - 1;
                    _budget = _budget with
                    {
                        RequestCount = requestCount,
                        HardStopped = _budget.InputTokens >= _budgetPolicy.MaxInputTokens
                            || _budget.OutputTokens >= _budgetPolicy.MaxOutputTokens
                            || _budget.EstimatedCost >= _budgetPolicy.MaxEstimatedCost
                            || requestCount >= _budgetPolicy.MaxRequests
                    };
                }
            }
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
            if (_stateStore is not null)
            {
                await PersistStateCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (budgetBeforeRelease is not null)
            {
                lock (_budgetGate)
                {
                    _budget = budgetBeforeRelease;
                }
            }

            lock (_pendingGate)
            {
                if (hadTerminalError)
                {
                    _terminalErrors[request.RequestId] = terminalErrorBefore!;
                }
                else
                {
                    _terminalErrors.Remove(request.RequestId);
                }
            }

            throw;
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

    private static bool IsRecoverableCredentialPreflight(string? errorCode) => errorCode is
        "AI_PROVIDER_CREDENTIAL_MISSING"
        or "AI_PROVIDER_CREDENTIAL_UNAVAILABLE"
        or "AI_PROVIDER_CREDENTIAL_INVALID";

}
