using System.IO;

namespace Desktop.App;

public sealed record AppLaunchOptions(bool ShowRecoveryFixture, string? DataRoot)
{
#if DEBUG
    private static readonly char[] DirectorySeparators = ['\\', '/'];
#endif
    public static AppLaunchOptions Default { get; } = new(false, null);

    public static AppLaunchOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var fixture = false;
        string? dataRoot = null;
        using var input = arguments.GetEnumerator();
        while (input.MoveNext())
        {
            var argument = input.Current;
            ArgumentException.ThrowIfNullOrWhiteSpace(argument);
            if (string.Equals(argument, "--fixture=recovery", StringComparison.OrdinalIgnoreCase))
            {
#if DEBUG
                fixture = true;
#else
                throw new ArgumentException("DESKTOP_DEVELOPMENT_ARGUMENT_UNSUPPORTED", nameof(arguments));
#endif
            }
            else if (string.Equals(argument, "--data-root", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase))
            {
#if DEBUG
                if (dataRoot is not null)
                    throw new ArgumentException("DESKTOP_DATA_ROOT_DUPLICATE", nameof(arguments));
                var value = argument.Length == "--data-root".Length
                    ? input.MoveNext() ? input.Current : null
                    : argument["--data-root=".Length..];
                dataRoot = ValidateDataRoot(value);
#else
                throw new ArgumentException("DESKTOP_DATA_ROOT_UNSUPPORTED", nameof(arguments));
#endif
            }
            else
            {
                // A typo or a separated value must never turn an intended isolated launch
                // into the normal product launch against the user's database.
                throw new ArgumentException("DESKTOP_LAUNCH_ARGUMENT_UNKNOWN", nameof(arguments));
            }
        }

        return new AppLaunchOptions(fixture, dataRoot);
    }

#if DEBUG
    private static string ValidateDataRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw InvalidRoot();
        var path = value;
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        // V1 isolation is a local, fully qualified directory, not a drive-relative,
        // network/device path, volume root, alternate stream or unresolved alias.
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':'
            || (path[2] != '\\' && path[2] != '/') || !Path.IsPathFullyQualified(path)) throw InvalidRoot();
        foreach (var component in path[3..].Split(DirectorySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or ".." || component.EndsWith(' ') || component.EndsWith('.')
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw InvalidRoot();
            var stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')) throw InvalidRoot();
        }
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) throw InvalidRoot();
            if (!Directory.Exists(Path.GetPathRoot(path))) throw InvalidRoot();
            // Inspect only; no mkdir/probe file or logger until the root has been accepted.
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            {
                try
                {
                    var attributes = File.GetAttributes(directory.FullName);
                    if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                        throw InvalidRoot();
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw InvalidRoot();
        }
    }

    private static ArgumentException InvalidRoot() => new("DESKTOP_DATA_ROOT_INVALID");
#endif
}
