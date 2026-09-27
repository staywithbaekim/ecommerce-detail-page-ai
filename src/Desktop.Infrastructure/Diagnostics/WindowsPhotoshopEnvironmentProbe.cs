using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Desktop.Application.Diagnostics;
using Microsoft.Win32;

namespace Desktop.Infrastructure.Diagnostics;

/// <summary>Metadata inventory only: no process launch, COM, bridge, document, credential or database access.</summary>
public sealed class WindowsPhotoshopEnvironmentProbe : IPhotoshopEnvironmentProbe
{
    internal const int MaximumCandidates = 128;
    private readonly IPhotoshopEnvironmentReader _reader;
    private readonly TimeProvider _clock;
    private PhotoshopEnvironmentSnapshot? _latest;
    private int _busy;

    public WindowsPhotoshopEnvironmentProbe() : this(new WindowsPhotoshopEnvironmentReader(), TimeProvider.System) { }

    internal WindowsPhotoshopEnvironmentProbe(IPhotoshopEnvironmentReader reader, TimeProvider clock)
    { _reader = reader; _clock = clock; }

    public PhotoshopEnvironmentSnapshot? Latest => Volatile.Read(ref _latest);

    public async Task<PhotoshopEnvironmentSnapshot> DetectAsync(string? additionalExecutablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("PHOTOSHOP_DETECTION_BUSY");
        try
        {
            var result = await Task.Run(() => Detect(additionalExecutablePath, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _latest, result);
            return result;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private PhotoshopEnvironmentSnapshot Detect(string? additionalPath, CancellationToken token)
    {
        var candidates = new Dictionary<string, PhotoshopDiscoverySource>(StringComparer.OrdinalIgnoreCase);
        PhotoshopInstallation? manual = null;
        // Validate explicit input before collecting any other metadata; never run an arbitrary registry command.
        if (additionalPath is not null)
        {
            var path = NormalizeExecutablePath(additionalPath);
            manual = _reader.ReadInstallation(path, PhotoshopDiscoverySource.UserSelectedFile);
            candidates.Add(path, PhotoshopDiscoverySource.UserSelectedFile);
        }
        var discovered = _reader.Discover(token);
        var unavailable = discovered.UnavailableSources;
        var truncated = discovered.Truncated;
        foreach (var item in discovered.Candidates.Take(MaximumCandidates))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = NormalizeExecutablePath(item.Path);
                if (candidates.TryGetValue(path, out var source)) candidates[path] = source | item.Source;
                else if (candidates.Count < MaximumCandidates) candidates.Add(path, item.Source);
                else truncated = true;
            }
            catch (InvalidDataException) { unavailable++; }
        }
        if (discovered.Candidates.Count > MaximumCandidates) truncated = true;
        var installations = new List<PhotoshopInstallation>();
        foreach (var item in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var installation = manual is not null && string.Equals(manual.ExecutablePath, item.Key, StringComparison.OrdinalIgnoreCase)
                    ? manual : _reader.ReadInstallation(item.Key, item.Value);
                installations.Add(installation with { Source = item.Value, IsRunning = item.Value.HasFlag(PhotoshopDiscoverySource.RunningProcess) });
            }
            catch (Exception error) when (IsReadFailure(error)) { unavailable++; }
        }
        token.ThrowIfCancellationRequested();
        return new(_clock.GetUtcNow(), discovered.OsVersion, discovered.OsArchitecture, discovered.IsWindowsClient,
            Array.AsReadOnly(installations.OrderByDescending(item => item.IsRunning)
                .ThenByDescending(item => item.IsVersionCandidate).ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToArray()),
            unavailable, truncated);
    }

    internal static bool IsReadFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or SecurityException or Win32Exception or InvalidOperationException or ArgumentException;

    internal static string NormalizeExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Length < 4
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path[2..].Contains(':') || path.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '|' or '?' or '*' or '/')
            || path[3..].Split('\\').Any(part => part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException("PHOTOSHOP_LOCAL_EXECUTABLE_REQUIRED");
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(full), "Photoshop.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PHOTOSHOP_LOCAL_EXECUTABLE_REQUIRED");
        return full;
    }
}

internal sealed record PhotoshopPathCandidate(string Path, PhotoshopDiscoverySource Source);
internal sealed record PhotoshopDiscovery(IReadOnlyList<PhotoshopPathCandidate> Candidates,
    string OsVersion, string OsArchitecture, bool IsWindowsClient, int UnavailableSources = 0, bool Truncated = false);

internal interface IPhotoshopEnvironmentReader
{
    PhotoshopDiscovery Discover(CancellationToken token);
    PhotoshopInstallation ReadInstallation(string path, PhotoshopDiscoverySource source);
}

internal sealed class WindowsPhotoshopEnvironmentReader : IPhotoshopEnvironmentReader
{
    public PhotoshopDiscovery Discover(CancellationToken token)
    {
        var paths = new List<PhotoshopPathCandidate>();
        var unavailable = 0;
        var truncated = false;
        void Add(string? path, PhotoshopDiscoverySource source, bool directory = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim().Trim('"');
            if (directory) path = Path.Combine(path, "Photoshop.exe");
            if (paths.Count < WindowsPhotoshopEnvironmentProbe.MaximumCandidates) paths.Add(new(path, source));
            else truncated = true;
        }
        void Read(Action action)
        {
            token.ThrowIfCancellationRequested();
            try { action(); }
            catch (Exception error) when (WindowsPhotoshopEnvironmentProbe.IsReadFailure(error)) { unavailable++; }
        }

        Read(() =>
        {
            var processes = Process.GetProcessesByName("Photoshop");
            try
            {
                if (processes.Length > 64) truncated = true;
                foreach (var process in processes.Take(64))
                    Read(() => Add(process.MainModule?.FileName, PhotoshopDiscoverySource.RunningProcess));
            }
            finally { foreach (var process in processes) process.Dispose(); }
        });
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            Read(() =>
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var appPath = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Photoshop.exe", writable: false);
                Add(appPath?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, PhotoshopDiscoverySource.Registry);
                using var adobe = root.OpenSubKey(@"SOFTWARE\Adobe\Photoshop", writable: false);
                if (adobe is null) return;
                var keys = adobe.GetSubKeyNames();
                if (keys.Length > 64) truncated = true;
                foreach (var name in keys.Take(64))
                    Read(() =>
                    {
                        using var version = adobe.OpenSubKey(name, writable: false);
                        Add(version?.GetValue("ApplicationPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                            PhotoshopDiscoverySource.Registry, directory: true);
                    });
            });
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            Read(() =>
            {
                var root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root)) return;
                root = Path.Combine(root, "Adobe");
                if (!Directory.Exists(root)) return;
                ValidateLocalPath(root);
                var directories = Directory.EnumerateDirectories(root, "Adobe Photoshop*", SearchOption.TopDirectoryOnly).Take(65).ToArray();
                if (directories.Length > 64) truncated = true;
                foreach (var directory in directories.Take(64)) Add(directory, PhotoshopDiscoverySource.StandardDirectory, directory: true);
            });
        var isClient = false;
        Read(() =>
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var version = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            isClient = string.Equals(version?.GetValue("InstallationType") as string, "Client", StringComparison.Ordinal);
        });
        return new(paths, Environment.OSVersion.Version.ToString(), RuntimeInformation.OSArchitecture.ToString(), isClient, unavailable, truncated);
    }

    public PhotoshopInstallation ReadInstallation(string path, PhotoshopDiscoverySource source)
    {
        path = WindowsPhotoshopEnvironmentProbe.NormalizeExecutablePath(path);
        ValidateLocalPath(path);
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.OriginalFilename, "Photoshop.exe", StringComparison.OrdinalIgnoreCase)
            || info.ProductName?.StartsWith("Adobe Photoshop", StringComparison.OrdinalIgnoreCase) != true
            || info.CompanyName?.StartsWith("Adobe", StringComparison.OrdinalIgnoreCase) != true
            || info.FileMajorPart <= 0)
            throw new InvalidDataException("PHOTOSHOP_EXECUTABLE_METADATA_UNRECOGNIZED");
        var numeric = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart).ToString();
        var display = info.FileVersion;
        if (string.IsNullOrWhiteSpace(display) || display.Length > 160 || display.Any(char.IsControl)) display = numeric;
        return new(path, numeric, display, source, source.HasFlag(PhotoshopDiscoverySource.RunningProcess));
    }

    private static void ValidateLocalPath(string path)
    {
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("PHOTOSHOP_LOCAL_EXECUTABLE_REQUIRED");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("PHOTOSHOP_REPARSE_PATH_REJECTED");
    }
}
