using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

/// <summary>
/// Batch-scoped commands and request facts. Owns its scheduler, recovery and
/// background runner; the workflow cancels and drains all batches before disposal.
/// </summary>
public sealed class AiBatchCommandService : IDisposable
{
    private const string ModelConfigVersion = AiAnalysisProductionPolicy.ModelConfigVersionV2;
    private const string PromptContractVersion = AiAnalysisProductionPolicy.PromptContractVersionV3;
    private const string CompressionProfileVersion = AiAnalysisCopyProfiles.WicBoundedV4;

    private readonly BatchDraft _batch;
    private readonly TemplateRevision _templateRevision;
    private readonly string _templateGroupName;
    private readonly AiAnalysisScheduler _scheduler;
    private readonly AiAnalysisRecoveryService _recovery;
    private readonly AiBatchBackgroundRunner _background;
    private readonly IAiAnalysisStore _store;
    private readonly IAiAnalysisSchedulingStateStore _schedulingState;
    private readonly IConsentPort _consent;
    private readonly ConcurrentDictionary<string, AiAnalysisRequestV1> _requests = new(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _terminalErrors = ImmutableDictionary<string, string>.Empty;
    private bool _started;

    public AiBatchCommandService(
        BatchDraft batch,
        TemplateRevision templateRevision,
        string templateGroupName,
        AiAnalysisScheduler scheduler,
        IAiAnalysisStore store,
        IAiAnalysisSchedulingStateStore schedulingState,
        IConsentPort consent,
        CancellationToken shutdownToken,
        IAiAnalysisCheckpointPort? checkpointPort = null)
    {
        _batch = batch;
        _templateRevision = templateRevision;
        _templateGroupName = templateGroupName;
        _scheduler = scheduler;
        _store = store;
        _schedulingState = schedulingState;
        _consent = consent;
        _recovery = new AiAnalysisRecoveryService(batch.BatchId, scheduler, checkpointPort);
        _background = new AiBatchBackgroundRunner(batch, templateRevision, scheduler, shutdownToken);
    }

    public async Task StartAsync(
        CancellationToken cancellationToken)
    {
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        await _recovery.EnsureAnalysisStateRecoveredAsync(cancellationToken).ConfigureAwait(false);
        _recovery.ThrowIfAnalysisStateUnavailable();
        if (_batch.ScanStatus != BatchScanStatus.ScanReady)
        {
            throw new InvalidOperationException("AI_INPUT_BATCH_NOT_READY");
        }

        var consent = await RequireUploadConsentAsync(cancellationToken).ConfigureAwait(false);

        var projectionOptions = ProjectionOptions(_batch.BatchId, consent.ConsentId);
        _started = true;
        await RefreshRequestsAsync(cancellationToken).ConfigureAwait(false);
        _background.StartAdmission(projectionOptions);
        _background.StartProcessing();
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken)
    {
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        await _recovery.EnsureAnalysisStateRecoveredAsync(cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(cancellationToken).ConfigureAwait(false);
        if (_started
            && _scheduler.ControlState == AiAnalysisSchedulingControlState.Running
            && (_scheduler.ReadyCount > 0 || _scheduler.PendingCount > 0))
        {
            _background.StartProcessing();
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken) =>
        ControlAsync((scheduler, token) => scheduler.RequestPauseAsync("用户已暂停分析；可恢复继续。", token), cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) =>
        ControlAsync((scheduler, token) => scheduler.ResumeAsync(token), cancellationToken);

    public async Task CancelAsync(
        CancellationToken cancellationToken)
    {
        _background.StopAdmission();
        await ControlAsync(
            (scheduler, token) => scheduler.CancelAsync(_batch.BatchId, token),
            cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForNetworkAsync(CancellationToken cancellationToken) =>
        ControlAsync((scheduler, token) => scheduler.WaitForNetworkAsync("网络暂不可用；恢复网络后可继续。", token), cancellationToken);

    public async Task LockSelectionAsync(
        string styleId,
        string requirementId,
        string assetId,
        CancellationToken cancellationToken)
    {
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        var request = await GetFirstLayerRequestAsync(styleId, cancellationToken).ConfigureAwait(false);
        _ = await _scheduler.LockSelectionAsync(
            request,
            requirementId,
            assetId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectSizeTableSourceAsync(
        string styleId,
        string assetId,
        CancellationToken cancellationToken)
    {
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        var request = await GetFirstLayerRequestAsync(styleId, cancellationToken).ConfigureAwait(false);
        var style = _batch.Styles.SingleOrDefault(item => item.StyleId == styleId)
            ?? throw new InvalidOperationException("AI_INPUT_STYLE_NOT_FOUND");
        if (!style.AssetIds.Contains(assetId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("AI_SIZE_TABLE_SOURCE_ASSET_INVALID");
        }

        foreach (var page in _templateRevision.Pages.Where(page =>
                     page.ContentType == TemplatePageContentType.DynamicSizeTable
                     && _batch.TemplateSnapshot.Pages.Any(snapshot => snapshot.PageId == page.PageId)))
        {
            _ = await _scheduler.LockSelectionAsync(
                request,
                AiAnalysisInputProjector.SizeTableRequirementId(page.PageId),
                assetId,
                cancellationToken).ConfigureAwait(false);
        }

        _ = await _scheduler.EnqueueRefinementAsync(
            request,
            [assetId],
            includeSizeTableSource: true,
            cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(cancellationToken).ConfigureAwait(false);
        _background.StartProcessing();
    }

    public async Task AnalyzeSelectedAssetAsync(
        string styleId,
        string assetId,
        bool includeSizeTableSource,
        CancellationToken cancellationToken)
    {
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        var request = await GetFirstLayerRequestAsync(styleId, cancellationToken).ConfigureAwait(false);
        var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
        var hasLockedAsset = session.LockedSelections.Values.Any(selection =>
            string.Equals(selection.AssetId, assetId, StringComparison.Ordinal));
        if ((_batch.SelectionMode == BatchSelectionMode.Manual && !hasLockedAsset)
            || (session.LockedSelections.Count > 0 && !hasLockedAsset))
        {
            throw new InvalidOperationException("AI_REFINEMENT_SELECTION_NOT_LOCKED");
        }
        _ = await _scheduler.EnqueueRefinementAsync(
            request,
            [assetId],
            includeSizeTableSource,
            cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(cancellationToken).ConfigureAwait(false);
        _background.StartProcessing();
    }

    public async Task RefreshForRenderingAsync(CancellationToken cancellationToken)
    {
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read/recovery check only; workflow holds command admission until the repair is registered.</summary>
    public async Task RequireRepairIdleAsync(CancellationToken cancellationToken)
    {
        if (!_background.IsAdmissionSettled || !_scheduler.IsIdleForRepair)
            throw new InvalidOperationException("SIZE_REPAIR_AI_NOT_SETTLED");
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        await _recovery.EnsureAnalysisStateRecoveredAsync(cancellationToken).ConfigureAwait(false);
        _recovery.ThrowIfAnalysisStateUnavailable();
        var state = await _schedulingState.LoadSchedulingStateAsync(_scheduler.StateKey, cancellationToken).ConfigureAwait(false);
        if (state is null || state.PendingItems.Count != 0 || state.HistoricalRequests is not { Count: > 0 } requests
            || requests.Any(request => request.BatchId != _batch.BatchId)
            || state.ControlState is AiAnalysisSchedulingControlState.PauseRequested or AiAnalysisSchedulingControlState.CancelRequested
            || _batch.Styles.Any(style => !requests.Any(request => request.StyleId == style.StyleId
                && request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding)))
            throw new InvalidOperationException("SIZE_REPAIR_AI_NOT_SETTLED");
        foreach (var request in requests)
        {
            var ledger = await _store.FindRequestByFingerprintAsync(AiAnalysisCacheKeyFactory.Create(request), cancellationToken).ConfigureAwait(false);
            if (ledger is null || ledger.BatchId != _batch.BatchId || ledger.StyleId != request.StyleId
                || ledger.RequestId != request.RequestId
                || !AiLedgerStatusPolicy.IsTerminal(ledger.Status)
                || AiLedgerStatusPolicy.IsUnknown(ledger.Status))
                throw new InvalidOperationException("SIZE_REPAIR_AI_NOT_SETTLED");
        }
        if (!_background.IsAdmissionSettled || !_scheduler.IsIdleForRepair || _scheduler.RecoveredInFlightRequestIds.Count != 0)
            throw new InvalidOperationException("SIZE_REPAIR_AI_NOT_SETTLED");
    }

    public ImmutableArray<AiAnalysisRequestV1> CaptureRequests() => _requests.Values.ToImmutableArray();

    private async Task ControlAsync(
        Func<AiAnalysisScheduler, CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        await _recovery.EnsureSchedulerRestoredAsync(cancellationToken).ConfigureAwait(false);
        await _recovery.EnsureAnalysisStateRecoveredAsync(cancellationToken).ConfigureAwait(false);
        await command(_scheduler, cancellationToken).ConfigureAwait(false);
        await RefreshRequestsAsync(cancellationToken).ConfigureAwait(false);
        if (_scheduler.ControlState == AiAnalysisSchedulingControlState.Running)
        {
            _background.StartProcessing();
        }
    }

    internal async Task RefreshRequestsAsync(CancellationToken cancellationToken)
    {
        var state = await _schedulingState.LoadSchedulingStateAsync(
            _scheduler.StateKey,
            cancellationToken).ConfigureAwait(false);
        if (state?.HistoricalRequests is { Count: > 0 } requests)
        {
            _started |= requests.Any(request =>
                string.Equals(request.BatchId, _batch.BatchId, StringComparison.Ordinal));
            foreach (var request in requests.Where(request =>
                         string.Equals(request.BatchId, _batch.BatchId, StringComparison.Ordinal)))
            {
                _requests[request.RequestId] = request;
            }
        }

        var terminalErrors = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in state?.TerminalErrors
                     ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            if (_requests.ContainsKey(pair.Key))
            {
                terminalErrors[pair.Key] = pair.Value;
            }
        }
        foreach (var pair in _scheduler.TerminalErrorsSnapshot)
        {
            if (_requests.ContainsKey(pair.Key))
            {
                terminalErrors[pair.Key] = pair.Value;
            }
        }

        Interlocked.Exchange(ref _terminalErrors, terminalErrors.ToImmutable());

        foreach (var style in _batch.Styles)
        {
            if (_requests.ContainsKey($"ai-{_batch.BatchId}-{style.StyleId}"))
            {
                continue;
            }

            try
            {
                var projected = AiAnalysisInputProjector.Project(
                    _batch,
                    _templateRevision,
                    style.StyleId,
                    ProjectionOptions(_batch.BatchId));
                _requests[projected.RequestId] = projected;
            }
            catch (InvalidOperationException)
            {
                // BuildProjection will expose an actionable terminal state.
            }
        }
    }

    public AiBatchProjectionSnapshot CaptureSnapshot() =>
        new(
            _batch,
            _templateRevision,
            _templateGroupName,
            _requests.Values.ToImmutableArray(),
            _scheduler.ControlState,
            _scheduler.ControlReason,
            _scheduler.Budget.HardStopped,
            _recovery.RestoreError,
            _started,
            _background.AdmissionError,
            _background.AdmissionFailures,
            CaptureTerminalErrors(),
            _background.RuntimeFailures)
        {
            RecoveredInFlightRequestIds = _scheduler.RecoveredInFlightRequestIds
        };

    // Called after the projection's asynchronous ledger reads: a newly observed
    // terminal ledger must be explained by the scheduler's corresponding code,
    // not only the facts captured before that transition completed.
    public ImmutableDictionary<string, string> CaptureTerminalErrors() =>
        Volatile.Read(ref _terminalErrors).SetItems(_scheduler.TerminalErrorsSnapshot
            .Where(pair => _requests.ContainsKey(pair.Key)));

    private static AiAnalysisProjectionOptions ProjectionOptions(string batchId, string? consentId = null) => new(
        $"wpf-{batchId}",
        consentId ?? $"consent-pending-{batchId}",
        ModelConfigVersion,
        PromptContractVersion,
        CompressionProfileVersion);

    private async Task<AiUploadConsentRecord> RequireUploadConsentAsync(CancellationToken cancellationToken)
    {
        var consent = await _consent.GetConsentAsync(
            AiConsentScopes.AnalysisCopy,
            cancellationToken).ConfigureAwait(false);
        if (consent?.IsGranted != true)
        {
            throw new InvalidOperationException(
                consent?.State == AiUploadConsentState.Revoked
                    ? "AI_UPLOAD_CONSENT_REVOKED"
                    : "AI_UPLOAD_CONSENT_REQUIRED");
        }

        return consent;
    }

    private async Task<AiAnalysisRequestV1> GetFirstLayerRequestAsync(
        string styleId,
        CancellationToken cancellationToken)
    {
        // A projection can observe a completed result after reading an older
        // scheduler snapshot. Commands must use the admitted parent's consent
        // and analysis-copy metadata, not that projection's placeholder.
        var state = await _schedulingState.LoadSchedulingStateAsync(
            _scheduler.StateKey, cancellationToken).ConfigureAwait(false);
        return state?.HistoricalRequests?.FirstOrDefault(request =>
                request.BatchId == _batch.BatchId && request.StyleId == styleId
                && request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding)
            ?? _requests.Values.FirstOrDefault(request =>
                request.StyleId == styleId && request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding)
            ?? throw new InvalidOperationException("AI_REQUEST_NOT_FOUND");
    }

    private static string SessionId(AiAnalysisRequestV1 request) => $"session-{request.RequestId}";

    public void StopAdmission() => _background.StopAdmission();

    public void WaitForBackgroundCompletion() => _background.WaitForCompletion();

    public void Dispose()
    {
        _background.StopAdmission();
        _scheduler.Dispose();
        _recovery.Dispose();
        _background.Dispose();
        GC.SuppressFinalize(this);
    }
}
