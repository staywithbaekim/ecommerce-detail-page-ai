using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

public enum AiVariantPlanningStatus
{
    NoVariantData = 0,
    Ready,
    ReviewRequired,
    Conflict
}

/// <summary>
/// A renderer-neutral output version.  Every frame selection in one plan is
/// tied to the same real product color group; an incomplete group is kept in
/// the result so the UI can explain which color is blocked.
/// </summary>
public sealed record AiOutputVariantPlan(
    string VariantId,
    string VariantName,
    bool IsPrimary,
    bool IsComplete,
    IReadOnlyDictionary<string, string> RequirementAssets,
    IReadOnlyList<string> MissingRequirementIds,
    IReadOnlyList<string> EvidenceCodes,
    string? ConflictCode = null,
    bool ProviderPrimarySuggestion = false)
{
    public bool CanRender => IsComplete && string.IsNullOrWhiteSpace(ConflictCode);
}

public sealed record AiVariantPlanningResult(
    AiVariantPlanningStatus Status,
    string? PrimaryVariantId,
    IReadOnlyList<AiOutputVariantPlan> Variants,
    string? ReasonCode = null)
{
    public bool HasVariants => Variants.Count > 0;
}

/// <summary>
/// Converts provider variant evidence into deterministic color versions.  The
/// planner intentionally knows nothing about concrete colors: red/blue/yellow
/// are examples only, while <c>dimension=color</c> is the contract.
/// </summary>
public static class AiVariantPlanner
{
    public const string ColorDimension = "color";
    public const string ManualSelectionConflictCode = "AI_VARIANT_MANUAL_SELECTION_CONFLICT";
    public const string MissingVariantDataCode = "AI_VARIANT_DATA_MISSING";
    public const string IncompleteVariantCode = "AI_VARIANT_INCOMPLETE";

    public static AiVariantPlanningResult Plan(
        IReadOnlyList<AiVariantGroupV1>? groups,
        IReadOnlyList<AiRequirementInputV1>? requirements,
        IReadOnlyList<AiRequirementMatchV1>? matches,
        IReadOnlyDictionary<string, string>? manualSelections = null)
    {
        var required = (requirements ?? [])
            .Where(requirement => requirement is not null && requirement.Required)
            .Select(requirement => requirement.RequirementId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !id.StartsWith("size_table_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidateMatches = matches ?? [];
        var colorGroups = (groups ?? [])
            .Where(group => group is not null && IsColorGroup(group))
            .GroupBy(group => group.VariantId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(group => group.VariantId, StringComparer.Ordinal)
            .ToArray();

        if (colorGroups.Length == 0)
        {
            return new AiVariantPlanningResult(
                AiVariantPlanningStatus.NoVariantData,
                null,
                [],
                MissingVariantDataCode);
        }

        var manual = manualSelections ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var plans = colorGroups.Select(group => BuildPlan(
                group,
                required,
                candidateMatches,
                manual))
            .ToArray();

        var primary = ChoosePrimary(plans);
        plans = plans.Select(plan => plan with { IsPrimary = plan.VariantId == primary.VariantId }).ToArray();
        var hasConflict = plans.Any(plan => plan.ConflictCode is not null);
        var hasIncomplete = plans.Any(plan => !plan.IsComplete);
        return new AiVariantPlanningResult(
            hasConflict
                ? AiVariantPlanningStatus.Conflict
                : hasIncomplete
                    ? AiVariantPlanningStatus.ReviewRequired
                    : AiVariantPlanningStatus.Ready,
            primary.VariantId,
            plans,
            hasConflict
                ? ManualSelectionConflictCode
                : hasIncomplete ? IncompleteVariantCode : null);
    }

    private static AiOutputVariantPlan BuildPlan(
        AiVariantGroupV1 group,
        IReadOnlyList<string> required,
        IReadOnlyList<AiRequirementMatchV1> matches,
        IReadOnlyDictionary<string, string> manual)
    {
        var assets = group.AssetIds.ToHashSet(StringComparer.Ordinal);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var conflict = false;

        foreach (var requirementId in required)
        {
            if (manual.TryGetValue(requirementId, out var manualAsset))
            {
                if (assets.Contains(manualAsset))
                {
                    selected[requirementId] = manualAsset;
                    continue;
                }

                // A manual selection from another color cannot silently leak
                // into this output version.
                conflict = true;
                missing.Add(requirementId);
                continue;
            }

            var match = matches
                .Where(item => string.Equals(item.RequirementId, requirementId, StringComparison.Ordinal)
                    && assets.Contains(item.AssetId))
                .OrderBy(item => item.Rank)
                .ThenByDescending(item => item.Confidence)
                .ThenBy(item => item.AssetId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (match is null)
            {
                missing.Add(requirementId);
            }
            else
            {
                selected[requirementId] = match.AssetId;
            }
        }

        foreach (var id in group.MissingRequirementIds ?? [])
        {
            if (required.Contains(id, StringComparer.Ordinal))
            {
                missing.Add(id);
            }
        }

        var complete = group.IsComplete
            ?? (missing.Count == 0 && required.All(id => selected.ContainsKey(id)));
        complete &= missing.Count == 0 && required.All(id => selected.ContainsKey(id));
        var evidence = (group.EvidenceCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AiOutputVariantPlan(
            group.VariantId,
            group.Value,
            false,
            complete,
            selected,
            missing.Order(StringComparer.Ordinal).ToArray(),
            evidence,
            conflict ? ManualSelectionConflictCode : null,
            group.IsPrimarySuggestion);
    }

    private static AiOutputVariantPlan ChoosePrimary(IReadOnlyList<AiOutputVariantPlan> plans)
    {
        var complete = plans.Where(plan => plan.IsComplete).ToArray();
        var pool = complete.Length > 0 ? complete : plans;
        return pool
            .OrderByDescending(plan => complete.Length > 0 && plan.IsComplete)
            .ThenByDescending(plan => plan.ProviderPrimarySuggestion)
            .ThenByDescending(plan => plan.EvidenceCodes.Count)
            .ThenBy(plan => plan.VariantId, StringComparer.Ordinal)
            .First();
    }

    private static bool IsColorGroup(AiVariantGroupV1 group) =>
        string.Equals(group.Dimension, ColorDimension, StringComparison.OrdinalIgnoreCase)
        || string.Equals(group.Dimension, "colour", StringComparison.OrdinalIgnoreCase)
        || string.Equals(group.Dimension, "颜色", StringComparison.Ordinal);
}
