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
    string TemplateResourceId);

public sealed record TemplateDraftSession(
    string GroupId,
    string GroupName,
    string DraftRevisionId,
    int RevisionNo,
    IReadOnlySet<string> BasePageIds)
{
    public bool IsIncremental => BasePageIds.Count > 0;
}

public sealed record TemplateDraftPage(
    TemplatePageProjection Page,
    ImportedTemplateResource Resource,
    bool IsAffected,
    string? PreviewSourceReference = null);

public sealed record TemplateDraftView(
    TemplateDraftSession Session,
    IReadOnlyList<TemplateDraftPage> Pages);

public sealed record TemplateWorkspaceImportResult(
    TemplatePageImportResult Import,
    TemplateDraftPage? AddedPage);

public interface ITemplateWorkspaceService
{
    Task<IReadOnlyList<TemplateLibraryItem>> ListAsync(CancellationToken cancellationToken);

    Task<TemplateGroupDetails?> GetDetailsAsync(string groupId, CancellationToken cancellationToken);

    Task<TemplateGroupDetails?> GetCommittedDetailsAsync(string groupId, CancellationToken cancellationToken);

    Task<TemplateDraftSession> BeginCreateAsync(string groupName, CancellationToken cancellationToken);

    Task<TemplateDraftSession> BeginIncrementAsync(string groupId, CancellationToken cancellationToken);

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
