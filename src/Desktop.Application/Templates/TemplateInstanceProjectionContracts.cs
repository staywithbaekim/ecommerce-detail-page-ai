using System.Collections.Immutable;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>
/// E0-only caller scope, not a persisted BatchTemplateSnapshot or authorization to render.
/// The revision and its business digest are verified by the projector; durable admission is E1.
/// </summary>
public sealed record TemplateInstanceScope(
    string BatchId, string StyleId, string SnapshotId, string RevisionId, string RuleDigest);

public sealed record TemplateInstanceVariantAssets(string VariantId, ImmutableArray<string> AssetIds);

/// <summary>Already selected evidence for one frame and color; E0 does not run AI or rank candidates.</summary>
public sealed record TemplateInstanceFrameSelection(string FrameId, string VariantId, string AssetId);

/// <summary>No frame-global override: the logical instance and frame must both match.</summary>
public sealed record TemplateInstanceSelectionOverride(
    string SourcePageId, TemplateRuleInstanceKey InstanceKey, string FrameId, string AssetId, long SelectionRevision);

public sealed record TemplateInstanceInputFacts(
    long Version,
    TemplateRuleColorFacts Colors,
    ImmutableArray<SourceAssetMetadata> Assets,
    ImmutableArray<TemplateInstanceVariantAssets> Variants,
    ImmutableArray<TemplateInstanceFrameSelection> Selections,
    ImmutableArray<TemplateInstanceSelectionOverride> Overrides);

public sealed record TemplateInstanceProjectionRequest(
    TemplateInstanceScope Scope, TemplateRevision Revision, TemplateInstanceInputFacts Facts);

public enum TemplateInstanceProjectionStatus { Rejected, Empty, Projected }

/// <summary>None of these values asserts RenderPlan, geometry, size or Photoshop readiness.</summary>
public enum TemplateInstancePreflightStatus { AssetBindingsSatisfied, BusinessBlocked, RequiresProductionValidation }

public sealed record TemplateInstanceBlockReason(
    string Code, string? FrameId = null, string? VariantId = null, string? AssetId = null);

public sealed record TemplateInstanceFrameProjection(
    string FrameId, string SlotId, string VariantId, string? AssetId, long SelectionRevision);

/// <summary>
/// Immutable, renderer-neutral preflight only. Order and RepetitionIndex are zero-based.
/// PageInstanceId is neither an execution ID nor an OutputVariantId.
/// </summary>
public sealed record TemplateInstanceProjection(
    string PageInstanceId,
    string BatchId,
    string StyleId,
    string SnapshotId,
    string RevisionId,
    string RuleDigest,
    string InputFactsDigest,
    string SourcePageId,
    string TemplateResourceId,
    string BranchId,
    string RecipeId,
    int RepetitionIndex,
    int Order,
    ImmutableArray<TemplateRuleColorAssignment> Slots,
    ImmutableArray<TemplateInstanceFrameProjection> Frames,
    TemplateInstancePreflightStatus Status,
    ImmutableArray<TemplateInstanceBlockReason> BlockingReasons);

/// <summary>Rejected has no instances/digest; Empty is a valid zero expansion, not successful output.</summary>
public sealed record TemplateInstanceProjectionResult(
    TemplateInstanceProjectionStatus Status,
    string? InputFactsDigest,
    ImmutableArray<TemplateInstanceProjection> Instances,
    ImmutableArray<TemplateRuleDiagnostic> Diagnostics);

/// <summary>Frozen E1 body; intentionally independent of the legacy batch-template snapshot schema.</summary>
public sealed record TemplateInstanceProjectionSnapshot(
    int SchemaVersion,
    long SnapshotVersion,
    string BatchId,
    string StyleId,
    string SnapshotId,
    string RevisionId,
    string RuleDigest,
    string InputFactsDigest,
    string ProjectionDigest,
    ImmutableArray<TemplateInstanceProjection> Instances);

/// <summary>Command identity and expected snapshot CAS version; version 0 means create only.</summary>
public sealed record FreezeTemplateInstanceProjectionCommand(
    string CommandId,
    long ExpectedSnapshotVersion,
    TemplateInstanceProjectionSnapshot Snapshot);

public enum FreezeTemplateInstanceProjectionOutcome { Created, AlreadyApplied }

public sealed record FreezeTemplateInstanceProjectionResult(
    FreezeTemplateInstanceProjectionOutcome Outcome,
    long SnapshotVersion,
    string ProjectionDigest);

/// <summary>Append-only E1 freeze boundary; no RenderPlan or execution admission is implied.</summary>
public interface ITemplateInstanceProjectionSnapshotStore
{
    Task<FreezeTemplateInstanceProjectionResult> FreezeAsync(
        FreezeTemplateInstanceProjectionCommand command, CancellationToken cancellationToken);

    Task<TemplateInstanceProjectionSnapshot?> ReadAsync(
        string batchId, string styleId, CancellationToken cancellationToken);
}
