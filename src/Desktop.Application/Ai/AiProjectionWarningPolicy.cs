using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

/// <summary>Pure warning classification for page projection.</summary>
public static class AiProjectionWarningPolicy
{
    public static bool IsRelevant(AiWarningV1 warning, bool hasMatches, bool hasMissingRequiredMatch, bool hasVariantConflict)
    {
        if (warning.Code is "low_confidence" or "size_source_conflict" || warning.Scope == "size_table") return false;
        return warning.Code switch
        {
            "asset_missing" => hasMissingRequiredMatch,
            "variant_conflict" => hasVariantConflict,
            _ => warning.Scope is "request" or "product" || hasMatches || hasMissingRequiredMatch
        };
    }
}
