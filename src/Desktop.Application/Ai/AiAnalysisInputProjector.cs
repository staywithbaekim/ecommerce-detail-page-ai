using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Contracts.Ai;
using Desktop.Application.Templates;
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
                CreateAnalysisCopyReference(asset.AssetId, options.CompressionProfileVersion)))
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
        var selectedBindings = templateRevision.ActiveFrameBindings
            .Where(binding => selectedPages.Contains(binding.PageId))
            .OrderBy(binding => binding.PageId, StringComparer.Ordinal)
            .ThenBy(binding => binding.FrameId, StringComparer.Ordinal)
            .ToArray();
        ValidateReferenceIsolation(selectedBindings, assets);

        var requirements = selectedBindings
            .Select(binding => new AiRequirementInputV1(
                binding.FrameId,
                binding.SelectionRequirement.Description,
                binding.LayoutIntent.Intent,
                true))
            .ToList();

        // A dynamic size-table page has no PSD image frame by design, but it
        // still needs an explicit source role in the AI request so the model
        // can identify a market size chart from the style's full photo set.
        foreach (var page in templateRevision.Pages
                     .Where(page => selectedPages.Contains(page.PageId)
                         && page.ContentType == TemplatePageContentType.DynamicSizeTable)
                     .OrderBy(page => page.PageId, StringComparer.Ordinal))
        {
            requirements.Add(new AiRequirementInputV1(
                SizeTableRequirementId(page.PageId),
                "清晰可读的市场尺码表图片",
                "完整表格区域可读，保留表头、尺码和数值",
                true));
        }

        if (requirements.Count == 0)
        {
            throw new InvalidOperationException("AI_INPUT_SELECTION_REQUIREMENTS_MISSING");
        }

        var requirementsDigest = ComputeRequirementsDigest(requirements, selectedBindings);

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

    public static string SizeTableRequirementId(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        return $"size_table_{pageId}";
    }

    private static string ComputeRequirementsDigest(
        IReadOnlyList<AiRequirementInputV1> requirements,
        IReadOnlyList<FrameBinding> bindings)
    {
        var bindingCriteria = bindings.ToDictionary(
            binding => binding.FrameId,
            binding => binding.SelectionRequirement.Criteria
                .OrderBy(criterion => criterion.Key, StringComparer.Ordinal)
                .Select(criterion => new { criterion.Key, criterion.Value })
                .ToArray(),
            StringComparer.Ordinal);
        var canonical = JsonSerializer.Serialize(requirements.Select(requirement => new
        {
            requirement.RequirementId,
            requirement.SelectionRequirement,
            requirement.LayoutIntent,
            requirement.Required,
            Criteria = bindingCriteria.GetValueOrDefault(requirement.RequirementId, [])
        }).ToArray());
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void ValidateReferenceIsolation(
        IReadOnlyList<FrameBinding> bindings,
        IReadOnlyList<AiAssetInputV1> assets)
    {
        var learnedBindings = bindings.Where(IsLearnedReferenceBinding).ToArray();
        if (learnedBindings.Length == 0)
        {
            return;
        }

        var referenceHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in learnedBindings)
        {
            var criteria = binding.SelectionRequirement.Criteria;
            var auditVersion = criteria.SingleOrDefault(item =>
                string.Equals(item.Key, "reference_audit_version", StringComparison.Ordinal))?.Value;
            var referenceSetDigest = criteria.SingleOrDefault(item =>
                string.Equals(item.Key, "reference_set_digest", StringComparison.Ordinal))?.Value;
            var chunks = criteria
                .Where(item => item.Key.StartsWith(
                    ReferenceLearningRuleProjector.ReferenceHashChunkPrefix,
                    StringComparison.Ordinal))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();
            if (!string.Equals(
                    auditVersion,
                    ReferenceLearningRuleProjector.ReferenceAuditVersion,
                    StringComparison.Ordinal)
                || !IsSha256Digest(referenceSetDigest)
                || chunks.Length == 0)
            {
                throw new InvalidOperationException("AI_INPUT_REFERENCE_AUDIT_MISSING");
            }

            foreach (var hash in chunks.SelectMany(chunk => chunk.Value.Split('.', StringSplitOptions.RemoveEmptyEntries)))
            {
                var normalized = hash.Trim().ToLowerInvariant();
                if (!IsSha256(normalized))
                {
                    throw new InvalidOperationException("AI_INPUT_REFERENCE_AUDIT_INVALID");
                }

                referenceHashes.Add(normalized);
            }
        }

        if (assets.Any(asset => referenceHashes.Contains(NormalizeDigest(asset.ContentHash)[7..])))
        {
            throw new InvalidOperationException("AI_INPUT_REFERENCE_ASSET_FORBIDDEN");
        }
    }

    internal static bool IsLearnedReferenceBinding(FrameBinding binding)
    {
        var criteria = binding.SelectionRequirement.Criteria;
        var source = criteria.SingleOrDefault(item =>
            string.Equals(item.Key, "semantic_source", StringComparison.Ordinal))?.Value;
        return criteria.Any(item => string.Equals(item.Key, "reference_only", StringComparison.Ordinal)
                && string.Equals(item.Value, "true", StringComparison.OrdinalIgnoreCase))
            && source is "AiV2" or "UserOverride";
    }

    private static bool IsSha256Digest(string? value) => value is not null
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && IsSha256(value[7..]);

    private static bool IsSha256(string value) => value.Length == 64
        && value.All(Uri.IsHexDigit);

    private static string NormalizeDigest(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("sha256:", StringComparison.Ordinal)
            ? normalized
            : $"sha256:{normalized}";
    }

    private static string CreateAnalysisCopyReference(string assetId, string compressionProfileVersion)
    {
        var prefix = AiAnalysisCopyProfiles.IsWicBoundedV2(compressionProfileVersion)
            ? "analysis-copy-v2-"
            : AiAnalysisCopyProfiles.IsWicBoundedV3(compressionProfileVersion)
                ? "analysis-copy-v3-"
                : AiAnalysisCopyProfiles.IsWicBoundedV4(compressionProfileVersion)
                    ? "analysis-copy-v4-"
                    : "analysis-copy-";
        var candidate = prefix + assetId;
        if (candidate.Length <= 128)
        {
            return candidate;
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assetId)))
            .ToLowerInvariant();
        return prefix + digest;
    }
}
