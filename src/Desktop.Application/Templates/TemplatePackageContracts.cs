namespace Desktop.Application.Templates;

using Desktop.Application.Psd;

public static class TemplatePackageProtocol
{
    public const string Version = "template-package/v1";
}

public sealed record TemplatePackageEntry(
    string LogicalId,
    string RelativePath,
    long Length,
    string Sha256);

public sealed record TemplatePackageManifest(
    string Protocol,
    string GroupName,
    int RevisionNo,
    string RevisionId,
    string RuleDigest,
    IReadOnlyList<TemplatePackageEntry> Entries,
    IReadOnlyList<TemplatePackagePage>? Pages = null);

public sealed record TemplatePackagePage(
    string PageId,
    int Order,
    string Name,
    TemplatePageContentTypeOption ContentType,
    TemplateGenerationModeOption GenerationMode,
    int? VariantCapacity,
    bool Enabled,
    string ResourceLogicalId,
    PsdInspectionDocument? Inspection,
    IReadOnlyList<TemplatePackageFrameBinding>? Bindings = null);

public sealed record TemplatePackageFrameBinding(
    string FrameId,
    string PageId,
    IReadOnlyList<int> LayerPath,
    int LayerId,
    string LayerName,
    bool Visible,
    bool Clipping,
    bool UserMask,
    bool VectorMask,
    byte Opacity,
    string BlendMode,
    string StructureSignature,
    string SelectionDescription,
    IReadOnlyList<TemplatePackageSelectionCriterion> SelectionCriteria,
    string Intent,
    double Scale,
    double OffsetX,
    double OffsetY,
    double SafeLeft,
    double SafeTop,
    double SafeRight,
    double SafeBottom);

public sealed record TemplatePackageSelectionCriterion(string Key, string Value);

public sealed record TemplatePackageImportResult(
    string GroupId,
    string RevisionId,
    int RevisionNo,
    IReadOnlyList<TemplatePackageEntry> Entries,
    TemplatePackageManifest? Manifest = null);

public interface ITemplatePackagePort
{
    Task ExportAsync(
        TemplatePackageManifest manifest,
        IReadOnlyDictionary<string, string> sourceFiles,
        string destinationPath,
        CancellationToken cancellationToken);

    Task<TemplatePackageImportResult> ValidateAsync(
        string packagePath,
        CancellationToken cancellationToken);

    Task CopyValidatedEntryAsync(
        string packagePath,
        TemplatePackageEntry entry,
        string destinationPath,
        CancellationToken cancellationToken);
}

public interface ITemplatePackageService
{
    Task ExportCurrentAsync(
        string groupId,
        string destinationPath,
        CancellationToken cancellationToken);

    Task<TemplateGroupDetails> ImportAsNewGroupAsync(
        string packagePath,
        string? groupName,
        CancellationToken cancellationToken);

    Task<TemplateGroupDetails> SaveAsNewGroupAsync(
        string groupId,
        string newGroupName,
        CancellationToken cancellationToken);
}
