namespace Desktop.Application.Templates;

public sealed record TemplateLibraryItem(
    string GroupId,
    string Name,
    string? CurrentRevisionId,
    int PageCount,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record TemplateGroupDetails(
    string GroupId,
    string Name,
    string RevisionId,
    int RevisionNo,
    IReadOnlyList<TemplatePageProjection> Pages,
    bool IsCommitted,
    DateTimeOffset UpdatedAt);

public enum TemplatePageContentTypeOption
{
    NormalComposition = 0,
    DynamicSizeTable
}

public enum TemplateGenerationModeOption
{
    OncePerProduct = 0,
    OncePerVariant,
    VariantCapacityCombination
}

public sealed record TemplatePageProjection(
    string PageId,
    int Order,
    string Name,
    TemplatePageContentTypeOption ContentType,
    TemplateGenerationModeOption GenerationMode,
    int? VariantCapacity,
    bool Enabled,
    string TemplateResourceId,
    string Notes = "",
    double CanvasX = 0,
    double CanvasY = 0,
    TemplateSizeCalibration? SizeTableCalibration = null);

public interface ISizeTableTemplateCalibrationService
{
    Task CalibrateSizeTableAsync(TemplateDraftSession session, string pageId,
        TemplateSizeCalibration calibration, CancellationToken cancellationToken);
}

public sealed record TemplateDraftSession(
    string GroupId,
    string GroupName,
    string DraftRevisionId,
    int RevisionNo,
    IReadOnlySet<string> BasePageIds)
{
    public bool IsIncremental => BasePageIds.Count > 0;

    // An incremental revision normally affects only pages outside the base set.
    // Page editing explicitly opts selected inherited pages back into the draft.
    public IReadOnlySet<string> EditablePageIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public bool IsPageAffected(string pageId) =>
        !BasePageIds.Contains(pageId) || EditablePageIds.Contains(pageId);

    public bool CanEditPage(string pageId) => IsPageAffected(pageId);

    public bool IsPageEdit => EditablePageIds.Count > 0;

    public bool IsFullTemplateEdit => IsPageEdit
        && BasePageIds.Count > 0
        && EditablePageIds.Count == BasePageIds.Count
        && BasePageIds.All(EditablePageIds.Contains);
}

public sealed record TemplateDraftPage(
    TemplatePageProjection Page,
    ImportedTemplateResource Resource,
    bool IsAffected,
    string? PreviewSourceReference = null);

public sealed record TemplateDraftView(
    TemplateDraftSession Session,
    IReadOnlyList<TemplateDraftPage> Pages);

public sealed record TemplatePagePresentationUpdate(
    string PageId,
    string Notes,
    double CanvasX,
    double CanvasY);

/// <summary>
/// Stores editor-only presentation state without coupling it to output order.
/// Implemented by the workspace service so older callers and test doubles can
/// continue using the original page configuration contract.
/// </summary>
public interface ITemplatePagePresentationService
{
    Task UpdatePagePresentationAsync(
        TemplateDraftSession session,
        TemplatePagePresentationUpdate update,
        CancellationToken cancellationToken);
}

public sealed record TemplateWorkspaceImportResult(
    TemplatePageImportResult Import,
    TemplateDraftPage? AddedPage);

public interface ITemplateWorkspaceService
{
    Task<IReadOnlyList<TemplateLibraryItem>> ListAsync(CancellationToken cancellationToken);

    Task<TemplateGroupDetails?> GetDetailsAsync(string groupId, CancellationToken cancellationToken);

    Task<TemplateGroupDetails?> GetCommittedDetailsAsync(string groupId, CancellationToken cancellationToken);

    Task RenameTemplateGroupAsync(
        string groupId,
        string newName,
        CancellationToken cancellationToken);

    Task<TemplateDraftSession> BeginCreateAsync(string groupName, CancellationToken cancellationToken);

    Task<TemplateDraftSession> BeginIncrementAsync(string groupId, CancellationToken cancellationToken);

    Task<TemplateDraftSession> BeginPageEditAsync(
        string groupId,
        string pageId,
        CancellationToken cancellationToken);

    Task<TemplateDraftSession> BeginFullEditAsync(
        string groupId,
        CancellationToken cancellationToken);

    Task<TemplateDraftSession> ResumeDraftAsync(string groupId, CancellationToken cancellationToken);

    Task<TemplateWorkspaceImportResult> ImportPageAsync(
        TemplateDraftSession session,
        string commandId,
        string resourceId,
        string sourcePsdPath,
        string pageName,
        TemplateDuplicateDecision duplicateDecision,
        string? selectedDuplicateResourceId,
        CancellationToken cancellationToken);

    Task<TemplateDraftView> GetDraftAsync(
        TemplateDraftSession session,
        CancellationToken cancellationToken);

    Task ConfigurePageAsync(
        TemplateDraftSession session,
        string pageId,
        string name,
        TemplatePageContentTypeOption contentType,
        TemplateGenerationModeOption generationMode,
        int? variantCapacity,
        bool enabled,
        CancellationToken cancellationToken);

    Task ReorderPagesAsync(
        TemplateDraftSession session,
        IReadOnlyList<string> orderedPageIds,
        CancellationToken cancellationToken);

    Task RemovePageAsync(
        TemplateDraftSession session,
        string pageId,
        CancellationToken cancellationToken);

    Task<TemplateGroupDetails> CommitDraftAsync(
        TemplateDraftSession session,
        CancellationToken cancellationToken);

    Task<TemplateGroupDetails> CommitDraftAsNewGroupAsync(
        TemplateDraftSession session,
        string newGroupName,
        CancellationToken cancellationToken);

    Task<TemplateBatchSnapshotView> CreateBatchSnapshotAsync(
        string groupId,
        string batchId,
        IReadOnlyList<string> selectedPageIds,
        CancellationToken cancellationToken);

    Task<TemplateBatchSnapshotView> UpdateDraftBatchSnapshotAsync(
        string groupId,
        string batchId,
        IReadOnlyList<string> selectedPageIds,
        bool analysisStarted,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> DeleteTemplateGroupAsync(
        string groupId,
        CancellationToken cancellationToken);
}

public sealed record TemplateBatchSnapshotView(
    string SnapshotId,
    string BatchId,
    string RevisionId,
    IReadOnlyList<string> PageIds,
    string RulesDigest,
    DateTimeOffset CreatedAt);
