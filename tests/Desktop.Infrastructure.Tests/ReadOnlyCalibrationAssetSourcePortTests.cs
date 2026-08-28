using System.Security.Cryptography;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class ReadOnlyCalibrationAssetSourcePortTests
{
    [Fact]
    public async Task InspectionUsesReadOnlySequentialHashAndDoesNotChangeSource()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "代表款.png");
        var bytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, bytes);
        var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

        var result = await new ReadOnlyCalibrationAssetSourcePort().InspectReadOnlyAsync(
            path,
            CancellationToken.None);

        var after = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Equal(before, after);
        Assert.StartsWith("asset_", result.AssetId, StringComparison.Ordinal);
        Assert.Equal("代表款.png", result.DisplayName);
        Assert.Equal(Path.GetFullPath(path), result.SourceReference);
    }
}
