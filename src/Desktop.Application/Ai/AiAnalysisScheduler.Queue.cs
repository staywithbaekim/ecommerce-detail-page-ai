using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

public sealed partial class AiAnalysisScheduler
{
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
                _terminalErrors.Remove(request.RequestId);
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

        if (existing.Status is AiLedgerStatusPolicy.Validated or AiLedgerStatusPolicy.ManualReview or AiLedgerStatusPolicy.PlanClosed
            or AiLedgerStatusPolicy.AiResultUnknown or AiLedgerStatusPolicy.Cancelled or AiLedgerStatusPolicy.TerminalError)
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
            RequestId = RefinementRequestId(
                firstLayerRequest.RequestId,
                "geometry",
                selected.Select(asset => asset.AssetId)),
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
                RequestId = RefinementRequestId(
                    firstLayerRequest.RequestId,
                    "size",
                    selected.Select(asset => asset.AssetId)),
                AnalysisKind = AiAnalysisKind.SizeTableExtraction,
                Assets = selected
            };
            results.Add(await EnqueueAsync(
                sizeRequest,
                AiAnalysisWorkStage.SizeTableExtraction,
                cancellationToken).ConfigureAwait(false));
        }

        // Keep the first-layer candidate-pool lease while human replacement
        // remains possible. A user may confirm A and later switch to B; an
        // early parent release would delete B's last application-owned copy.
        // Explicit PlanClosed/cancel/terminal cleanup remains the parent
        // lease lifecycle boundary.

        return results;
    }

    private static string RefinementRequestId(
        string firstLayerRequestId,
        string kind,
        IEnumerable<string> selectedAssetIds)
    {
        var assetSet = string.Join(
            "\n",
            selectedAssetIds.Order(StringComparer.Ordinal));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{firstLayerRequestId}|{kind}|{assetSet}")))
            .ToLowerInvariant()[..16];
        var tail = $"-{kind}-{digest}";
        var prefixLength = Math.Min(firstLayerRequestId.Length, 128 - tail.Length);
        return firstLayerRequestId[..prefixLength] + tail;
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
                        if (ledger?.Status is AiLedgerStatusPolicy.Queued or AiLedgerStatusPolicy.RetryableError)
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

        if (ledger.Status is AiLedgerStatusPolicy.Validated or AiLedgerStatusPolicy.ManualReview or AiLedgerStatusPolicy.PlanClosed
            or AiLedgerStatusPolicy.Cancelled or AiLedgerStatusPolicy.TerminalError or AiLedgerStatusPolicy.AiResultUnknown)
        {
            return new(AiAnalysisWorkDisposition.AlreadySettled, item.Request.RequestId, ledger.ResultReference, null, item.Attempt);
        }

        if (!AiLedgerStatusPolicy.IsRunnable(ledger.Status))
        {
            return Failure(item, AiAnalysisWorkDisposition.ResultUnknown, "AI_REQUEST_IN_FLIGHT");
        }

        if (_consentPort is not null)
        {
            // This read is only an early, user-facing pause check.  It is not
            // the security boundary because revocation may race it; the
            // authoritative admission read occurs immediately before budget
            // reservation below.
            var consent = await _consentPort.GetConsentAsync(
                AiConsentScopes.AnalysisCopy,
                cancellationToken).ConfigureAwait(false);
            if (consent?.IsGranted != true)
            {
                var reason = consent?.State == AiUploadConsentState.Revoked
                    ? "AI_UPLOAD_CONSENT_REVOKED"
                    : "AI_UPLOAD_CONSENT_REQUIRED";
                SetControlState(
                    AiAnalysisSchedulingControlState.Paused,
                    reason);
                await PersistStateAsync(cancellationToken).ConfigureAwait(false);
                return Failure(item, AiAnalysisWorkDisposition.Paused, reason);
            }
        }

        if (ledger.Status == AiLedgerStatusPolicy.RetryableError)
        {
            // A refill may observe the durable retry checkpoint before the
            // normal RetryableError -> Queued hop completes. Normalize only
            // when a worker actually takes the item, and do it before budget
            // reservation or provider execution. A failed normalization stays
            // fail-closed without consuming another attempt.
            await TransitionToAsync(
                item.Request, AiAnalysisSessionStatus.Queued, cancellationToken).ConfigureAwait(false);
        }

        // This is the final local admission boundary.  SqliteDesktopStore
        // implements TryAcquireAsync with the same serialized writer used by
        // RevokeAsync, so a revoke committed first is observed here and no
        // budget/provider side effect follows.  Once this read commits, the
        // request is considered admitted; revocation after that point does
        // not retroactively cancel an already-admitted provider call.  The
        // admission operation itself never performs network I/O.
        if (_consentPort is not null)
        {
            AiUploadConsentRecord? admitted;
            try
            {
                admitted = await _consentPort.TryAcquireAsync(
                    AiConsentScopes.AnalysisCopy,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The final authorization source is a local security boundary.
                // If it cannot be read, keep the request queued, consume no
                // budget and never cross the provider boundary. Persistence is
                // best-effort because the same storage fault may also prevent
                // saving the paused scheduler state; the in-process projection
                // must still stop showing false progress.
                SetControlState(
                    AiAnalysisSchedulingControlState.Paused,
                    "AI_UPLOAD_CONSENT_UNAVAILABLE");
                try
                {
                    await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ledger remains Queued. A repaired store plus explicit
                    // Resume/restart can safely re-evaluate admission.
                }
                return Failure(
                    item,
                    AiAnalysisWorkDisposition.Paused,
                    "AI_UPLOAD_CONSENT_UNAVAILABLE");
            }
            if (admitted?.IsGranted != true)
            {
                var reason = admitted?.State == AiUploadConsentState.Revoked
                    ? "AI_UPLOAD_CONSENT_REVOKED"
                    : "AI_UPLOAD_CONSENT_REQUIRED";
                SetControlState(
                    AiAnalysisSchedulingControlState.Paused,
                    reason);
                await PersistStateAsync(cancellationToken).ConfigureAwait(false);
                return Failure(item, AiAnalysisWorkDisposition.Paused, reason);
            }
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

        if (execution.Outcome == AiProviderOutcome.TerminalError
            && IsRecoverableCredentialPreflight(execution.Error?.Code))
        {
            // These adapter codes are emitted before any HTTP request is
            // constructed or sent. Keep the logical request queued so fixing
            // Credential Manager plus an explicit Resume retries only this
            // failed range; unknown/in-flight outcomes never enter this path.
            SetControlState(
                AiAnalysisSchedulingControlState.Paused,
                execution.Error!.Code);
            await TransitionToAsync(
                item.Request,
                AiAnalysisSessionStatus.RetryableError,
                persistenceToken,
                releaseUnusedRequestReservation: true).ConfigureAwait(false);
            await TransitionToAsync(
                item.Request,
                AiAnalysisSessionStatus.Queued,
                persistenceToken).ConfigureAwait(false);
            return Failure(
                item,
                AiAnalysisWorkDisposition.Paused,
                execution.Error.Code);
        }

        if (execution.Outcome == AiProviderOutcome.TerminalError
            && string.Equals(execution.Error?.Code, "AI_PROVIDER_REQUEST_TOO_LARGE", StringComparison.Ordinal))
        {
            // The final JSON preflight runs before HttpClient.SendAsync. No
            // provider request was admitted, so release the local reservation
            // while retaining the terminal error and its durable UI projection.
            return await CompleteTerminalAsync(
                item,
                AiAnalysisWorkDisposition.TerminalError,
                execution.Error!.Code,
                AiAnalysisSessionStatus.TerminalError,
                persistenceToken,
                releaseUnusedRequestReservation: true).ConfigureAwait(false);
        }

        if (execution.Outcome == AiProviderOutcome.Completed && execution.Result is not null)
        {
            try
            {
                return await CompleteResultAsync(item, execution, persistenceToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                if (execution.Usage is not null)
                {
                    await RecordObservedUsageAsync(item, execution.Usage, persistenceToken)
                        .ConfigureAwait(false);
                }
                return await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.TerminalError, "AI_RESULT_PERSISTENCE_FAILED", AiAnalysisSessionStatus.TerminalError, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (execution.Outcome == AiProviderOutcome.RetryableError)
        {
            if (item.Attempt + 1 < _queueOptions.MaxAttempts)
            {
                return await ScheduleRetryAsync(item, persistenceToken).ConfigureAwait(false);
            }

            var providerCode = execution.Error?.Code;
            var terminalCode = providerCode is
                "AI_PROVIDER_SUBMISSION_TIMEOUT" or "AI_PROVIDER_SUBMISSION_NETWORK_FAILURE"
                    ? providerCode
                    : "AI_PROVIDER_RETRY_EXHAUSTED";
            return await CompleteTerminalAsync(
                item,
                AiAnalysisWorkDisposition.TerminalError,
                terminalCode,
                AiAnalysisSessionStatus.TerminalError,
                persistenceToken).ConfigureAwait(false);
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
            AiProviderOutcome.AiResultUnknown => await CompleteTerminalAsync(
                item,
                AiAnalysisWorkDisposition.ResultUnknown,
                execution.Error?.Code ?? "AI_PROVIDER_RESULT_UNKNOWN",
                AiAnalysisSessionStatus.AiResultUnknown,
                persistenceToken).ConfigureAwait(false),
            AiProviderOutcome.Cancelled => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.Cancelled, "AI_REQUEST_CANCELLED", AiAnalysisSessionStatus.Cancelled, persistenceToken).ConfigureAwait(false),
            _ => await CompleteTerminalAsync(item, AiAnalysisWorkDisposition.TerminalError, execution.Error?.Code ?? "AI_PROVIDER_ERROR", AiAnalysisSessionStatus.TerminalError, persistenceToken).ConfigureAwait(false)
        };
    }


}
