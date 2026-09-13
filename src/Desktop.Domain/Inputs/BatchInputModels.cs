using System.Collections.Immutable;
using System.Text;

namespace Desktop.Domain.Inputs;

public enum BatchInputType
{
    SingleStyle = 0,
    StyleDirectoryBatch
}

public enum BatchScanStatus
{
    Draft = 0,
    Scanning,
    ScanReady,
    ScanBlocked
}

public enum BatchSelectionMode
{
    Automatic = 0,
    Manual
}

public enum ScanBlockCategory
{
    DirectoryStructure = 0,
    SourceAsset,
    PathAccess,
    Cancellation,
    System
}

public enum SourceImageFormat
{
    Jpeg = 0,
    Png,
    Tiff
}

public enum AssetReadability
{
    Readable = 0,
    Unreadable
}

public sealed record ScanBlockReason
{
    private ScanBlockReason(string code, ScanBlockCategory category, string userAction)
    {
        Code = code;
        Category = category;
        UserAction = userAction;
    }

    public string Code { get; }

    public ScanBlockCategory Category { get; }

    public string UserAction { get; }

    public static ScanBlockReason Create(
        string code,
        ScanBlockCategory category,
        string userAction) => new(
            InputValueRules.Identifier(code, "INPUT_BLOCK_CODE_INVALID"),
            category,
            InputValueRules.RequiredText(userAction, 512, "INPUT_BLOCK_ACTION_INVALID"));

    public static ScanBlockReason MixedLayout() => Create(
        "INPUT_MIXED_LAYOUT",
        ScanBlockCategory.DirectoryStructure,
        "所选目录同时包含根目录图片和款式子文件夹。请整理后重新选择；软件不会猜测图片归属。");

    public static ScanBlockReason EmptyLayout() => Create(
        "INPUT_NO_SUPPORTED_IMAGES",
        ScanBlockCategory.DirectoryStructure,
        "未找到可用商品图片。请选择直接包含商品图片的单款目录，或包含款式子文件夹的批次目录。");

    public static ScanBlockReason Cancelled() => Create(
        "INPUT_SCAN_CANCELLED",
        ScanBlockCategory.Cancellation,
        "扫描已取消。源图片没有更改，可在方便时重新扫描。");

    public static ScanBlockReason SystemFailure() => Create(
        "INPUT_SCAN_SYSTEM_FAILURE",
        ScanBlockCategory.System,
        "扫描未能安全提交。请重试；如果问题持续，请导出诊断信息。");

    public static ScanBlockReason PersistenceFailure() => Create(
        "INPUT_PERSISTENCE_FAILURE",
        ScanBlockCategory.System,
        "本地状态未能安全保存，未发送 AI 请求。请重新扫描并建立新批次；源图片未修改。");

    public static ScanBlockReason ManifestDatabaseMismatch() => Create(
        "INPUT_MANIFEST_DATABASE_MISMATCH",
        ScanBlockCategory.System,
        "本地批次清单与数据库记录不一致，已阻止 AI 请求。请点击“重试扫描”；系统会使用同一文件夹建立新批次，源图片和 PSD 不会修改。");

    public static ScanBlockReason Interrupted() => Create(
        "INPUT_SCAN_INTERRUPTED",
        ScanBlockCategory.System,
        "上次扫描未安全完成。源图片没有更改，请重新扫描。");

    public static ScanBlockReason TooManyStyles() => Create(
        "INPUT_STYLE_LIMIT_EXCEEDED",
        ScanBlockCategory.DirectoryStructure,
        "所选目录包含过多款式文件夹。请拆分为多个批次后重试。");

    public static ScanBlockReason TooManyAssets() => Create(
        "INPUT_ASSET_LIMIT_EXCEEDED",
        ScanBlockCategory.DirectoryStructure,
        "所选目录包含过多商品图片。请拆分为多个批次后重试。");
}

public sealed record InputFileReference
{
    private InputFileReference(string relativePath, string fileName)
    {
        RelativePath = relativePath;
        FileName = fileName;
    }

    public string RelativePath { get; }

    public string FileName { get; }

    public bool HasSupportedExtension => InputImageRules.IsSupportedExtension(FileName);

    public static InputFileReference Create(string relativePath, string fileName) => new(
        InputValueRules.RelativeSourcePath(relativePath, allowRoot: false),
        InputValueRules.FileName(fileName));
}

public sealed record FirstLevelDirectoryInventory
{
    private FirstLevelDirectoryInventory(
        string name,
        string relativePath,
        ImmutableArray<InputFileReference> directFiles)
    {
        Name = name;
        RelativePath = relativePath;
        DirectFiles = directFiles;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public ImmutableArray<InputFileReference> DirectFiles { get; }

    public static FirstLevelDirectoryInventory Create(
        string name,
        string relativePath,
        IEnumerable<InputFileReference>? directFiles) => new(
            InputValueRules.RequiredText(name, 256, "INPUT_DIRECTORY_NAME_INVALID"),
            InputValueRules.RelativeSourcePath(relativePath, allowRoot: false),
            InputValueRules.FreezeFiles(directFiles));
}

public sealed record InputDirectoryInventory
{
    private InputDirectoryInventory(
        string rootDisplayName,
        ImmutableArray<InputFileReference> rootFiles,
        ImmutableArray<FirstLevelDirectoryInventory> firstLevelDirectories)
    {
        RootDisplayName = rootDisplayName;
        RootFiles = rootFiles;
        FirstLevelDirectories = firstLevelDirectories;
    }

    public string RootDisplayName { get; }

    public ImmutableArray<InputFileReference> RootFiles { get; }

    public ImmutableArray<FirstLevelDirectoryInventory> FirstLevelDirectories { get; }

    public static InputDirectoryInventory Create(
        string rootDisplayName,
        IEnumerable<InputFileReference>? rootFiles,
        IEnumerable<FirstLevelDirectoryInventory>? firstLevelDirectories)
    {
        var directories = firstLevelDirectories?.ToImmutableArray() ?? [];
        if (directories.Any(directory => directory is null)
            || directories.Select(directory => directory.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Length)
        {
            throw new InputRuleViolationException("INPUT_DIRECTORY_INVENTORY_INVALID");
        }

        return new InputDirectoryInventory(
            InputValueRules.RequiredText(rootDisplayName, 256, "INPUT_DIRECTORY_NAME_INVALID"),
            InputValueRules.FreezeFiles(rootFiles),
            directories);
    }
}

public sealed record DetectedStyleSource(
    string DefaultStyleCode,
    string SourceDirectoryRelativePath,
    ImmutableArray<InputFileReference> Files);

public sealed record InputLayoutDetection
{
    private InputLayoutDetection(
        BatchInputType? inputType,
        ImmutableArray<DetectedStyleSource> styles,
        ScanBlockReason? blockReason)
    {
        InputType = inputType;
        Styles = styles;
        BlockReason = blockReason;
    }

    public BatchInputType? InputType { get; }

    public ImmutableArray<DetectedStyleSource> Styles { get; }

    public ScanBlockReason? BlockReason { get; }

    public bool IsBlocked => BlockReason is not null;

    public static InputLayoutDetection Success(
        BatchInputType inputType,
        IEnumerable<DetectedStyleSource> styles)
    {
        var frozen = styles.ToImmutableArray();
        if (frozen.IsDefaultOrEmpty || frozen.Any(style => style.Files.IsDefaultOrEmpty))
        {
            throw new InputRuleViolationException("INPUT_LAYOUT_RESULT_INVALID");
        }

        return new InputLayoutDetection(inputType, frozen, null);
    }

    public static InputLayoutDetection Blocked(ScanBlockReason reason) => new(null, [], reason);
}

public sealed record StyleDraft
{
    private StyleDraft(
        string styleId,
        string defaultStyleCode,
        string sourceDirectoryRelativePath,
        int order,
        ImmutableArray<string> assetIds)
    {
        StyleId = styleId;
        DefaultStyleCode = defaultStyleCode;
        SourceDirectoryRelativePath = sourceDirectoryRelativePath;
        Order = order;
        AssetIds = assetIds;
    }

    public string StyleId { get; }

    public string DefaultStyleCode { get; }

    public string SourceDirectoryRelativePath { get; }

    public int Order { get; }

    public ImmutableArray<string> AssetIds { get; }

    public static StyleDraft Create(
        string styleId,
        string defaultStyleCode,
        string sourceDirectoryRelativePath,
        int order,
        IEnumerable<string> assetIds)
    {
        var frozenAssetIds = assetIds
            .Select(assetId => InputValueRules.Identifier(assetId, "INPUT_ASSET_ID_INVALID"))
            .ToImmutableArray();
        if (order < 0
            || frozenAssetIds.IsDefaultOrEmpty
            || frozenAssetIds.Distinct(StringComparer.Ordinal).Count() != frozenAssetIds.Length)
        {
            throw new InputRuleViolationException("INPUT_STYLE_INVALID");
        }

        return new StyleDraft(
            InputValueRules.Identifier(styleId, "INPUT_STYLE_ID_INVALID"),
            InputValueRules.RequiredText(defaultStyleCode, 256, "INPUT_STYLE_CODE_INVALID"),
            InputValueRules.RelativeSourcePath(sourceDirectoryRelativePath, allowRoot: true),
            order,
            frozenAssetIds);
    }
}

public sealed record SourceAssetMetadata
{
    private SourceAssetMetadata(
        string assetId,
        string styleId,
        string sourceRelativePath,
        string fileName,
        long length,
        string sha256,
        int pixelWidth,
        int pixelHeight,
        SourceImageFormat format,
        string mimeType,
        AssetReadability readability)
    {
        AssetId = assetId;
        StyleId = styleId;
        SourceRelativePath = sourceRelativePath;
        FileName = fileName;
        Length = length;
        Sha256 = sha256;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Format = format;
        MimeType = mimeType;
        Readability = readability;
    }

    public string AssetId { get; }

    public string StyleId { get; }

    public string SourceRelativePath { get; }

    public string FileName { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public SourceImageFormat Format { get; }

    public string MimeType { get; }

    public AssetReadability Readability { get; }

    public static SourceAssetMetadata CreateReadable(
        string assetId,
        string styleId,
        string sourceRelativePath,
        string fileName,
        long length,
        string sha256,
        int pixelWidth,
        int pixelHeight,
        SourceImageFormat format,
        string mimeType)
    {
        if (length <= 0 || pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new InputRuleViolationException("INPUT_ASSET_METADATA_INVALID");
        }

        return new SourceAssetMetadata(
            InputValueRules.Identifier(assetId, "INPUT_ASSET_ID_INVALID"),
            InputValueRules.Identifier(styleId, "INPUT_STYLE_ID_INVALID"),
            InputValueRules.RelativeSourcePath(sourceRelativePath, allowRoot: false),
            InputValueRules.FileName(fileName),
            length,
            InputValueRules.Sha256(sha256),
            pixelWidth,
            pixelHeight,
            format,
            InputValueRules.MimeType(mimeType, format),
            AssetReadability.Readable);
    }
}

public sealed class InputRuleViolationException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public static class InputImageRules
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff"
    };

    public static bool IsSupportedExtension(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && SupportedExtensions.Contains(Path.GetExtension(fileName));
}

internal static class InputValueRules
{
    public static string Identifier(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new InputRuleViolationException(code);
        }

        return value;
    }

    public static string RequiredText(string value, int maximumLength, string code)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
        {
            throw new InputRuleViolationException(code);
        }

        return normalized;
    }

    public static string FileName(string value)
    {
        var normalized = RequiredText(value, 512, "INPUT_FILE_NAME_INVALID");
        if (!string.Equals(normalized, Path.GetFileName(normalized), StringComparison.Ordinal)
            || normalized is "." or "..")
        {
            throw new InputRuleViolationException("INPUT_FILE_NAME_INVALID");
        }

        return normalized;
    }

    public static string RelativeSourcePath(string value, bool allowRoot)
    {
        var normalized = value?.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (allowRoot && string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 32_767
            || Path.IsPathFullyQualified(normalized)
            || normalized[0] == '/'
            || normalized.Contains('\0')
            || normalized.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.Contains(':')))
        {
            throw new InputRuleViolationException("INPUT_SOURCE_REFERENCE_INVALID");
        }

        return normalized;
    }

    public static string Sha256(string value)
    {
        var normalized = value?.ToLowerInvariant();
        if (normalized is not { Length: 64 } || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InputRuleViolationException("INPUT_ASSET_HASH_INVALID");
        }

        return normalized;
    }

    public static string ManifestDigest(string value)
    {
        const string prefix = "sha256:";
        if (value?.StartsWith(prefix, StringComparison.Ordinal) != true)
        {
            throw new InputRuleViolationException("INPUT_MANIFEST_DIGEST_INVALID");
        }

        return prefix + Sha256(value[prefix.Length..]);
    }

    public static string OwnedRelativePath(string value)
    {
        var normalized = RelativeSourcePath(value, allowRoot: false);
        return normalized;
    }

    public static string MimeType(string value, SourceImageFormat format)
    {
        var expected = format switch
        {
            SourceImageFormat.Jpeg => "image/jpeg",
            SourceImageFormat.Png => "image/png",
            SourceImageFormat.Tiff => "image/tiff",
            _ => throw new InputRuleViolationException("INPUT_ASSET_FORMAT_INVALID")
        };
        if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputRuleViolationException("INPUT_ASSET_MIME_INVALID");
        }

        return expected;
    }

    public static ImmutableArray<InputFileReference> FreezeFiles(
        IEnumerable<InputFileReference>? files)
    {
        var frozen = files?.ToImmutableArray() ?? [];
        if (frozen.Any(file => file is null)
            || frozen.Select(file => file.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != frozen.Length)
        {
            throw new InputRuleViolationException("INPUT_FILE_INVENTORY_INVALID");
        }

        return frozen;
    }
}
