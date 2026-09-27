using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Inputs;

public sealed record FileInventoryReadResult
{
    private FileInventoryReadResult(
        InputDirectoryInventory? inventory,
        ScanBlockReason? failure)
    {
        Inventory = inventory;
        Failure = failure;
    }

    public InputDirectoryInventory? Inventory { get; }

    public ScanBlockReason? Failure { get; }

    public bool IsSuccess => Inventory is not null;

    public static FileInventoryReadResult Success(InputDirectoryInventory inventory) =>
        new(inventory ?? throw new ArgumentNullException(nameof(inventory)), null);

    public static FileInventoryReadResult Blocked(ScanBlockReason failure) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)));
}

public interface IFileInventory
{
    Task<FileInventoryReadResult> ReadApprovedLevelsAsync(
        string inputRoot,
        CancellationToken cancellationToken);
}

public sealed record AssetFileMetadata(
    long Length,
    string Sha256,
    int PixelWidth,
    int PixelHeight,
    SourceImageFormat Format,
    string MimeType);

public sealed record AssetMetadataReadResult
{
    private AssetMetadataReadResult(
        AssetFileMetadata? metadata,
        ScanBlockReason? failure)
    {
        Metadata = metadata;
        Failure = failure;
    }

    public AssetFileMetadata? Metadata { get; }

    public ScanBlockReason? Failure { get; }

    public bool IsSuccess => Metadata is not null;

    public static AssetMetadataReadResult Success(AssetFileMetadata metadata) =>
        new(metadata ?? throw new ArgumentNullException(nameof(metadata)), null);

    public static AssetMetadataReadResult Blocked(ScanBlockReason failure) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)));
}

public interface IAssetMetadataReader
{
    Task<AssetMetadataReadResult> ReadAsync(
        string inputRoot,
        string sourceRelativePath,
        CancellationToken cancellationToken);
}

public interface IBatchRepository
{
    Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken);

    Task<BatchDraft?> GetAsync(string batchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(CancellationToken cancellationToken);

    Task DeleteDraftAsync(string batchId, CancellationToken cancellationToken);
}

public static class AssetManifestProtocol
{
    public const string Version = "asset-manifest/v1";
}

public sealed record AssetManifestTemplateSnapshot(
    string SnapshotId,
    string RevisionId,
    string RulesDigest,
    IReadOnlyList<string> SelectedPageIds);

public sealed record AssetManifestStyle(
    string StyleId,
    string DefaultSku,
    string SourceRelativeDirectory,
    int Order,
    IReadOnlyList<string> AssetIds);

public sealed record AssetManifestAsset(
    string AssetId,
    string StyleId,
    string SourceRelativePath,
    string FileName,
    long Length,
    string Sha256,
    int PixelWidth,
    int PixelHeight,
    string Format,
    string MimeType,
    string Readability);

public sealed record AssetManifestV1(
    string SchemaVersion,
    string ManifestId,
    string BatchId,
    string InputType,
    AssetManifestTemplateSnapshot TemplateSnapshot,
    IReadOnlyList<AssetManifestStyle> Styles,
    IReadOnlyList<AssetManifestAsset> Assets,
    string InventoryDigest,
    DateTimeOffset CreatedAt)
{
    public static AssetManifestV1 Create(
        BatchDraft scanningDraft,
        BatchInputType inputType,
        IReadOnlyList<StyleDraft> styles,
        IReadOnlyList<SourceAssetMetadata> assets,
        string inventoryDigest,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(scanningDraft);
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(assets);
        if (scanningDraft.ScanStatus != BatchScanStatus.Scanning)
        {
            throw new InputRuleViolationException("INPUT_MANIFEST_REQUIRES_SCANNING_DRAFT");
        }

        var snapshot = scanningDraft.TemplateSnapshot;
        return new AssetManifestV1(
            AssetManifestProtocol.Version,
            InputStableIds.ForManifest(scanningDraft.BatchId, inventoryDigest),
            scanningDraft.BatchId,
            inputType == BatchInputType.SingleStyle ? "single_style" : "style_directory_batch",
            new AssetManifestTemplateSnapshot(
                snapshot.SnapshotId,
                snapshot.RevisionId,
                snapshot.RulesDigest,
                snapshot.Pages.OrderBy(page => page.Order).Select(page => page.PageId).ToArray()),
            styles.OrderBy(style => style.Order).Select(style => new AssetManifestStyle(
                style.StyleId,
                style.DefaultStyleCode,
                style.SourceDirectoryRelativePath,
                style.Order,
                style.AssetIds.ToArray())).ToArray(),
            assets.OrderBy(asset => asset.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(asset => new AssetManifestAsset(
                    asset.AssetId,
                    asset.StyleId,
                    asset.SourceRelativePath,
                    asset.FileName,
                    asset.Length,
                    asset.Sha256,
                    asset.PixelWidth,
                    asset.PixelHeight,
                    FormatValue(asset.Format),
                    asset.MimeType,
                    "readable")).ToArray(),
            inventoryDigest,
            createdAt);
    }

    private static string FormatValue(SourceImageFormat format) => format switch
    {
        SourceImageFormat.Jpeg => "jpeg",
        SourceImageFormat.Png => "png",
        SourceImageFormat.Tiff => "tiff",
        _ => throw new InputRuleViolationException("INPUT_ASSET_FORMAT_INVALID")
    };
}

public sealed record StagedAssetManifest(
    string BatchId,
    string ManifestReference,
    string ManifestDigest,
    string StageToken);

public sealed record AssetManifestInspection(
    string BatchId,
    string ManifestReference,
    string ManifestDigest,
    bool IsStaged,
    bool IsCommitted);

public static class InputCommitJournalProtocol
{
    public const string Version = "input-commit-journal/v1";
    public const string OutputSelectionVersion = "input-commit-journal/v2";
}

public enum InputCommitJournalState
{
    Staged = 0,
    DatabaseCommitted,
    Committed,
    RecoveryRequired
}

/// <summary>
/// Application-owned evidence for the input batch two-phase commit.  The
/// ready draft is intentionally kept in the journal so a committed manifest
/// can rebuild a missing SQLite graph after a process or disk failure.  It
/// contains metadata only; source bytes and PSDs never enter this record.
/// </summary>
public sealed record InputCommitJournal(
    string SchemaVersion,
    string BatchId,
    InputCommitJournalState State,
    StagedAssetManifest StagedManifest,
    BatchDraft ReadyDraft,
    DateTimeOffset UpdatedAt)
{
    public static InputCommitJournal Create(
        StagedAssetManifest stagedManifest,
        BatchDraft readyDraft,
        DateTimeOffset updatedAt) => new(
        readyDraft.OutputSelection is null ? InputCommitJournalProtocol.Version
            : InputCommitJournalProtocol.OutputSelectionVersion,
        readyDraft.BatchId,
        InputCommitJournalState.Staged,
        stagedManifest,
        readyDraft,
        updatedAt);

    public InputCommitJournal WithState(
        InputCommitJournalState state,
        DateTimeOffset updatedAt) => this with
        {
            State = state,
            UpdatedAt = updatedAt
        };
}

public interface IInputCommitJournalStore
{
    Task SaveAsync(
        InputCommitJournal journal,
        CancellationToken cancellationToken);

    Task<InputCommitJournal?> GetAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListBatchIdsAsync(
        CancellationToken cancellationToken);
}

public interface IAssetManifestStore
{
    Task<StagedAssetManifest> StageAsync(
        AssetManifestV1 manifest,
        CancellationToken cancellationToken);

    Task CommitAsync(StagedAssetManifest stagedManifest, CancellationToken cancellationToken);

    Task RollbackAsync(StagedAssetManifest stagedManifest, CancellationToken cancellationToken);

    Task<AssetManifestInspection?> InspectAsync(
        string batchId,
        CancellationToken cancellationToken);
}

public sealed record BatchScanCommitRequest(
    BatchDraft ScanningDraft,
    BatchInputType InputType,
    IReadOnlyList<StyleDraft> Styles,
    IReadOnlyList<SourceAssetMetadata> Assets,
    AssetManifestV1 Manifest);

public sealed record BatchScanCommitResult
{
    private BatchScanCommitResult(
        BatchDraft? draft,
        ScanBlockReason? failure,
        bool requiresRecovery)
    {
        Draft = draft;
        Failure = failure;
        RequiresRecovery = requiresRecovery;
    }

    public BatchDraft? Draft { get; }

    public ScanBlockReason? Failure { get; }

    /// <summary>
    /// Indicates that the durable commit boundary was crossed.  The caller
    /// must keep the journal evidence intact instead of overwriting it with a
    /// normal blocked draft.
    /// </summary>
    public bool RequiresRecovery { get; }

    public bool IsSuccess => Draft?.ScanStatus == BatchScanStatus.ScanReady;

    public static BatchScanCommitResult Success(BatchDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ScanStatus != BatchScanStatus.ScanReady)
        {
            throw new InputRuleViolationException("INPUT_COMMIT_RESULT_NOT_READY");
        }

        return new BatchScanCommitResult(draft, null, false);
    }

    public static BatchScanCommitResult Blocked(
        ScanBlockReason failure,
        bool requiresRecovery = false) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)), requiresRecovery);
}

public interface IBatchScanCommitter
{
    Task<BatchScanCommitResult> CommitAsync(
        BatchScanCommitRequest request,
        CancellationToken cancellationToken);
}

public sealed record CreateBatchDraftCommand(
    string BatchId,
    string InputRoot,
    string OutputRoot,
    BatchTemplateSnapshot TemplateSnapshot,
    BatchSelectionMode SelectionMode,
    bool IncludeEditablePsd,
    BatchOutputSelection? OutputSelection = null);

public sealed record CreateBatchFromTemplateCommand(
    string BatchId,
    string TemplateGroupId,
    string InputRoot,
    string OutputRoot,
    IReadOnlyList<string> SelectedPageIds,
    BatchSelectionMode SelectionMode,
    bool IncludeEditablePsd,
    BatchOutputSelection? OutputSelection = null);

public sealed record BatchScanOptions
{
    public BatchScanOptions(int maxConcurrentMetadataReads = 4)
    {
        if (maxConcurrentMetadataReads is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentMetadataReads));
        }

        MaxConcurrentMetadataReads = maxConcurrentMetadataReads;
    }

    public int MaxConcurrentMetadataReads { get; }
}
