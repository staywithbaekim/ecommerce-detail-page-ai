using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;

namespace Desktop.Application.Ai;

public interface IAiAnalysisResultReader
{
    Task<AiStyleAnalysisResultInput[]> LoadStyleResultsAsync(
        IEnumerable<AiAnalysisRequestV1> requests,
        string styleId,
        CancellationToken cancellationToken);

    Task<AiAnalysisSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken);
}

public enum AiAnalysisResultReadFailure
{
    None = 0,
    Missing,
    Unreadable,
    InvalidReference
}

public sealed record AiStyleAnalysisResultInput(
    AiAnalysisRequestV1 Request,
    AiRequestLedgerEntry? Ledger,
    AiAnalysisResultV1? Result,
    AiAnalysisResultReadFailure Failure);

/// <summary>Shared durable-result validation for projections and rendering preparation.</summary>
public sealed class AiAnalysisResultReader : IAiAnalysisResultReader
{
    private readonly IAiAnalysisStore _store;
    private readonly IAiAnalysisResultStore _results;

    public AiAnalysisResultReader(IAiAnalysisStore store, IAiAnalysisResultStore results)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _results = results ?? throw new ArgumentNullException(nameof(results));
    }

    public Task<AiAnalysisSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _store.LoadSessionAsync(sessionId, cancellationToken);

    public async Task<AiStyleAnalysisResultInput[]> LoadStyleResultsAsync(
        IEnumerable<AiAnalysisRequestV1> requests,
        string styleId,
        CancellationToken cancellationToken)
    {
        var inputs = new List<AiStyleAnalysisResultInput>();
        var styleRequests = requests
            .Where(request => string.Equals(request.StyleId, styleId, StringComparison.Ordinal))
            .OrderBy(request => request.RequestId, StringComparer.Ordinal)
            .ToArray();
        var firstLayerRequest = styleRequests.FirstOrDefault(request =>
            string.Equals(
                request.AnalysisKind,
                AiAnalysisKind.ProductJointUnderstanding,
                StringComparison.Ordinal));
        foreach (var request in styleRequests)
        {
            var expectedFingerprint = AiAnalysisCacheKeyFactory.Create(request);
            var ledger = await _store.FindRequestByFingerprintAsync(
                expectedFingerprint,
                cancellationToken).ConfigureAwait(false);
            if (ledger is not null
                && (!string.Equals(ledger.RequestId, request.RequestId, StringComparison.Ordinal)
                    || !string.Equals(ledger.Fingerprint, expectedFingerprint, StringComparison.Ordinal)
                    || !string.Equals(ledger.BatchId, request.BatchId, StringComparison.Ordinal)
                    || !string.Equals(ledger.StyleId, request.StyleId, StringComparison.Ordinal)))
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.InvalidReference));
                continue;
            }

            if (request.AnalysisKind is AiAnalysisKind.SelectedAssetGeometry
                    or AiAnalysisKind.SizeTableExtraction
                && (firstLayerRequest is null
                    || !IsRefinementClosedOverParent(firstLayerRequest, request)))
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.InvalidReference));
                continue;
            }

            if (ledger is null
                || !AiLedgerStatusPolicy.IsTerminal(ledger.Status) || ledger.Status is AiLedgerStatusPolicy.TerminalError or AiLedgerStatusPolicy.AiResultUnknown or AiLedgerStatusPolicy.Cancelled)
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.None));
                continue;
            }

            if (ledger.ResultReference is null)
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.Missing));
                continue;
            }

            AiAnalysisResultV1? result;
            try
            {
                // Successful ledgers can outlive a removed, malformed, locked,
                // or ACL-inaccessible result file. Keep those local storage
                // faults as data for the page projection; cancellation must
                // still propagate to the caller.
                result = await _results.LoadAsync(
                    ledger.ResultReference,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                InvalidDataException or IOException or UnauthorizedAccessException)
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.Unreadable));
                continue;
            }

            if (result is null)
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.Missing));
                continue;
            }

            if (!string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                || !string.Equals(
                    result.ModelConfigVersion,
                    request.ModelConfigVersion,
                    StringComparison.Ordinal))
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.InvalidReference));
                continue;
            }

            try
            {
                // Compatibility repair for already measured v1 results that
                // predate transport-alias closure. This is in-memory only: it
                // never rewrites the durable result or resends the request.
                var normalized = AiAnalysisResultReferenceNormalizer.Normalize(request, result);
                inputs.Add(new(request, ledger, normalized, AiAnalysisResultReadFailure.None));
            }
            catch (InvalidDataException exception) when (
                AiAnalysisResultSemanticValidator.IsSemanticError(exception))
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.Unreadable));
            }
            catch (InvalidDataException)
            {
                inputs.Add(new(request, ledger, null, AiAnalysisResultReadFailure.InvalidReference));
            }
        }

        return inputs.ToArray();
    }

    private static bool IsRefinementClosedOverParent(
        AiAnalysisRequestV1 parent,
        AiAnalysisRequestV1 child)
    {
        var refinementKind = child.AnalysisKind switch
        {
            AiAnalysisKind.SelectedAssetGeometry => "geometry",
            AiAnalysisKind.SizeTableExtraction => "size",
            _ => null
        };
        if (refinementKind is null
            || child.Assets.Count == 0
            || !string.Equals(
                child.RequestId,
                ExpectedRefinementRequestId(parent.RequestId, refinementKind, child.Assets.Select(asset => asset.AssetId)),
                StringComparison.Ordinal)
            || !string.Equals(child.SchemaVersion, parent.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(child.DocumentKind, parent.DocumentKind, StringComparison.Ordinal)
            || !string.Equals(child.CorrelationId, parent.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(child.ProductId, parent.ProductId, StringComparison.Ordinal)
            || !string.Equals(child.BatchId, parent.BatchId, StringComparison.Ordinal)
            || !string.Equals(child.StyleId, parent.StyleId, StringComparison.Ordinal)
            || !string.Equals(child.TemplateSnapshotId, parent.TemplateSnapshotId, StringComparison.Ordinal)
            || !string.Equals(child.TemplateRevisionId, parent.TemplateRevisionId, StringComparison.Ordinal)
            || !string.Equals(child.ModelConfigVersion, parent.ModelConfigVersion, StringComparison.Ordinal)
            || !string.Equals(child.PromptContractVersion, parent.PromptContractVersion, StringComparison.Ordinal)
            || !string.Equals(child.ResponseSchemaVersion, parent.ResponseSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(child.CompressionProfileVersion, parent.CompressionProfileVersion, StringComparison.Ordinal)
            || !string.Equals(child.ConsentId, parent.ConsentId, StringComparison.Ordinal)
            || !string.Equals(child.RequirementsDigest, parent.RequirementsDigest, StringComparison.Ordinal)
            || !child.Requirements.SequenceEqual(parent.Requirements))
        {
            return false;
        }

        var parentAssets = parent.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        return child.Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count()
                == child.Assets.Count
            && child.Assets.All(asset => parentAssets.TryGetValue(asset.AssetId, out var expected)
                && asset == expected);
    }

    private static string ExpectedRefinementRequestId(
        string parentRequestId,
        string kind,
        IEnumerable<string> assetIds)
    {
        var assetSet = string.Join("\n", assetIds.Order(StringComparer.Ordinal));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{parentRequestId}|{kind}|{assetSet}")))
            .ToLowerInvariant()[..16];
        var tail = $"-{kind}-{digest}";
        var prefixLength = Math.Min(parentRequestId.Length, 128 - tail.Length);
        return parentRequestId[..prefixLength] + tail;
    }
}
