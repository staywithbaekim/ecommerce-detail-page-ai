using System.Diagnostics;
using System.IO;

namespace Desktop.App;

internal interface IOutputDirectoryOpener
{
    void Open(string directory);
}

/// <summary>Opens an existing local directory only; no command interpreter or user-supplied arguments.</summary>
internal sealed class WindowsOutputDirectoryOpener : IOutputDirectoryOpener
{
    public void Open(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (directory.Length < 3 || !char.IsAsciiLetter(directory[0]) || directory[1] != ':' || directory[2] != '\\'
            || directory[2..].Contains(':') || directory.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '|' or '?' or '*'))
            throw new InvalidDataException("RENDER_OUTPUT_DIRECTORY_INVALID");
        var path = Path.GetFullPath(directory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("RENDER_OUTPUT_DIRECTORY_UNAVAILABLE");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RENDER_OUTPUT_DIRECTORY_INVALID");
        using var process = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "open" });
    }
}
