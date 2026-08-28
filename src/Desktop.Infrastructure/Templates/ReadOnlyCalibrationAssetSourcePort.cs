using System.Security.Cryptography;
using Desktop.Application.Templates;

namespace Desktop.Infrastructure.Templates;

public sealed class ReadOnlyCalibrationAssetSourcePort : ICalibrationAssetSourcePort
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp"
    };

    public async Task<TemplateCalibrationAsset> InspectReadOnlyAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || !Path.IsPathFullyQualified(sourcePath)
            || !SupportedExtensions.Contains(Path.GetExtension(sourcePath))
            || !File.Exists(sourcePath))
        {
            throw new InvalidOperationException("TPL_CALIBRATION_ASSET_REF_INVALID");
        }

        var fullPath = Path.GetFullPath(sourcePath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0)
        {
            throw new InvalidOperationException("TPL_CALIBRATION_ASSET_REF_INVALID");
        }

        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return new TemplateCalibrationAsset(
            $"asset_{hash[..24]}",
            Path.GetFileName(fullPath),
            fullPath,
            Width: 0,
            Height: 0);
    }
}
