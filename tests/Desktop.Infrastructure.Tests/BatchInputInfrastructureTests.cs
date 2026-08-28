using System.Buffers.Binary;
using System.Security.Cryptography;
using Desktop.Application.Inputs;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class BatchInputInfrastructureTests
{
    private const string RuleDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task TwoHundredStyleBatchCommitsManifestPreservesSourcesAndSurvivesRestart()
    {
        using var directory = new TestDirectory();
        var sourceRoot = Path.Combine(directory.Path, "客户 输入 Source 2026");
        var outputRoot = Path.Combine(directory.Path, "正式 输出");
        var appRoot = Path.Combine(directory.Path, "app-owned");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var sourceFiles = new List<string>();
        for (var index = 0; index < 200; index++)
        {
            var styleDirectory = Path.Combine(sourceRoot, $"款式 Style {index:D3}");
            Directory.CreateDirectory(styleDirectory);
            var file = Path.Combine(styleDirectory, $"商品 图 {index:D3}.png");
            await File.WriteAllBytesAsync(file, CreatePng(width: 3, height: 2));
            sourceFiles.Add(file);
        }
        var hashesBefore = await HashFilesAsync(sourceFiles);

        var options = new DesktopDataOptions(appRoot);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var (group, revision) = await SeedCommittedTemplateAsync(store);
            var manifestStore = CreateManifestStore(appRoot);
            var repository = new RecoveringBatchRepository(store, manifestStore, TimeProvider.System);
            var service = new BatchInputService(
                new ReadOnlyFileInventory(),
                new StreamingAssetMetadataReader(),
                repository,
                new SqliteBatchScanCommitter(store, manifestStore, TimeProvider.System),
                TimeProvider.System,
                new BatchScanOptions(4),
                store);
            var draft = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
                "batch_200_styles",
                group.GroupId,
                sourceRoot,
                outputRoot,
                [revision.Pages[0].PageId],
                BatchSelectionMode.Automatic,
                false));

            var ready = await service.ScanAsync(draft.BatchId);

            Assert.Equal(BatchScanStatus.ScanReady, ready.ScanStatus);
            Assert.Equal(BatchInputType.StyleDirectoryBatch, ready.InputType);
            Assert.Equal(200, ready.Styles.Length);
            Assert.Equal(200, ready.Assets.Length);
            Assert.All(ready.Assets, asset =>
            {
                Assert.False(Path.IsPathRooted(asset.SourceRelativePath));
                Assert.Equal(AssetReadability.Readable, asset.Readability);
                Assert.Equal(SourceImageFormat.Png, asset.Format);
            });
            Assert.NotNull(await manifestStore.InspectAsync(ready.BatchId, CancellationToken.None));
            Assert.Equal(hashesBefore, await HashFilesAsync(sourceFiles));

            var removable = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
                "batch_delete_source_safe",
                group.GroupId,
                sourceRoot,
                outputRoot,
                [revision.Pages[0].PageId],
                BatchSelectionMode.Automatic,
                false));
            await repository.DeleteDraftAsync(removable.BatchId, CancellationToken.None);
            Assert.Null(await repository.GetAsync(removable.BatchId, CancellationToken.None));
            Assert.Equal(hashesBefore, await HashFilesAsync(sourceFiles));
        }

        await using var reopenedStore = new SqliteDesktopStore(options);
        await reopenedStore.InitializeAsync();
        var reopenedManifestStore = CreateManifestStore(appRoot);
        var reopenedRepository = new RecoveringBatchRepository(
            reopenedStore,
            reopenedManifestStore,
            TimeProvider.System);
        var recovered = await reopenedRepository.GetAsync("batch_200_styles", CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(BatchScanStatus.ScanReady, recovered.ScanStatus);
        Assert.Equal(200, recovered.Styles.Length);
        Assert.Equal(200, recovered.Assets.Length);
        Assert.NotNull(await reopenedStore.GetGroupAsync("group_input", CancellationToken.None));
        Assert.Equal(hashesBefore, await HashFilesAsync(sourceFiles));
    }

    [Fact]
    public async Task ReadersAcceptSupportedFormatsAndBlockCorruptMismatchDeepAndEscapingPaths()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "中文 English 123 空格");
        Directory.CreateDirectory(root);
        var jpegPath = Path.Combine(root, "正面 01.jpg");
        var pngPath = Path.Combine(root, "detail_02.png");
        var tiffPath = Path.Combine(root, "材质 03.tiff");
        await File.WriteAllBytesAsync(jpegPath, CreateJpeg(3, 2));
        await File.WriteAllBytesAsync(pngPath, CreatePng(4, 5));
        await File.WriteAllBytesAsync(tiffPath, CreateTiff(6, 7));
        var longStyleDirectory = Path.Combine(root, "长路径款式 " + new string('A', 140));
        Directory.CreateDirectory(longStyleDirectory);
        var longFilePath = Path.Combine(longStyleDirectory, "Long 中文 123 " + new string('B', 100) + ".png");
        await File.WriteAllBytesAsync(longFilePath, CreatePng(8, 9));
        Assert.True(longFilePath.Length > 260);
        var hashesBefore = await HashFilesAsync([jpegPath, pngPath, tiffPath, longFilePath]);
        var reader = new StreamingAssetMetadataReader();

        var jpeg = await reader.ReadAsync(root, Path.GetFileName(jpegPath), CancellationToken.None);
        var png = await reader.ReadAsync(root, Path.GetFileName(pngPath), CancellationToken.None);
        var tiff = await reader.ReadAsync(root, Path.GetFileName(tiffPath), CancellationToken.None);
        var longPath = await reader.ReadAsync(
            root,
            Path.GetRelativePath(root, longFilePath),
            CancellationToken.None);

        Assert.Equal((3, 2, SourceImageFormat.Jpeg),
            (jpeg.Metadata!.PixelWidth, jpeg.Metadata.PixelHeight, jpeg.Metadata.Format));
        Assert.Equal((4, 5, SourceImageFormat.Png),
            (png.Metadata!.PixelWidth, png.Metadata.PixelHeight, png.Metadata.Format));
        Assert.Equal((6, 7, SourceImageFormat.Tiff),
            (tiff.Metadata!.PixelWidth, tiff.Metadata.PixelHeight, tiff.Metadata.Format));
        Assert.Equal((8, 9), (longPath.Metadata!.PixelWidth, longPath.Metadata.PixelHeight));
        Assert.Equal(hashesBefore, await HashFilesAsync([jpegPath, pngPath, tiffPath, longFilePath]));

        var mismatchPath = Path.Combine(root, "扩展名不符.png");
        var corruptPath = Path.Combine(root, "损坏.jpg");
        var truncatedTiffPath = Path.Combine(root, "截断.tif");
        await File.WriteAllBytesAsync(mismatchPath, CreateJpeg(1, 1));
        await File.WriteAllBytesAsync(corruptPath, [0xff, 0xd8, 0xff]);
        var truncated = CreateTiff(2, 2)[..^1];
        await File.WriteAllBytesAsync(truncatedTiffPath, truncated);
        var deepDirectory = Path.Combine(root, "一级", "二级");
        Directory.CreateDirectory(deepDirectory);
        await File.WriteAllBytesAsync(Path.Combine(deepDirectory, "deep.png"), CreatePng(1, 1));

        Assert.Equal("INPUT_ASSET_CORRUPT",
            (await reader.ReadAsync(root, Path.GetFileName(mismatchPath), CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_CORRUPT",
            (await reader.ReadAsync(root, Path.GetFileName(corruptPath), CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_CORRUPT",
            (await reader.ReadAsync(root, Path.GetFileName(truncatedTiffPath), CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_UNREADABLE",
            (await reader.ReadAsync(root, "一级/二级/deep.png", CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_UNREADABLE",
            (await reader.ReadAsync(root, "../outside.png", CancellationToken.None)).Failure!.Code);

        var inventory = await new ReadOnlyFileInventory().ReadApprovedLevelsAsync(root, CancellationToken.None);
        Assert.True(inventory.IsSuccess);
        Assert.DoesNotContain(
            inventory.Inventory!.FirstLevelDirectories.SelectMany(item => item.DirectFiles),
            file => file.RelativePath.Contains("deep.png", StringComparison.Ordinal));

        var lockedPath = Path.Combine(root, "被占用.png");
        await File.WriteAllBytesAsync(lockedPath, CreatePng(1, 1));
        await using (var locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var unreadable = await reader.ReadAsync(root, Path.GetFileName(lockedPath), CancellationToken.None);
            Assert.Equal("INPUT_ASSET_UNREADABLE", unreadable.Failure!.Code);
        }

        var disappeared = await new ReadOnlyFileInventory().ReadApprovedLevelsAsync(
            Path.Combine(root, "已经消失"),
            CancellationToken.None);
        Assert.Equal("INPUT_DIRECTORY_DISAPPEARED", disappeared.Failure!.Code);
    }

    [Fact]
    public async Task FirstLevelDirectoryReparsePointIsNeverFollowedWhenPlatformAllowsCreation()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "root");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.png"), CreatePng(1, 1));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked-style"), outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var result = await new ReadOnlyFileInventory().ReadApprovedLevelsAsync(root, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INPUT_DIRECTORY_ACCESS_DENIED", result.Failure!.Code);
    }

    [Fact]
    public async Task DraftSnapshotCanUpdateBeforeScanButScanningSnapshotIsFrozenInSqlite()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var (group, revision) = await SeedCommittedTemplateAsync(store, includeSecondPage: true);
        var manifestStore = CreateManifestStore(options.RootDirectory);
        var repository = new RecoveringBatchRepository(store, manifestStore, TimeProvider.System);
        var service = new BatchInputService(
            new ReadOnlyFileInventory(),
            new StreamingAssetMetadataReader(),
            repository,
            new SqliteBatchScanCommitter(store, manifestStore, TimeProvider.System),
            new FixedTimeProvider(now),
            templates: store);
        var first = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
            "batch_autosave",
            group.GroupId,
            directory.Path,
            directory.Path,
            ["page_input_01"],
            BatchSelectionMode.Automatic,
            false));

        var updated = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
            first.BatchId,
            group.GroupId,
            directory.Path,
            Path.Combine(directory.Path, "new-output"),
            ["page_input_02"],
            BatchSelectionMode.Manual,
            true));
        var restored = await store.GetAsync(first.BatchId, CancellationToken.None);

        Assert.Equal(first.TemplateSnapshot.SnapshotId, updated.TemplateSnapshot.SnapshotId);
        Assert.Equal("page_input_02", Assert.Single(restored!.TemplateSnapshot.Pages).PageId);
        Assert.Equal(BatchSelectionMode.Manual, restored.SelectionMode);
        var scanning = updated.BeginScanning(now);
        await store.SaveAsync(scanning, CancellationToken.None);
        var changedSnapshot = BatchTemplateSnapshot.Create(
            scanning.TemplateSnapshot.SnapshotId,
            scanning.BatchId,
            revision,
            ["page_input_01"],
            scanning.TemplateSnapshot.CreatedAt);
        var conflicting = BatchDraft.Restore(
            scanning.BatchId,
            BatchScanStatus.Scanning,
            null,
            scanning.InputRoot,
            scanning.OutputRoot,
            changedSnapshot,
            scanning.SelectionMode,
            scanning.IncludeEditablePsd,
            [],
            [],
            null,
            null,
            null,
            scanning.CreatedAt,
            scanning.UpdatedAt);

        var error = await Assert.ThrowsAsync<InputRuleViolationException>(
            () => store.SaveAsync(conflicting, CancellationToken.None));
        Assert.Equal("INPUT_TEMPLATE_SNAPSHOT_CONFLICT", error.Code);
    }

    [Fact]
    public async Task DatabaseCommitFailureRollsBackApplicationOwnedManifestWithoutReadyState()
    {
        using var directory = new TestDirectory();
        var appRoot = Path.Combine(directory.Path, "app");
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot));
        await store.InitializeAsync();
        var revision = CommittedRevision("missing_revision", "missing_group", includeSecondPage: false);
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_db_failure",
            "batch_db_failure",
            revision,
            ["page_input_01"],
            DateTimeOffset.UtcNow);
        var scanning = BatchDraft.Create(
            "batch_db_failure",
            directory.Path,
            directory.Path,
            snapshot,
            BatchSelectionMode.Automatic,
            false,
            DateTimeOffset.UtcNow).BeginScanning(DateTimeOffset.UtcNow);
        var style = StyleDraft.Create("style_db_failure", "款式", "", 0, ["asset_db_failure"]);
        var asset = SourceAssetMetadata.CreateReadable(
            "asset_db_failure",
            style.StyleId,
            "image.png",
            "image.png",
            100,
            new string('a', 64),
            1,
            1,
            SourceImageFormat.Png,
            "image/png");
        var manifest = AssetManifestV1.Create(
            scanning,
            BatchInputType.SingleStyle,
            [style],
            [asset],
            new string('b', 64),
            DateTimeOffset.UtcNow);
        var manifests = CreateManifestStore(appRoot);
        var committer = new SqliteBatchScanCommitter(store, manifests, TimeProvider.System);

        var result = await committer.CommitAsync(
            new BatchScanCommitRequest(scanning, BatchInputType.SingleStyle, [style], [asset], manifest),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INPUT_SCAN_SYSTEM_FAILURE", result.Failure!.Code);
        Assert.Null(await store.GetAsync(scanning.BatchId, CancellationToken.None));
        Assert.Null(await manifests.InspectAsync(scanning.BatchId, CancellationToken.None));
    }

    private static AssetManifestFileStore CreateManifestStore(string appRoot) => new(
        new AtomicFileStore(new LocalAppPathPolicy(appRoot)),
        new JsonSchemaContractValidator());

    private static async Task<(TemplateGroup Group, TemplateRevision Revision)> SeedCommittedTemplateAsync(
        SqliteDesktopStore store,
        bool includeSecondPage = false)
    {
        var group = TemplateGroup.Create("group_input", "INPUT 测试模板", DateTimeOffset.UtcNow);
        var revision = CommittedRevision("revision_input", group.GroupId, includeSecondPage);
        await store.SaveGroupAsync(group, CancellationToken.None);
        await store.CommitRevisionAsync(revision, null, CancellationToken.None);
        return ((await store.GetGroupAsync(group.GroupId, CancellationToken.None))!, revision);
    }

    private static TemplateRevision CommittedRevision(
        string revisionId,
        string groupId,
        bool includeSecondPage)
    {
        var pages = new List<TemplatePage>
        {
            Page("page_input_01", revisionId, 0)
        };
        if (includeSecondPage)
        {
            pages.Add(Page("page_input_02", revisionId, 1));
        }

        var revision = TemplateRevision.CreateDraft(
            revisionId,
            groupId,
            1,
            DateTimeOffset.UtcNow,
            pages);
        var importing = revision.AdvanceTo(
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
        return committing.Commit(
            $"templates/groups/{groupId}/revisions/{revisionId}/revision-manifest.json",
            RuleDigest,
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }

    private static TemplatePage Page(string pageId, string revisionId, int order) => TemplatePage.Create(
        pageId,
        revisionId,
        order,
        pageId,
        TemplatePageContentType.NormalComposition,
        TemplateGenerationMode.OncePerProduct,
        null,
        enabled: true,
        $"resource_{pageId}");

    private static async Task<string[]> HashFilesAsync(IEnumerable<string> paths)
    {
        var results = new List<string>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            results.Add(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        return [.. results];
    }

    private static byte[] CreateJpeg(ushort width, ushort height) =>
    [
        0xff, 0xd8,
        0xff, 0xc0, 0x00, 0x0b, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
        0xff, 0xda, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3f, 0x00,
        0x01, 0xff, 0xd9
    ];

    private static byte[] CreatePng(uint width, uint height)
    {
        var result = new List<byte>(80) { 137, 80, 78, 71, 13, 10, 26, 10 };
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..8], height);
        header[8] = 8;
        header[9] = 2;
        WritePngChunk(result, "IHDR"u8, header);
        WritePngChunk(result, "IDAT"u8, [0x00]);
        WritePngChunk(result, "IEND"u8, []);
        return [.. result];
    }

    private static void WritePngChunk(List<byte> output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.AddRange(length);
        output.AddRange(type);
        output.AddRange(data);
        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, PngCrc32(crcInput));
        output.AddRange(crc);
    }

    private static uint PngCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }
        return ~crc;
    }

    private static byte[] CreateTiff(uint width, uint height)
    {
        var bytes = new byte[63];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 4);
        WriteTiffEntry(bytes.AsSpan(10, 12), 256, 4, 1, width);
        WriteTiffEntry(bytes.AsSpan(22, 12), 257, 4, 1, height);
        WriteTiffEntry(bytes.AsSpan(34, 12), 273, 4, 1, 62);
        WriteTiffEntry(bytes.AsSpan(46, 12), 279, 4, 1, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(58, 4), 0);
        bytes[62] = 0x7f;
        return bytes;
    }

    private static void WriteTiffEntry(
        Span<byte> entry,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry[..2], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..4], type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..8], count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..12], value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
