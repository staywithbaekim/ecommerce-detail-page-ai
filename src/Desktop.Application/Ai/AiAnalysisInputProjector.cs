using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Ai;

public sealed record AiAnalysisProjectionOptions(
    string CorrelationId,
    string ConsentId,
    string ModelConfigVersion,
    string PromptContractVersion,
    string CompressionProfileVersion,
    string AnalysisKind = AiAnalysisKind.ProductJointUnderstanding);

public static class AiAnalysisInputProjector
{
    public static AiAnalysisRequestV1 Project(
        BatchDraft batch,
        TemplateRevision templateRevision,
        string styleId,
        AiAnalysisProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(templateRevision);
        ArgumentNullException.ThrowIfNull(options);

        if (batch.ScanStatus != BatchScanStatus.ScanReady)
        {
            throw new InvalidOperationException("AI_INPUT_BATCH_NOT_READY");
        }

        if (!string.Equals(
                batch.ManifestReference,
                $"batches/{batch.BatchId}/asset-manifest/manifest.json",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_INPUT_MANIFEST_REFERENCE_INVALID");
        }

        _ = NormalizeDigest(batch.ManifestDigest
            ?? throw new InvalidOperationException("AI_INPUT_MANIFEST_DIGEST_MISSING"));

        if (templateRevision.State != TemplateRevisionState.Committed
            || templateRevision.RuleDigest is null
            || !string.Equals(
                batch.TemplateSnapshot.RulesDigest,
                templateRevision.RuleDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_INPUT_TEMPLATE_RULE_DIGEST_MISMATCH");
        }

        if (!string.Equals(batch.TemplateSnapshot.RevisionId, templateRevision.RevisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_INPUT_TEMPLATE_SNAPSHOT_MISMATCH");
        }

        if (!AiAnalysisKind.IsSupported(options.AnalysisKind))
        {
            throw new InvalidOperationException("AI_ANALYSIS_KIND_UNSUPPORTED");
        }

        var style = batch.Styles.SingleOrDefault(item => string.Equals(item.StyleId, styleId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("AI_INPUT_STYLE_NOT_FOUND");
        var assetIds = style.AssetIds.ToHashSet(StringComparer.Ordinal);
        var assets = batch.Assets
            .Where(asset => assetIds.Contains(asset.AssetId))
            .OrderBy(asset => asset.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(asset => new AiAssetInputV1(
                asset.AssetId,
                NormalizeDigest(asset.Sha256),
                asset.MimeType,
                asset.PixelWidth,
                asset.PixelHeight,
                $"analysis-copy-{asset.AssetId}"))
            .ToArray();

        if (assets.Length != assetIds.Count || assets.Any(asset => !assetIds.Contains(asset.AssetId)))
        {
            throw new InvalidOperationException("AI_INPUT_ASSET_MANIFEST_INCOMPLETE");
        }

        if (batch.Assets.Any(asset => assetIds.Contains(asset.AssetId)
            && (!string.Equals(asset.StyleId, style.StyleId, StringComparison.Ordinal)
                || asset.Readability != AssetReadability.Readable)))
        {
            throw new InvalidOperationException("AI_INPUT_ASSET_NOT_READABLE");
        }

        var selectedPages = batch.TemplateSnapshot.Pages
            .Select(page => page.PageId)
            .ToHashSet(StringComparer.Ordinal);
        var requirements = templateRevision.FrameBindings
            .Where(binding => selectedPages.Contains(binding.PageId))
            .OrderBy(binding => binding.PageId, StringComparer.Ordinal)
            .ThenBy(binding => binding.FrameId, StringComparer.Ordinal)
            .Select(binding => new AiRequirementInputV1(
                binding.FrameId,
                binding.SelectionRequirement.Description,
                binding.LayoutIntent.Intent,
                true))
            .ToArray();

        if (requirements.Length == 0)
        {
            throw new InvalidOperationException("AI_INPUT_SELECTION_REQUIREMENTS_MISSING");
        }

        var requirementsDigest = ComputeRequirementsDigest(requirements);

        return new AiAnalysisRequestV1(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            $"ai-{batch.BatchId}-{style.StyleId}",
            options.CorrelationId,
            style.StyleId,
            batch.BatchId,
            style.StyleId,
            batch.TemplateSnapshot.SnapshotId,
            templateRevision.RevisionId,
            options.AnalysisKind,
            options.ModelConfigVersion,
            options.PromptContractVersion,
            AiAnalysisProtocol.ResultSchemaVersion,
            options.CompressionProfileVersion,
            options.ConsentId,
            requirementsDigest,
            assets,
            requirements);
    }

    private static string ComputeRequirementsDigest(IReadOnlyList<AiRequirementInputV1> requirements)
    {
        var canonical = JsonSerializer.Serialize(requirements.Select(requirement => new
        {
            requirement.RequirementId,
            requirement.SelectionRequirement,
            requirement.LayoutIntent,
            requirement.Required
        }).ToArray());
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string NormalizeDigest(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("sha256:", StringComparison.Ordinal)
            ? normalized
            : $"sha256:{normalized}";
    }
}
