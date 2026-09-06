using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class ReferencePhotoFingerprintPortTests
{
    [Fact]
    public async Task ScansSupportedPhotosReadOnlyAndComputesStableIdsAndDimensions()
    {
        using var directory = new TestDirectory();
        var png = Path.Combine(directory.Path, "nested-not-followed.png");
        await File.WriteAllBytesAsync(png, PngHeader(3, 5));
        var port = new ReferencePhotoFingerprintPort();

        var result = await port.ScanAsync(directory.Path, CancellationToken.None);

        var photo = Assert.Single(result.Photos);
        Assert.Equal("nested-not-followed.png", photo.RelativePath);
        Assert.Equal("image/png", photo.MimeType);
        Assert.Equal(3, photo.Width);
        Assert.Equal(5, photo.Height);
        Assert.StartsWith("asset_", photo.AssetId, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", result.FolderDigest, StringComparison.Ordinal);
        Assert.Equal(64, result.FolderDigest.Length - "sha256:".Length);
    }

    [Fact]
    public async Task RejectsEmptyReferenceFolderInsteadOfReturningAnEmptyLearningSet()
    {
        using var directory = new TestDirectory();
        var port = new ReferencePhotoFingerprintPort();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.ScanAsync(directory.Path, CancellationToken.None));

        Assert.Equal("TPL_LEARN_REFERENCE_PHOTOS_MISSING", error.Message);
    }

    [Fact]
    public async Task IdenticalContentAtDifferentRelativePathsKeepsDistinctStableAssetIds()
    {
        using var directory = new TestDirectory();
        var bytes = PngHeader(3, 5);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "one.png"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "two.png"), bytes);
        var port = new ReferencePhotoFingerprintPort();

        var result = await port.ScanAsync(directory.Path, CancellationToken.None);

        Assert.Equal(2, result.Photos.Count);
        Assert.Equal(result.Photos[0].Sha256, result.Photos[1].Sha256);
        Assert.NotEqual(result.Photos[0].AssetId, result.Photos[1].AssetId);
    }

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        // Length/type are not needed for fingerprinting; only IHDR dimensions
        // are read and the original bytes remain untouched.
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        return bytes;
    }
}
