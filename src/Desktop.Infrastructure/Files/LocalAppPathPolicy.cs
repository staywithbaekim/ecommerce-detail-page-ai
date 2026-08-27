using Desktop.Application.Files;

namespace Desktop.Infrastructure.Files;

public sealed class LocalAppPathPolicy : IPathPolicy
{
    private readonly string _root;
    private readonly string _rootWithSeparator;

    public LocalAppPathPolicy(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        _rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
    }

    public string ResolveOwnedPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
        {
            throw new InvalidOperationException("PATH_NOT_APPLICATION_OWNED");
        }

        var resolved = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!resolved.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PATH_ESCAPES_APPLICATION_ROOT");
        }

        return resolved;
    }

    public bool IsOwnedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var resolved = Path.GetFullPath(path);
        return resolved.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
