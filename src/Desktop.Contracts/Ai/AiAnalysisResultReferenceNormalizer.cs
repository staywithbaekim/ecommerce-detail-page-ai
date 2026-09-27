namespace Desktop.Contracts.Ai;

/// <summary>
/// Closes result references over the request that produced them. Some
/// multimodal transports expose request-owned references to the model by
/// short stable aliases. Those aliases are transport-only and must never
/// reach application state or user-selection persistence.
/// </summary>
public static class AiAnalysisResultReferenceNormalizer
{
    public const string RequestAlias = "request_0";
    public const string ModelConfigVersionAlias = "model_0";

    public static string RequirementAlias(int index) => $"req_{index}";

    public static string AssetAlias(int index) => $"img_{index}";

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

        var requestAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        AddReference(requestAliases, request.RequestId, request.RequestId);
        AddReference(requestAliases, RequestAlias, request.RequestId);

        var modelAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        AddReference(modelAliases, request.ModelConfigVersion, request.ModelConfigVersion);
        AddReference(modelAliases, ModelConfigVersionAlias, request.ModelConfigVersion);

        var requirementAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Requirements.Count; index++)
        {
            var requirementId = request.Requirements[index].RequirementId;
            AddReference(requirementAliases, requirementId, requirementId);
            AddReference(requirementAliases, RequirementAlias(index), requirementId);
        }

        var assetAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Assets.Count; index++)
        {
            var assetId = request.Assets[index].AssetId;
            AddReference(assetAliases, assetId, assetId);
            AddReference(assetAliases, AssetAlias(index), assetId);
        }

        static string ResolveReference(
            IReadOnlyDictionary<string, string> aliases,
            string value,
            string mismatchCode)
        {
            if (!aliases.TryGetValue(value, out var normalized))
            {
                throw new InvalidDataException(mismatchCode);
            }

            return normalized;
        }

        var matches = result.RequirementMatches.Select(match =>
        {
            return match with
            {
                RequirementId = ResolveReference(
                    requirementAliases,
                    match.RequirementId,
                    "AI_RESULT_REQUIREMENT_REFERENCE_MISMATCH"),
                AssetId = ResolveReference(
                    assetAliases,
                    match.AssetId,
                    "AI_RESULT_ASSET_REFERENCE_MISMATCH")
            };
        }).ToArray();
        var variants = result.VariantGroups
            .Select(group => group with
            {
                AssetIds = group.AssetIds.Select(value => ResolveReference(
                    assetAliases,
                    value,
                    "AI_RESULT_ASSET_REFERENCE_MISMATCH")).ToArray(),
                CoverageRequirementIds = group.CoverageRequirementIds?.Select(value => ResolveReference(
                    requirementAliases,
                    value,
                    "AI_RESULT_REQUIREMENT_REFERENCE_MISMATCH")).ToArray(),
                MissingRequirementIds = group.MissingRequirementIds?.Select(value => ResolveReference(
                    requirementAliases,
                    value,
                    "AI_RESULT_REQUIREMENT_REFERENCE_MISMATCH")).ToArray()
            })
            .ToArray();
        var geometry = result.SubjectGeometry
            .Select(item => item with
            {
                AssetId = ResolveReference(
                    assetAliases,
                    item.AssetId,
                    "AI_RESULT_ASSET_REFERENCE_MISMATCH")
            })
            .ToArray();
        var sizeTable = result.SizeTableCandidate is null
            ? null
            : result.SizeTableCandidate with
            {
                SourceAssetId = ResolveReference(
                    assetAliases,
                    result.SizeTableCandidate.SourceAssetId,
                    "AI_RESULT_ASSET_REFERENCE_MISMATCH")
            };

        var normalized = result with
        {
            RequestId = ResolveReference(
                requestAliases,
                result.RequestId,
                "AI_RESULT_REQUEST_REFERENCE_MISMATCH"),
            ModelConfigVersion = ResolveReference(
                modelAliases,
                result.ModelConfigVersion,
                "AI_RESULT_MODEL_REFERENCE_MISMATCH"),
            RequirementMatches = matches,
            VariantGroups = variants,
            SubjectGeometry = geometry,
            SizeTableCandidate = sizeTable
        };

        AiAnalysisResultSemanticValidator.Validate(normalized);
        return normalized;
    }

    private static void AddReference(
        Dictionary<string, string> aliases,
        string alias,
        string normalized)
    {
        if (aliases.TryGetValue(alias, out var existing))
        {
            if (!string.Equals(existing, normalized, StringComparison.Ordinal))
            {
                throw new InvalidDataException("AI_RESULT_REFERENCE_MAP_INVALID");
            }

            return;
        }

        aliases.Add(alias, normalized);
    }
}
