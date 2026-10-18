using Desktop.Domain.Templates;
using Desktop.Application.Files;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desktop.Application.Templates;

public sealed partial class TemplateWorkspaceService
{
    public Task<TemplateGroupDetails> CommitDraftAsync(
        TemplateDraftSession session,
        CancellationToken cancellationToken) => CommitDraftCoreAsync(session, null, cancellationToken);

    private async Task<TemplateGroupDetails> CommitDraftCoreAsync(
        TemplateDraftSession session, TemplateRuleConfirmation? confirmation, CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(session.GroupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var draft = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        if (draft.State == TemplateRevisionState.Committed
            && string.Equals(group.CurrentRevisionId, draft.RevisionId, StringComparison.Ordinal))
        {
            await EnsureCommittedMarkerAsync(draft, cancellationToken).ConfigureAwait(false);
            return ToDetails(group, draft);
        }
        if (draft.Pages.IsDefaultOrEmpty)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_PAGE_SET_INVALID");
        }
        if (draft.RuleWorkspace is { } ruleWorkspace)
        {
            if (confirmation is null || confirmation.DraftVersion != ruleWorkspace.Version
                || confirmation.RuleDigest != TemplateRuleCodec.BusinessDigest(draft))
                throw new TemplateRuleViolationException("TPL_RULE_OVERVIEW_STALE");
            if (ruleWorkspace.Rules is { } rules && !TemplateRuleEvaluator.Validate(rules, draft).IsEmpty)
                throw new TemplateRuleViolationException("TPL_RULE_COMMIT_INVALID");
        }
        await EnsureCalibrationCompleteAsync(session, draft, cancellationToken).ConfigureAwait(false);

        var importing = draft.AdvanceTo(
            TemplateRevisionState.ImportingCopy,
            new RevisionTransitionEvidence(SourceReadable: true));
        var inspecting = importing.AdvanceTo(
            TemplateRevisionState.StructureInspecting,
            new RevisionTransitionEvidence(CopyMatchesSource: true));
        var binding = inspecting.AdvanceTo(
            TemplateRevisionState.FrameBinding,
            new RevisionTransitionEvidence(StructureReadable: true));
        var calibrating = binding.AdvanceTo(
            TemplateRevisionState.Calibrating,
            new RevisionTransitionEvidence(RegisteredFramesHaveEvidence: true));
        var review = calibrating.AdvanceTo(
            TemplateRevisionState.AwaitingReview,
            new RevisionTransitionEvidence(CalibrationComplete: true));
        var committing = review.AdvanceTo(
            TemplateRevisionState.Committing,
            new RevisionTransitionEvidence(ConfigurationValid: true));
        if (templates is ITemplatePreviewRepository previews)
        {
            foreach (var page in draft.Pages)
            {
                await previews.SavePreviewStateAsync(
                    TemplatePreviewState.NotPreviewed(
                        draft.RevisionId,
                        page.PageId,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var manifestPath = $"templates/groups/{group.GroupId}/revisions/{draft.RevisionId}/revision-manifest.json";
        var manifest = new AppManifest(
            $"manifest_{draft.RevisionId}",
            1,
            await BuildManifestEntriesAsync(draft, cancellationToken).ConfigureAwait(false),
            timeProvider.GetUtcNow());
        var ruleDigest = draft.RuleWorkspace is null
            ? $"sha256:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest))).ToLowerInvariant()}"
            : TemplateRuleCodec.BusinessDigest(draft);
        if (manifestStore is not null)
        {
            await manifestStore.SaveAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        }
        await WriteReadyMarkerAsync(
            draft,
            manifestPath,
            ruleDigest,
            cancellationToken).ConfigureAwait(false);

        var committed = committing.Commit(
            manifestPath,
            ruleDigest,
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
        await templates.CommitRevisionAsync(
            committed,
            group.CurrentRevisionId,
            cancellationToken).ConfigureAwait(false);
        await WriteCommittedMarkerAsync(
            committed,
            manifestPath,
            ruleDigest,
            cancellationToken).ConfigureAwait(false);
        return ToDetails(group, committed);
    }

    public async Task<TemplateGroupDetails> CommitDraftAsNewGroupAsync(
        TemplateDraftSession session,
        string newGroupName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        TemplateValueRules.RequiredText(newGroupName, 256, "TPL_GROUP_NAME_INVALID");
        if (!session.IsFullTemplateEdit)
        {
            throw new TemplateRuleViolationException("TPL_FULL_EDIT_REQUIRED");
        }
        if (templates is null || resources is null || sourceFiles is null || paths is null || files is null)
        {
            throw new TemplateRuleViolationException("TPL_TEMPLATE_CLONE_UNAVAILABLE");
        }

        var sourceRevision = await templates.GetRevisionAsync(
                session.DraftRevisionId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        if (sourceRevision.State != TemplateRevisionState.Draft)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        }
        if (sourceRevision.RuleWorkspace?.Rules is { } sourceRules
            && !TemplateRuleEvaluator.Validate(sourceRules, sourceRevision).IsEmpty)
            throw new TemplateRuleViolationException("TPL_RULE_VALIDATION_FAILED");
        if (sourceRevision.Pages.IsDefaultOrEmpty)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_PAGE_SET_INVALID");
        }

        var newGroupId = NewId("group");
        var newRevisionId = NewId("revision");
        var now = timeProvider.GetUtcNow();
        var newGroup = TemplateGroup.Create(newGroupId, newGroupName.Trim(), now);
        var newRevision = TemplateRevision.CreateDraft(newRevisionId, newGroupId, 1, now);
        await templates.SaveGroupAsync(newGroup, cancellationToken).ConfigureAwait(false);
        await templates.SaveDraftRevisionAsync(newRevision, cancellationToken).ConfigureAwait(false);

        try
        {
            var resourceMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var page in sourceRevision.Pages)
            {
                if (resourceMap.ContainsKey(page.TemplateResourceId))
                {
                    continue;
                }

                var sourceResource = await resources.GetByResourceIdAsync(
                        page.TemplateResourceId,
                        cancellationToken).ConfigureAwait(false)
                    ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
                var resourceId = NewId("resource");
                var destination =
                    $"templates/groups/{newGroupId}/drafts/{newRevisionId}/staging/{resourceId}/template.psd";
                var copied = await sourceFiles.CopyVerifiedAsync(
                    paths.ResolveOwnedPath(sourceResource.InternalRelativePath),
                    new TemplateSourceFingerprint(sourceResource.Length, sourceResource.Sha256),
                    destination,
                    cancellationToken).ConfigureAwait(false);
                if (!copied.IsSuccess)
                {
                    throw new TemplateRuleViolationException(
                        copied.Error?.Code ?? "TPL_SOURCE_UNREADABLE");
                }

                var inspection = sourceResource.Inspection;
                var sourcePreview = TemplatePageImportService.GetPreviewRelativePath(
                    sourceResource.InternalRelativePath);
                var destinationPreview = TemplatePageImportService.GetPreviewRelativePath(destination);
                var preview = await files.ReadAsync(sourcePreview, cancellationToken).ConfigureAwait(false);
                if (preview is not null)
                {
                    await files.WriteAtomicallyAsync(destinationPreview, preview, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    inspection = inspection with { Preview = null };
                }

                var savedResource = new ImportedTemplateResource(
                    resourceId,
                    newGroupId,
                    newRevisionId,
                    destination,
                    copied.Copy!.Length,
                    copied.Copy.Sha256,
                    sourceResource.StructureSignature,
                    inspection,
                    now);
                await resources.SaveImportedAsync(
                    NewId("import"),
                    savedResource,
                    cancellationToken).ConfigureAwait(false);
                resourceMap[page.TemplateResourceId] = resourceId;
            }

            var pageMap = sourceRevision.Pages.ToDictionary(
                page => page.PageId,
                _ => NewId("page"),
                StringComparer.Ordinal);
            foreach (var page in sourceRevision.Pages.OrderBy(page => page.Order))
            {
                newRevision = newRevision.AddPage(TemplatePage.Create(
                    pageMap[page.PageId],
                    newRevisionId,
                    page.Order,
                    page.Name,
                    page.ContentType,
                    page.GenerationMode,
                    page.VariantCapacity,
                    page.Enabled,
                    resourceMap[page.TemplateResourceId], page.Notes, page.CanvasX, page.CanvasY,
                    page.SizeTableCalibration, page.TypeRecognition));
            }

            var frameMap = sourceRevision.FrameBindings.ToDictionary(binding => binding.FrameId,
                _ => NewId("frame"), StringComparer.Ordinal);
            foreach (var binding in sourceRevision.FrameBindings)
            {
                newRevision = newRevision.AddFrameBinding(FrameBinding.Create(
                    frameMap[binding.FrameId],
                    pageMap[binding.PageId],
                    binding.LayerPath,
                    binding.ExpectedLayer,
                    binding.SupersededLayers,
                    binding.SelectionRequirement,
                    binding.LayoutIntent));
            }

            if (sourceRevision.RuleWorkspace is { } ruleWorkspace)
                newRevision = newRevision.WithRuleWorkspace(TemplateRuleCodec.Remap(ruleWorkspace, pageMap, frameMap));
            await templates.SaveDraftRevisionAsync(newRevision, cancellationToken).ConfigureAwait(false);
            var cloneSession = new TemplateDraftSession(
                newGroupId,
                newGroupName.Trim(),
                newRevisionId,
                1,
                new HashSet<string>(StringComparer.Ordinal));
            if (newRevision.RuleWorkspace is not null)
            {
                var view = await GetRulesAsync(cloneSession, cancellationToken).ConfigureAwait(false);
                return await ConfirmRulesAsync(cloneSession, new(view.Version, view.RuleDigest), cancellationToken).ConfigureAwait(false);
            }
            return await CommitDraftAsync(cloneSession, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await templates.DeleteGroupAsync(newGroupId, CancellationToken.None).ConfigureAwait(false);
                if (ownedStorage is not null)
                {
                    await ownedStorage.DeleteGroupFilesAsync(newGroupId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Preserve the original clone error; startup recovery can clean
                // any unreferenced staging files if the process stops mid-copy.
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<string>> DeleteTemplateGroupAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        try
        {
            var blockers = await templates.GetBlockingBatchIdsAsync(groupId, cancellationToken)
                .ConfigureAwait(false);
            if (blockers.Count > 0)
            {
                return blockers;
            }

            var deletionStage = ownedStorage is null
                ? null
                : await ownedStorage.StageGroupFilesForDeletionAsync(groupId, cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                await templates.DeleteGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (deletionStage is not null)
                {
                    await ownedStorage!.RestoreGroupFilesAsync(deletionStage, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                throw;
            }

            if (deletionStage is not null)
            {
                // Once metadata deletion commits, finish the staged filesystem
                // cleanup even if the caller cancels; leaving the tombstone is
                // no longer a user-cancellable operation.
                await ownedStorage!.CommitGroupFilesDeletionAsync(deletionStage, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return [];
        }
        catch (IOException ioException)
        {
            throw new IOException(
                "TPL_DELETE_FILES_FAILED",
                ioException);
        }
        catch (UnauthorizedAccessException accessException)
        {
            throw new IOException(
                "TPL_DELETE_ACCESS_DENIED",
                accessException);
        }
    }

    private async Task<IReadOnlyList<ManifestEntry>> BuildManifestEntriesAsync(
        TemplateRevision revision,
        CancellationToken cancellationToken)
    {
        var entries = new List<ManifestEntry>(revision.Pages.Length);
        if (revision.RuleWorkspace is { } workspace)
        {
            if (files is null) throw new TemplateRuleViolationException("TPL_RULE_MANIFEST_UNAVAILABLE");
            var relative = $"templates/groups/{revision.GroupId}/revisions/{revision.RevisionId}/rules.json";
            var bytes = System.Text.Encoding.UTF8.GetBytes(TemplateRuleCodec.Serialize(workspace));
            await files.WriteAtomicallyAsync(relative, bytes, cancellationToken).ConfigureAwait(false);
            entries.Add(new ManifestEntry("rules_" + revision.RevisionId, relative, bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "ready"));
        }
        foreach (var page in revision.Pages)
        {
            var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
            entries.Add(new ManifestEntry(
                page.TemplateResourceId,
                resource.InternalRelativePath,
                resource.Length,
                resource.Sha256,
                "ready"));
            if (page.SizeTableCalibration is { } calibration)
            {
                if (files is null || !SizeTables.SizeTableCalibrationResolver.Matches(calibration, resource.Inspection))
                    throw new TemplateRuleViolationException("SIZE_TABLE_CALIBRATION_INVALID");
                var relative = $"templates/groups/{revision.GroupId}/revisions/{revision.RevisionId}/{page.PageId}-size-calibration.json";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(calibration);
                await files.WriteAtomicallyAsync(relative, bytes, cancellationToken).ConfigureAwait(false);
                entries.Add(new ManifestEntry("size_" + page.PageId, relative, bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), "ready"));
            }
        }

        return entries;
    }

    private async Task EnsureCalibrationCompleteAsync(
        TemplateDraftSession session,
        TemplateRevision revision,
        CancellationToken cancellationToken)
    {
        foreach (var page in revision.Pages.Where(page =>
                     page.Enabled
                     && page.ContentType == TemplatePageContentType.NormalComposition
                     && session.IsPageAffected(page.PageId)))
        {
            var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
            var registered = revision.FrameBindings.Where(binding => string.Equals(
                binding.PageId,
                page.PageId,
                StringComparison.Ordinal)).ToArray();
            if (registered.Length == 0
                || registered.Any(binding => string.IsNullOrWhiteSpace(binding.SelectionRequirement.Description)))
            {
                throw new TemplateRuleViolationException("TPL_CALIBRATION_INCOMPLETE");
            }
        }
    }

    private async Task EnsureCommittedMarkerAsync(
        TemplateRevision revision,
        CancellationToken cancellationToken)
    {
        if (manifestStore is not IRevisionCommitMarkerStore markers
            || revision.ManifestRef is null
            || revision.RuleDigest is null)
        {
            return;
        }

        var directory = revision.ManifestRef[..revision.ManifestRef.LastIndexOf('/')];
        var marker = new RevisionCommitMarker(
            revision.RevisionId,
            revision.ManifestRef,
            revision.RuleDigest,
            "committed",
            timeProvider.GetUtcNow());
        var existing = await markers.ReadMarkerAsync(
            directory,
            "committed.marker",
            cancellationToken).ConfigureAwait(false);
        if (existing is null
            || !string.Equals(existing.RevisionId, marker.RevisionId, StringComparison.Ordinal)
            || !string.Equals(existing.ManifestSha256, marker.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            await markers.WriteCommittedMarkerAsync(directory, marker, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteReadyMarkerAsync(
        TemplateRevision revision,
        string manifestPath,
        string ruleDigest,
        CancellationToken cancellationToken)
    {
        if (manifestStore is not IRevisionCommitMarkerStore markers)
        {
            return;
        }

        var directory = manifestPath[..manifestPath.LastIndexOf('/')];
        var marker = new RevisionCommitMarker(
            revision.RevisionId,
            manifestPath,
            ruleDigest,
            "ready",
            timeProvider.GetUtcNow());
        try
        {
            await markers.WriteReadyMarkerAsync(directory, marker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_COMMIT_FAILED");
        }
    }

    private async Task WriteCommittedMarkerAsync(
        TemplateRevision revision,
        string manifestPath,
        string ruleDigest,
        CancellationToken cancellationToken)
    {
        if (manifestStore is not IRevisionCommitMarkerStore markers)
        {
            return;
        }

        var directory = manifestPath[..manifestPath.LastIndexOf('/')];
        var marker = new RevisionCommitMarker(
            revision.RevisionId,
            manifestPath,
            ruleDigest,
            "committed",
            timeProvider.GetUtcNow());
        try
        {
            await markers.WriteCommittedMarkerAsync(directory, marker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TemplateRuleViolationException("TPL_COMMITTED_MARKER_MISSING");
        }
    }


}
