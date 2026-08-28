using Desktop.Domain.Templates;
using Desktop.Application.Files;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desktop.Application.Templates;

public sealed class TemplateWorkspaceService(
    ITemplateRepository templates,
    ITemplateResourceRepository resources,
    TemplatePageImportService importer,
    TimeProvider timeProvider,
    IManifestStore? manifestStore = null,
    ITemplateOwnedStorage? ownedStorage = null,
    IPathPolicy? paths = null) : ITemplateWorkspaceService
{
    public async Task<IReadOnlyList<TemplateLibraryItem>> ListAsync(CancellationToken cancellationToken)
    {
        var groups = await templates.ListGroupsAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<TemplateLibraryItem>(groups.Count);
        foreach (var group in groups)
        {
            var latest = await templates.GetLatestRevisionForGroupAsync(group.GroupId, cancellationToken)
                .ConfigureAwait(false);
            var revision = latest is { State: TemplateRevisionState.Draft }
                ? latest
                : group.CurrentRevisionId is null
                    ? latest
                    : await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken)
                        .ConfigureAwait(false);
            items.Add(new TemplateLibraryItem(
                group.GroupId,
                group.Name,
                group.CurrentRevisionId,
                revision?.Pages.Length ?? 0,
                revision?.State == TemplateRevisionState.Draft ? "草稿" : "可用",
                group.UpdatedAt));
        }

        return items;
    }

    public async Task<TemplateGroupDetails?> GetDetailsAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var latest = await templates.GetLatestRevisionForGroupAsync(group.GroupId, cancellationToken)
            .ConfigureAwait(false);
        var revision = latest is { State: TemplateRevisionState.Draft }
            ? latest
            : group.CurrentRevisionId is null
                ? latest
                : await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken)
                    .ConfigureAwait(false);
        return revision is null
            ? null
            : new TemplateGroupDetails(
                group.GroupId,
                group.Name,
                revision.RevisionId,
                revision.RevisionNo,
                revision.Pages.OrderBy(page => page.Order).Select(ToProjection).ToArray(),
                group.CurrentRevisionId is not null,
                group.UpdatedAt);
    }

    public async Task<TemplateDraftSession> BeginCreateAsync(
        string groupName,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var groupId = NewId("group");
        var revisionId = NewId("revision");
        var group = TemplateGroup.Create(groupId, groupName, now);
        var revision = TemplateRevision.CreateDraft(revisionId, groupId, 1, now);
        await templates.SaveGroupAsync(group, cancellationToken).ConfigureAwait(false);
        await templates.SaveDraftRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        return new TemplateDraftSession(groupId, group.Name, revisionId, 1, new HashSet<string>(StringComparer.Ordinal));
    }

    public async Task<TemplateDraftSession> BeginIncrementAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        if (group.CurrentRevisionId is null)
        {
            throw new TemplateRuleViolationException("TPL_INCREMENT_BASE_INVALID");
        }

        var current = await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_INCREMENT_BASE_INVALID");
        var revisionId = NewId("revision");
        var revision = TemplateRevision.CreateIncrement(
            revisionId,
            current,
            current.RevisionNo + 1,
            timeProvider.GetUtcNow(),
            []);
        await templates.SaveDraftRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        return new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revisionId,
            revision.RevisionNo,
            current.Pages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal));
    }

    public async Task<TemplateDraftSession> ResumeDraftAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var draft = await templates.GetLatestRevisionForGroupAsync(groupId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_DRAFT_NOT_FOUND");
        if (draft.State != TemplateRevisionState.Draft)
        {
            throw new TemplateRuleViolationException("TPL_DRAFT_NOT_FOUND");
        }

        IReadOnlySet<string> basePageIds = new HashSet<string>(StringComparer.Ordinal);
        if (group.CurrentRevisionId is not null)
        {
            var current = await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_INCREMENT_BASE_INVALID");
            basePageIds = current.Pages
                .Select(page => page.PageId)
                .ToHashSet(StringComparer.Ordinal);
        }

        return new TemplateDraftSession(
            group.GroupId,
            group.Name,
            draft.RevisionId,
            draft.RevisionNo,
            basePageIds);
    }

    public async Task<TemplateWorkspaceImportResult> ImportPageAsync(
        TemplateDraftSession session,
        string commandId,
        string resourceId,
        string sourcePsdPath,
        string pageName,
        TemplateDuplicateDecision duplicateDecision,
        string? selectedDuplicateResourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var import = await importer.ImportAsync(
            new TemplatePageImportRequest(
                commandId,
                session.GroupId,
                session.DraftRevisionId,
                resourceId,
                sourcePsdPath,
                duplicateDecision,
                selectedDuplicateResourceId),
            cancellationToken).ConfigureAwait(false);
        if (!import.IsSuccess)
        {
            return new TemplateWorkspaceImportResult(import, null);
        }

        var importedResource = import.Resource
            ?? throw new InvalidOperationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");

        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        var existing = revision.Pages.SingleOrDefault(page => string.Equals(
            page.TemplateResourceId,
            importedResource.ResourceId,
            StringComparison.Ordinal));
        if (existing is not null)
        {
            return new TemplateWorkspaceImportResult(
                import,
                new TemplateDraftPage(
                    ToProjection(existing),
                    importedResource,
                    !session.BasePageIds.Contains(existing.PageId),
                    GetPreviewSource(importedResource, paths)));
        }

        var page = TemplatePage.Create(
            NewId("page"),
            revision.RevisionId,
            revision.Pages.Length,
            pageName,
            TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct,
            variantCapacity: null,
            enabled: true,
            importedResource.ResourceId);
        var updated = revision.AddPage(page);
        await templates.SaveDraftRevisionAsync(updated, cancellationToken).ConfigureAwait(false);
        return new TemplateWorkspaceImportResult(
            import,
            new TemplateDraftPage(
                ToProjection(page),
                importedResource,
                IsAffected: true,
                GetPreviewSource(importedResource, paths)));
    }

    public async Task<TemplateDraftView> GetDraftAsync(
        TemplateDraftSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        var pages = new List<TemplateDraftPage>(revision.Pages.Length);
        foreach (var page in revision.Pages.OrderBy(page => page.Order))
        {
            var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
            pages.Add(new TemplateDraftPage(
                ToProjection(page),
                resource,
                !session.BasePageIds.Contains(page.PageId),
                GetPreviewSource(resource, paths)));
        }

        return new TemplateDraftView(session, pages);
    }

    public async Task ConfigurePageAsync(
        TemplateDraftSession session,
        string pageId,
        string name,
        TemplatePageContentTypeOption contentType,
        TemplateGenerationModeOption generationMode,
        int? variantCapacity,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsIncremental && session.BasePageIds.Contains(pageId))
        {
            throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        }

        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        var updated = revision.ConfigurePage(
            pageId,
            name,
            ToDomain(contentType),
            ToDomain(generationMode),
            variantCapacity,
            enabled);
        await templates.SaveDraftRevisionAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderPagesAsync(
        TemplateDraftSession session,
        IReadOnlyList<string> orderedPageIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(orderedPageIds);
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        var updated = revision.ReorderPages(orderedPageIds);
        await templates.SaveDraftRevisionAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePageAsync(
        TemplateDraftSession session,
        string pageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsIncremental && session.BasePageIds.Contains(pageId))
        {
            throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        }

        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        // A draft must retain at least one page; committing an empty revision is invalid
        // and leaving the draft unusable would make recovery impossible.
        if (revision.Pages.Length <= 1)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_PAGE_SET_EMPTY");
        }

        var updated = revision.RemovePage(pageId);
        await templates.SaveDraftRevisionAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplateGroupDetails> CommitDraftAsync(
        TemplateDraftSession session,
        CancellationToken cancellationToken)
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
        var ruleDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest))).ToLowerInvariant()}";
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

    public async Task<TemplateBatchSnapshotView> CreateBatchSnapshotAsync(
        string groupId,
        string batchId,
        IReadOnlyList<string> selectedPageIds,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var revisionId = group.CurrentRevisionId
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var revision = await templates.GetRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var snapshot = BatchTemplateSnapshot.Create(
            $"snapshot_{Guid.NewGuid():N}",
            batchId,
            revision,
            selectedPageIds,
            timeProvider.GetUtcNow());
        if (templates is IBatchSnapshotRepository snapshots)
        {
            await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        return new TemplateBatchSnapshotView(
            snapshot.SnapshotId,
            snapshot.BatchId,
            snapshot.RevisionId,
            snapshot.Pages.Select(page => page.PageId).ToArray(),
            snapshot.RulesDigest,
            snapshot.CreatedAt);
    }

    public async Task<TemplateBatchSnapshotView> UpdateDraftBatchSnapshotAsync(
        string groupId,
        string batchId,
        IReadOnlyList<string> selectedPageIds,
        bool analysisStarted,
        CancellationToken cancellationToken)
    {
        if (templates is not IBatchSnapshotRepository snapshots)
        {
            throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_REPOSITORY_UNAVAILABLE");
        }

        var existing = await snapshots.GetForBatchAsync(batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_NOT_FOUND");
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var revisionId = group.CurrentRevisionId
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var revision = await templates.GetRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        var replacement = existing.ReplaceForDraftBatch(
            $"snapshot_{Guid.NewGuid():N}",
            revision,
            selectedPageIds,
            analysisStarted,
            timeProvider.GetUtcNow());
        await snapshots.ReplaceDraftAsync(
            replacement,
            existing.SnapshotId,
            cancellationToken).ConfigureAwait(false);
        return new TemplateBatchSnapshotView(
            replacement.SnapshotId,
            replacement.BatchId,
            replacement.RevisionId,
            replacement.Pages.Select(page => page.PageId).ToArray(),
            replacement.RulesDigest,
            replacement.CreatedAt);
    }

    public async Task<IReadOnlyList<string>> DeleteTemplateGroupAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var blockers = await templates.GetBlockingBatchIdsAsync(groupId, cancellationToken)
            .ConfigureAwait(false);
        if (blockers.Count > 0)
        {
            return blockers;
        }

        await templates.DeleteGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (ownedStorage is not null)
        {
            await ownedStorage.DeleteGroupFilesAsync(groupId, cancellationToken).ConfigureAwait(false);
        }
        return [];
    }

    private async Task<IReadOnlyList<ManifestEntry>> BuildManifestEntriesAsync(
        TemplateRevision revision,
        CancellationToken cancellationToken)
    {
        var entries = new List<ManifestEntry>(revision.Pages.Length);
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
                     && (!session.IsIncremental || !session.BasePageIds.Contains(page.PageId))))
        {
            var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
            var registered = revision.FrameBindings.Where(binding => string.Equals(
                binding.PageId,
                page.PageId,
                StringComparison.Ordinal)).ToArray();
            if (resource.Inspection.SmartObjectCount <= 0
                || registered.Length == 0
                || registered.Any(binding => !binding.SelectionRequirement.Criteria.Any(criterion =>
                    string.Equals(criterion.Key, "asset_id", StringComparison.Ordinal))))
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

    private static TemplateGroupDetails ToDetails(TemplateGroup group, TemplateRevision revision) => new(
        group.GroupId,
        group.Name,
        revision.RevisionId,
        revision.RevisionNo,
        revision.Pages.OrderBy(page => page.Order).Select(ToProjection).ToArray(),
        IsCommitted: revision.State == TemplateRevisionState.Committed,
        revision.CreatedAt);

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static TemplatePageProjection ToProjection(TemplatePage page) => new(
        page.PageId,
        page.Order,
        page.Name,
        page.ContentType switch
        {
            TemplatePageContentType.DynamicSizeTable => TemplatePageContentTypeOption.DynamicSizeTable,
            _ => TemplatePageContentTypeOption.NormalComposition
        },
        page.GenerationMode switch
        {
            TemplateGenerationMode.OncePerVariant => TemplateGenerationModeOption.OncePerVariant,
            TemplateGenerationMode.VariantCapacityCombination => TemplateGenerationModeOption.VariantCapacityCombination,
            _ => TemplateGenerationModeOption.OncePerProduct
        },
        page.VariantCapacity,
        page.Enabled,
        page.TemplateResourceId);

    private static string? GetPreviewSource(ImportedTemplateResource resource, IPathPolicy? paths)
    {
        if (paths is null || resource.Inspection.Preview is null)
        {
            return null;
        }

        return paths.ResolveOwnedPath(TemplatePageImportService.GetPreviewRelativePath(
            resource.InternalRelativePath));
    }

    private static TemplatePageContentType ToDomain(TemplatePageContentTypeOption value) => value switch
    {
        TemplatePageContentTypeOption.DynamicSizeTable => TemplatePageContentType.DynamicSizeTable,
        _ => TemplatePageContentType.NormalComposition
    };

    private static TemplateGenerationMode ToDomain(TemplateGenerationModeOption value) => value switch
    {
        TemplateGenerationModeOption.OncePerVariant => TemplateGenerationMode.OncePerVariant,
        TemplateGenerationModeOption.VariantCapacityCombination => TemplateGenerationMode.VariantCapacityCombination,
        _ => TemplateGenerationMode.OncePerProduct
    };
}
