using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

public sealed partial class AiAnalysisScheduler
{
    public async Task<AiAnalysisSession> LockSelectionAsync(
        AiAnalysisRequestV1 request,
        string requirementId,
        string assetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_pendingGate)
        {
            if (!_historicalRequests.TryGetValue(request.RequestId, out var historical)
                || !string.Equals(historical.BatchId, request.BatchId, StringComparison.Ordinal)
                || !string.Equals(historical.StyleId, request.StyleId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI_REQUEST_NOT_FOUND");
            }
        }

        var fingerprint = AiAnalysisCacheKeyFactory.Create(request);
        var ledger = await _store.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_REQUEST_NOT_FOUND");
        var isVerifiedResult = ledger.Status is AiLedgerStatusPolicy.Validated or AiLedgerStatusPolicy.ManualReview
            && ledger.ResultReference is not null;
        var isUnknownResult = AiLedgerStatusPolicy.IsUnknown(ledger.Status)
            && ledger.ResultReference is null;
        if (!isVerifiedResult && !isUnknownResult)
        {
            throw new InvalidOperationException("AI_SELECTION_NOT_AVAILABLE");
        }

        var session = await _store.LoadSessionAsync(SessionId(request), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI_SESSION_NOT_FOUND");
        if (!string.Equals(ledger.Status, session.Status.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_SELECTION_STATE_MISMATCH");
        }
        var locked = session.LockSelection(requirementId, assetId, session.SelectionRevision);
        // The first manual lock transitions a validated request to
        // ManualReview.  Later replacements only advance the session's
        // selection revision; rewriting the already-terminal ledger with the
        // same status would look like a stale CAS update and incorrectly make
        // the second (or third) replacement fail.
        var nextLedger = isUnknownResult || string.Equals(
                ledger.Status,
                AiAnalysisSessionStatus.ManualReview.ToString(),
                StringComparison.Ordinal)
            ? ledger
            : ledger with
            {
                Status = AiAnalysisSessionStatus.ManualReview.ToString(),
                UpdatedAt = NextTimestamp(ledger.UpdatedAt)
            };
        var usage = (await _store.LoadUsageAsync(request.RequestId, cancellationToken).ConfigureAwait(false))
            .OrderBy(item => item.RecordedAt)
            .LastOrDefault();
        var cache = await _store.FindCacheReferenceAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        await CommitGraphAsync(
            request,
            nextLedger,
            ledger.Status,
            locked,
            session.Status,
            session.SelectionRevision,
            usage,
            cache,
            cancellationToken).ConfigureAwait(false);
        return locked;
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
        var preparations = request.Assets
            .Select(asset => PrepareCopyAsync(
                batch,
                style,
                asset,
                sourceById,
                compressionProfileVersion,
                cancellationToken))
            .ToArray();
        try
        {
            var prepared = await Task.WhenAll(preparations).ConfigureAwait(false);
            return request with { Assets = prepared };
        }
        catch
        {
            var prepared = preparations
                .Where(task => task.Status == TaskStatus.RanToCompletion)
                .Select(task => task.Result)
                .ToArray();
            await ReleasePreparedLeaseIdsAsync(
                request with { Assets = prepared }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AiAssetInputV1> PrepareCopyAsync(
        BatchDraft batch,
        StyleDraft style,
        AiAssetInputV1 asset,
        Dictionary<string, SourceAssetMetadata> sourceById,
        string compressionProfileVersion,
        CancellationToken cancellationToken)
    {
        if (!sourceById.TryGetValue(asset.AssetId, out var source)
            || !string.Equals(source.StyleId, style.StyleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_INPUT_ASSET_NOT_FOUND");
        }

        await _copyPreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await _copyPreparer!.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    asset.AnalysisCopyReference,
                    batch.InputRoot,
                    source,
                    compressionProfileVersion),
                cancellationToken).ConfigureAwait(false);
            return asset with
            {
                AnalysisCopyDigest = metadata.Digest,
                AnalysisCopyLength = metadata.ByteLength,
                AnalysisCopyMimeType = metadata.MimeType,
                AnalysisCopyCompressionProfile = metadata.CompressionProfileVersion,
                AnalysisCopyLeaseId = metadata.LeaseId
            };
        }
        finally
        {
            _copyPreparationGate.Release();
        }
    }


}
