namespace Desktop.Infrastructure.Inputs;

internal static class InputPathGuard
{
    public static string ValidateRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RejectDevicePath(rootPath);

        var fullPath = Path.GetFullPath(rootPath);
        RejectDevicePath(fullPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("INPUT_DIRECTORY_NOT_FOUND");
        }

        RejectReparsePoint(fullPath);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string ValidateDirectChild(string rootPath, string candidatePath, bool directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        RejectDevicePath(candidate);

        var parent = Path.GetDirectoryName(candidate);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("INPUT_PATH_OUTSIDE_APPROVED_LEVEL");
        }

        if (directory ? !Directory.Exists(candidate) : !File.Exists(candidate))
        {
            throw new FileNotFoundException("INPUT_ENTRY_DISAPPEARED", candidate);
        }

        RejectReparsePoint(candidate);
        return candidate;
    }

    public static string ResolveApprovedFile(string rootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
        {
            throw new UnauthorizedAccessException("INPUT_RELATIVE_PATH_INVALID");
        }

        var root = ValidateRoot(rootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        RejectDevicePath(candidate);
        var relative = Path.GetRelativePath(root, candidate);
        if (relative is ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("INPUT_PATH_ESCAPES_ROOT");
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 or > 2)
        {
            throw new UnauthorizedAccessException("INPUT_PATH_OUTSIDE_APPROVED_LEVEL");
        }

        if (segments.Length == 2)
        {
            ValidateDirectChild(root, Path.Combine(root, segments[0]), directory: true);
        }

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException("INPUT_ASSET_DISAPPEARED", candidate);
        }

        RejectReparsePoint(candidate);
        return candidate;
    }

    private static void RejectDevicePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("INPUT_DEVICE_OR_NETWORK_PATH_REJECTED");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("INPUT_REPARSE_POINT_REJECTED");
        }
    }
}
