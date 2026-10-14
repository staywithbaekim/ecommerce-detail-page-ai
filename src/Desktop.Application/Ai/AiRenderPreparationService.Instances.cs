using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Ai;

public sealed partial class AiRenderPreparationService
{
    private async Task<AiRenderPlanPreparation> PrepareInstancesAsync(AiRenderPreparationSnapshot source, CancellationToken token)
    {
        if (_productionEvidence is null || _instanceSnapshots is null || _revisions is null || _planValidator is null)
            throw new InvalidOperationException("TPL_INSTANCE_PRODUCTION_NOT_CONFIGURED");
        var batch = source.Batch;
        var context = (BatchRuleTemplateContext)batch.TemplateSnapshot;
        var revision = source.TemplateRevision;
        if (batch.ScanStatus != BatchScanStatus.ScanReady || batch.ManifestDigest is null
            || context.BatchId != batch.BatchId || context.RevisionId != revision.RevisionId
            || revision.State != TemplateRevisionState.Committed || context.RulesDigest != revision.RuleDigest
            || context.BusinessRuleDigest != TemplateRuleCodec.BusinessDigest(revision))
            throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_CONTEXT_MISMATCH");

        var pages = new List<AiRenderPagePreparation>();
        var sets = new List<RenderBatchInstanceSet>();
        foreach (var style in batch.Styles.OrderBy(style => style.Order))
        {
            var evidence = await _productionEvidence.ReadProductionEvidenceAsync(batch.BatchId, style.StyleId, token).ConfigureAwait(false);
            if (evidence is null)
            {
                // An orphaned E1 body cannot be silently adopted from guessed current AI facts.
                if (await _instanceSnapshots.ReadAsync(batch.BatchId, style.StyleId, token).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_EVIDENCE_MISSING");
                evidence = await CollectInstanceEvidenceAsync(source, context, style, token).ConfigureAwait(false);
                await _productionEvidence.FreezeProductionEvidenceAsync(evidence, token).ConfigureAwait(false);
            }
            if (evidence.InputManifestDigest != batch.ManifestDigest || evidence.Snapshot.SnapshotId != context.SnapshotId
                || evidence.Snapshot.RuleDigest != context.BusinessRuleDigest || evidence.Snapshot.RevisionId != context.RevisionId
                || !evidence.Facts().Assets.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).SequenceEqual(
                    batch.Assets.Where(asset => asset.StyleId == style.StyleId).OrderBy(asset => asset.AssetId, StringComparer.Ordinal)))
                throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_CONTEXT_MISMATCH");

            // Evidence-first freeze is restartable: a crash before E1 repeats the same command/body.
            var frozen = evidence.Snapshot;
            await _instanceSnapshots.FreezeAsync(new("production_" + frozen.ProjectionDigest[7..], 0, frozen), token).ConfigureAwait(false);
            var inputs = new List<TemplateInstanceRenderBatchInput>();
            foreach (var instance in frozen.Instances)
            {
                var template = await _templateResources.GetByResourceIdAsync(instance.TemplateResourceId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("TPL_INSTANCE_TEMPLATE_RESOURCE_MISSING");
                var origin = template.DraftId == revision.RevisionId ? null
                    : await _revisions.GetRevisionAsync(template.DraftId, token).ConfigureAwait(false);
                inputs.Add(new(evidence.PlanRequest(instance, revision, template, origin, batch.IncludeEditablePsd), evidence.Facts()));
            }
            var planned = new TemplateInstanceRenderBatchPlanner(_planValidator).Build(new(batch.BatchId, style.StyleId,
                revision.RevisionId, inputs, frozen.Instances.IsEmpty ? [new(frozen, revision, evidence.Facts())] : null));
            if (planned.Status is TemplateInstanceRenderBatchStatus.Blocked or TemplateInstanceRenderBatchStatus.ProductionValidationRequired)
                throw new InvalidDataException(planned.Diagnostics.FirstOrDefault()?.Code ?? "TPL_INSTANCE_PRODUCTION_BLOCKED");
            sets.Add(new(style.StyleId, planned.SchemaVersion, planned.BatchPlanDigest!, planned.InputFactsDigest!));
            foreach (var member in planned.Members)
            {
                var plan = planned.Plans.SingleOrDefault(plan => plan.PageInstanceId == member.PageInstanceId);
                var instance = frozen.Instances.Single(instance => instance.PageInstanceId == member.PageInstanceId);
                var identity = new RenderInstanceIdentity(1, batch.BatchId, style.StyleId, member.PageInstanceId,
                    member.SourcePageId, frozen.SnapshotId, frozen.RevisionId, frozen.RuleDigest, frozen.InputFactsDigest, frozen.ProjectionDigest);
                pages.Add(new(style.StyleId, member.SourcePageId, plan is null ? AiRenderPagePreparationStatus.BusinessBlocked
                    : AiRenderPagePreparationStatus.ReadyToRender, plan is null ? member.Diagnostics[0].Code : "AI_RENDER_PLAN_VALID",
                    plan?.RenderPlanJson, plan?.PlanDigest, OutputVariantName: "实例 " + (instance.Order + 1).ToString(CultureInfo.InvariantCulture),
                    Instance: identity));
            }
        }
        return new(batch.BatchId, pages.Any(page => page.HasPlan) ? AiRenderPreparationStatus.WaitingForPhotoshop
            : AiRenderPreparationStatus.UserActionRequired, pages.Count == 0 ? "TPL_INSTANCE_EMPTY" : "TPL_INSTANCE_PREPARATION_CLOSED", pages,
            UsesInstancePipeline: true, InstanceSets: sets.ToArray());
    }

    internal async Task<TemplateInstanceSelectionReview?> ReviewInstanceSelectionAsync(AiRenderPreparationSnapshot source,
        string styleId, string frameId, string assetId, CancellationToken token)
    {
        if (source.Batch.TemplateSnapshot is not BatchRuleTemplateContext context) return null;
        var style = source.Batch.Styles.SingleOrDefault(style => style.StyleId == styleId)
            ?? throw new InvalidOperationException("TPL_INSTANCE_SELECTION_SCOPE_INVALID");
        var evidence = await LoadSelectionEvidenceAsync(source, context, style, token).ConfigureAwait(false);
        var targets = evidence.Snapshot.Instances.Where(instance => instance.Frames.Any(frame => frame.FrameId == frameId
                && evidence.Variants.Single(variant => variant.VariantId == frame.VariantId).AssetIds.Contains(assetId, StringComparer.Ordinal)))
            .Select(instance =>
            {
                var frame = instance.Frames.Single(frame => frame.FrameId == frameId);
                var page = source.TemplateRevision.Pages.Single(page => page.PageId == instance.SourcePageId);
                return new TemplateInstanceSelectionTarget(instance.PageInstanceId,
                    $"{page.Name} · 第 {instance.Order + 1} 个输出实例",
                    frame.AssetId, frame.SelectionRevision);
            }).ToArray();
        return new(source.Batch.BatchId, styleId, frameId, assetId, evidence.SourceVersion!.ManualRevision,
            evidence.SourceVersion.BaseFactsDigest, targets);
    }

    internal async Task LockInstanceSelectionAsync(AiRenderPreparationSnapshot source, TemplateInstanceSelectionCommand command,
        CancellationToken token)
    {
        var review = await ReviewInstanceSelectionAsync(source, command.StyleId, command.FrameId, command.AssetId, token).ConfigureAwait(false);
        if (review is null || command.BatchId != source.Batch.BatchId || review.BaseFactsDigest != command.BaseFactsDigest
            || review.Revision != command.ExpectedRevision || !review.Targets.Any(target => target.PageInstanceId == command.PageInstanceId))
            throw new InvalidOperationException("TPL_INSTANCE_SELECTION_REVISION_CONFLICT");
        var evidence = await LoadSelectionEvidenceAsync(source, (BatchRuleTemplateContext)source.Batch.TemplateSnapshot,
            source.Batch.Styles.Single(style => style.StyleId == command.StyleId), token).ConfigureAwait(false);
        var instance = evidence.Snapshot.Instances.Single(instance => instance.PageInstanceId == command.PageInstanceId);
        var previous = await _productionEvidence!.ReadManualSelectionsAsync(command.BatchId, command.StyleId, token).ConfigureAwait(false);
        var key = new TemplateRuleInstanceKey(instance.BranchId, instance.RecipeId, instance.RepetitionIndex);
        var selections = (previous?.Overrides ?? []).Where(item => !(item.SourcePageId == instance.SourcePageId
            && item.InstanceKey == key && item.FrameId == command.FrameId)).Append(new(instance.SourcePageId, key,
                command.FrameId, command.AssetId, checked(command.ExpectedRevision + 1))).ToImmutableArray();
        await _productionEvidence.SaveManualSelectionsAsync(command.BatchId, command.StyleId,
            new(1, checked(command.ExpectedRevision + 1), command.BaseFactsDigest, selections), command.ExpectedRevision, token).ConfigureAwait(false);
    }

    private async Task<TemplateInstanceProductionEvidence> LoadSelectionEvidenceAsync(
        AiRenderPreparationSnapshot source, BatchRuleTemplateContext context, StyleDraft style, CancellationToken token)
    {
        var frozen = await _productionEvidence!.ReadProductionEvidenceAsync(source.Batch.BatchId, style.StyleId, token)
            .ConfigureAwait(false);
        if (frozen is null)
        {
            if (await _instanceSnapshots!.ReadAsync(source.Batch.BatchId, style.StyleId, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("TPL_INSTANCE_PRODUCTION_FACTS_FROZEN");
            return await CollectInstanceEvidenceAsync(source, context, style, token, prepareSize: false).ConfigureAwait(false);
        }
        if (frozen.Snapshot.SnapshotId != context.SnapshotId || frozen.Snapshot.RevisionId != context.RevisionId
            || frozen.Snapshot.RuleDigest != context.BusinessRuleDigest || frozen.InputManifestDigest != source.Batch.ManifestDigest)
            throw new InvalidDataException("TPL_INSTANCE_PRODUCTION_CONTEXT_MISMATCH");
        return frozen;
    }

    private async Task RequireInstanceEditingAsync(string batchId, string styleId, CancellationToken token)
    {
        if (_productionEvidence is null || _instanceSnapshots is null) throw new InvalidOperationException("TPL_INSTANCE_PRODUCTION_NOT_CONFIGURED");
        if (await _productionEvidence.ReadProductionEvidenceAsync(batchId, styleId, token).ConfigureAwait(false) is not null
            || await _instanceSnapshots.ReadAsync(batchId, styleId, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("TPL_INSTANCE_PRODUCTION_FACTS_FROZEN");
    }

    private async Task<TemplateInstanceProductionEvidence> CollectInstanceEvidenceAsync(AiRenderPreparationSnapshot source,
        BatchRuleTemplateContext context, StyleDraft style, CancellationToken token, bool prepareSize = true)
    {
        var inputs = await _resultReader.LoadStyleResultsAsync(source.Requests, style.StyleId, token).ConfigureAwait(false);
        if (inputs.Any(input => input.Failure != AiAnalysisResultReadFailure.None))
            throw new InvalidDataException("TPL_INSTANCE_AI_EVIDENCE_INVALID");
        var joint = inputs.SingleOrDefault(input => input.Request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding);
        if (joint?.Result is null) throw new InvalidDataException("TPL_INSTANCE_AI_EVIDENCE_MISSING");
        var session = await _resultReader.LoadSessionAsync("session-" + joint.Request.RequestId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("TPL_INSTANCE_SESSION_MISSING");
        if (session.BatchId != source.Batch.BatchId || session.StyleId != style.StyleId || session.TemplateSnapshotId != context.SnapshotId
            || joint.Request.BatchId != source.Batch.BatchId || joint.Request.StyleId != style.StyleId)
            throw new InvalidDataException("TPL_INSTANCE_AI_SCOPE_MISMATCH");
        var variantPlan = AiVariantPlanner.Plan(joint.Result.VariantGroups, joint.Request.Requirements, joint.Result.RequirementMatches);
        var groups = joint.Result.VariantGroups.Where(group => group.Dimension == AiVariantPlanner.ColorDimension).ToArray();
        if (!variantPlan.HasVariants || groups.Select(group => group.VariantId).Distinct(StringComparer.Ordinal).Count() != groups.Length)
            throw new InvalidDataException("TPL_INSTANCE_COLOR_FACTS_UNRESOLVED");
        var selections = ImmutableArray.CreateBuilder<TemplateInstanceFrameSelection>();
        foreach (var variant in variantPlan.Variants)
        foreach (var binding in source.TemplateRevision.ActiveFrameBindings)
        {
            var group = groups.Single(group => group.VariantId == variant.VariantId);
            var matches = joint.Result.RequirementMatches.Where(match => match.RequirementId == binding.FrameId
                && group.AssetIds.Contains(match.AssetId, StringComparer.Ordinal)).OrderBy(match => match.Rank)
                .ThenBy(match => match.AssetId, StringComparer.Ordinal).ToArray();
            var assessment = AiTargetMatchPolicy.Assess(binding, matches, false);
            if (assessment == AiTargetRequirementAssessment.InvalidResult) throw new InvalidDataException("TPL_INSTANCE_AI_EVIDENCE_INVALID");
            var top = matches.FirstOrDefault();
            var isUsableLowConfidenceFit = assessment == AiTargetRequirementAssessment.ReviewRequired
                && top is not null
                && !top.EvidenceCodes.Contains(AiTargetMatchEvidence.Unsafe, StringComparer.Ordinal)
                && (!AiAnalysisInputProjector.IsLearnedReferenceBinding(binding)
                    || top.EvidenceCodes.Any(code => code is AiTargetMatchEvidence.Exact or AiTargetMatchEvidence.SafeSimilar));
            if ((assessment == AiTargetRequirementAssessment.Ready || isUsableLowConfidenceFit) && top is not null)
                selections.Add(new(binding.FrameId, variant.VariantId, top.AssetId));
        }
        var facts = new TemplateInstanceInputFacts(1,
            new(variantPlan.Variants.Select(variant => variant.VariantId).ToImmutableArray(), variantPlan.PrimaryVariantId, true),
            source.Batch.Assets.Where(asset => asset.StyleId == style.StyleId).ToImmutableArray(),
            groups.Select(group => new TemplateInstanceVariantAssets(group.VariantId, group.AssetIds.ToImmutableArray())).ToImmutableArray(),
            selections.ToImmutable(), []);
        var baseFactsDigest = TemplateInstanceProjector.ValidateAndComputeInputDigest(facts);
        var manual = await _productionEvidence!.ReadManualSelectionsAsync(source.Batch.BatchId, style.StyleId, token).ConfigureAwait(false);
        if (manual is not null && manual.BaseFactsDigest != baseFactsDigest)
            throw new InvalidOperationException("TPL_INSTANCE_SELECTION_FACTS_CHANGED");
        var scope = new TemplateInstanceScope(source.Batch.BatchId, style.StyleId, context.SnapshotId,
            context.RevisionId, context.BusinessRuleDigest);
        var first = TemplateInstanceProjector.Project(new(scope, source.TemplateRevision, facts));
        var overrides = ImmutableArray.CreateBuilder<TemplateInstanceSelectionOverride>();
        foreach (var selection in session.LockedSelections.Values.Where(selection =>
            source.TemplateRevision.ActiveFrameBindings.Any(frame => frame.FrameId == selection.RequirementId)))
        {
            var targets = first.Instances.Where(instance => instance.Frames.Any(frame => frame.FrameId == selection.RequirementId
                && facts.Variants.Single(variant => variant.VariantId == frame.VariantId).AssetIds.Contains(selection.AssetId, StringComparer.Ordinal))).ToArray();
            // Legacy frame-wide locks cannot silently become one shared lock on multiple instances.
            if (targets.Length != 1)
            {
                if (!prepareSize) continue; // Preview exposes explicit targets; never silently propagates the legacy lock.
                if (targets.Length == 0 || targets.Any(target => !(manual?.Overrides ?? []).Any(item =>
                    item.SourcePageId == target.SourcePageId && item.InstanceKey == new TemplateRuleInstanceKey(target.BranchId,
                        target.RecipeId, target.RepetitionIndex) && item.FrameId == selection.RequirementId)))
                    throw new InvalidOperationException("TPL_INSTANCE_MANUAL_SCOPE_AMBIGUOUS");
                continue;
            }
            var target = targets[0];
            overrides.Add(new(target.SourcePageId, new(target.BranchId, target.RecipeId, target.RepetitionIndex),
                selection.RequirementId, selection.AssetId, selection.SelectionRevision));
        }
        foreach (var selection in manual?.Overrides ?? [])
        {
            // An explicit instance lock supersedes only its own legacy unambiguous lock.
            for (var i = overrides.Count - 1; i >= 0; i--)
                if (overrides[i].SourcePageId == selection.SourcePageId && overrides[i].InstanceKey == selection.InstanceKey
                    && overrides[i].FrameId == selection.FrameId) overrides.RemoveAt(i);
            overrides.Add(selection);
        }
        facts = facts with { Overrides = overrides.ToImmutable() };
        var request = new TemplateInstanceProjectionRequest(scope, source.TemplateRevision, facts);
        var projection = TemplateInstanceProjector.Project(request);
        var frozen = TemplateInstanceProjectionSnapshotCodec.Freeze(request, projection);
        var sizePages = ImmutableArray.CreateBuilder<TemplateInstanceSizePreparation>();
        var sizeInputs = inputs.Where(input => input.Result is not null && input.Request.AnalysisKind is
            AiAnalysisKind.ProductJointUnderstanding or AiAnalysisKind.SizeTableExtraction).ToArray();
        var candidates = sizeInputs.Where(input => input.Result!.SizeTableCandidate is not null)
            .Select(input => input.Result!.SizeTableCandidate!).GroupBy(candidate => JsonSerializer.Serialize(new
            { candidate.SourceAssetId, candidate.Columns, candidate.Rows }), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First()).ToArray();
        var conflict = sizeInputs.SelectMany(input => input.Result!.Warnings).Any(warning => warning.Code == "size_source_conflict");
        foreach (var sizePage in source.TemplateRevision.Pages.Where(page => prepareSize && page.ContentType == TemplatePageContentType.DynamicSizeTable))
        {
            // The SIZE service keeps per-page calibration and manual table revisions. Do not spread
            // one page's blocking reason over another page, or mistake adapter failure for business red.
            var result = await PrepareSizeTablesAsync(source, style, candidates, conflict, session,
                source.Batch.UpdatedAt, token, sizePage.PageId).ConfigureAwait(false);
            var prepared = result.Pages.SingleOrDefault();
            if (prepared is null || prepared.CalibratedTarget is null && prepared.CalibratedInsertion is null)
            {
                var reason = prepared is null ? result.ReasonCode : "SIZE_TABLE_RENDER_TARGET_REQUIRED";
                if (!TemplateInstanceRenderPlanFactory.IsSizeBusinessReason(reason)) throw new InvalidDataException(reason);
                sizePages.Add(new(sizePage.PageId, null, null, reason));
                continue;
            }
            var raster = prepared.RasterizedResource;
            var expectedPath = $"batches/{source.Batch.BatchId}/size-tables/{style.StyleId}/{sizePage.PageId}/resources/{raster.ResourceId}.png";
            if (raster.OwnedRelativePath != expectedPath || raster.TableDataDigest != prepared.Table.DataDigest)
                throw new InvalidDataException("TPL_INSTANCE_SIZE_RESOURCE_PATH_INVALID");
            var template = await _templateResources.GetByResourceIdAsync(sizePage.TemplateResourceId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("TPL_INSTANCE_TEMPLATE_RESOURCE_MISSING");
            var key = TemplateRuleCodec.Hash(JsonSerializer.Serialize(new { scope, sizePage.PageId, prepared.Table.DataDigest,
                raster.ResourceId, raster.Sha256, templateSha256 = template.Sha256 }))[7..];
            var basePlan = new SizeTableRenderPlanBuilder(_planValidator!).Build(source.TemplateRevision, sizePage, template,
                new("size_" + key[..24], "render/size/" + key[..48], "corr_" + key[24..48], source.Batch.BatchId, style.StyleId,
                    "artifact_" + key[40..64], "尺码表.jpg", source.Batch.IncludeEditablePsd,
                    source.Batch.IncludeEditablePsd ? "尺码表.psd" : null, DateTimeOffset.UnixEpoch, prepared.Table, raster,
                    prepared.CalibratedTarget, prepared.CalibratedInsertion));
            sizePages.Add(new(sizePage.PageId, SizeTableRepairData.Serialize(prepared.Table), basePlan, null));
        }
        var geometry = inputs.Where(input => input.Result is not null && input.Request.AnalysisKind is
                AiAnalysisKind.ProductJointUnderstanding or AiAnalysisKind.SelectedAssetGeometry)
            .SelectMany(input => input.Result!.SubjectGeometry).GroupBy(item => item.AssetId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .Select(item => new TemplateRenderSubjectGeometry(item.AssetId, item.Left, item.Top, item.Right, item.Bottom, item.Confidence))
            .OrderBy(item => item.AssetResourceId, StringComparer.Ordinal).ToImmutableArray();
        return new(1, source.Batch.ManifestDigest!, frozen, facts.Colors,
            ProjectManifest(source.Batch).Assets.Where(asset => asset.StyleId == style.StyleId).ToImmutableArray(),
            facts.Variants, facts.Selections, facts.Overrides, geometry,
            TemplateRuleCodec.Hash(JsonSerializer.Serialize(new { requests = inputs.Select(input => new
            { input.Request.RequestId, input.Ledger?.Fingerprint, input.Ledger?.ResultReference }),
                session.SelectionRevision, locks = session.LockedSelections.OrderBy(item => item.Key, StringComparer.Ordinal) })), sizePages.ToImmutable(),
            new(session.SessionId, session.SelectionRevision, manual?.Revision ?? 0, baseFactsDigest));
    }
}
