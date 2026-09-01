namespace Desktop.Contracts.Ai;

/// <summary>
/// Closes result references over the request that produced them. Some
/// multimodal transports expose images to the model by stable zero-based
/// aliases (img_0, img_1, ...); those aliases are transport-only and must
/// never reach application state or user-selection persistence.
/// </summary>
public static class AiAnalysisResultReferenceNormalizer
{
    public static AiAnalysisResultV1 Normalize(
        AiAnalysisRequestV1 request,
        AiAnalysisResultV1 result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        // Typed results can come from non-HTTP providers or damaged local
        // state without first crossing the JSON Schema gate. Validate before
        // traversing nested collections so null or otherwise malformed
        // members fail closed with a stable semantic code rather than a
        // NullReferenceException.
        AiAnalysisResultSemanticValidator.Validate(result);

        var requirementIds = request.Requirements
            .Select(requirement => requirement.RequirementId)
            .ToHashSet(StringComparer.Ordinal);
        var assetAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Assets.Count; index++)
        {
            var assetId = request.Assets[index].AssetId;
            if (!assetAliases.TryAdd(assetId, assetId)
                || !assetAliases.TryAdd($"img_{index}", assetId))
            {
                throw new InvalidDataException("AI_RESULT_REFERENCE_MAP_INVALID");
            }
        }

        string AssetId(string value)
        {
            if (!assetAliases.TryGetValue(value, out var normalized))
            {
                throw new InvalidDataException("AI_RESULT_ASSET_REFERENCE_MISMATCH");
            }

            return normalized;
        }

        var matches = result.RequirementMatches.Select(match =>
        {
            if (!requirementIds.Contains(match.RequirementId))
            {
                throw new InvalidDataException("AI_RESULT_REQUIREMENT_REFERENCE_MISMATCH");
            }

            return match with { AssetId = AssetId(match.AssetId) };
        }).ToArray();
        var variants = result.VariantGroups
            .Select(group => group with { AssetIds = group.AssetIds.Select(AssetId).ToArray() })
            .ToArray();
        var geometry = result.SubjectGeometry
            .Select(item => item with { AssetId = AssetId(item.AssetId) })
            .ToArray();
        var sizeTable = result.SizeTableCandidate is null
            ? null
            : result.SizeTableCandidate with
            {
                SourceAssetId = AssetId(result.SizeTableCandidate.SourceAssetId)
            };

        var normalized = result with
        {
            RequirementMatches = matches,
            VariantGroups = variants,
            SubjectGeometry = geometry,
            SizeTableCandidate = sizeTable
        };

        AiAnalysisResultSemanticValidator.Validate(normalized);
        return normalized;
    }
}
