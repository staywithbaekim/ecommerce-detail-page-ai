using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Inputs;

internal static class InputInventoryReader
{
    public static InputDirectoryInventory ReadApprovedLevels(string inputRoot)
    {
        var root = InputPathGuard.ValidateRoot(inputRoot);
        var rootFiles = EnumerateDirectFiles(root, root);
        var directories = new List<FirstLevelDirectoryInventory>();
        foreach (var directoryPath in Directory.EnumerateDirectories(root)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var approvedDirectory = InputPathGuard.ValidateDirectChild(root, directoryPath, directory: true);
            var name = Path.GetFileName(approvedDirectory);
            directories.Add(FirstLevelDirectoryInventory.Create(
                name,
                NormalizeRelativePath(Path.GetRelativePath(root, approvedDirectory)),
                EnumerateDirectFiles(root, approvedDirectory)));
        }

        var displayName = new DirectoryInfo(root).Name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = root;
        }

        return InputDirectoryInventory.Create(displayName, rootFiles, directories);
    }

    private static List<InputFileReference> EnumerateDirectFiles(
        string root,
        string directory)
    {
        var files = new List<InputFileReference>();
        foreach (var filePath in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var approvedFile = string.Equals(root, directory, StringComparison.OrdinalIgnoreCase)
                ? InputPathGuard.ValidateDirectChild(root, filePath, directory: false)
                : ValidateFirstLevelFile(root, directory, filePath);
            files.Add(InputFileReference.Create(
                NormalizeRelativePath(Path.GetRelativePath(root, approvedFile)),
                Path.GetFileName(approvedFile)));
        }

        return files;
    }

    private static string ValidateFirstLevelFile(string root, string directory, string filePath)
    {
        _ = InputPathGuard.ValidateDirectChild(root, directory, directory: true);
        var candidate = Path.GetFullPath(filePath);
        if (!string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("INPUT_PATH_OUTSIDE_APPROVED_LEVEL");
        }

        return InputPathGuard.ResolveApprovedFile(root, Path.GetRelativePath(root, candidate));
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
