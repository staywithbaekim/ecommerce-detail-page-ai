using System.Collections.Immutable;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>
/// One selected member of a complete E1 snapshot and the facts that produced it.
/// PlanRequest.Assets is instance-local; Facts is the full digest preimage, not an asset fallback pool.
/// All members of every referenced snapshot must be supplied; silently omitting blocked members is forbidden.
/// </summary>
public sealed record TemplateInstanceRenderBatchInput(
    TemplateInstanceRenderPlanRequest PlanRequest,
    TemplateInstanceInputFacts Facts);

public sealed record TemplateInstanceRenderBatchRequest(
    string BatchId,
    string StyleId,
    string RevisionId,
    IReadOnlyList<TemplateInstanceRenderBatchInput> Instances,
    IReadOnlyList<TemplateInstanceRenderBatchEmptySnapshot>? EmptySnapshots = null);

/// <summary>A legal zero expansion has a complete body/fact preimage, but no fabricated member ID or plan parameters.</summary>
public sealed record TemplateInstanceRenderBatchEmptySnapshot(
    TemplateInstanceProjectionSnapshot Snapshot, TemplateRevision Revision, TemplateInstanceInputFacts Facts);

public enum TemplateInstanceRenderBatchStatus
{
    Ready, Blocked, Empty, PartiallyReady, UserActionRequired, ProductionValidationRequired
}

public enum TemplateInstanceBatchDiagnosticKind { IntegrityError, BusinessBlock, ProductionValidationRequired, Information }
public enum TemplateInstanceBatchMemberStatus { Plannable, BusinessBlocked, RequiresProductionValidation, Rejected, Withheld }

/// <summary>Planning admission only, not a task transition, journal or permission to submit to a renderer.</summary>
public sealed record TemplateInstanceRenderBatchResult(
    string SchemaVersion,
    string BatchId,
    string StyleId,
    string RevisionId,
    TemplateInstanceRenderBatchStatus Status,
    string? BatchPlanDigest,
    string? InputFactsDigest,
    ImmutableArray<TemplateInstanceBatchPlan> Plans,
    ImmutableArray<TemplateInstanceBatchDiagnostic> Diagnostics,
    ImmutableArray<TemplateInstanceBatchMember> Members,
    bool HasCompleteMembership)
{
    public const string CurrentSchemaVersion = "template-instance-render-batch/v2";
    public const string InputDigestSchemaVersion = "template-instance-render-batch-input/v2";

    public bool CanContinue => Status is TemplateInstanceRenderBatchStatus.Ready or TemplateInstanceRenderBatchStatus.PartiallyReady
        && HasCompleteMembership && !Plans.IsEmpty
        && Diagnostics.All(item => item.Kind is TemplateInstanceBatchDiagnosticKind.BusinessBlock or TemplateInstanceBatchDiagnosticKind.Information);
}

/// <summary>Complete frozen-member disposition, including members without plans. Rejected batches expose only known bodies.</summary>
public sealed record TemplateInstanceBatchMember(
    string PageInstanceId, int StableOrder, string SourcePageId, string RecipeId, int RepetitionIndex,
    string SnapshotId, string RuleDigest, string InputFactsDigest, string ProjectionDigest,
    TemplateInstanceBatchMemberStatus Status, ImmutableArray<TemplateInstanceBatchDiagnostic> Diagnostics,
    string? PlanId, string? ArtifactSetId, string? PlanDigest);

public sealed record TemplateInstanceBatchPlan(
    string PageInstanceId,
    int StableOrder,
    string SourcePageId,
    string RecipeId,
    int RepetitionIndex,
    string SnapshotId,
    string RuleDigest,
    string InputFactsDigest,
    string ProjectionDigest,
    string PlanId,
    string ArtifactSetId,
    string PlanDigest,
    string RenderPlanJson);

/// <summary>Unclassified/default errors fail closed. Only evidence-verified frozen reasons may be BusinessBlock.</summary>
public sealed record TemplateInstanceBatchDiagnostic(
    string Code,
    string? PageInstanceId = null,
    string? SourcePageId = null,
    string? RecipeId = null,
    int? RepetitionIndex = null,
    string? FrameId = null,
    string? VariantId = null,
    string? AssetId = null,
    TemplateInstanceBatchDiagnosticKind Kind = TemplateInstanceBatchDiagnosticKind.IntegrityError);
