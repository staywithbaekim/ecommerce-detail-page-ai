using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Domain.Tests;

public sealed class BatchInputDomainTests
{
    [Theory]
    [InlineData(BatchOutputMode.UnifiedDirectory, @"D:\成品")]
    [InlineData(BatchOutputMode.StyleSourceDirectories, null)]
    public void OutputSelectionSurvivesScanBlockAndReadyTransitions(BatchOutputMode mode, string? root)
    {
        var now = DateTimeOffset.UtcNow;
        var original = Draft(now);
        Assert.Null(original.OutputSelection); // Legacy calls remain legacy, never opt in silently.
        var selection = BatchOutputSelection.Create(mode, root);
        var draft = original.Reconfigure(original.InputRoot, original.OutputRoot,
            original.TemplateSnapshot, original.SelectionMode, false, now, selection);
        var scanning = draft.BeginScanning(now);
        Assert.Equal(selection, scanning.OutputSelection);
        Assert.Equal(selection, scanning.Block(ScanBlockReason.Cancelled(), now).OutputSelection);
        var ready = scanning.MarkScanReady(BatchInputType.SingleStyle,
            [StyleDraft.Create("style_01", "新建文件夹", "", 0, ["asset_01"])],
            [SourceAssetMetadata.CreateReadable("asset_01", "style_01", "front.png", "front.png",
                100, new string('a', 64), 4, 3, SourceImageFormat.Png, "image/png")],
            "batches/batch_01/manifest.json", "sha256:" + new string('b', 64), now);
        Assert.Equal(selection, ready.OutputSelection);
        Assert.Equal(original.OutputRoot, ready.OutputRoot);
        Assert.Equal("", Assert.Single(ready.Styles).SourceDirectoryRelativePath);
        Assert.Throws<InputRuleViolationException>(() => ready.Reconfigure(ready.InputRoot,
            ready.OutputRoot, ready.TemplateSnapshot, ready.SelectionMode, false, now, null));
    }

    [Theory]
    [InlineData(BatchOutputMode.UnifiedDirectory, null)]
    [InlineData(BatchOutputMode.UnifiedDirectory, "")]
    [InlineData(BatchOutputMode.UnifiedDirectory, " \t")]
    [InlineData(BatchOutputMode.UnifiedDirectory, "C:\\bad\0root")]
    [InlineData(BatchOutputMode.StyleSourceDirectories, "")]
    [InlineData(BatchOutputMode.StyleSourceDirectories, @"C:\guess-parent")]
    [InlineData((BatchOutputMode)42, null)]
    public void OutputSelectionRejectsAmbiguousOrUnknownMode(BatchOutputMode mode, string? root) =>
        Assert.Equal("INPUT_OUTPUT_SELECTION_INVALID",
            Assert.Throws<InputRuleViolationException>(() => BatchOutputSelection.Create(mode, root)).Code);

    [Fact]
    public void DetectsSingleStyleFromRootImagesOnly()
    {
        var inventory = InputDirectoryInventory.Create(
            "中文 款式 6001",
            [File("中文 正面 01.jpg"), File("notes.txt")],
            [FirstLevelDirectoryInventory.Create("更深目录", "更深目录", [])]);

        var result = InputLayoutDetector.Detect(inventory);

        Assert.False(result.IsBlocked);
        Assert.Equal(BatchInputType.SingleStyle, result.InputType);
        var style = Assert.Single(result.Styles);
        Assert.Equal("中文 款式 6001", style.DefaultStyleCode);
        Assert.Equal(string.Empty, style.SourceDirectoryRelativePath);
        Assert.Equal("中文 正面 01.jpg", Assert.Single(style.Files).RelativePath);
    }

    [Fact]
    public void DetectsNormalBatchFromFirstLevelImagesOnly()
    {
        var inventory = InputDirectoryInventory.Create(
            "批次",
            [],
            [
                FirstLevelDirectoryInventory.Create("A 款", "A 款", [File("A 款/1.png")]),
                FirstLevelDirectoryInventory.Create("无图目录", "无图目录", [File("无图目录/readme.txt")]),
                FirstLevelDirectoryInventory.Create("B款", "B款", [File("B款/2.tiff")])
            ]);

        var result = InputLayoutDetector.Detect(inventory);

        Assert.False(result.IsBlocked);
        Assert.Equal(BatchInputType.StyleDirectoryBatch, result.InputType);
        Assert.Equal(["A 款", "B款"], result.Styles.Select(style => style.DefaultStyleCode));
        Assert.All(result.Styles, style => Assert.Single(style.Files));
    }

    [Fact]
    public void MixedAndEmptyLayoutsBlockWithoutPartialStyles()
    {
        var mixed = InputLayoutDetector.Detect(InputDirectoryInventory.Create(
            "批次",
            [File("root.jpg")],
            [FirstLevelDirectoryInventory.Create("A", "A", [File("A/child.jpg")])]));
        var empty = InputLayoutDetector.Detect(InputDirectoryInventory.Create(
            "批次",
            [File("readme.txt")],
            [FirstLevelDirectoryInventory.Create("A", "A", [File("A/raw.cr3")])]));

        Assert.Equal("INPUT_MIXED_LAYOUT", mixed.BlockReason?.Code);
        Assert.Empty(mixed.Styles);
        Assert.Equal("INPUT_NO_SUPPORTED_IMAGES", empty.BlockReason?.Code);
        Assert.Empty(empty.Styles);
    }

    [Fact]
    public void BlockedDraftCannotContainPartialAssetsOrReadyEvidence()
    {
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var draft = Draft(now).BeginScanning(now.AddMinutes(1));

        var blocked = draft.Block(ScanBlockReason.MixedLayout(), now.AddMinutes(2));

        Assert.Equal(BatchScanStatus.ScanBlocked, blocked.ScanStatus);
        Assert.Empty(blocked.Styles);
        Assert.Empty(blocked.Assets);
        Assert.Null(blocked.ManifestReference);
        Assert.Throws<InputRuleViolationException>(() => BatchDraft.Restore(
            blocked.BatchId,
            BatchScanStatus.ScanBlocked,
            null,
            blocked.InputRoot,
            blocked.OutputRoot,
            blocked.TemplateSnapshot,
            blocked.SelectionMode,
            blocked.IncludeEditablePsd,
            [StyleDraft.Create("style_01", "A", "", 0, ["asset_01"])],
            [],
            null,
            null,
            blocked.BlockReason,
            blocked.CreatedAt,
            blocked.UpdatedAt));
    }

    [Fact]
    public void StableIdsAreRepeatableAndSourceSensitive()
    {
        var hash = new string('a', 64);
        var style = InputStableIds.ForStyle("batch_01", "中文 款式");

        Assert.Equal(style, InputStableIds.ForStyle("batch_01", "中文 款式"));
        Assert.Equal(
            InputStableIds.ForAsset("batch_01", style, "中文 款式/正面.jpg", hash),
            InputStableIds.ForAsset("batch_01", style, "中文 款式/正面.jpg", hash));
        Assert.NotEqual(
            InputStableIds.ForAsset("batch_01", style, "中文 款式/正面.jpg", hash),
            InputStableIds.ForAsset("batch_01", style, "中文 款式/背面.jpg", hash));
    }

    private static InputFileReference File(string relativePath) =>
        InputFileReference.Create(relativePath, Path.GetFileName(relativePath));

    private static BatchDraft Draft(DateTimeOffset now)
    {
        var revision = Revision(now);
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_01",
            "batch_01",
            revision,
            ["page_01"],
            now);
        return BatchDraft.Create(
            "batch_01",
            @"C:\输入目录",
            @"D:\输出目录",
            snapshot,
            BatchSelectionMode.Automatic,
            false,
            now);
    }

    private static TemplateRevision Revision(DateTimeOffset now) => TemplateRevision.Restore(
        "revision_01",
        "group_01",
        1,
        TemplateRevisionState.Committed,
        "templates/revision_01/manifest.json",
        "sha256:" + new string('a', 64),
        now,
        [TemplatePage.Create(
            "page_01",
            "revision_01",
            0,
            "详情页",
            TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct,
            null,
            true,
            "resource_01")],
        []);
}
