using System.Collections.Immutable;

namespace Desktop.Domain.Templates;

public enum TemplateRuleColorParity { Any = 0, Odd, Even }
public enum TemplateRuleRepeat { Once = 0, ColorChunks }
public enum TemplateRuleColorRange { None = 0, All, Secondary }
public enum TemplateRuleColorSource { Primary = 0, SingleSecondary, Allocated }

/// <summary>
/// Cross-page orchestration only. Structural evidence, layout and image semantics
/// remain on the referenced revision's FrameBindings. Not a persistence or AI DTO.
/// </summary>
public sealed record TemplateRuleSet(int SchemaVersion, ImmutableArray<TemplateRuleBranch> Branches)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record TemplateRuleColorCount(
    int Minimum,
    int? Maximum = null,
    TemplateRuleColorParity Parity = TemplateRuleColorParity.Any)
{
    internal bool Matches(int count) => count >= Minimum && (Maximum is null || count <= Maximum)
        && (Parity == TemplateRuleColorParity.Any
            || count % 2 == (Parity == TemplateRuleColorParity.Odd ? 1 : 0));
}

public sealed record TemplateRuleBranch(
    string BranchId,
    TemplateRuleColorCount Condition,
    ImmutableArray<TemplatePageRecipe> Recipes,
    bool RequireCompleteColorCoverage = false);

/// <summary>
/// Allocated slots, in their declared order, define colors per instance. Multiple
/// frames can reference one slot. ExpectedInstanceCount is an assertion, not a loop override.
/// </summary>
public sealed record TemplatePageRecipe(
    string RecipeId,
    string PageId,
    TemplateRuleRepeat Repeat,
    TemplateRuleColorRange ColorRange,
    ImmutableArray<TemplateRuleColorSlot> ColorSlots,
    ImmutableArray<TemplateFrameColorBinding> FrameBindings,
    int? ExpectedInstanceCount = null);

public sealed record TemplateRuleColorSlot(string SlotId, TemplateRuleColorSource Source);
public sealed record TemplateFrameColorBinding(string FrameId, string SlotId);

/// <summary>Caller supplies the frozen product-color order, not arbitrary dictionary order.</summary>
public sealed record TemplateRuleColorFacts(
    ImmutableArray<string> OrderedVariantIds,
    string? PrimaryVariantId,
    bool IsGroupingResolved);

public sealed record TemplateRuleDiagnostic(
    string Code,
    string? BranchId = null,
    string? RecipeId = null,
    string? PageId = null,
    string? FrameId = null);

/// <summary>
/// Unique only within one frozen rule snapshot and product. This is NOT an execution
/// id, OutputVariantId, or permission to render; the application must persist that mapping.
/// </summary>
public sealed record TemplateRuleInstanceKey(string BranchId, string RecipeId, int RepetitionIndex);

public sealed record TemplateRuleColorAssignment(string SlotId, string VariantId);
public sealed record TemplateRuleFrameAssignment(string FrameId, string SlotId, string VariantId);

public sealed record TemplateRulePageInstance(
    TemplateRuleInstanceKey Key,
    string PageId,
    ImmutableArray<TemplateRuleColorAssignment> Colors,
    ImmutableArray<TemplateRuleFrameAssignment> Frames);

public sealed record TemplateRuleEvaluation(
    ImmutableArray<TemplateRulePageInstance> Instances,
    ImmutableArray<TemplateRuleDiagnostic> Diagnostics)
{
    // An explicitly empty loop (e.g. no secondary colors) is a valid expansion.
    // This says nothing about image matching, size data, calibration or render readiness.
    public bool CanExpand => Diagnostics.IsEmpty;
}
