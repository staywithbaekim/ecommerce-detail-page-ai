using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed record TemplateFrameRuleEdit(string FrameId, string Description);
[JsonConverter(typeof(TemplateBranchRuleEditJsonConverter))]
public sealed record TemplateBranchRuleEdit(string BranchId, TemplateRuleBranchView? Branch)
{
    // Keep the domain-shaped construction source-compatible for existing
    // Infrastructure tests and package fixtures. Presentation code uses the
    // Application projection above and never sees the domain type.
    public TemplateBranchRuleEdit(string branchId, TemplateRuleBranch? branch)
        : this(branchId, TemplateRuleViewMapper.ToView(branch)) { }
}

internal sealed class TemplateBranchRuleEditJsonConverter : JsonConverter<TemplateBranchRuleEdit>
{
    private sealed record Payload(string BranchId, TemplateRuleBranchView? Branch);

    public override TemplateBranchRuleEdit Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var payload = JsonSerializer.Deserialize<Payload>(ref reader, options)
            ?? throw new JsonException("Null branch edit.");
        return new(payload.BranchId, payload.Branch);
    }

    public override void Write(Utf8JsonWriter writer, TemplateBranchRuleEdit value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("branchId", value.BranchId);
        writer.WritePropertyName("branch");
        JsonSerializer.Serialize(writer, value.Branch, options);
        writer.WriteEndObject();
    }
}
public sealed record TemplateRulePageState(int Order, string Name, TemplatePageContentType ContentType,
    TemplateGenerationMode GenerationMode, int? VariantCapacity, bool Enabled, string ResourceId,
    string? Notes, double CanvasX, double CanvasY, SizeTableTemplateCalibration? SizeTableCalibration);
public sealed record TemplateRulePageEdit(string PageId, TemplateRulePageState? Page,
    IReadOnlyList<TemplatePackageFrameBinding> Bindings);
public sealed record TemplateRuleTargetEdit(string FrameId, TemplatePackageFrameBinding? Binding);
public sealed record TemplateRuleCandidateVisibilityEdit(string FrameId, bool Excluded);
public sealed record TemplateRuleEditSource(string Kind, string Text, string RequestId, string ContextDigest,
    string ModelVersion, string PromptVersion);
public sealed record TemplateRuleEditSet(
    ImmutableArray<TemplateFrameRuleEdit> Frames,
    ImmutableArray<TemplateBranchRuleEdit> Branches,
    IReadOnlyList<TemplateRulePageEdit>? Pages = null,
    IReadOnlyList<TemplateRuleTargetEdit>? Targets = null,
    TemplateRuleEditSource? Source = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TemplateRuleCandidateVisibilityEdit>? CandidateVisibility { get; init; }
}
public sealed record TemplateRulePatch(string CommandId, long ExpectedDraftVersion, TemplateRuleEditSet Changes);
public sealed record TemplateRuleCommandReceipt(
    string CommandId, string RequestDigest, long ResultVersion,
    TemplateRuleEditSet? Applied, TemplateRuleEditSet? Inverse, bool IsUndone, bool BeforeRulesWereLegacy = false);
public sealed record TemplateRuleDraftView(
    string RevisionId, long Version, string RuleDigest, TemplateRuleSetView? Rules,
    ImmutableArray<TemplateReferenceAliasView> Aliases,
    ImmutableArray<TemplateRuleDiagnosticView> Diagnostics,
    ImmutableArray<TemplateRuleFrameOverview> Frames,
    IReadOnlyList<string>? UndoCommandIds = null)
{
    public IReadOnlyList<TemplateRuleCandidateView> Candidates { get; init; } = [];
    public IReadOnlyList<TemplateRuleCandidateView> ExcludedCandidates { get; init; } = [];
}
public sealed record TemplateRuleCandidateView(string PageId, string FrameId, TemplateFrameProjection Target,
    bool CanInterpret, bool IsExcluded = false);
public sealed record TemplateRuleFrameOverview(string PageId, string FrameId, string Description);
public sealed record TemplateRuleConfirmation(long DraftVersion, string RuleDigest);
public sealed record TemplateRuleReference(string BranchId, string RecipeId, string PageId, string? FrameId);

/// <summary>Extension of the existing template writer, never a parallel database or queue.</summary>
public interface ITemplateRuleCommandStore
{
    Task<IReadOnlyList<string>> GetUndoableRuleCommandsAsync(string revisionId, CancellationToken cancellationToken);
    Task<TemplateRuleCommandReceipt?> GetRuleCommandAsync(string revisionId, string commandId, CancellationToken cancellationToken);
    Task SaveRuleCommandAsync(TemplateRevision revision, TemplateRuleCommandReceipt receipt,
        string? undoOf, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateRuleReference>> GetRuleReferencesAsync(string revisionId, string pageId, CancellationToken cancellationToken);
}

public interface ITemplateRuleEditingService
{
    Task<TemplateRuleDraftView> GetRulesAsync(TemplateDraftSession session, CancellationToken cancellationToken);
    Task<TemplateRuleDraftView> ApplyRulePatchAsync(TemplateDraftSession session, TemplateRulePatch patch, CancellationToken cancellationToken);
    Task<TemplateRuleDraftView> UndoRulePatchAsync(TemplateDraftSession session, string commandId,
        string undoCommandId, long expectedDraftVersion, CancellationToken cancellationToken);
    Task<TemplateGroupDetails> ConfirmRulesAsync(TemplateDraftSession session,
        TemplateRuleConfirmation confirmation, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateRuleReference>> GetRuleImpactAsync(TemplateDraftSession session, string pageId, CancellationToken cancellationToken);
}
