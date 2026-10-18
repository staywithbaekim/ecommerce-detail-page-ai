using Desktop.Domain.Templates;
using Desktop.Application.Files;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desktop.Application.Templates;

public sealed partial class TemplateWorkspaceService
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
                revision.State == TemplateRevisionState.Committed,
                group.UpdatedAt);
    }

    public async Task<TemplateGroupDetails?> GetCommittedDetailsAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (group?.CurrentRevisionId is null)
        {
            return null;
        }

        var revision = await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken)
            .ConfigureAwait(false);
        return revision is null
            ? null
            : new TemplateGroupDetails(
                group.GroupId,
                group.Name,
                revision.RevisionId,
                revision.RevisionNo,
                revision.Pages.OrderBy(page => page.Order).Select(ToProjection).ToArray(),
                IsCommitted: true,
                group.UpdatedAt);
    }

    public async Task RenameTemplateGroupAsync(
        string groupId,
        string newName,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var renamed = group.Rename(newName?.Trim() ?? string.Empty, timeProvider.GetUtcNow());
        await templates.SaveGroupAsync(renamed, cancellationToken).ConfigureAwait(false);
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

    public async Task<TemplateDraftSession> BeginPageEditAsync(
        string groupId,
        string pageId,
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
        if (current.Pages.All(page => !string.Equals(page.PageId, pageId, StringComparison.Ordinal)))
        {
            throw new TemplateRuleViolationException("TPL_PAGE_NOT_FOUND");
        }

        var latest = await templates.GetLatestRevisionForGroupAsync(groupId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is { State: TemplateRevisionState.Draft })
        {
            var editablePageIds = await ResolveEditablePageIdsAsync(
                latest,
                current,
                cancellationToken).ConfigureAwait(false);
            if (editablePageIds.Contains(pageId))
            {
                return CreatePageEditSession(group, current, latest, editablePageIds);
            }

            // A crash can happen after the draft revision is durable but before
            // its page-edit marker is written. If the graph is still an exact
            // copy of the committed revision, the user's explicit Edit action
            // is sufficient intent to recover the selected page's scope.
            if (HasSamePageGraph(latest, current))
            {
                editablePageIds.Add(pageId);
                return CreatePageEditSession(group, current, latest, editablePageIds);
            }

            throw new TemplateRuleViolationException("TPL_DRAFT_IN_PROGRESS");
        }

        var revisionId = NewId("revision");
        var revision = TemplateRevision.CreateIncrement(
            revisionId,
            current,
            current.RevisionNo + 1,
            timeProvider.GetUtcNow(),
            []);
        await templates.SaveDraftRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        await templates.SaveCalibrationDraftAsync(
            CalibrationDraft.Create(
                PageEditDraftId(revision.RevisionId),
                revision.RevisionId,
                [pageId]),
            cancellationToken).ConfigureAwait(false);

        return new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revisionId,
            revision.RevisionNo,
            current.Pages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal))
        {
            EditablePageIds = new HashSet<string>([pageId], StringComparer.Ordinal)
        };
    }

    public async Task<TemplateDraftSession> BeginFullEditAsync(
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
        var latest = await templates.GetLatestRevisionForGroupAsync(groupId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is { State: TemplateRevisionState.Draft })
        {
            var editablePageIds = await ResolveEditablePageIdsAsync(
                latest,
                current,
                cancellationToken).ConfigureAwait(false);
            if (editablePageIds.Count == current.Pages.Length
                && current.Pages.All(page => editablePageIds.Contains(page.PageId)))
            {
                return CreatePageEditSession(group, current, latest, editablePageIds);
            }

            throw new TemplateRuleViolationException("TPL_DRAFT_IN_PROGRESS");
        }

        var revisionId = NewId("revision");
        var revision = TemplateRevision.CreateIncrement(
            revisionId,
            current,
            current.RevisionNo + 1,
            timeProvider.GetUtcNow(),
            []);
        await templates.SaveDraftRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        var allPageIds = current.Pages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal);
        await templates.SaveCalibrationDraftAsync(
            CalibrationDraft.Create(
                PageEditDraftId(revision.RevisionId),
                revision.RevisionId,
                allPageIds),
            cancellationToken).ConfigureAwait(false);

        return new TemplateDraftSession(
            group.GroupId,
            group.Name,
            revisionId,
            revision.RevisionNo,
            allPageIds)
        {
            EditablePageIds = allPageIds
        };
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
        IReadOnlySet<string> editablePageIds = new HashSet<string>(StringComparer.Ordinal);
        TemplateRevision? currentRevision = null;
        if (group.CurrentRevisionId is not null)
        {
            currentRevision = await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_INCREMENT_BASE_INVALID");
            basePageIds = currentRevision.Pages
                .Select(page => page.PageId)
                .ToHashSet(StringComparer.Ordinal);
        }

        if (currentRevision is not null)
        {
            editablePageIds = await ResolveEditablePageIdsAsync(
                draft,
                currentRevision,
                cancellationToken).ConfigureAwait(false);
        }

        return new TemplateDraftSession(
            group.GroupId,
            group.Name,
            draft.RevisionId,
            draft.RevisionNo,
            basePageIds)
        {
            EditablePageIds = editablePageIds
        };
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
                    session.IsPageAffected(existing.PageId),
                    GetPreviewSource(importedResource, paths)));
        }

        var recognition = TemplatePageTypeRecognizer.Recognize(pageName, importedResource.Inspection);
        var contentType = recognition switch
        {
            TemplatePageTypeRecognition.SizeTablePage => TemplatePageContentType.DynamicSizeTable,
            _ => TemplatePageContentType.NormalComposition
        };
        var page = TemplatePage.Create(
            NewId("page"),
            revision.RevisionId,
            revision.Pages.Length,
            pageName,
            contentType,
            TemplateGenerationMode.OncePerProduct,
            variantCapacity: null,
            enabled: true,
            importedResource.ResourceId,
            typeRecognition: recognition);
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
                session.IsPageAffected(page.PageId),
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
        if (!session.CanEditPage(pageId))
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

    public async Task UpdatePagePresentationAsync(
        TemplateDraftSession session,
        TemplatePagePresentationUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(update);
        if (!session.CanEditPage(update.PageId))
        {
            throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        }

        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        var page = revision.Pages.SingleOrDefault(item =>
            string.Equals(item.PageId, update.PageId, StringComparison.Ordinal))
            ?? throw new TemplateRuleViolationException("TPL_PAGE_NOT_FOUND");
        var notes = TemplateValueRules.OptionalText(update.Notes, 2_000, "TPL_PAGE_NOTES_INVALID") ?? string.Empty;
        var updated = revision.ConfigurePage(
            page.PageId,
            page.Name,
            page.ContentType,
            page.GenerationMode,
            page.VariantCapacity,
            page.Enabled,
            notes,
            update.CanvasX,
            update.CanvasY);
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
        if (!session.CanEditPage(pageId))
        {
            throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        }

        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        var updated = revision.RemovePage(pageId);
        await templates.SaveDraftRevisionAsync(updated, cancellationToken).ConfigureAwait(false);

        // Removing a page is a logical draft edit. Keep the application-owned PSD
        // copy and preview until the draft is abandoned or a reference-aware cleanup
        // path runs after the edit is no longer undoable. The rule editor records the
        // resource ID/path in its inverse patch, so deleting the files here would make
        // a later undo restore metadata that cannot be opened.
    }


}
