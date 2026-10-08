using Desktop.Domain.Templates;
using Desktop.Application.Files;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desktop.Application.Templates;

public sealed partial class TemplateWorkspaceService(
    ITemplateRepository templates,
    ITemplateResourceRepository resources,
    TemplatePageImportService importer,
    TimeProvider timeProvider,
    IManifestStore? manifestStore = null,
    ITemplateOwnedStorage? ownedStorage = null,
    IPathPolicy? paths = null,
    ITemplateSourceFilePort? sourceFiles = null,
    IFileStore? files = null) : ITemplateWorkspaceService, ITemplatePagePresentationService, ISizeTableTemplateCalibrationService, ITemplateRuleEditingService
{
    public async Task CalibrateSizeTableAsync(TemplateDraftSession session, string pageId,
        TemplateSizeCalibration calibration, CancellationToken cancellationToken)
    {
        if (!session.CanEditPage(pageId)) throw new TemplateRuleViolationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        if (revision.GroupId != session.GroupId) throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        var page = revision.Pages.SingleOrDefault(item => item.PageId == pageId)
            ?? throw new TemplateRuleViolationException("TPL_PAGE_NOT_FOUND");
        var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
        if (!calibration.Matches(resource.Inspection))
            throw new TemplateRuleViolationException("SIZE_TABLE_CALIBRATION_INVALID");
        await templates.SaveDraftRevisionAsync(revision.CalibrateSizeTable(pageId, calibration.ToDomain()), cancellationToken).ConfigureAwait(false);
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

    private static string PageEditDraftId(string revisionId) =>
        $"page_edit_{revisionId}";

    private static string CalibrationDraftId(string revisionId, string pageId) =>
        $"calibration_{revisionId}_{pageId}";

    private async Task<HashSet<string>> ResolveEditablePageIdsAsync(
        TemplateRevision draft,
        TemplateRevision current,
        CancellationToken cancellationToken)
    {
        var basePageIds = current.Pages
            .Select(page => page.PageId)
            .ToHashSet(StringComparer.Ordinal);
        var editablePageIds = new HashSet<string>(StringComparer.Ordinal);

        var pageEditScope = await templates.GetCalibrationDraftAsync(
            PageEditDraftId(draft.RevisionId),
            cancellationToken).ConfigureAwait(false);
        AddEditablePages(pageEditScope, draft.RevisionId, current.RevisionId, basePageIds, editablePageIds);

        // The calibration editor's durable state is also a reliable page-edit
        // marker. Check the current format and the legacy format so an already
        // saved local draft remains recoverable after an app update.
        foreach (var pageId in basePageIds)
        {
            var draftScope = await templates.GetCalibrationDraftAsync(
                CalibrationDraftId(draft.RevisionId, pageId),
                cancellationToken).ConfigureAwait(false);
            AddEditablePages(draftScope, draft.RevisionId, current.RevisionId, basePageIds, editablePageIds);

            var legacyScope = await templates.GetCalibrationDraftAsync(
                CalibrationDraftId(current.RevisionId, pageId),
                cancellationToken).ConfigureAwait(false);
            AddEditablePages(legacyScope, draft.RevisionId, current.RevisionId, basePageIds, editablePageIds);
        }

        return editablePageIds;
    }

    private static void AddEditablePages(
        CalibrationDraft? scope,
        string draftRevisionId,
        string currentRevisionId,
        HashSet<string> basePageIds,
        HashSet<string> editablePageIds)
    {
        if (scope is null
            || (!string.Equals(scope.BaseRevisionId, draftRevisionId, StringComparison.Ordinal)
                && !string.Equals(scope.BaseRevisionId, currentRevisionId, StringComparison.Ordinal)))
        {
            return;
        }

        foreach (var pageId in scope.AffectedPageIds)
        {
            if (basePageIds.Contains(pageId))
            {
                editablePageIds.Add(pageId);
            }
        }
    }

    private static bool HasSamePageGraph(TemplateRevision left, TemplateRevision right)
    {
        var leftPages = left.Pages
            .OrderBy(page => page.Order)
            .Select(page => new
            {
                page.PageId,
                page.Order,
                page.Name,
                page.ContentType,
                page.GenerationMode,
                page.VariantCapacity,
                page.Enabled,
                page.TemplateResourceId
            });
        var rightPages = right.Pages
            .OrderBy(page => page.Order)
            .Select(page => new
            {
                page.PageId,
                page.Order,
                page.Name,
                page.ContentType,
                page.GenerationMode,
                page.VariantCapacity,
                page.Enabled,
                page.TemplateResourceId
            });
        return string.Equals(
                   JsonSerializer.Serialize(leftPages),
                   JsonSerializer.Serialize(rightPages),
                   StringComparison.Ordinal)
               && string.Equals(
                   JsonSerializer.Serialize(left.FrameBindings.OrderBy(binding => binding.FrameId)),
                   JsonSerializer.Serialize(right.FrameBindings.OrderBy(binding => binding.FrameId)),
                   StringComparison.Ordinal);
    }

    private static TemplateDraftSession CreatePageEditSession(
        TemplateGroup group,
        TemplateRevision current,
        TemplateRevision draft,
        IReadOnlySet<string> editablePageIds) =>
        new(
            group.GroupId,
            group.Name,
            draft.RevisionId,
            draft.RevisionNo,
            current.Pages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal))
        {
            EditablePageIds = editablePageIds.ToHashSet(StringComparer.Ordinal)
        };

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
        page.TemplateResourceId,
        page.Notes,
        page.CanvasX,
        page.CanvasY,
        TemplateSizeCalibration.FromDomain(page.SizeTableCalibration));

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
