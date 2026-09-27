namespace Desktop.Contracts.Ai;

/// <summary>
/// Validates cross-field invariants that cannot be expressed by the v1 JSON
/// Schema alone.  This validator is deliberately supplier-neutral and throws
/// stable, non-sensitive codes so every result ingestion path can fail closed
/// in the same way.
/// </summary>
public static class AiAnalysisResultSemanticValidator
{
    public const string StructureInvalidCode = "AI_RESULT_STRUCTURE_INVALID";
    public const string RequirementRankDuplicateCode = "AI_RESULT_REQUIREMENT_RANK_DUPLICATE";
    public const string RequirementAssetDuplicateCode = "AI_RESULT_REQUIREMENT_ASSET_DUPLICATE";
    public const string GeometryInvalidCode = "AI_RESULT_GEOMETRY_INVALID";
    public const string SizeTableShapeInvalidCode = "AI_RESULT_SIZE_TABLE_SHAPE_INVALID";

    /// <summary>
    /// Validates a typed result after transport aliases have been closed over
    /// the producing request.  No business/product rule is inferred here;
    /// these checks only protect the structural contract consumed downstream.
    /// </summary>
    public static void Validate(AiAnalysisResultV1 result)
    {
        if (result is null
            || !string.Equals(result.SchemaVersion, AiAnalysisProtocol.ResultSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(result.DocumentKind, "result", StringComparison.Ordinal)
            || !IsId(result.RequestId)
            || !IsOptionalProviderReference(result.ProviderRequestReference)
            || !IsVersion(result.ModelConfigVersion)
            || result.RequirementMatches is null
            || result.RequirementMatches.Count > 40_000
            || result.VariantGroups is null
            || result.VariantGroups.Count > 200
            || result.SubjectGeometry is null
            || result.SubjectGeometry.Count > 20_000
            || result.Warnings is null
            || result.Warnings.Count > 200
            || result.Usage is null)
        {
            throw Invalid(StructureInvalidCode);
        }

        var ranks = new HashSet<(string RequirementId, int Rank)>();
        var requirementAssets = new HashSet<(string RequirementId, string AssetId)>();
        foreach (var match in result.RequirementMatches)
        {
            if (match is null
                || !IsId(match.RequirementId)
                || !IsId(match.AssetId)
                || match.Rank is < 1 or > 20_000
                || match.EvidenceCodes is null
                || match.EvidenceCodes.Count is < 1 or > 32
                || !AreUniqueIds(match.EvidenceCodes)
                || !IsUnitInterval(match.Confidence))
            {
                throw Invalid(StructureInvalidCode);
            }

            if (!ranks.Add((match.RequirementId, match.Rank)))
            {
                throw Invalid(RequirementRankDuplicateCode);
            }

            if (!requirementAssets.Add((match.RequirementId, match.AssetId)))
            {
                throw Invalid(RequirementAssetDuplicateCode);
            }
        }

        foreach (var group in result.VariantGroups)
        {
            if (group is null
                || !IsId(group.VariantId)
                || !HasLength(group.Dimension, 1, 200)
                || !HasLength(group.Value, 1, 200)
                || group.AssetIds is null
                || group.AssetIds.Count is < 1 or > 20_000
                || !AreUniqueIds(group.AssetIds)
                || group.CoverageRequirementIds is not null
                    && (group.CoverageRequirementIds.Count > 20_000
                        || !AreUniqueIds(group.CoverageRequirementIds))
                || group.MissingRequirementIds is not null
                    && (group.MissingRequirementIds.Count > 20_000
                        || !AreUniqueIds(group.MissingRequirementIds))
                || group.EvidenceCodes is not null
                    && (group.EvidenceCodes.Count is < 1 or > 32
                        || !AreUniqueIds(group.EvidenceCodes))
                || group.VariantRole is not null
                    && !HasLength(group.VariantRole, 1, 64)
                || group.PrimarySuggestionEvidence is not null
                    && !HasLength(group.PrimarySuggestionEvidence, 1, 500))
            {
                throw Invalid(StructureInvalidCode);
            }
        }

        foreach (var geometry in result.SubjectGeometry)
        {
            if (geometry is null
                || !IsId(geometry.AssetId)
                || !IsUnitInterval(geometry.Left)
                || !IsUnitInterval(geometry.Top)
                || !IsUnitInterval(geometry.Right)
                || !IsUnitInterval(geometry.Bottom)
                || !IsUnitInterval(geometry.Confidence)
                || geometry.Left >= geometry.Right
                || geometry.Top >= geometry.Bottom)
            {
                throw Invalid(GeometryInvalidCode);
            }
        }

        foreach (var warning in result.Warnings)
        {
            if (warning is null
                || !IsId(warning.Code)
                || !IsWarningScope(warning.Scope)
                || !HasLength(warning.MessageSummary, 1, 500))
            {
                throw Invalid(StructureInvalidCode);
            }
        }

        var usage = result.Usage;
        if (!IsId(usage.UsageEventId)
            || usage.InputUnits < 0
            || usage.OutputUnits < 0
            || usage.RequestCount < 1
            || usage.ProviderCostAmount < 0m
            || !IsOptionalCurrency(usage.ProviderCostCurrency))
        {
            throw Invalid(StructureInvalidCode);
        }

        var sizeTable = result.SizeTableCandidate;
        if (sizeTable is null)
        {
            return;
        }

        if (!IsId(sizeTable.SourceAssetId)
            || sizeTable.Columns is null
            || sizeTable.Rows is null
            || sizeTable.Columns.Count is < 1 or > 50
            || sizeTable.Rows.Count is < 1 or > 200
            || sizeTable.Columns.Any(column => !HasLength(column, 1, 200))
            || !IsUnitInterval(sizeTable.Confidence))
        {
            throw Invalid(SizeTableShapeInvalidCode);
        }

        var columnCount = sizeTable.Columns.Count;
        foreach (var row in sizeTable.Rows)
        {
            if (row is null
                || row.Count != columnCount
                || row.Any(cell => cell is null || cell.Length > 500))
            {
                throw Invalid(SizeTableShapeInvalidCode);
            }
        }
    }

    public static bool IsSemanticError(InvalidDataException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsSemanticErrorCode(exception.Message);
    }

    public static bool IsSemanticErrorCode(string? code) => code is
        StructureInvalidCode
        or RequirementRankDuplicateCode
        or RequirementAssetDuplicateCode
        or GeometryInvalidCode
        or SizeTableShapeInvalidCode;

    private static InvalidDataException Invalid(string code) => new(code);

    private static bool IsUnitInterval(decimal value) => value is >= 0m and <= 1m;

    private static bool AreUniqueIds(IReadOnlyList<string> values)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsId(value) || !unique.Add(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsId(string? value)
    {
        if (!HasLength(value, 1, 128))
        {
            return false;
        }

        return value!.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsVersion(string? value)
    {
        if (!HasLength(value, 1, 128))
        {
            return false;
        }

        return value!.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/');
    }

    private static bool IsOptionalProviderReference(string? value) => value is null
        || (value.Length <= 256
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':'));

    private static bool IsOptionalCurrency(string? value) => value is null
        || (value.Length == 3 && value.All(character => character is >= 'A' and <= 'Z'));

    private static bool IsWarningScope(string? value) => value is
        "request" or "product" or "requirement" or "asset" or "size_table";

    private static bool HasLength(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum;
}
