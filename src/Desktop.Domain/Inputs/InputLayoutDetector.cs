using System.Collections.Immutable;

namespace Desktop.Domain.Inputs;

public static class InputLayoutDetector
{
    public static InputLayoutDetection Detect(InputDirectoryInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var rootFiles = SupportedFiles(inventory.RootFiles);
        var imageDirectories = inventory.FirstLevelDirectories
            .Select(directory => new DetectedStyleSource(
                directory.Name,
                directory.RelativePath,
                SupportedFiles(directory.DirectFiles)))
            .Where(style => !style.Files.IsDefaultOrEmpty)
            .OrderBy(style => style.SourceDirectoryRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        if (!rootFiles.IsDefaultOrEmpty && imageDirectories.IsDefaultOrEmpty)
        {
            return InputLayoutDetection.Success(
                BatchInputType.SingleStyle,
                [new DetectedStyleSource(inventory.RootDisplayName, string.Empty, rootFiles)]);
        }

        if (rootFiles.IsDefaultOrEmpty && !imageDirectories.IsDefaultOrEmpty)
        {
            if (imageDirectories.Length > 200)
            {
                return InputLayoutDetection.Blocked(ScanBlockReason.TooManyStyles());
            }

            return InputLayoutDetection.Success(BatchInputType.StyleDirectoryBatch, imageDirectories);
        }

        return InputLayoutDetection.Blocked(
            !rootFiles.IsDefaultOrEmpty && !imageDirectories.IsDefaultOrEmpty
                ? ScanBlockReason.MixedLayout()
                : ScanBlockReason.EmptyLayout());
    }

    private static ImmutableArray<InputFileReference> SupportedFiles(
        IEnumerable<InputFileReference> files) => files
            .Where(file => file.HasSupportedExtension)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
}
