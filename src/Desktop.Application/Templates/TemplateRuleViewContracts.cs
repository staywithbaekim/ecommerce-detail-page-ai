using System.Collections.Immutable;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

// Presentation-safe projections. WPF consumes these contracts instead of
// referencing Desktop.Domain directly; conversion back to the domain remains
// inside the Application layer at the persistence/AI boundary.
public enum TemplateRuleParityOption { Any = 0, Odd, Even }
public enum TemplateRuleRepeatOption { Once = 0, ColorChunks }
public enum TemplateRuleColorRangeOption { None = 0, All, Secondary }
public enum TemplateRuleColorSourceOption { Primary = 0, SingleSecondary, Allocated }

public sealed record TemplateRuleColorCountView(int Minimum, int? Maximum, TemplateRuleParityOption Parity);
public sealed record TemplateRuleColorSlotView(string SlotId, TemplateRuleColorSourceOption Source);
public sealed record TemplateFrameColorBindingView(string FrameId, string SlotId);
public sealed record TemplatePageRecipeView(
    string RecipeId,
    string PageId,
    TemplateRuleRepeatOption Repeat,
    TemplateRuleColorRangeOption ColorRange,
    ImmutableArray<TemplateRuleColorSlotView> ColorSlots,
    ImmutableArray<TemplateFrameColorBindingView> FrameBindings,
    int? ExpectedInstanceCount = null);
public sealed record TemplateRuleBranchView(
    string BranchId,
    TemplateRuleColorCountView Condition,
    ImmutableArray<TemplatePageRecipeView> Recipes,
    bool RequireCompleteColorCoverage = false);
public sealed record TemplateRuleSetView(int SchemaVersion, ImmutableArray<TemplateRuleBranchView> Branches);
public sealed record TemplateReferenceAliasView(string PageId, string? FrameId, int Number);
public sealed record TemplateRuleDiagnosticView(
    string Code,
    string? BranchId = null,
    string? RecipeId = null,
    string? PageId = null,
    string? FrameId = null);

internal static class TemplateRuleViewMapper
{
    public static TemplateRuleSetView? ToView(TemplateRuleSet? rules) => rules is null ? null : new(
        rules.SchemaVersion,
        [.. rules.Branches.Select(branch => new TemplateRuleBranchView(
            branch.BranchId,
            new(branch.Condition.Minimum, branch.Condition.Maximum, (TemplateRuleParityOption)branch.Condition.Parity),
            [.. branch.Recipes.Select(recipe => new TemplatePageRecipeView(
                recipe.RecipeId,
                recipe.PageId,
                (TemplateRuleRepeatOption)recipe.Repeat,
                (TemplateRuleColorRangeOption)recipe.ColorRange,
                [.. recipe.ColorSlots.Select(slot => new TemplateRuleColorSlotView(slot.SlotId, (TemplateRuleColorSourceOption)slot.Source))],
                [.. recipe.FrameBindings.Select(binding => new TemplateFrameColorBindingView(binding.FrameId, binding.SlotId))],
                recipe.ExpectedInstanceCount))],
            branch.RequireCompleteColorCoverage))]);

    public static TemplateRuleBranchView? ToView(TemplateRuleBranch? branch) => branch is null ? null : new(
        branch.BranchId,
        new(branch.Condition.Minimum, branch.Condition.Maximum, (TemplateRuleParityOption)branch.Condition.Parity),
        [.. branch.Recipes.Select(recipe => new TemplatePageRecipeView(
            recipe.RecipeId,
            recipe.PageId,
            (TemplateRuleRepeatOption)recipe.Repeat,
            (TemplateRuleColorRangeOption)recipe.ColorRange,
            [.. recipe.ColorSlots.Select(slot => new TemplateRuleColorSlotView(slot.SlotId, (TemplateRuleColorSourceOption)slot.Source))],
            [.. recipe.FrameBindings.Select(binding => new TemplateFrameColorBindingView(binding.FrameId, binding.SlotId))],
            recipe.ExpectedInstanceCount))],
        branch.RequireCompleteColorCoverage);

    public static TemplateRuleSet? ToDomain(TemplateRuleSetView? rules) => rules is null ? null : new(
        rules.SchemaVersion,
        [.. rules.Branches.Select(branch => new TemplateRuleBranch(
            branch.BranchId,
            new(branch.Condition.Minimum, branch.Condition.Maximum, (TemplateRuleColorParity)branch.Condition.Parity),
            [.. branch.Recipes.Select(recipe => new TemplatePageRecipe(
                recipe.RecipeId,
                recipe.PageId,
                (TemplateRuleRepeat)recipe.Repeat,
                (TemplateRuleColorRange)recipe.ColorRange,
                [.. recipe.ColorSlots.Select(slot => new TemplateRuleColorSlot(slot.SlotId, (TemplateRuleColorSource)slot.Source))],
                [.. recipe.FrameBindings.Select(binding => new TemplateFrameColorBinding(binding.FrameId, binding.SlotId))],
                recipe.ExpectedInstanceCount))],
            branch.RequireCompleteColorCoverage))]);

    public static TemplateRuleBranch ToDomain(TemplateRuleBranchView branch) => new(
        branch.BranchId,
        new(branch.Condition.Minimum, branch.Condition.Maximum, (TemplateRuleColorParity)branch.Condition.Parity),
        [.. branch.Recipes.Select(recipe => new TemplatePageRecipe(
            recipe.RecipeId,
            recipe.PageId,
            (TemplateRuleRepeat)recipe.Repeat,
            (TemplateRuleColorRange)recipe.ColorRange,
            [.. recipe.ColorSlots.Select(slot => new TemplateRuleColorSlot(slot.SlotId, (TemplateRuleColorSource)slot.Source))],
            [.. recipe.FrameBindings.Select(binding => new TemplateFrameColorBinding(binding.FrameId, binding.SlotId))],
            recipe.ExpectedInstanceCount))],
        branch.RequireCompleteColorCoverage);

    public static TemplateReferenceAliasView ToView(TemplateReferenceAlias alias) =>
        new(alias.PageId, alias.FrameId, alias.Number);

    public static ImmutableArray<TemplateReferenceAliasView> ToViews(
        IEnumerable<TemplateReferenceAlias> aliases) => [.. aliases.Select(ToView)];

    public static ImmutableArray<TemplateRuleDiagnosticView> ToViews(
        IEnumerable<TemplateRuleDiagnostic> diagnostics) => [.. diagnostics.Select(item =>
        new TemplateRuleDiagnosticView(item.Code, item.BranchId, item.RecipeId, item.PageId, item.FrameId))];
}
