using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>
/// Pure E0 projection. No repositories, legacy snapshot creation/restoration, AI calls or rendering.
/// Invalid input contracts reject the whole projection; absent selections block only their instances.
/// </summary>
public static class TemplateInstanceProjector
{
    // Bounded in-memory admission, not a batch/UI/render performance promise.
    public const int MaximumFactEntries = 10_000;

    public static TemplateInstanceProjectionResult Project(TemplateInstanceProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentNullException.ThrowIfNull(request.Revision);
        ArgumentNullException.ThrowIfNull(request.Facts);
        try
        {
            var scope = ValidateScope(request.Scope, request.Revision);
            var facts = request.Facts;
            ValidateFactShape(facts);
            var evaluation = TemplateRuleEvaluator.Evaluate(request.Revision.RuleWorkspace!.Rules!, request.Revision, facts.Colors);
            if (!evaluation.CanExpand)
                return new(TemplateInstanceProjectionStatus.Rejected, null, [], evaluation.Diagnostics);

            ValidateReferences(scope, request.Revision, facts, evaluation.Instances);
            var inputDigest = InputDigest(facts);
            var colorDigest = Hash(new { schema = "template-instance-colors/v1", facts.Colors });
            var assets = facts.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
            var variants = facts.Variants.ToDictionary(variant => variant.VariantId,
                variant => variant.AssetIds.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            var selections = facts.Selections.ToDictionary(selection => (selection.FrameId, selection.VariantId));
            var overrides = facts.Overrides.ToDictionary(selection =>
                (selection.SourcePageId, selection.InstanceKey, selection.FrameId));
            var pages = request.Revision.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
            var instances = ImmutableArray.CreateBuilder<TemplateInstanceProjection>(evaluation.Instances.Length);
            foreach (var instance in evaluation.Instances)
            {
                var reasons = ImmutableArray.CreateBuilder<TemplateInstanceBlockReason>();
                var frames = ImmutableArray.CreateBuilder<TemplateInstanceFrameProjection>();
                // Frame declarations are a map; preserve slot/recipe/repetition order, not map insertion order.
                foreach (var frame in instance.Frames.OrderBy(frame => frame.FrameId, StringComparer.Ordinal))
                {
                    overrides.TryGetValue((instance.PageId, instance.Key, frame.FrameId), out var manual);
                    var assetId = manual?.AssetId ?? selections.GetValueOrDefault((frame.FrameId, frame.VariantId))?.AssetId;
                    string? reason = assetId is null ? "TPL_INSTANCE_SELECTION_MISSING"
                        : !variants[frame.VariantId].Contains(assetId) ? "TPL_INSTANCE_ASSET_VARIANT_MISMATCH"
                        : assets[assetId].Readability != AssetReadability.Readable ? "TPL_INSTANCE_ASSET_UNREADABLE" : null;
                    if (reason is not null) reasons.Add(new(reason, frame.FrameId, frame.VariantId, assetId));
                    frames.Add(new(frame.FrameId, frame.SlotId, frame.VariantId,
                        reason is null ? assetId : null, manual?.SelectionRevision ?? 0));
                }

                var page = pages[instance.PageId];
                if (page.ContentType == TemplatePageContentType.DynamicSizeTable)
                    reasons.Add(new("TPL_INSTANCE_SIZE_VALIDATION_REQUIRED"));
                var status = reasons.Count == 0 ? TemplateInstancePreflightStatus.AssetBindingsSatisfied
                    : reasons.Any(reason => reason.Code != "TPL_INSTANCE_SIZE_VALIDATION_REQUIRED")
                        ? TemplateInstancePreflightStatus.BusinessBlocked : TemplateInstancePreflightStatus.RequiresProductionValidation;
                // Full SHA-256 over named JSON fields, not ambiguous delimiter concatenation or random IDs.
                // Selection/override changes alter InputFactsDigest, not the logical instance identity.
                var id = "instance_" + Hash(new
                {
                    schema = "template-instance-identity/v1", scope.BatchId, scope.StyleId, scope.SnapshotId,
                    scope.RevisionId, scope.RuleDigest, colorDigest, sourcePageId = instance.PageId,
                    instance.Key.BranchId, instance.Key.RecipeId, instance.Key.RepetitionIndex
                })[7..];
                instances.Add(new(id, scope.BatchId, scope.StyleId, scope.SnapshotId, scope.RevisionId,
                    scope.RuleDigest, inputDigest, instance.PageId, page.TemplateResourceId,
                    instance.Key.BranchId, instance.Key.RecipeId, instance.Key.RepetitionIndex, instances.Count,
                    instance.Colors, frames.ToImmutable(), status, reasons.ToImmutable()));
            }
            return new(instances.Count == 0 ? TemplateInstanceProjectionStatus.Empty : TemplateInstanceProjectionStatus.Projected,
                inputDigest, instances.ToImmutable(), []);
        }
        catch (TemplateRuleViolationException error)
        {
            return new(TemplateInstanceProjectionStatus.Rejected, null, [], [new(error.Code)]);
        }
    }

    private static TemplateInstanceScope ValidateScope(TemplateInstanceScope scope, TemplateRevision revision)
    {
        foreach (var id in new[] { scope.BatchId, scope.StyleId, scope.SnapshotId, scope.RevisionId })
            TemplateValueRules.Identifier(id, "TPL_INSTANCE_SCOPE_INVALID");
        var digest = TemplateValueRules.Digest(scope.RuleDigest, "TPL_INSTANCE_RULE_DIGEST_MISMATCH");
        if (revision.State != TemplateRevisionState.Committed || revision.RevisionId != scope.RevisionId)
            throw Invalid("TPL_INSTANCE_REVISION_MISMATCH");
        if (revision.RuleWorkspace?.Rules is null)
            throw Invalid("TPL_INSTANCE_RULES_REQUIRED");
        TemplateRuleCodec.ValidateWorkspace(revision.RuleWorkspace);
        if (revision.RuleWorkspace.Version < 1)
            throw Invalid("TPL_INSTANCE_RULE_VERSION_INVALID");
        if (revision.RuleDigest != digest || TemplateRuleCodec.BusinessDigest(revision) != digest)
            throw Invalid("TPL_INSTANCE_RULE_DIGEST_MISMATCH");
        return scope with { RuleDigest = digest };
    }

    private static void ValidateFactShape(TemplateInstanceInputFacts facts)
    {
        if (facts.Version < 1 || facts.Colors is null || facts.Colors.OrderedVariantIds.IsDefault
            || facts.Colors.OrderedVariantIds.Length > MaximumFactEntries
            || !Bounded(facts.Assets) || !Bounded(facts.Variants) || !Bounded(facts.Selections) || !Bounded(facts.Overrides)
            || facts.Variants.Any(variant => !Bounded(variant.AssetIds))
            || facts.Variants.Sum(variant => (long)variant.AssetIds.Length) > MaximumFactEntries)
            throw Invalid("TPL_INSTANCE_FACTS_INVALID");
    }

    private static void ValidateReferences(TemplateInstanceScope scope, TemplateRevision revision,
        TemplateInstanceInputFacts facts, ImmutableArray<TemplateRulePageInstance> instances)
    {
        var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in facts.Assets)
            if (asset.StyleId != scope.StyleId || !assets.Add(asset.AssetId))
                throw Invalid("TPL_INSTANCE_ASSET_OWNERSHIP_INVALID");

        var variants = new HashSet<string>(StringComparer.Ordinal);
        var colorIds = facts.Colors.OrderedVariantIds.ToHashSet(StringComparer.Ordinal);
        var assignedAssets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in facts.Variants)
        {
            if (!colorIds.Contains(variant.VariantId)
                || !variants.Add(variant.VariantId)) throw Invalid("TPL_INSTANCE_VARIANTS_INVALID");
            foreach (var asset in variant.AssetIds)
                if (!assets.Contains(asset) || !assignedAssets.Add(asset))
                    throw Invalid("TPL_INSTANCE_VARIANT_ASSETS_INVALID");
        }
        if (!variants.SetEquals(facts.Colors.OrderedVariantIds)) throw Invalid("TPL_INSTANCE_VARIANTS_INVALID");

        var frames = revision.ActiveFrameBindings.Select(frame => frame.FrameId).ToHashSet(StringComparer.Ordinal);
        var selections = new HashSet<(string, string)>();
        foreach (var selection in facts.Selections)
        {
            if (!frames.Contains(selection.FrameId) || !variants.Contains(selection.VariantId)
                || !selections.Add((selection.FrameId, selection.VariantId))) throw Invalid("TPL_INSTANCE_SELECTION_INVALID");
            if (!assets.Contains(selection.AssetId)) throw Invalid("TPL_INSTANCE_ASSET_REFERENCE_INVALID");
        }

        var targets = instances.ToDictionary(instance => (instance.PageId, instance.Key));
        var overrides = new HashSet<(string, TemplateRuleInstanceKey, string)>();
        foreach (var manual in facts.Overrides)
        {
            if (manual.InstanceKey is null || manual.SelectionRevision < 1
                || !targets.TryGetValue((manual.SourcePageId, manual.InstanceKey), out var target)
                || !target.Frames.Any(frame => frame.FrameId == manual.FrameId)
                || !overrides.Add((manual.SourcePageId, manual.InstanceKey, manual.FrameId)))
                throw Invalid("TPL_INSTANCE_OVERRIDE_INVALID");
            if (!assets.Contains(manual.AssetId)) throw Invalid("TPL_INSTANCE_ASSET_REFERENCE_INVALID");
        }
    }

    // E2 admission reuses the E0 preimage verbatim, without evaluating rules or regenerating IDs.
    internal static string ValidateAndComputeInputDigest(TemplateInstanceInputFacts facts)
    {
        ValidateFactShape(facts);
        return InputDigest(facts);
    }

    private static string InputDigest(TemplateInstanceInputFacts facts) => Hash(new
    {
        schema = "template-instance-input/v1", facts.Version, facts.Colors,
        // Never hash machine paths or presentation aliases. Asset IDs/hash/metadata bind actual input facts.
        assets = facts.Assets.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).Select(asset => new
        {
            asset.AssetId, asset.StyleId, asset.Sha256, asset.Length, asset.PixelWidth, asset.PixelHeight,
            asset.Format, asset.MimeType, asset.Readability
        }),
        variants = facts.Variants.OrderBy(variant => variant.VariantId, StringComparer.Ordinal).Select(variant => new
        { variant.VariantId, assetIds = variant.AssetIds.Order(StringComparer.Ordinal) }),
        selections = facts.Selections.OrderBy(selection => selection.FrameId, StringComparer.Ordinal)
            .ThenBy(selection => selection.VariantId, StringComparer.Ordinal),
        overrides = facts.Overrides.OrderBy(selection => selection.SourcePageId, StringComparer.Ordinal)
            .ThenBy(selection => selection.InstanceKey.BranchId, StringComparer.Ordinal)
            .ThenBy(selection => selection.InstanceKey.RecipeId, StringComparer.Ordinal)
            .ThenBy(selection => selection.InstanceKey.RepetitionIndex)
            .ThenBy(selection => selection.FrameId, StringComparer.Ordinal)
    });

    private static bool Bounded<T>(ImmutableArray<T> items) => !items.IsDefault
        && items.Length <= MaximumFactEntries && items.All(item => item is not null);

    private static string Hash<T>(T value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static TemplateRuleViolationException Invalid(string code) => new(code);
}
