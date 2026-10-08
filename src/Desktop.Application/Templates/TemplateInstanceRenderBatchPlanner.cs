using System.Collections.Immutable;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>
/// Pure E2 planning. Reuses E1 validation and the single-instance factory; no rule expansion,
/// repositories, clocks, execution identities, files or legacy batch execution adapters.
/// Integrity errors discard the entire candidate set; verified preflight business red pages are isolated.
/// </summary>
public sealed class TemplateInstanceRenderBatchPlanner(IRenderPlanValidator validator)
{
    private readonly TemplateInstanceRenderPlanFactory _instances = new(validator);

    public TemplateInstanceRenderBatchResult Build(TemplateInstanceRenderBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<TemplateInstanceBatchDiagnostic>();
        var members = new List<TemplateInstanceProjection>();
        var candidates = new List<TemplateInstanceBatchPlan>();
        var snapshots = new List<(string Id, string Digest, TemplateInstanceProjection Instance)>();
        var resources = new List<(string Id, string Digest, TemplateInstanceProjection Instance)>();
        var bodies = new Dictionary<(string SnapshotId, string ProjectionDigest), TemplateInstanceProjectionSnapshot>();
        try
        {
            TemplateValueRules.Identifier(request.BatchId, "TPL_INSTANCE_BATCH_SCOPE_INVALID");
            TemplateValueRules.Identifier(request.StyleId, "TPL_INSTANCE_BATCH_SCOPE_INVALID");
            TemplateValueRules.Identifier(request.RevisionId, "TPL_INSTANCE_BATCH_SCOPE_INVALID");
            if (request.Instances is null || request.Instances.Count + (long)(request.EmptySnapshots?.Count ?? 0)
                    > TemplateInstanceProjector.MaximumFactEntries)
                throw Invalid("TPL_INSTANCE_BATCH_INPUT_INVALID");
        }
        catch (TemplateRuleViolationException error)
        {
            return Finish(request, candidates, [new(error.Code)], []);
        }

        foreach (var empty in request.EmptySnapshots ?? [])
        {
            try
            {
                if (empty?.Snapshot is null || empty.Revision is null || empty.Facts is null)
                    throw Invalid("TPL_INSTANCE_BATCH_INPUT_INVALID");
                TemplateInstanceProjectionSnapshotCodec.Validate(empty.Snapshot);
                if (!empty.Snapshot.Instances.IsEmpty)
                    throw Invalid("TPL_INSTANCE_BATCH_EMPTY_SNAPSHOT_INVALID");
                if (empty.Snapshot.BatchId != request.BatchId || empty.Snapshot.StyleId != request.StyleId
                    || empty.Snapshot.RevisionId != request.RevisionId)
                    throw Invalid("TPL_INSTANCE_BATCH_SCOPE_MISMATCH");
                ValidateSnapshotFacts(empty.Snapshot, empty.Revision, empty.Facts);
                bodies.TryAdd((empty.Snapshot.SnapshotId, empty.Snapshot.ProjectionDigest), empty.Snapshot);
            }
            catch (TemplateRuleViolationException error) { diagnostics.Add(new(error.Code)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
            { diagnostics.Add(new("TPL_INSTANCE_BATCH_INPUT_INVALID")); }
        }

        foreach (var input in request.Instances)
        {
            TemplateInstanceProjection? instance = null;
            var plan = input?.PlanRequest;
            try
            {
                if (plan?.Snapshot is null || plan.Revision is null || plan.TemplateResource is null
                    || plan.Assets is null || input!.Facts is null)
                    throw Invalid("TPL_INSTANCE_BATCH_INPUT_INVALID");

                // Do not reconstruct a body from member rows or sort/re-digest a damaged E1 body.
                if (!plan.Snapshot.Instances.IsDefault && plan.Snapshot.Instances.Any(item =>
                        item is not null && string.IsNullOrWhiteSpace(item.PageInstanceId)))
                    throw Invalid("TPL_INSTANCE_SNAPSHOT_BINDING_INVALID");
                TemplateInstanceProjectionSnapshotCodec.Validate(plan.Snapshot);
                bodies.TryAdd((plan.Snapshot.SnapshotId, plan.Snapshot.ProjectionDigest), plan.Snapshot);
                instance = plan.Snapshot.Instances.SingleOrDefault(item => item.PageInstanceId == plan.PageInstanceId);
                if (instance is null)
                    throw Invalid(plan.Snapshot.Instances.IsEmpty
                        ? "TPL_INSTANCE_BATCH_EMPTY_SNAPSHOT" : "TPL_INSTANCE_RENDER_INSTANCE_NOT_FOUND");
                members.Add(instance);
                snapshots.Add((instance.SnapshotId, plan.Snapshot.ProjectionDigest, instance));
                if (instance.BatchId != request.BatchId || instance.StyleId != request.StyleId
                    || instance.RevisionId != request.RevisionId || plan.Revision.RevisionId != request.RevisionId)
                    throw Invalid("TPL_INSTANCE_BATCH_SCOPE_MISMATCH");

                ValidateFacts(plan, input.Facts, instance);
                var preparedSize = instance.Status == TemplateInstancePreflightStatus.RequiresProductionValidation
                    && plan.Production?.Size is not null;
                if (preparedSize)
                {
                    TemplateInstanceRenderPlanFactory.ValidateSizePreparation(plan, instance);
                    if (plan.Production!.Size!.BusinessReason is { } businessReason)
                    {
                        AddResourceClaims(plan, instance, resources);
                        diagnostics.Add(Diagnostic(businessReason, instance) with { Kind = TemplateInstanceBatchDiagnosticKind.BusinessBlock });
                        continue;
                    }
                }
                if (!preparedSize && (instance.Status != TemplateInstancePreflightStatus.AssetBindingsSatisfied
                    || !instance.BlockingReasons.IsEmpty))
                {
                    // ValidateFacts has matched every reason against frozen evidence. No exception is
                    // downgraded to a red page, and blocked members still participate in resource checks.
                    AddResourceClaims(plan, instance, resources);
                    diagnostics.AddRange(instance.BlockingReasons.Select(reason =>
                        Diagnostic(reason.Code, instance) with
                        {
                            FrameId = reason.FrameId, VariantId = reason.VariantId, AssetId = reason.AssetId,
                            Kind = reason.Code == "TPL_INSTANCE_SIZE_VALIDATION_REQUIRED"
                                ? TemplateInstanceBatchDiagnosticKind.ProductionValidationRequired
                                : TemplateInstanceBatchDiagnosticKind.BusinessBlock
                        }));
                    continue;
                }

                // Frame declarations are a map in the existing rule digest. A repository's row order
                // must not become RenderPlan target order. Never normalize the frozen snapshot body.
                var revision = plan.Revision;
                var canonicalRevision = TemplateRevision.Restore(revision.RevisionId, revision.GroupId, revision.RevisionNo,
                    revision.State, revision.ManifestRef, revision.RuleDigest, revision.CreatedAt,
                    revision.Pages.OrderBy(page => page.Order),
                    revision.FrameBindings.OrderBy(frame => frame.FrameId, StringComparer.Ordinal), revision.RuleWorkspace);
                var built = _instances.Build(plan with { Revision = canonicalRevision });
                AddResourceClaims(plan, instance, resources);
                using var document = JsonDocument.Parse(built.RenderPlanJson);
                var artifactSetId = document.RootElement.GetProperty("output").GetProperty("artifact_set_id").GetString()!;
                candidates.Add(new(instance.PageInstanceId, instance.Order, instance.SourcePageId, instance.RecipeId,
                    instance.RepetitionIndex, instance.SnapshotId, instance.RuleDigest, instance.InputFactsDigest,
                    plan.Snapshot.ProjectionDigest, built.PlanId, artifactSetId, built.PlanDigest, built.RenderPlanJson));
            }
            catch (TemplateRuleViolationException error)
            {
                diagnostics.Add(instance is null
                    ? new(error.Code, plan?.PageInstanceId) : Diagnostic(error.Code, instance));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or InvalidDataException)
            {
                // Malformed collections (e.g. duplicate asset keys) are not unhandled partial successes.
                diagnostics.Add(instance is null
                    ? new("TPL_INSTANCE_BATCH_INPUT_INVALID", plan?.PageInstanceId)
                    : Diagnostic("TPL_INSTANCE_BATCH_INPUT_INVALID", instance));
            }
        }

        // A snapshot freezes the whole expansion, including blocked members. Caller-side filtering
        // cannot make that batch look complete. No assets are inferred for absent member requests.
        var suppliedMembers = members.Select(item => (item.SnapshotId, item.PageInstanceId)).ToHashSet();
        foreach (var absent in bodies.Values.SelectMany(body => body.Instances)
                     .Where(item => !suppliedMembers.Contains((item.SnapshotId, item.PageInstanceId))))
            diagnostics.Add(Diagnostic("TPL_INSTANCE_BATCH_INSTANCE_SET_INCOMPLETE", absent));

        foreach (var group in members.GroupBy(item => item.PageInstanceId, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            diagnostics.AddRange(group.Select(item => Diagnostic("TPL_INSTANCE_BATCH_PAGE_INSTANCE_DUPLICATE", item)));
            if (group.Select(item => item.RuleDigest).Distinct(StringComparer.Ordinal).Skip(1).Any())
                diagnostics.AddRange(group.Select(item => Diagnostic("TPL_INSTANCE_BATCH_RULE_DIGEST_CONFLICT", item)));
            if (group.Select(item => item.InputFactsDigest).Distinct(StringComparer.Ordinal).Skip(1).Any())
                diagnostics.AddRange(group.Select(item => Diagnostic("TPL_INSTANCE_BATCH_INPUT_FACTS_CONFLICT", item)));
        }
        foreach (var group in members.GroupBy(item => (item.SourcePageId, item.RecipeId, item.RepetitionIndex))
                     .Where(group => group.Count() > 1))
        {
            // The E1 unique logical position is stricter than a page or an output-color key.
            if (group.Select(item => JsonSerializer.Serialize(item)).Distinct(StringComparer.Ordinal).Skip(1).Any())
                diagnostics.AddRange(group.Select(item => Diagnostic("TPL_INSTANCE_BATCH_LOGICAL_POSITION_CONFLICT", item)));
        }
        // One committed revision / snapshot identity cannot denote different bodies, even on disjoint members.
        foreach (var group in members.GroupBy(item => item.RevisionId, StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.RuleDigest).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            diagnostics.AddRange(group.Select(item => Diagnostic("TPL_INSTANCE_BATCH_RULE_DIGEST_CONFLICT", item)));
        foreach (var group in snapshots.GroupBy(item => item.Id, StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.Digest).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            diagnostics.AddRange(group.Select(item => Diagnostic("TPL_INSTANCE_BATCH_SNAPSHOT_CONFLICT", item.Instance)));
        if (bodies.Values.GroupBy(body => body.SnapshotId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            diagnostics.Add(new("TPL_INSTANCE_BATCH_SNAPSHOT_CONFLICT"));
        if (bodies.Values.GroupBy(body => body.RevisionId, StringComparer.Ordinal)
            .Any(group => group.Select(body => body.RuleDigest).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            diagnostics.Add(new("TPL_INSTANCE_BATCH_RULE_DIGEST_CONFLICT"));
        foreach (var group in resources.GroupBy(item => item.Id, StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.Digest).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            diagnostics.AddRange(group.Select(item => Diagnostic("TPL_INSTANCE_BATCH_RESOURCE_IDENTITY_COLLISION", item.Instance)));
        diagnostics.AddRange(TemplateInstanceRenderBatchCollisionGuard.Validate(candidates));
        return Finish(request, candidates, diagnostics, bodies.Values);
    }

    private static void ValidateSnapshotFacts(TemplateInstanceProjectionSnapshot snapshot, TemplateRevision revision,
        TemplateInstanceInputFacts facts)
    {
        if (revision.RevisionId != snapshot.RevisionId || revision.State != TemplateRevisionState.Committed || revision.RuleWorkspace?.Rules is null
            || revision.RuleWorkspace.Version < 1)
            throw Invalid("TPL_INSTANCE_REVISION_MISMATCH");
        TemplateRuleCodec.ValidateWorkspace(revision.RuleWorkspace);
        if (revision.RuleDigest != snapshot.RuleDigest || TemplateRuleCodec.BusinessDigest(revision) != snapshot.RuleDigest)
            throw Invalid("TPL_INSTANCE_RULE_DIGEST_MISMATCH");
        if (TemplateInstanceProjector.ValidateAndComputeInputDigest(facts) != snapshot.InputFactsDigest)
            throw Invalid("TPL_INSTANCE_INPUT_FACTS_DIGEST_MISMATCH");
    }

    private static void ValidateFacts(TemplateInstanceRenderPlanRequest plan, TemplateInstanceInputFacts facts,
        TemplateInstanceProjection instance)
    {
        ValidateSnapshotFacts(plan.Snapshot, plan.Revision, facts);
        var revision = plan.Revision;
        var page = revision.Pages.SingleOrDefault(item => item.PageId == instance.SourcePageId);
        if (page is null || page.TemplateResourceId != instance.TemplateResourceId
            || instance.TemplateResourceId != plan.TemplateResource.ResourceId
            || !TemplateInstanceRenderPlanFactory.HasValidResourceOrigin(plan))
            throw Invalid("TPL_INSTANCE_RENDER_TEMPLATE_MISMATCH");
        var bindings = revision.FrameBindings.Where(item => item.PageId == instance.SourcePageId).ToArray();
        if (instance.Frames.Length != bindings.Length
            || page.ContentType == TemplatePageContentType.NormalComposition && bindings.Length == 0
            || instance.Frames.Select(frame => frame.FrameId).Distinct(StringComparer.Ordinal).Count() != instance.Frames.Length
            || bindings.Any(binding => !instance.Frames.Any(frame => frame.FrameId == binding.FrameId)))
            throw Invalid("TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");

        // Compare the instance-local metadata with the full frozen preimage, excluding paths exactly as E0 does.
        var assets = facts.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        var variants = facts.Variants.ToDictionary(item => item.VariantId, StringComparer.Ordinal);
        var selections = facts.Selections.ToDictionary(item => (item.FrameId, item.VariantId));
        var overrides = facts.Overrides.ToDictionary(item => (item.SourcePageId, item.InstanceKey, item.FrameId));
        if (instance.Slots.Select(slot => slot.SlotId).Distinct(StringComparer.Ordinal).Count() != instance.Slots.Length)
            throw Invalid("TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");
        if (plan.Assets.Any(asset => asset is null || !assets.ContainsKey(asset.AssetId))
            || plan.Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count() != plan.Assets.Count)
            throw Invalid("TPL_INSTANCE_RENDER_ASSET_INVALID");
        var supplied = plan.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        if (assets.Values.Any(asset => asset.StyleId != instance.StyleId))
            throw Invalid("TPL_INSTANCE_RENDER_ASSET_INVALID");
        var substituted = facts with
        { Assets = [.. facts.Assets.Select(asset => supplied.GetValueOrDefault(asset.AssetId, asset))] };
        if (TemplateInstanceProjector.ValidateAndComputeInputDigest(substituted) != instance.InputFactsDigest)
            throw Invalid("TPL_INSTANCE_INPUT_FACTS_DIGEST_MISMATCH");

        // A red label alone is not evidence. Verify null bindings/reasons as well as successful
        // bindings, without expanding recipes, changing IDs, selecting assets or repairing the body.
        var verifiedReasons = new List<TemplateInstanceBlockReason>();
        foreach (var frame in instance.Frames)
        {
            if (!variants.TryGetValue(frame.VariantId, out var variant)
                || instance.Slots.SingleOrDefault(slot => slot.SlotId == frame.SlotId)?.VariantId != frame.VariantId)
                throw Invalid("TPL_INSTANCE_ASSET_VARIANT_MISMATCH");
            overrides.TryGetValue((instance.SourcePageId,
                new(instance.BranchId, instance.RecipeId, instance.RepetitionIndex), frame.FrameId), out var manual);
            var selected = manual?.AssetId ?? selections.GetValueOrDefault((frame.FrameId, frame.VariantId))?.AssetId;
            if (frame.SelectionRevision != (manual?.SelectionRevision ?? 0))
                throw Invalid("TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");
            if (selected is not null && !assets.ContainsKey(selected))
                throw Invalid("TPL_INSTANCE_RENDER_ASSET_INVALID");
            string? reason = selected is null ? "TPL_INSTANCE_SELECTION_MISSING"
                : !variant.AssetIds.Contains(selected, StringComparer.Ordinal) ? "TPL_INSTANCE_ASSET_VARIANT_MISMATCH"
                : assets[selected].Readability != AssetReadability.Readable ? "TPL_INSTANCE_ASSET_UNREADABLE" : null;
            if (frame.AssetId is not null)
            {
                if (reason is not null || selected != frame.AssetId || !supplied.ContainsKey(frame.AssetId))
                    throw Invalid("TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");
            }
            else
            {
                if (reason is null) throw Invalid("TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");
                verifiedReasons.Add(new(reason, frame.FrameId, frame.VariantId, selected));
            }
        }
        if (page.ContentType == TemplatePageContentType.DynamicSizeTable)
            verifiedReasons.Add(new("TPL_INSTANCE_SIZE_VALIDATION_REQUIRED"));
        if (instance.BlockingReasons.Any(reason => reason.Code is not ("TPL_INSTANCE_SELECTION_MISSING"
                or "TPL_INSTANCE_ASSET_VARIANT_MISMATCH" or "TPL_INSTANCE_ASSET_UNREADABLE" or "TPL_INSTANCE_SIZE_VALIDATION_REQUIRED")))
            throw Invalid("TPL_INSTANCE_BATCH_BLOCK_REASON_UNCLASSIFIED");
        if (instance.BlockingReasons.Length != verifiedReasons.Count
            || !instance.BlockingReasons.ToHashSet().SetEquals(verifiedReasons))
            throw Invalid("TPL_INSTANCE_BATCH_BLOCK_REASON_MISMATCH");
        var expectedStatus = verifiedReasons.Count == 0 ? TemplateInstancePreflightStatus.AssetBindingsSatisfied
            : verifiedReasons.Any(reason => reason.Code != "TPL_INSTANCE_SIZE_VALIDATION_REQUIRED")
                ? TemplateInstancePreflightStatus.BusinessBlocked : TemplateInstancePreflightStatus.RequiresProductionValidation;
        if (instance.Status != expectedStatus) throw Invalid("TPL_INSTANCE_BATCH_BLOCK_REASON_MISMATCH");

        var resource = plan.TemplateResource;
        if (resource.GroupId != revision.GroupId || resource.Inspection is null
            || resource.Length <= 0 || resource.Sha256 is not { Length: 64 } || !resource.Sha256.All(Uri.IsHexDigit)
            || resource.Length != resource.Inspection.SizeBytes
            || !string.Equals(resource.Sha256, resource.Inspection.FileSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resource.StructureSignature, resource.Inspection.StructureSignature, StringComparison.OrdinalIgnoreCase))
            throw Invalid("TPL_INSTANCE_RENDER_TEMPLATE_MISMATCH");
        if (resource.Inspection.Canvas is null || resource.Inspection.Canvas.Width <= 0 || resource.Inspection.Canvas.Height <= 0
            || bindings.Any(binding => binding.LayoutIntent.SafeBounds.Right > resource.Inspection.Canvas.Width
                || binding.LayoutIntent.SafeBounds.Bottom > resource.Inspection.Canvas.Height))
            throw Invalid("TPL_RENDER_PLAN_SCHEMA_REJECTED");
    }

    private static void AddResourceClaims(TemplateInstanceRenderPlanRequest plan, TemplateInstanceProjection instance,
        List<(string Id, string Digest, TemplateInstanceProjection Instance)> claims)
    {
        var resource = plan.TemplateResource;
        claims.Add((resource.ResourceId, TemplateRuleCodec.Hash(JsonSerializer.Serialize(new
        {
            role = "template_copy", resource.GroupId, resource.DraftId,
            sha256 = resource.Sha256.ToLowerInvariant(), resource.Length,
            resource.StructureSignature, resource.Inspection.Canvas
        })), instance));
        foreach (var asset in plan.Assets.Where(asset => instance.Frames.Any(frame => frame.AssetId == asset.AssetId)))
            claims.Add((asset.AssetId, TemplateRuleCodec.Hash(JsonSerializer.Serialize(new
            {
                role = "high_resolution_render_source", asset.StyleId,
                sha256 = asset.Sha256.ToLowerInvariant(), asset.Length, asset.PixelWidth, asset.PixelHeight,
                asset.Format, asset.MimeType, asset.Readability
            })), instance));
        if (plan.Production?.Size?.BasePlanJson is { } sizeJson)
        {
            using var size = JsonDocument.Parse(sizeJson);
            foreach (var item in size.RootElement.GetProperty("resources").EnumerateArray()
                .Where(item => item.GetProperty("role").GetString() != "template_copy"))
                claims.Add((item.GetProperty("resource_id").GetString()!, TemplateRuleCodec.Hash(item.GetRawText()), instance));
        }
    }

    private static TemplateInstanceRenderBatchResult Finish(TemplateInstanceRenderBatchRequest request,
        IEnumerable<TemplateInstanceBatchPlan> candidates, IEnumerable<TemplateInstanceBatchDiagnostic> diagnostics,
        IEnumerable<TemplateInstanceProjectionSnapshot> snapshots)
    {
        var errors = diagnostics.Distinct().OrderBy(item => item.PageInstanceId, StringComparer.Ordinal)
            .ThenBy(item => item.SourcePageId, StringComparer.Ordinal).ThenBy(item => item.RecipeId, StringComparer.Ordinal)
            .ThenBy(item => item.RepetitionIndex).ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.FrameId, StringComparer.Ordinal).ThenBy(item => item.VariantId, StringComparer.Ordinal)
            .ThenBy(item => item.AssetId, StringComparer.Ordinal).ToImmutableArray();
        var rejected = errors.Any(item => item.Kind is not (TemplateInstanceBatchDiagnosticKind.BusinessBlock
            or TemplateInstanceBatchDiagnosticKind.ProductionValidationRequired or TemplateInstanceBatchDiagnosticKind.Information));
        var productionRequired = errors.Any(item => item.Kind == TemplateInstanceBatchDiagnosticKind.ProductionValidationRequired);
        var ordered = (rejected || productionRequired ? [] : candidates)
            .OrderBy(item => item.StableOrder).ThenBy(item => item.SourcePageId, StringComparer.Ordinal)
            .ThenBy(item => item.RecipeId, StringComparer.Ordinal).ThenBy(item => item.RepetitionIndex)
            .ThenBy(item => item.PageInstanceId, StringComparer.Ordinal).ToImmutableArray();
        var byId = ordered.ToDictionary(plan => plan.PageInstanceId, StringComparer.Ordinal);
        var bodies = snapshots.OrderBy(body => body.SnapshotId, StringComparer.Ordinal)
            .ThenBy(body => body.ProjectionDigest, StringComparer.Ordinal).ToArray();
        var members = bodies.SelectMany(body => body.Instances.Select(instance =>
        {
            var reasons = errors.Where(error => error.PageInstanceId == instance.PageInstanceId).ToImmutableArray();
            var status = reasons.Any(reason => reason.Kind == TemplateInstanceBatchDiagnosticKind.IntegrityError)
                ? TemplateInstanceBatchMemberStatus.Rejected
                : reasons.Any(reason => reason.Kind == TemplateInstanceBatchDiagnosticKind.ProductionValidationRequired)
                    ? TemplateInstanceBatchMemberStatus.RequiresProductionValidation
                    : reasons.Any(reason => reason.Kind == TemplateInstanceBatchDiagnosticKind.BusinessBlock)
                        ? TemplateInstanceBatchMemberStatus.BusinessBlocked
                        : rejected || productionRequired ? TemplateInstanceBatchMemberStatus.Withheld : TemplateInstanceBatchMemberStatus.Plannable;
            byId.TryGetValue(instance.PageInstanceId, out var plan);
            return new TemplateInstanceBatchMember(instance.PageInstanceId, instance.Order, instance.SourcePageId,
                instance.RecipeId, instance.RepetitionIndex, instance.SnapshotId, instance.RuleDigest, instance.InputFactsDigest,
                body.ProjectionDigest, status, reasons, plan?.PlanId, plan?.ArtifactSetId, plan?.PlanDigest);
        })).OrderBy(item => item.StableOrder).ThenBy(item => item.SourcePageId, StringComparer.Ordinal)
            .ThenBy(item => item.RecipeId, StringComparer.Ordinal).ThenBy(item => item.RepetitionIndex)
            .ThenBy(item => item.PageInstanceId, StringComparer.Ordinal).ThenBy(item => item.SnapshotId, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectionDigest, StringComparer.Ordinal).ToImmutableArray();
        var batchStatus = rejected ? TemplateInstanceRenderBatchStatus.Blocked
            : productionRequired ? TemplateInstanceRenderBatchStatus.ProductionValidationRequired
            : members.IsEmpty ? TemplateInstanceRenderBatchStatus.Empty
            : ordered.IsEmpty ? TemplateInstanceRenderBatchStatus.UserActionRequired
            : members.Any(member => member.Status == TemplateInstanceBatchMemberStatus.BusinessBlocked)
                ? TemplateInstanceRenderBatchStatus.PartiallyReady : TemplateInstanceRenderBatchStatus.Ready;
        if (rejected || productionRequired || bodies.Length == 0)
            return new(TemplateInstanceRenderBatchResult.CurrentSchemaVersion, request.BatchId, request.StyleId, request.RevisionId,
                batchStatus, null, null, ordered, errors, members, !rejected && bodies.Length > 0);

        var factsDigest = TemplateRuleCodec.Hash(JsonSerializer.Serialize(new
        {
            schema = TemplateInstanceRenderBatchResult.InputDigestSchemaVersion, request.BatchId, request.StyleId, request.RevisionId,
            snapshots = bodies.Select(body => new { body.SnapshotId, body.InputFactsDigest }),
            instances = members.Select(item => new { item.PageInstanceId, item.InputFactsDigest })
        }));
        // Preserve the E2 v2 preimage byte-for-byte when no production preparation exists.
        var schemaVersion = request.Instances.Any(input => input.PlanRequest.Production is not null)
            ? "template-instance-render-batch/v3" : TemplateInstanceRenderBatchResult.CurrentSchemaVersion;
        var batchDigest = TemplateRuleCodec.Hash(JsonSerializer.Serialize(new
        {
            schema = schemaVersion, request.BatchId, request.StyleId, request.RevisionId,
            inputFactsDigest = factsDigest,
            status = batchStatus.ToString(),
            snapshots = bodies.Select(body => new { body.SnapshotId, body.RuleDigest, body.InputFactsDigest, body.ProjectionDigest }),
            // A non-plannable member still binds its supplied template/output parameters. Never hash paths or inspection timing.
            parameters = request.Instances.OrderBy(input => input.PlanRequest.PageInstanceId, StringComparer.Ordinal).Select(input =>
            {
                var parameters = JsonSerializer.SerializeToNode(new
                {
                    input.PlanRequest.PageInstanceId, input.PlanRequest.IncludeEditablePsd,
                    input.PlanRequest.TemplateResource.ResourceId, input.PlanRequest.TemplateResource.Sha256,
                    input.PlanRequest.TemplateResource.Length, input.PlanRequest.TemplateResource.StructureSignature,
                    input.PlanRequest.TemplateResource.Inspection.Canvas
                })!;
                if (input.PlanRequest.Production is not null)
                    parameters["Production"] = JsonSerializer.SerializeToNode(input.PlanRequest.Production);
                return parameters;
            }),
            members
        }));
        return new(schemaVersion, request.BatchId, request.StyleId, request.RevisionId,
            batchStatus, batchDigest, factsDigest, ordered, errors, members, true);
    }

    private static TemplateInstanceBatchDiagnostic Diagnostic(string code, TemplateInstanceProjection instance) =>
        new(code, instance.PageInstanceId, instance.SourcePageId, instance.RecipeId, instance.RepetitionIndex);

    private static TemplateRuleViolationException Invalid(string code) => new(code);
}
