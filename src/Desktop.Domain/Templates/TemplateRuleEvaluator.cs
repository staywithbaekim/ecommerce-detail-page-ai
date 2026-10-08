using System.Collections.Immutable;

namespace Desktop.Domain.Templates;

/// <summary>
/// Pure bounded rule evaluation. No AI, image matching, IO, random identities or renderer.
/// Invalid rules never produce a partially executable instance list.
/// </summary>
public static class TemplateRuleEvaluator
{
    // Safety admission, not a claim that this many pages have passed UI/render performance tests.
    public const int MaximumInstances = 10_000;

    public static ImmutableArray<TemplateRuleDiagnostic> Validate(TemplateRuleSet rules, TemplateRevision revision)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(revision);
        var diagnostics = ImmutableArray.CreateBuilder<TemplateRuleDiagnostic>();
        if (rules.SchemaVersion != TemplateRuleSet.CurrentSchemaVersion)
            return [new("TPL_RULE_SCHEMA_UNSUPPORTED")];
        if (rules.Branches.IsDefaultOrEmpty)
            return [new("TPL_RULE_BRANCHES_INVALID")];

        var branches = new HashSet<string>(StringComparer.Ordinal);
        var conditions = new List<TemplateRuleBranch>();
        foreach (var branch in rules.Branches)
        {
            if (branch is null)
            {
                diagnostics.Add(new("TPL_RULE_BRANCHES_INVALID"));
                continue;
            }
            if (!ValidId(branch.BranchId) || !branches.Add(branch.BranchId))
                diagnostics.Add(new("TPL_RULE_BRANCH_ID_INVALID", branch.BranchId));
            if (!ValidCondition(branch.Condition))
                diagnostics.Add(new("TPL_RULE_CONDITION_INVALID", branch.BranchId));
            else
            {
                if (conditions.Any(other => ConditionsOverlap(other.Condition, branch.Condition)))
                    diagnostics.Add(new("TPL_RULE_ROUTE_AMBIGUOUS", branch.BranchId));
                conditions.Add(branch);
            }
            if (branch.Recipes.IsDefaultOrEmpty)
            {
                diagnostics.Add(new("TPL_RULE_RECIPES_INVALID", branch.BranchId));
                continue;
            }
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in branch.Recipes)
            {
                if (recipe is null)
                {
                    diagnostics.Add(new("TPL_RULE_RECIPES_INVALID", branch.BranchId));
                    continue;
                }
                if (!ValidId(recipe.RecipeId) || !recipes.Add(recipe.RecipeId))
                    Add("TPL_RULE_RECIPE_ID_INVALID");
                ValidateRecipe(branch.BranchId, recipe, revision, diagnostics);

                void Add(string code) => diagnostics.Add(new(code, branch.BranchId, recipe.RecipeId, recipe.PageId));
            }
        }
        return diagnostics.ToImmutable();
    }

    public static TemplateRuleEvaluation Evaluate(
        TemplateRuleSet rules, TemplateRevision revision, TemplateRuleColorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var validation = Validate(rules, revision);
        if (!validation.IsEmpty)
            return new([], validation);
        if (!facts.IsGroupingResolved)
            return Blocked("TPL_RULE_COLOR_GROUPING_UNRESOLVED");
        if (facts.OrderedVariantIds.IsDefault
            || facts.OrderedVariantIds.Any(id => !ValidId(id))
            || facts.OrderedVariantIds.Distinct(StringComparer.Ordinal).Count() != facts.OrderedVariantIds.Length)
            return Blocked("TPL_RULE_COLOR_IDS_INVALID");
        if (facts.OrderedVariantIds.IsEmpty ? facts.PrimaryVariantId is not null
            : facts.PrimaryVariantId is null || !facts.OrderedVariantIds.Contains(facts.PrimaryVariantId, StringComparer.Ordinal))
            return Blocked("TPL_RULE_PRIMARY_INVALID");

        var matching = rules.Branches.Where(branch => branch.Condition.Matches(facts.OrderedVariantIds.Length)).ToArray();
        if (matching.Length == 0)
            return Blocked(facts.OrderedVariantIds.IsEmpty ? "TPL_RULE_COLOR_DATA_MISSING" : "TPL_RULE_ROUTE_UNCOVERED");
        if (matching.Length != 1)
            return Blocked("TPL_RULE_ROUTE_AMBIGUOUS");
        var selected = matching[0];
        if (facts.OrderedVariantIds.IsEmpty && selected.Recipes.Any(recipe => !recipe.ColorSlots.IsEmpty))
            return Blocked("TPL_RULE_COLOR_DATA_MISSING", selected.BranchId);

        var secondary = facts.OrderedVariantIds.Where(id => id != facts.PrimaryVariantId).ToImmutableArray();
        ImmutableArray<string> ordered = facts.PrimaryVariantId is { } primary ? [primary, .. secondary] : [];
        var instances = ImmutableArray.CreateBuilder<TemplateRulePageInstance>();
        var diagnostics = ImmutableArray.CreateBuilder<TemplateRuleDiagnostic>();
        foreach (var recipe in selected.Recipes)
            Expand(selected.BranchId, recipe, facts.PrimaryVariantId, ordered, secondary, instances, diagnostics);

        if (diagnostics.Count == 0 && selected.RequireCompleteColorCoverage)
        {
            var used = instances.SelectMany(instance => instance.Colors).Select(slot => slot.VariantId).ToArray();
            if (used.Length != ordered.Length || used.Distinct(StringComparer.Ordinal).Count() != used.Length
                || !used.ToHashSet(StringComparer.Ordinal).SetEquals(ordered))
                diagnostics.Add(new("TPL_RULE_COLOR_COVERAGE_INVALID", selected.BranchId));
        }
        return diagnostics.Count == 0 ? new(instances.ToImmutable(), []) : new([], diagnostics.ToImmutable());
    }

    private static void ValidateRecipe(string branchId, TemplatePageRecipe recipe, TemplateRevision revision,
        ImmutableArray<TemplateRuleDiagnostic>.Builder diagnostics)
    {
        void Add(string code, string? frame = null) =>
            diagnostics.Add(new(code, branchId, recipe.RecipeId, recipe.PageId, frame));

        var page = revision.Pages.SingleOrDefault(page => page.PageId == recipe.PageId);
        if (page is null || !page.Enabled || !Enum.IsDefined(page.ContentType))
            Add("TPL_RULE_PAGE_REFERENCE_INVALID");
        if (!Enum.IsDefined(recipe.Repeat) || !Enum.IsDefined(recipe.ColorRange) || recipe.ExpectedInstanceCount < 0)
            Add("TPL_RULE_RECIPE_INVALID");
        if (recipe.ColorSlots.IsDefault || recipe.FrameBindings.IsDefault)
        {
            Add("TPL_RULE_BINDINGS_INVALID");
            return;
        }
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in recipe.ColorSlots)
        {
            if (slot is null || !ValidId(slot.SlotId) || !slots.Add(slot.SlotId) || !Enum.IsDefined(slot.Source))
                Add("TPL_RULE_COLOR_SLOT_INVALID");
        }
        var allocatedCount = recipe.ColorSlots.Count(slot => slot?.Source == TemplateRuleColorSource.Allocated);
        if (recipe.Repeat == TemplateRuleRepeat.Once
                && (recipe.ColorRange != TemplateRuleColorRange.None || allocatedCount != 0)
            || recipe.Repeat == TemplateRuleRepeat.ColorChunks
                && (recipe.ColorRange == TemplateRuleColorRange.None || allocatedCount == 0))
            Add("TPL_RULE_COLOR_ALLOCATION_INVALID");

        var registered = revision.FrameBindings.Where(frame => frame.PageId == recipe.PageId)
            .Select(frame => frame.FrameId).ToHashSet(StringComparer.Ordinal);
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var usedSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in recipe.FrameBindings)
        {
            if (binding is null)
            {
                Add("TPL_RULE_BINDINGS_INVALID");
                continue;
            }
            if (!registered.Contains(binding.FrameId))
                Add("TPL_RULE_FRAME_REFERENCE_INVALID", binding.FrameId);
            if (!bound.Add(binding.FrameId))
                Add("TPL_RULE_FRAME_BINDING_DUPLICATE", binding.FrameId);
            if (!slots.Contains(binding.SlotId))
                Add("TPL_RULE_COLOR_SLOT_REFERENCE_INVALID", binding.FrameId);
            usedSlots.Add(binding.SlotId);
        }
        if (!slots.SetEquals(usedSlots))
            Add("TPL_RULE_COLOR_SLOT_UNUSED");
        if (page?.ContentType == TemplatePageContentType.NormalComposition
            && (registered.Count == 0 || !registered.SetEquals(bound)))
            Add("TPL_RULE_FRAME_BINDINGS_INCOMPLETE");
    }

    private static void Expand(string branchId, TemplatePageRecipe recipe, string? primary,
        ImmutableArray<string> ordered, ImmutableArray<string> secondary,
        ImmutableArray<TemplateRulePageInstance>.Builder instances,
        ImmutableArray<TemplateRuleDiagnostic>.Builder diagnostics)
    {
        void Add(string code) => diagnostics.Add(new(code, branchId, recipe.RecipeId, recipe.PageId));

        var allocatedCount = recipe.ColorSlots.Count(slot => slot.Source == TemplateRuleColorSource.Allocated);
        var range = recipe.ColorRange == TemplateRuleColorRange.Secondary ? secondary : ordered;
        var count = 1;
        if (recipe.Repeat == TemplateRuleRepeat.ColorChunks)
        {
            if (range.Length % allocatedCount != 0)
            {
                Add("TPL_RULE_COLOR_CHUNK_INCOMPLETE");
                return;
            }
            count = range.Length / allocatedCount;
        }
        if (recipe.ExpectedInstanceCount is { } expected && expected != count)
        {
            Add("TPL_RULE_INSTANCE_COUNT_CONFLICT");
            return;
        }
        if (count > MaximumInstances - instances.Count)
        {
            Add("TPL_RULE_INSTANCE_LIMIT_EXCEEDED");
            return;
        }
        if (count == 0)
            return;
        if (recipe.ColorSlots.Any(slot => slot.Source == TemplateRuleColorSource.SingleSecondary) && secondary.Length != 1)
        {
            Add(secondary.IsEmpty ? "TPL_RULE_SECONDARY_MISSING" : "TPL_RULE_SECONDARY_AMBIGUOUS");
            return;
        }
        for (var repeat = 0; repeat < count; repeat++)
        {
            var colors = ImmutableArray.CreateBuilder<TemplateRuleColorAssignment>();
            var allocation = repeat * allocatedCount;
            foreach (var slot in recipe.ColorSlots)
            {
                var variant = slot.Source switch
                {
                    TemplateRuleColorSource.Primary => primary!,
                    TemplateRuleColorSource.SingleSecondary => secondary[0],
                    TemplateRuleColorSource.Allocated => range[allocation++],
                    _ => throw new InvalidOperationException("Rule sources must be validated before expansion.")
                };
                colors.Add(new(slot.SlotId, variant));
            }
            var bySlot = colors.ToDictionary(slot => slot.SlotId, slot => slot.VariantId, StringComparer.Ordinal);
            instances.Add(new(new(branchId, recipe.RecipeId, repeat), recipe.PageId, colors.ToImmutable(),
                recipe.FrameBindings.Select(binding => new TemplateRuleFrameAssignment(
                    binding.FrameId, binding.SlotId, bySlot[binding.SlotId])).ToImmutableArray()));
        }
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool ValidCondition(TemplateRuleColorCount? condition) => condition is not null
        && condition.Minimum >= 0 && (condition.Maximum is null || condition.Maximum >= condition.Minimum)
        && Enum.IsDefined(condition.Parity) && ConditionsOverlap(condition, condition);

    private static bool ConditionsOverlap(TemplateRuleColorCount left, TemplateRuleColorCount right)
    {
        if (left.Parity != TemplateRuleColorParity.Any && right.Parity != TemplateRuleColorParity.Any
            && left.Parity != right.Parity)
            return false;
        long minimum = Math.Max(left.Minimum, right.Minimum);
        var maximum = Math.Min(left.Maximum ?? int.MaxValue, right.Maximum ?? int.MaxValue);
        var parity = left.Parity == TemplateRuleColorParity.Any ? right.Parity : left.Parity;
        if (parity != TemplateRuleColorParity.Any && minimum % 2 != (parity == TemplateRuleColorParity.Odd ? 1 : 0))
            minimum++;
        return minimum <= maximum;
    }

    private static TemplateRuleEvaluation Blocked(string code, string? branchId = null) => new([], [new(code, branchId)]);
}
