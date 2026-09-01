using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class AiAnalysisCopyAndResultStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CandidateProfileRejectsParameterDriftWithoutANewVersion()
    {
        var options = new AiAnalysisCopyEncodingOptions(MaxLongestEdge: 2000);

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Equal("AI_COPY_COMPRESSION_PROFILE_OPTIONS_MISMATCH", exception.Message);
    }

    [Fact]
    public async Task PreparerCreatesBoundedJpegAndNeverChangesSource()
    {
        var root = NewRoot("ai01-copy-wic-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var sourceBytes = CreateImage(SourceImageFormat.Jpeg, 2400, 1200, jpegQuality: 98);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var sourceDigest = Digest(sourceBytes);
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var files = new AtomicFileStore(policy);
            using var preparer = new FileAiAnalysisCopyPreparer(policy, files);
            var metadata = await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                "copy_01",
                input,
                Asset("asset_01", "photo.jpg", sourceBytes, 2400, 1200, SourceImageFormat.Jpeg),
                AiAnalysisCopyProfiles.WicBoundedV1), CancellationToken.None);

            Assert.Equal("image/jpeg", metadata.MimeType);
            Assert.Equal(1600, metadata.PixelWidth);
            Assert.Equal(800, metadata.PixelHeight);
            Assert.Equal(AiAnalysisCopyProfiles.WicBoundedV1, metadata.CompressionProfileVersion);
            Assert.True(metadata.ByteLength < sourceBytes.Length);
            Assert.NotEqual("sha256:" + sourceDigest, metadata.Digest);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));

            var sourceStore = new FileAiAnalysisCopySource(policy);
            var copy = await sourceStore.OpenAsync("copy_01", CancellationToken.None);
            Assert.Equal(metadata.Digest, copy.ContentDigest);
            Assert.Equal(metadata.PixelWidth, copy.PixelWidth);
            Assert.Equal(metadata.PixelHeight, copy.PixelHeight);
            Assert.Equal("image/jpeg", copy.MimeType);

            await preparer.ReleaseAsync("copy_01", metadata.LeaseId, CancellationToken.None);
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_01.bin")));
            Assert.True(File.Exists(source));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(SourceImageFormat.Png, "image/png")]
    [InlineData(SourceImageFormat.Tiff, "image/jpeg")]
    public async Task PreparerUsesTrustedOutputMimeForPngAndTiff(
        SourceImageFormat sourceFormat,
        string expectedMime)
    {
        var root = NewRoot("ai01-copy-format-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var extension = sourceFormat == SourceImageFormat.Png ? ".png" : ".tif";
        var sourceName = "photo" + extension;
        var source = Path.Combine(input, sourceName);
        var sourceBytes = CreateImage(sourceFormat, 1800, 900);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var files = new AtomicFileStore(policy);
            using var preparer = new FileAiAnalysisCopyPreparer(policy, files);
            var metadata = await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                "copy_format",
                input,
                Asset("asset_format", sourceName, sourceBytes, 1800, 900, sourceFormat),
                AiAnalysisCopyProfiles.WicBoundedV1), CancellationToken.None);

            Assert.Equal(expectedMime, metadata.MimeType);
            Assert.Equal(1600, metadata.PixelWidth);
            Assert.Equal(800, metadata.PixelHeight);
            var opened = await new FileAiAnalysisCopySource(policy).OpenAsync("copy_format", CancellationToken.None);
            Assert.Equal(expectedMime, opened.MimeType);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(1, "ABCD", 120, 80)]
    [InlineData(2, "BADC", 120, 80)]
    [InlineData(3, "DCBA", 120, 80)]
    [InlineData(4, "CDAB", 120, 80)]
    [InlineData(5, "ACBD", 80, 120)]
    [InlineData(6, "CADB", 80, 120)]
    [InlineData(7, "DBCA", 80, 120)]
    [InlineData(8, "BDAC", 80, 120)]
    public async Task PreparerAppliesEveryExifOrientationWithoutCroppingAsymmetricImage(
        ushort orientation,
        string expectedQuadrants,
        int expectedWidth,
        int expectedHeight)
    {
        var root = NewRoot("ai01-copy-orientation-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "rotated.jpg");
        var sourceBytes = CreateQuadrantJpeg(120, 80, orientation);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy));
            var metadata = await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                "copy_rotated",
                input,
                Asset("asset_rotated", "rotated.jpg", sourceBytes, 120, 80, SourceImageFormat.Jpeg),
                AiAnalysisCopyProfiles.WicBoundedV1), CancellationToken.None);

            Assert.Equal(expectedWidth, metadata.PixelWidth);
            Assert.Equal(expectedHeight, metadata.PixelHeight);
            var output = await File.ReadAllBytesAsync(policy.ResolveOwnedPath("ai-analysis/copies/copy_rotated.bin"));
            Assert.Equal(expectedQuadrants, ReadQuadrants(output));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task PngReencodingPreservesNonOpaqueAlpha()
    {
        var root = NewRoot("ai01-copy-alpha-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "alpha.png");
        var sourceBytes = CreateImage(SourceImageFormat.Png, 200, 100, seed: 29);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy));
            var metadata = await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                "copy_alpha",
                input,
                Asset("asset_alpha", "alpha.png", sourceBytes, 200, 100, SourceImageFormat.Png),
                AiAnalysisCopyProfiles.WicBoundedV1), CancellationToken.None);

            var output = await File.ReadAllBytesAsync(policy.ResolveOwnedPath("ai-analysis/copies/copy_alpha.bin"));
            var alphaValues = ReadAlpha(output);
            Assert.Equal("image/png", metadata.MimeType);
            Assert.True(alphaValues.Min < byte.MaxValue);
            Assert.True(alphaValues.Max > alphaValues.Min);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ReencodingRemovesExifOrientationAndCommentMetadata()
    {
        var root = NewRoot("ai01-copy-metadata-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "metadata.jpg");
        var sourceBytes = CreateQuadrantJpeg(120, 80, orientation: 6, comment: "private-source-note");
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy));
            await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                "copy_metadata",
                input,
                Asset("asset_metadata", "metadata.jpg", sourceBytes, 120, 80, SourceImageFormat.Jpeg),
                AiAnalysisCopyProfiles.WicBoundedV1), CancellationToken.None);

            var output = await File.ReadAllBytesAsync(policy.ResolveOwnedPath("ai-analysis/copies/copy_metadata.bin"));
            using var stream = new MemoryStream(output, writable: false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var metadata = Assert.IsType<BitmapMetadata>(frame.Metadata);
            Assert.Null(ReadMetadataQuery(metadata, "/app1/ifd/{ushort=274}"));
            Assert.True(string.IsNullOrEmpty(metadata.Comment));
            Assert.DoesNotContain("private-source-note", System.Text.Encoding.UTF8.GetString(output), StringComparison.Ordinal);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CopyReaderRejectsSidecarMimeOrDimensionThatDoesNotMatchBytes()
    {
        var root = NewRoot("ai01-copy-closure-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var sourceBytes = CreateImage(SourceImageFormat.Jpeg, 320, 160);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var files = new AtomicFileStore(policy);
            using var preparer = new FileAiAnalysisCopyPreparer(policy, files);
            var metadata = await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                "copy_tampered",
                input,
                Asset("asset_tampered", "photo.jpg", sourceBytes, 320, 160, SourceImageFormat.Jpeg),
                AiAnalysisCopyProfiles.WicBoundedV1), CancellationToken.None);
            var metadataPath = policy.ResolveOwnedPath("ai-analysis/copies/copy_tampered.json");
            await File.WriteAllBytesAsync(
                metadataPath,
                JsonSerializer.SerializeToUtf8Bytes(metadata with { MimeType = "image/png", PixelWidth = metadata.PixelWidth + 1 }));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new FileAiAnalysisCopySource(policy).OpenAsync("copy_tampered", CancellationToken.None));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task OversizedEncodedCopyRollsBackOwnedFiles()
    {
        var root = NewRoot("ai01-copy-limit-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "noise.png");
        var sourceBytes = CreateImage(SourceImageFormat.Png, 256, 256, seed: 73);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var options = new AiAnalysisCopyEncodingOptions(
                MaxLongestEdge: 1600,
                MaxOutputBytes: 1024,
                CompressionProfileVersion: "wic-bounded/test-output-1024/v1-candidate");
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy), encodingOptions: options);

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_too_large",
                    input,
                    Asset("asset_too_large", "noise.png", sourceBytes, 256, 256, SourceImageFormat.Png),
                    options.CompressionProfileVersion),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_too_large.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_too_large.json")));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task SourceByteLimitRejectsBeforeWritingOwnedFiles()
    {
        var root = NewRoot("ai01-copy-source-limit-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "large.png");
        var sourceBytes = CreateImage(SourceImageFormat.Png, 128, 128, seed: 41);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var options = new AiAnalysisCopyEncodingOptions(
                MaxSourceBytes: 1024,
                CompressionProfileVersion: "wic-bounded/test-source-1024/v1-candidate");
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy), encodingOptions: options);

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_source_too_large",
                    input,
                    Asset("asset_source_too_large", "large.png", sourceBytes, 128, 128, SourceImageFormat.Png),
                    options.CompressionProfileVersion),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_source_too_large.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_source_too_large.json")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task PixelLimitRejectsBeforeWritingOwnedFiles()
    {
        var root = NewRoot("ai01-copy-pixel-limit-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "pixels.jpg");
        var sourceBytes = CreateImage(SourceImageFormat.Jpeg, 1200, 900, jpegQuality: 60);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var options = new AiAnalysisCopyEncodingOptions(
                MaxSourcePixels: 1_000_000,
                CompressionProfileVersion: "wic-bounded/test-pixels-1000000/v1-candidate");
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy), encodingOptions: options);

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_pixel_limit",
                    input,
                    Asset("asset_pixel_limit", "pixels.jpg", sourceBytes, 1200, 900, SourceImageFormat.Jpeg),
                    options.CompressionProfileVersion),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_pixel_limit.bin")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task SourceDimensionMismatchIsRejectedBeforeWritingOwnedFiles()
    {
        var root = NewRoot("ai01-copy-dimension-mismatch-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var sourceBytes = CreateImage(SourceImageFormat.Jpeg, 320, 180);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy));

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_dimension_mismatch",
                    input,
                    Asset("asset_dimension_mismatch", "photo.jpg", sourceBytes, 321, 180, SourceImageFormat.Jpeg),
                    AiAnalysisCopyProfiles.WicBoundedV1),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_dimension_mismatch.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_dimension_mismatch.json")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CorruptInputAndMimeMismatchAreRejectedWithoutOwnedFiles()
    {
        var root = NewRoot("ai01-copy-corrupt-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var corruptPath = Path.Combine(input, "corrupt.jpg");
        var corrupt = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(corruptPath, corrupt);
        var pngPath = Path.Combine(input, "actually.png");
        var png = CreateImage(SourceImageFormat.Png, 64, 32);
        await File.WriteAllBytesAsync(pngPath, png);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy));

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_corrupt",
                    input,
                    Asset("asset_corrupt", "corrupt.jpg", corrupt, 64, 32, SourceImageFormat.Jpeg),
                    AiAnalysisCopyProfiles.WicBoundedV1),
                CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_mismatch",
                    input,
                    Asset("asset_mismatch", "actually.png", png, 64, 32, SourceImageFormat.Jpeg),
                    AiAnalysisCopyProfiles.WicBoundedV1),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_corrupt.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_mismatch.bin")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CancellationBeforePreparationLeavesNoOwnedFiles()
    {
        var root = NewRoot("ai01-copy-cancel-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var sourceBytes = CreateImage(SourceImageFormat.Jpeg, 320, 180);
        await File.WriteAllBytesAsync(source, sourceBytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_cancelled",
                    input,
                    Asset("asset_cancelled", "photo.jpg", sourceBytes, 320, 180, SourceImageFormat.Jpeg),
                    AiAnalysisCopyProfiles.WicBoundedV1),
                cancellation.Token));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_cancelled.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_cancelled.json")));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task MetadataWriteFailureRollsBackEncodedCopyAndKeepsSource()
    {
        var root = NewRoot("ai01-copy-write-failure-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var sourceBytes = CreateImage(SourceImageFormat.Jpeg, 320, 180);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var files = new FailMetadataWriteStore(new AtomicFileStore(policy));
            using var preparer = new FileAiAnalysisCopyPreparer(policy, files);

            await Assert.ThrowsAsync<IOException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_write_failure",
                    input,
                    Asset("asset_write_failure", "photo.jpg", sourceBytes, 320, 180, SourceImageFormat.Jpeg),
                    AiAnalysisCopyProfiles.WicBoundedV1),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_write_failure.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_write_failure.json")));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ResultStoreRoundTripsAndRejectsCorruption()
    {
        var root = NewRoot("ai01-result-");
        try
        {
            var policy = new LocalAppPathPolicy(root);
            var files = new AtomicFileStore(policy);
            var store = new FileAiAnalysisResultStore(files, new Desktop.Contracts.Validation.JsonSchemaContractValidator());
            var result = new AiAnalysisResultV1(AiAnalysisProtocol.ResultSchemaVersion, "result", "request_01", null, "model/proposed", [], [], [], null, [], new AiUsageSummaryV1("usage_request_01", 1, 2, 1, null, null));
            await store.SaveAsync("results/request_01", result, CancellationToken.None);
            var roundTrip = await store.LoadAsync("results/request_01", CancellationToken.None);
            Assert.Equal(result.RequestId, roundTrip!.RequestId);
            Assert.Equal(result.ModelConfigVersion, roundTrip.ModelConfigVersion);
            Assert.Equal(result.Usage, roundTrip.Usage);
            await File.WriteAllTextAsync(Path.Combine(root, "results", "request_01.json"), "not-json");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("results/request_01", CancellationToken.None));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("duplicate-rank", "AI_RESULT_REQUIREMENT_RANK_DUPLICATE")]
    [InlineData("duplicate-asset", "AI_RESULT_REQUIREMENT_ASSET_DUPLICATE")]
    [InlineData("invalid-geometry", "AI_RESULT_GEOMETRY_INVALID")]
    [InlineData("ragged-size-table", "AI_RESULT_SIZE_TABLE_SHAPE_INVALID")]
    public async Task ResultStoreRejectsSemanticShapeOnSaveAndLoad(
        string mutation,
        string expectedCode)
    {
        var root = NewRoot("ai01-result-semantic-");
        try
        {
            var policy = new LocalAppPathPolicy(root);
            var files = new AtomicFileStore(policy);
            var store = new FileAiAnalysisResultStore(
                files,
                new Desktop.Contracts.Validation.JsonSchemaContractValidator());
            var invalid = SemanticResult(mutation);

            var saveException = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveAsync("results/request_01", invalid, CancellationToken.None));
            Assert.Equal(expectedCode, saveException.Message);
            Assert.False(File.Exists(Path.Combine(root, "results", "request_01.json")));

            // Seed the directory with a valid record, then replace its bytes
            // with the malformed-but-schema-valid semantic shape. Loading must
            // apply the same validator and fail closed as well.
            await store.SaveAsync(
                "results/request_01",
                SemanticResult("valid"),
                CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(root, "results", "request_01.json"),
                JsonSerializer.Serialize(invalid, JsonOptions));

            var loadException = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.LoadAsync("results/request_01", CancellationToken.None));
            Assert.Equal(expectedCode, loadException.Message);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CopyLeasesSurvivePreparerRestartAndSharedReferencesAreNotOverwritten()
    {
        var root = NewRoot("ai01-copy-restart-");
        var input = Path.Combine(root, "input", "nested", "three");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var sourceBytes = CreateImage(SourceImageFormat.Jpeg, 320, 180);
        await File.WriteAllBytesAsync(source, sourceBytes);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var files = new AtomicFileStore(policy);
            var sourceMetadata = Asset(
                "asset_01", "nested/three/photo.jpg", sourceBytes, 320, 180, SourceImageFormat.Jpeg);
            using var first = new FileAiAnalysisCopyPreparer(policy, files);
            var prepared = await first.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_shared",
                    Path.Combine(root, "input"),
                    sourceMetadata,
                    AiAnalysisCopyProfiles.WicBoundedV1),
                CancellationToken.None);
            using var restarted = new FileAiAnalysisCopyPreparer(policy, files);
            await restarted.AcquireAsync("copy_shared", "lease-second", CancellationToken.None);
            await restarted.ReleaseAsync("copy_shared", prepared.LeaseId, CancellationToken.None);
            Assert.True(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_shared.bin")));
            await restarted.ReleaseAsync("copy_shared", "lease-second", CancellationToken.None);
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_shared.bin")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task SourceChangeAfterCopyPreparationRollsBackOwnedCopyAndMetadata()
    {
        var root = NewRoot("ai01-copy-source-change-");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var original = CreateImage(SourceImageFormat.Jpeg, 320, 180);
        var changed = original.ToArray();
        changed[^1] ^= 0x01;
        await File.WriteAllBytesAsync(source, original);
        try
        {
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var inner = new AtomicFileStore(policy);
            var files = new MutateSourceAfterMetadataWriteStore(inner, source, changed);
            using var preparer = new FileAiAnalysisCopyPreparer(policy, files);

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_changed",
                    input,
                    Asset("asset_01", "photo.jpg", original, 320, 180, SourceImageFormat.Jpeg),
                    AiAnalysisCopyProfiles.WicBoundedV1),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_changed.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_changed.json")));
            Assert.Equal(changed, await File.ReadAllBytesAsync(source));
        }
        finally { DeleteRoot(root); }
    }

    private static SourceAssetMetadata Asset(
        string assetId,
        string relativePath,
        byte[] bytes,
        int width,
        int height,
        SourceImageFormat format) => SourceAssetMetadata.CreateReadable(
        assetId,
        "style_01",
        relativePath,
        Path.GetFileName(relativePath),
        bytes.Length,
        Digest(bytes),
        width,
        height,
        format,
        format switch
        {
            SourceImageFormat.Jpeg => "image/jpeg",
            SourceImageFormat.Png => "image/png",
            SourceImageFormat.Tiff => "image/tiff",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        });

    private static AiAnalysisResultV1 SemanticResult(string mutation) => mutation switch
    {
        "duplicate-rank" => new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [
                new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.9m, ["front"]),
                new AiRequirementMatchV1("frame_01", "asset_02", 1, 0.8m, ["alternate"])
            ],
            [], [], null, [],
            new AiUsageSummaryV1("usage_request_01", 1, 1, 1, null, null)),
        "duplicate-asset" => new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [
                new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.9m, ["front"]),
                new AiRequirementMatchV1("frame_01", "asset_01", 2, 0.8m, ["alternate"])
            ],
            [], [], null, [],
            new AiUsageSummaryV1("usage_request_01", 1, 1, 1, null, null)),
        "invalid-geometry" => new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [],
            [],
            [new AiSubjectGeometryV1("asset_01", 0.6m, 0.2m, 0.6m, 0.9m, 0.9m)],
            null,
            [],
            new AiUsageSummaryV1("usage_request_01", 1, 1, 1, null, null)),
        "ragged-size-table" => new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [],
            [],
            [],
            new AiSizeTableCandidateV1("asset_01", 0.9m, ["尺码", "胸围"], [["M"]]),
            [],
            new AiUsageSummaryV1("usage_request_01", 1, 1, 1, null, null)),
        "valid" => new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [], [], [], null, [],
            new AiUsageSummaryV1("usage_request_01", 1, 1, 1, null, null)),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
    };

    private static byte[] CreateImage(
        SourceImageFormat format,
        int width,
        int height,
        ushort orientation = 1,
        int jpegQuality = 92,
        int seed = 19)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var random = new Random(seed);
        random.NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = format == SourceImageFormat.Png
                ? (byte)(64 + pixels[index] % 192)
                : byte.MaxValue;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        BitmapEncoder encoder = format switch
        {
            SourceImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            SourceImageFormat.Png => new PngBitmapEncoder(),
            SourceImageFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        BitmapMetadata? metadata = null;
        if (format == SourceImageFormat.Jpeg && orientation != 1)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        }

        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreateQuadrantJpeg(
        int width,
        int height,
        ushort orientation,
        string? comment = null)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * stride + x * 4;
                var quadrant = (y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1);
                var color = quadrant switch
                {
                    0 => (B: (byte)16, G: (byte)16, R: (byte)240),
                    1 => (B: (byte)16, G: (byte)220, R: (byte)16),
                    2 => (B: (byte)240, G: (byte)16, R: (byte)16),
                    _ => (B: (byte)16, G: (byte)220, R: (byte)240)
                };
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = byte.MaxValue;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        if (comment is not null) metadata.Comment = comment;
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string ReadQuadrants(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var source = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var samples = new[]
        {
            (converted.PixelWidth / 4, converted.PixelHeight / 4),
            (converted.PixelWidth * 3 / 4, converted.PixelHeight / 4),
            (converted.PixelWidth / 4, converted.PixelHeight * 3 / 4),
            (converted.PixelWidth * 3 / 4, converted.PixelHeight * 3 / 4)
        };
        return new string(samples.Select(sample =>
        {
            var index = sample.Item2 * stride + sample.Item1 * 4;
            var b = pixels[index];
            var g = pixels[index + 1];
            var r = pixels[index + 2];
            if (r > g + 60 && r > b + 60) return 'A';
            if (g > r + 60 && g > b + 60) return 'B';
            if (b > r + 60 && b > g + 60) return 'C';
            if (r > 120 && g > 120 && b < 100) return 'D';
            return '?';
        }).ToArray());
    }

    private static (byte Min, byte Max) ReadAlpha(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var source = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            min = Math.Min(min, pixels[index]);
            max = Math.Max(max, pixels[index]);
        }

        return (min, max);
    }

    private static object? ReadMetadataQuery(BitmapMetadata metadata, string query)
    {
        try { return metadata.GetQuery(query); }
        catch (NotSupportedException) { return null; }
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NewRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class MutateSourceAfterMetadataWriteStore(
        IFileStore inner,
        string sourcePath,
        byte[] changedBytes) : IFileStore
    {
        private bool _mutated;

        public async Task WriteAtomicallyAsync(
            string relativePath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            await inner.WriteAtomicallyAsync(relativePath, content, cancellationToken);
            if (!_mutated && relativePath.EndsWith(".json", StringComparison.Ordinal))
            {
                _mutated = true;
                await File.WriteAllBytesAsync(sourcePath, changedBytes, cancellationToken);
            }
        }

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.ReadAsync(relativePath, cancellationToken);

        public Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken) =>
            inner.ComputeSha256Async(relativePath, cancellationToken);

        public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.DeleteAsync(relativePath, cancellationToken);
    }

    private sealed class FailMetadataWriteStore(IFileStore inner) : IFileStore
    {
        public Task WriteAtomicallyAsync(
            string relativePath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken) => relativePath.EndsWith(".json", StringComparison.Ordinal)
            ? Task.FromException(new IOException("CONTROLLED_METADATA_WRITE_FAILURE"))
            : inner.WriteAtomicallyAsync(relativePath, content, cancellationToken);

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.ReadAsync(relativePath, cancellationToken);

        public Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken) =>
            inner.ComputeSha256Async(relativePath, cancellationToken);

        public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.DeleteAsync(relativePath, cancellationToken);
    }
}
