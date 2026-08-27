using Desktop.Application.Psd;

namespace Desktop.Application.Templates;

public enum TemplateDuplicateDecision
{
    NotSpecified = 0,
    ReuseExisting,
    ContinueAsSeparate,
    Cancel
}

public enum TemplatePageImportStatus
{
    Imported = 0,
    ReusedExisting,
    DuplicateReviewRequired,
    Cancelled,
    Failed
}

public sealed record TemplatePageImportRequest(
    string CommandId,
    string GroupId,
    string DraftId,
    string ResourceId,
    string SourcePsdPath,
    TemplateDuplicateDecision DuplicateDecision = TemplateDuplicateDecision.NotSpecified,
    string? SelectedDuplicateResourceId = null);

public sealed record TemplateImportError(
    string Code,
    bool Retryable,
    string Scope,
    string UserAction);

public sealed record TemplateSourceFingerprint(long Length, string Sha256);

public sealed record SourceFingerprintResult(
    TemplateSourceFingerprint? Fingerprint,
    TemplateImportError? Error)
{
    public bool IsSuccess => Fingerprint is not null && Error is null;

    public static SourceFingerprintResult Success(TemplateSourceFingerprint fingerprint) => new(fingerprint, null);

    public static SourceFingerprintResult Failure(TemplateImportError error) => new(null, error);
}

public sealed record OwnedTemplateCopy(
    string InternalRelativePath,
    long Length,
    string Sha256);

public sealed record OwnedTemplateCopyResult(
    OwnedTemplateCopy? Copy,
    TemplateImportError? Error)
{
    public bool IsSuccess => Copy is not null && Error is null;

    public static OwnedTemplateCopyResult Success(OwnedTemplateCopy copy) => new(copy, null);

    public static OwnedTemplateCopyResult Failure(TemplateImportError error) => new(null, error);
}

public sealed record ImportedTemplateResource(
    string ResourceId,
    string GroupId,
    string DraftId,
    string InternalRelativePath,
    long Length,
    string Sha256,
    string StructureSignature,
    PsdInspectionDocument Inspection,
    DateTimeOffset CreatedAt);

public sealed record TemplatePageImportResult(
    TemplatePageImportStatus Status,
    ImportedTemplateResource? Resource,
    IReadOnlyList<ImportedTemplateResource> DuplicateCandidates,
    TemplateImportError? Error,
    bool Idempotent)
{
    public bool IsSuccess => Status is TemplatePageImportStatus.Imported or TemplatePageImportStatus.ReusedExisting;
}

public interface ITemplateSourceFilePort
{
    Task<SourceFingerprintResult> FingerprintAsync(
        string sourcePsdPath,
        CancellationToken cancellationToken);

    Task<OwnedTemplateCopyResult> CopyVerifiedAsync(
        string sourcePsdPath,
        TemplateSourceFingerprint expectedSource,
        string destinationRelativePath,
        CancellationToken cancellationToken);

    Task<bool> DeleteOwnedCopyAsync(
        string internalRelativePath,
        CancellationToken cancellationToken);
}

public interface ITemplateResourceRepository
{
    Task<ImportedTemplateResource?> GetByCommandIdAsync(
        string commandId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ImportedTemplateResource>> FindBySha256Async(
        string sha256,
        CancellationToken cancellationToken);

    Task<ImportedTemplateResource> SaveImportedAsync(
        string commandId,
        ImportedTemplateResource resource,
        CancellationToken cancellationToken);

    Task<ImportedTemplateResource> RecordExistingSelectionAsync(
        string commandId,
        ImportedTemplateResource resource,
        CancellationToken cancellationToken);
}
