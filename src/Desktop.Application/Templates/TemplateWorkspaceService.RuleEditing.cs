using System.Collections.Immutable;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed partial class TemplateWorkspaceService
{
    public async Task<IReadOnlyList<TemplateRuleReference>> GetRuleImpactAsync(TemplateDraftSession session, string pageId,
        CancellationToken cancellationToken)
    {
        _ = await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false);
        var store = templates as ITemplateRuleCommandStore ?? throw new TemplateRuleViolationException("TPL_RULE_STORE_UNAVAILABLE");
        return await store.GetRuleReferencesAsync(session.DraftRevisionId, pageId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplateRuleDraftView> GetRulesAsync(TemplateDraftSession session, CancellationToken cancellationToken)
    {
        var revision = await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false);
        if (revision.RuleWorkspace is null)
        {
            await templates.SaveDraftRevisionAsync(revision.WithRuleWorkspace(TemplateRuleWorkspace.Empty), cancellationToken).ConfigureAwait(false);
            revision = await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        var allCandidates = await LoadRuleCandidatesAsync(revision, session, cancellationToken).ConfigureAwait(false);
        var reserved = revision.RuleWorkspace!.Reconcile(revision)
            .ReserveFrames(allCandidates.Select(candidate => (candidate.PageId, candidate.FrameId)))
            .ReorderUnboundCandidateAliases(
                allCandidates.Select(candidate => (candidate.PageId, candidate.FrameId)),
                revision.FrameBindings.Select(binding => binding.FrameId).ToHashSet(StringComparer.Ordinal));
        if (!reserved.Aliases.SequenceEqual(revision.RuleWorkspace.Aliases))
        {
            var store = templates as ITemplateRuleCommandStore ?? throw new TemplateRuleViolationException("TPL_RULE_STORE_UNAVAILABLE");
            var digest = TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(new { revision.RevisionId, reserved.Version, reserved.Aliases }));
            // Reference reservation is a CAS write, not a binding or an undoable
            // user edit. A failed request must never leave a placeholder target.
            await store.SaveRuleCommandAsync(revision.WithRuleWorkspace(reserved),
                new("reserve_" + digest[7..], digest, checked(reserved.Version + 1), null, null, false), null,
                cancellationToken).ConfigureAwait(false);
            revision = await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        var excluded = allCandidates.Where(candidate => candidate.IsExcluded).ToArray();
        var candidates = allCandidates.Where(candidate => !candidate.IsExcluded).ToArray();
        return RuleView(revision) with
        {
            Candidates = candidates,
            ExcludedCandidates = excluded,
            UndoCommandIds = templates is ITemplateRuleCommandStore commands
                ? await commands.GetUndoableRuleCommandsAsync(revision.RevisionId, cancellationToken).ConfigureAwait(false) : []
        };
    }

    public Task<TemplateGroupDetails> ConfirmRulesAsync(TemplateDraftSession session,
        TemplateRuleConfirmation confirmation, CancellationToken cancellationToken) =>
        CommitDraftCoreAsync(session, confirmation, cancellationToken);

    public async Task<TemplateRuleDraftView> ApplyRulePatchAsync(TemplateDraftSession session,
        TemplateRulePatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        TemplateValueRules.Identifier(patch.CommandId, "TPL_RULE_COMMAND_INVALID");
        ValidateEdits(patch.Changes);
        var store = templates as ITemplateRuleCommandStore ?? throw new TemplateRuleViolationException("TPL_RULE_STORE_UNAVAILABLE");
        var digest = TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(patch));
        var receipt = await store.GetRuleCommandAsync(session.DraftRevisionId, patch.CommandId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (receipt.RequestDigest != digest) throw new TemplateRuleViolationException("TPL_RULE_COMMAND_REUSED");
            return RuleView(await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false));
        }
        var current = await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false);
        EnsureRuleVersion(current, patch.ExpectedDraftVersion);
        var additions = await ValidateCandidateAdditionsAsync(current, session, patch.Changes, cancellationToken).ConfigureAwait(false);
        var visibilityCandidates = await ValidateCandidateVisibilityAsync(current, session, patch.Changes, cancellationToken)
            .ConfigureAwait(false);
        var (updated, inverse) = ApplyEdits(current, session, patch.Changes,
            additions: additions, visibilityCandidates: visibilityCandidates);
        await store.SaveRuleCommandAsync(updated,
            new(patch.CommandId, digest, checked(patch.ExpectedDraftVersion + 1), patch.Changes, inverse, false,
                current.RuleWorkspace!.Rules is null), null, cancellationToken).ConfigureAwait(false);
        return RuleView(await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TemplateRuleDraftView> UndoRulePatchAsync(TemplateDraftSession session, string commandId,
        string undoCommandId, long expectedDraftVersion, CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(commandId, "TPL_RULE_COMMAND_INVALID");
        TemplateValueRules.Identifier(undoCommandId, "TPL_RULE_COMMAND_INVALID");
        var store = templates as ITemplateRuleCommandStore ?? throw new TemplateRuleViolationException("TPL_RULE_STORE_UNAVAILABLE");
        var digest = TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(new { commandId, undoCommandId, expectedDraftVersion }));
        var duplicate = await store.GetRuleCommandAsync(session.DraftRevisionId, undoCommandId, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            if (duplicate.RequestDigest != digest) throw new TemplateRuleViolationException("TPL_RULE_COMMAND_REUSED");
            return RuleView(await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false));
        }
        var original = await store.GetRuleCommandAsync(session.DraftRevisionId, commandId, cancellationToken).ConfigureAwait(false);
        if (original?.Applied is null || original.Inverse is null || original.IsUndone)
            throw new TemplateRuleViolationException("TPL_RULE_UNDO_UNAVAILABLE");
        var current = await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false);
        EnsureRuleVersion(current, expectedDraftVersion);
        var currentCandidates = await LoadRuleCandidatesAsync(current, session, cancellationToken).ConfigureAwait(false);
        var candidatesById = currentCandidates.ToDictionary(candidate => candidate.FrameId, StringComparer.Ordinal);
        var candidateVisibilityConflicts = (original.Applied.CandidateVisibility ?? []).Any(edit =>
            !candidatesById.TryGetValue(edit.FrameId, out var candidate) || candidate.IsExcluded != edit.Excluded);
        if (original.Applied.Frames.Any(edit => current.FrameBindings.SingleOrDefault(frame => frame.FrameId == edit.FrameId)
                ?.SelectionRequirement.Description != edit.Description.Trim())
            || (original.Applied.Pages ?? []).Any(edit => current.Pages.Any(page => page.PageId == edit.PageId))
            || (original.Applied.Targets ?? []).Any(edit => edit.Binding is null
                ? current.FrameBindings.Any(frame => frame.FrameId == edit.FrameId)
                : current.FrameBindings.SingleOrDefault(frame => frame.FrameId == edit.FrameId) is not { } target
                    || TemplateRuleCodec.Serialize(TemplatePackageService.ToPackageBinding(target)) != TemplateRuleCodec.Serialize(edit.Binding))
            || candidateVisibilityConflicts
            || original.Applied.Branches.Any(edit => TemplateRuleCodec.Serialize(current.RuleWorkspace!.Rules?.Branches
                .SingleOrDefault(branch => branch.BranchId == edit.BranchId)) != TemplateRuleCodec.Serialize(edit.Branch)))
            throw new TemplateRuleViolationException("TPL_RULE_UNDO_CONFLICT");
        var visibilityCandidates = await ValidateCandidateVisibilityAsync(current, session, original.Inverse, cancellationToken)
            .ConfigureAwait(false);
        var (updated, inverse) = ApplyEdits(current, session, original.Inverse, restoring: true,
            visibilityCandidates: visibilityCandidates);
        if (original.BeforeRulesWereLegacy && updated.RuleWorkspace!.Rules is { Branches.IsEmpty: true })
            updated = updated.WithRuleWorkspace(updated.RuleWorkspace with { Rules = null });
        await store.SaveRuleCommandAsync(updated, new(undoCommandId, digest, checked(expectedDraftVersion + 1),
            original.Inverse, inverse, false, current.RuleWorkspace!.Rules is null), commandId, cancellationToken).ConfigureAwait(false);
        return RuleView(await LoadRuleRevisionAsync(session, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TemplateRevision> LoadRuleRevisionAsync(TemplateDraftSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        if (revision.GroupId != session.GroupId) throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        return revision;
    }

    private static TemplateRuleDraftView RuleView(TemplateRevision revision)
    {
        var workspace = revision.RuleWorkspace ?? throw new TemplateRuleViolationException("TPL_RULE_WORKSPACE_MISSING");
        return new(revision.RevisionId, workspace.Version, TemplateRuleCodec.BusinessDigest(revision),
            TemplateRuleViewMapper.ToView(workspace.Rules), TemplateRuleViewMapper.ToViews(workspace.Aliases),
            workspace.Rules is null ? [] : TemplateRuleViewMapper.ToViews(TemplateRuleEvaluator.Validate(workspace.Rules, revision)),
            revision.ActiveFrameBindings.Select(frame => new TemplateRuleFrameOverview(frame.PageId, frame.FrameId,
                frame.SelectionRequirement.Description)).ToImmutableArray());
    }

    private static void EnsureRuleVersion(TemplateRevision revision, long expected)
    {
        if (revision.State != TemplateRevisionState.Draft) throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        if (revision.RuleWorkspace is null || revision.RuleWorkspace.Version != expected)
            throw new TemplateRuleViolationException("TPL_RULE_VERSION_CONFLICT");
    }

    private static (TemplateRevision Updated, TemplateRuleEditSet Inverse) ApplyEdits(
        TemplateRevision revision, TemplateDraftSession session, TemplateRuleEditSet changes, bool restoring = false,
        HashSet<string>? additions = null,
        Dictionary<string, TemplateRuleCandidateView>? visibilityCandidates = null)
    {
        ValidateEdits(changes);
        var inverseFrames = ImmutableArray.CreateBuilder<TemplateFrameRuleEdit>();
        var inverseBranches = ImmutableArray.CreateBuilder<TemplateBranchRuleEdit>();
        var updated = revision;
        var inversePages = new List<TemplateRulePageEdit>();
        var inverseTargets = new List<TemplateRuleTargetEdit>();
        var inverseVisibility = new List<TemplateRuleCandidateVisibilityEdit>();
        foreach (var edit in changes.Pages ?? [])
        {
            if (!session.CanEditPage(edit.PageId)) throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
            if (edit.Page is null)
            {
                var page = updated.Pages.SingleOrDefault(page => page.PageId == edit.PageId)
                    ?? throw new TemplateRuleViolationException("TPL_PAGE_NOT_FOUND");
                inversePages.Add(new(page.PageId, new(page.Order, page.Name, page.ContentType, page.GenerationMode,
                    page.VariantCapacity, page.Enabled, page.TemplateResourceId, page.Notes, page.CanvasX, page.CanvasY,
                    page.SizeTableCalibration, page.TypeRecognition),
                    updated.FrameBindings.Where(frame => frame.PageId == page.PageId).Select(TemplatePackageService.ToPackageBinding).ToArray()));
                updated = updated.RemovePage(edit.PageId);
            }
            else
            {
                if (!restoring) throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
                var page = edit.Page;
                var order = updated.Pages.OrderBy(item => item.Order).Select(item => item.PageId).ToList();
                order.Insert(Math.Min(page.Order, order.Count), edit.PageId);
                updated = updated.AddPage(TemplatePage.Create(edit.PageId, updated.RevisionId, updated.Pages.Length,
                    page.Name, page.ContentType, page.GenerationMode, page.VariantCapacity, page.Enabled, page.ResourceId,
                    page.Notes, page.CanvasX, page.CanvasY, page.SizeTableCalibration, page.TypeRecognition)).ReorderPages(order);
                foreach (var frame in edit.Bindings)
                    updated = updated.AddFrameBinding(TemplatePackageService.ToDomain(frame, edit.PageId, frame.FrameId));
                inversePages.Add(new(edit.PageId, null, []));
            }
        }
        foreach (var edit in changes.Targets ?? [])
        {
            if (edit.Binding is null)
            {
                var frame = updated.FrameBindings.SingleOrDefault(frame => frame.FrameId == edit.FrameId)
                    ?? throw new TemplateRuleViolationException("TPL_RULE_FRAME_REFERENCE_INVALID");
                if (!session.CanEditPage(frame.PageId)) throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
                inverseTargets.Add(new(frame.FrameId, TemplatePackageService.ToPackageBinding(frame)));
                updated = updated.RemoveFrameBinding(frame.FrameId);
            }
            else
            {
                if (!session.CanEditPage(edit.Binding.PageId) || !restoring && additions?.Contains(edit.FrameId) != true)
                    throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
                updated = updated.AddFrameBinding(TemplatePackageService.ToDomain(edit.Binding, edit.Binding.PageId, edit.FrameId));
                inverseTargets.Add(new(edit.FrameId, null));
            }
        }
        foreach (var edit in changes.CandidateVisibility ?? [])
        {
            if (visibilityCandidates is null || !visibilityCandidates.TryGetValue(edit.FrameId, out var candidate))
                throw new TemplateRuleViolationException("TPL_RULE_TARGET_REFERENCE_INVALID");
            if (!session.CanEditPage(candidate.PageId))
                throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
            inverseVisibility.Add(new(edit.FrameId, !edit.Excluded));
            updated = updated.WithRuleWorkspace(updated.RuleWorkspace!.SetCandidateExcluded(
                candidate.PageId,
                candidate.Target.TemplateSha256,
                candidate.Target.StructureSignature,
                candidate.Target.LayerId,
                candidate.Target.LayerPath,
                edit.Excluded));
        }
        foreach (var edit in changes.Frames)
        {
            var frame = updated.FrameBindings.SingleOrDefault(frame => frame.FrameId == edit.FrameId)
                ?? throw new TemplateRuleViolationException("TPL_RULE_FRAME_REFERENCE_INVALID");
            if (updated.IsFrameExcluded(frame)) throw new TemplateRuleViolationException("TPL_RULE_REFERENCE_INACTIVE");
            if (!session.CanEditPage(frame.PageId)) throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
            if (updated.Pages.Single(page => page.PageId == frame.PageId).ContentType != TemplatePageContentType.NormalComposition)
                throw new TemplateRuleViolationException("TPL_RULE_SIZE_PAGE_FRAME_BINDINGS_FORBIDDEN");
            inverseFrames.Add(new(frame.FrameId, frame.SelectionRequirement.Description));
            updated = updated.UpdateFrameBinding(frame.FrameId, SelectionRequirement.Create(edit.Description, frame.SelectionRequirement.Criteria));
        }
        var branches = (revision.RuleWorkspace!.Rules?.Branches ?? []).ToList();
        foreach (var edit in changes.Branches)
        {
            var index = branches.FindIndex(branch => branch.BranchId == edit.BranchId);
            var previous = index < 0 ? null : branches[index];
            var replacement = edit.Branch is null ? null : TemplateRuleViewMapper.ToDomain(edit.Branch);
            if (previous is null && edit.Branch is null) throw new TemplateRuleViolationException("TPL_RULE_BRANCH_REFERENCE_INVALID");
            if ((previous?.Recipes ?? []).Concat(replacement?.Recipes ?? []).Any(recipe => !session.CanEditPage(recipe.PageId)))
                throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
            inverseBranches.Add(new(edit.BranchId, previous));
            if (replacement is null) branches.RemoveAt(index);
            else
            {
                if (replacement.Recipes.Any(recipe => !updated.Pages.Any(page => page.PageId == recipe.PageId)
                    || recipe.FrameBindings.Any(binding => !updated.FrameBindings.Any(frame =>
                        frame.PageId == recipe.PageId && frame.FrameId == binding.FrameId))))
                    throw new TemplateRuleViolationException("TPL_RULE_TARGET_REFERENCE_INVALID");
                if (index < 0) branches.Add(replacement); else branches[index] = replacement;
            }
        }
        if (!changes.Branches.IsEmpty)
            updated = updated.WithRuleWorkspace(updated.RuleWorkspace! with { Rules = new(1, [.. branches]) });
        var inverse = new TemplateRuleEditSet(inverseFrames.ToImmutable(), inverseBranches.ToImmutable(), inversePages, inverseTargets)
        {
            CandidateVisibility = inverseVisibility
        };
        return (updated, inverse);
    }

    private async Task<List<TemplateRuleCandidateView>> LoadRuleCandidatesAsync(TemplateRevision revision,
        TemplateDraftSession session, CancellationToken cancellationToken)
    {
        var result = new List<TemplateRuleCandidateView>();
        if (revision.State != TemplateRevisionState.Draft) return result;
        foreach (var page in revision.Pages.Where(page => session.CanEditPage(page.PageId)
            && page.ContentType == TemplatePageContentType.NormalComposition).OrderBy(page => page.Order))
        {
            var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken).ConfigureAwait(false);
            // Candidate discovery is optional for reading existing rules. A
            // missing resource cannot authorize a new target, but must not
            // break legacy rule-only editing/recovery.
            if (resource is null) continue;
            if (importer is not null)
                resource = await importer.EnsureCurrentInspectionAsync(resource, cancellationToken).ConfigureAwait(false);
            foreach (var target in TemplateCalibrationService.OrderFramesVisually(
                TemplateCalibrationService.InspectFrames(page.PageId, resource.Inspection)))
            {
                var binding = revision.FrameBindings.SingleOrDefault(frame => frame.PageId == page.PageId
                    && frame.ExpectedLayer.LayerId == target.LayerId
                    && frame.LayerPath.Segments.SequenceEqual(target.LayerPath.Select(segment => segment + 1)));
                if (binding is null && !TemplateCalibrationService.CanSetRule(target,
                    resource.Inspection.Canvas.Width, resource.Inspection.Canvas.Height)) continue;
                var excluded = revision.RuleWorkspace?.IsCandidateExcluded(
                    target.PageId, target.TemplateSha256, target.StructureSignature,
                    target.LayerId, target.LayerPath) == true;
                result.Add(new(page.PageId, binding?.FrameId ?? TemplateCalibrationService.CandidateBindingIdentity(revision, target), target,
                    !excluded && binding is null && target.HasCompositeContribution == true, excluded));
            }
        }
        return result;
    }

    private async Task<HashSet<string>> ValidateCandidateAdditionsAsync(TemplateRevision revision,
        TemplateDraftSession session, TemplateRuleEditSet changes, CancellationToken cancellationToken)
    {
        var additions = (changes.Targets ?? []).Where(edit => edit.Binding is not null).ToArray();
        if (additions.Length == 0) return [];
        if (changes.Source?.Kind != "ai") throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
        var candidates = await LoadRuleCandidatesAsync(revision, session, cancellationToken).ConfigureAwait(false);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in additions)
        {
            var candidate = candidates.SingleOrDefault(item => item.FrameId == edit.FrameId && item.CanInterpret)
                ?? throw new TemplateRuleViolationException("TPL_RULE_TARGET_REFERENCE_INVALID");
            var page = revision.Pages.Single(item => item.PageId == candidate.PageId);
            var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken).ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
            if (!revision.RuleWorkspace!.Aliases.Any(alias => alias.PageId == candidate.PageId && alias.FrameId == candidate.FrameId))
                throw new TemplateRuleViolationException("TPL_RULE_REFERENCE_INACTIVE");
            var expected = TemplateCalibrationService.CreateRuleOnlyBinding(revision, candidate.Target,
                resource.Inspection.Canvas, edit.Binding!.SelectionDescription);
            if (TemplateRuleCodec.Serialize(TemplatePackageService.ToPackageBinding(expected)) != TemplateRuleCodec.Serialize(edit.Binding))
                throw new TemplateRuleViolationException("TPL_RULE_TARGET_REFERENCE_INVALID");
            result.Add(edit.FrameId);
        }
        return result;
    }

    private async Task<Dictionary<string, TemplateRuleCandidateView>> ValidateCandidateVisibilityAsync(
        TemplateRevision revision,
        TemplateDraftSession session,
        TemplateRuleEditSet changes,
        CancellationToken cancellationToken)
    {
        var edits = changes.CandidateVisibility ?? [];
        if (edits.Count == 0) return new(StringComparer.Ordinal);
        var candidates = await LoadRuleCandidatesAsync(revision, session, cancellationToken).ConfigureAwait(false);
        var byId = candidates.ToDictionary(candidate => candidate.FrameId, StringComparer.Ordinal);
        var result = new Dictionary<string, TemplateRuleCandidateView>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!byId.TryGetValue(edit.FrameId, out var candidate))
                throw new TemplateRuleViolationException("TPL_RULE_TARGET_REFERENCE_INVALID");
            var excluded = revision.RuleWorkspace!.IsCandidateExcluded(
                candidate.PageId, candidate.Target.TemplateSha256, candidate.Target.StructureSignature,
                candidate.Target.LayerId, candidate.Target.LayerPath);
            if (excluded == edit.Excluded)
                throw new TemplateRuleViolationException("TPL_RULE_TARGET_REFERENCE_INVALID");
            result.Add(edit.FrameId, candidate);
        }
        return result;
    }

    private static void ValidateEdits(TemplateRuleEditSet changes)
    {
        if (changes is null || changes.Frames.IsDefault || changes.Branches.IsDefault
            || changes.Frames.Length > 1024 || changes.Branches.Length > 128
            || changes.Pages?.Count > 256 || changes.Targets?.Count > 1024
            || changes.CandidateVisibility?.Count > 1024
            || changes.Frames.IsEmpty && changes.Branches.IsEmpty && (changes.Pages?.Count ?? 0) == 0
                && (changes.Targets?.Count ?? 0) == 0 && (changes.CandidateVisibility?.Count ?? 0) == 0)
            throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
        if (changes.Source is { } source)
        {
            if (source.Kind != "ai" || string.IsNullOrWhiteSpace(source.Text) || source.Text.Length > 8000
                || string.IsNullOrWhiteSpace(source.ModelVersion) || source.ModelVersion.Length > 128
                || source.ModelVersion.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/'))
                || source.PromptVersion != TemplateRuleAiProtocol.PromptVersion)
                throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
            TemplateValueRules.Identifier(source.RequestId, "TPL_RULE_PATCH_INVALID");
            TemplateValueRules.Digest(source.ContextDigest, "TPL_RULE_PATCH_INVALID");
        }
        if ((changes.Pages ?? []).Any(edit => edit is null || edit.Bindings is null)
            || (changes.Targets ?? []).Any(edit => edit is null)
            || (changes.CandidateVisibility ?? []).Any(edit => edit is null)
            || (changes.CandidateVisibility ?? []).Select(edit => edit.FrameId).Distinct(StringComparer.Ordinal).Count()
                != (changes.CandidateVisibility?.Count ?? 0)
            || (changes.Pages ?? []).Select(edit => edit.PageId).Distinct(StringComparer.Ordinal).Count() != (changes.Pages?.Count ?? 0)
            || (changes.Targets ?? []).Select(edit => edit.FrameId).Distinct(StringComparer.Ordinal).Count() != (changes.Targets?.Count ?? 0))
            throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
        foreach (var edit in changes.Pages ?? []) TemplateValueRules.Identifier(edit.PageId, "TPL_RULE_PATCH_INVALID");
        foreach (var edit in changes.Targets ?? []) TemplateValueRules.Identifier(edit.FrameId, "TPL_RULE_PATCH_INVALID");
        foreach (var edit in changes.CandidateVisibility ?? []) TemplateValueRules.Identifier(edit.FrameId, "TPL_RULE_PATCH_INVALID");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in changes.Frames)
        {
            if (edit is null || !targets.Add(edit.FrameId)) throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
            TemplateValueRules.Identifier(edit.FrameId, "TPL_RULE_PATCH_INVALID");
            _ = SelectionRequirement.Create(edit.Description);
        }
        targets.Clear();
        foreach (var edit in changes.Branches)
        {
            if (edit is null || !targets.Add(edit.BranchId)) throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
            TemplateValueRules.Identifier(edit.BranchId, "TPL_RULE_PATCH_INVALID");
            if (edit.Branch is { } branch)
            {
                if (branch.BranchId != edit.BranchId) throw new TemplateRuleViolationException("TPL_RULE_PATCH_INVALID");
                TemplateRuleCodec.ValidateRuleShape(new(1, [TemplateRuleViewMapper.ToDomain(branch)]));
            }
        }
        _ = TemplateRuleCodec.Serialize(changes);
    }
}
