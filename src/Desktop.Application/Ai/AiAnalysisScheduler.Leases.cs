using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

public sealed partial class AiAnalysisScheduler
{

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
            if (ledger is null || !ShouldRetryTerminalLeaseCleanup(request, ledger.Status))
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
        string status)
    {
        if (status is not (AiLedgerStatusPolicy.Validated or AiLedgerStatusPolicy.ManualReview or AiLedgerStatusPolicy.PlanClosed
            or AiLedgerStatusPolicy.AiResultUnknown or AiLedgerStatusPolicy.Cancelled or AiLedgerStatusPolicy.TerminalError))
        {
            return false;
        }

        if (request.AnalysisKind != AiAnalysisKind.ProductJointUnderstanding
            || status is AiLedgerStatusPolicy.PlanClosed or AiLedgerStatusPolicy.AiResultUnknown or AiLedgerStatusPolicy.Cancelled or AiLedgerStatusPolicy.TerminalError)
        {
            return true;
        }

        // Validated/ManualReview first-layer results still back the candidate
        // replacement UI, even if refinement children already exist. Only an
        // explicit PlanClosed or terminal status can release the complete
        // candidate-pool lease.
        return false;
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


}
