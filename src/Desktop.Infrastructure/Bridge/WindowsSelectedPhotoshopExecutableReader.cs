using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Desktop.Application.Diagnostics;
using Desktop.Infrastructure.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Desktop.Infrastructure.Bridge;

internal interface ISelectedPhotoshopExecutableReader
{
    Task<SelectedPhotoshopExecutable> OpenAsync(string path, CancellationToken token);
}

internal sealed class SelectedPhotoshopExecutable(string executablePath, string numericFileVersion,
    long length, string sha256, IDisposable hold) : IDisposable
{
    private int _disposed;
    public string ExecutablePath { get; } = executablePath;
    public string NumericFileVersion { get; } = numericFileVersion;
    public long Length { get; } = length;
    public string Sha256 { get; } = sha256;
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) hold.Dispose(); }
}

internal sealed class WindowsSelectedPhotoshopExecutableReader : ISelectedPhotoshopExecutableReader
{
    public async Task<SelectedPhotoshopExecutable> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = WindowsPhotoshopEnvironmentProbe.NormalizeExecutablePath(path);
        SelectedPhotoshopDirectoryLocks? parents = null;
        FileStream? stream = null;
        try
        {
            parents = SelectedPhotoshopDirectoryLocks.Open(path);
            BridgeOutputReservation.ValidatePath(path);
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, path);
            var installation = new WindowsPhotoshopEnvironmentReader().ReadInstallation(path, PhotoshopDiscoverySource.UserSelectedFile);
            if (!installation.IsVersionCandidate) throw new InvalidOperationException("PHOTOSHOP_HOST_UNSUPPORTED");
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            BridgeOutputReservation.ValidatePath(path);
            BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, path);
            var value = new SelectedPhotoshopExecutable(path, installation.NumericFileVersion, stream.Length, hash,
                new FileHold(stream, parents));
            stream = null; parents = null;
            return value;
        }
        catch (InvalidDataException error)
        {
            throw new InvalidOperationException(error.Message == "PHOTOSHOP_EXECUTABLE_METADATA_UNRECOGNIZED"
                ? "PHOTOSHOP_HOST_UNSUPPORTED" : "PHOTOSHOP_HOST_PATH_INVALID");
        }
        finally { stream?.Dispose(); parents?.Dispose(); }
    }

    private sealed class FileHold(FileStream stream, SelectedPhotoshopDirectoryLocks parents) : IDisposable
    {
        public void Dispose() { try { stream.Dispose(); } finally { parents.Dispose(); } }
    }
}

/// <summary>Holds the local directory chain against rename/replacement while a chosen identity is in use.</summary>
internal sealed class SelectedPhotoshopDirectoryLocks : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private SelectedPhotoshopDirectoryLocks() { }

    public static SelectedPhotoshopDirectoryLocks Open(string filePath, bool allowOwnedDirectoryMapping = false)
    {
        BridgeOutputReservation.ValidatePath(filePath);
        if (new DriveInfo(Path.GetPathRoot(filePath)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidOperationException("PHOTOSHOP_HOST_PATH_INVALID");
        var chain = new Stack<string>();
        for (var parent = Path.GetDirectoryName(filePath); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            chain.Push(parent);
        var locks = new SelectedPhotoshopDirectoryLocks();
        try
        {
            foreach (var path in chain)
            {
                // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES: attributes-only handles do not
                // enforce the delete-sharing restriction. Keep read/write sharing but never
                // delete sharing; OPEN_REPARSE_POINT avoids following a newly inserted link.
                var handle = CreateFileW(@"\\?\" + path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    if (error is 2 or 3) throw new DirectoryNotFoundException();
                    throw new IOException("PHOTOSHOP_HOST_PATH_UNAVAILABLE");
                }
                locks._handles.Add(handle);
                BridgeOutputReservation.ValidatePath(path);
                if (allowOwnedDirectoryMapping)
                    locks.VerifyOwnedDirectory(handle, path);
                else
                    BridgeResourceLease.VerifyOpenedPath(handle, path);
            }
            BridgeOutputReservation.ValidatePath(filePath);
            return locks;
        }
        catch { locks.Dispose(); throw; }
    }

    private void VerifyOwnedDirectory(SafeFileHandle handle, string expected)
    {
        var actual = FinalDirectoryPath(handle);
        BridgeOutputReservation.ValidatePath(actual);
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;

        // Packaged Windows launchers can map an application-owned LocalAppData directory
        // while leaving its logical parent unmapped. A file/parent equality check is not a
        // valid proof for that directory. Pin the complete OS-resolved physical chain too;
        // do not whitelist package paths, rewrite roots, or accept any reparse component.
        var physicalChain = new Stack<string>();
        for (var path = actual; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            physicalChain.Push(path);
        foreach (var path in physicalChain)
        {
            BridgeOutputReservation.ValidatePath(path);
            var physical = CreateFileW(@"\\?\" + path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (physical.IsInvalid)
            {
                physical.Dispose();
                throw new IOException("PHOTOSHOP_HOST_PATH_UNAVAILABLE");
            }
            _handles.Add(physical);
            BridgeOutputReservation.ValidatePath(path);
            if (!string.Equals(FinalDirectoryPath(physical), path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PHOTOSHOP_HOST_PATH_INVALID");
        }
        // Once both chains are pinned, opening the logical name must still identify the
        // same physical directory. The selected executable never uses this storage mode.
        using var confirmed = CreateFileW(@"\\?\" + expected, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (confirmed.IsInvalid
            || !string.Equals(FinalDirectoryPath(confirmed), actual, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(FinalDirectoryPath(handle), actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PHOTOSHOP_HOST_PATH_INVALID");
        BridgeOutputReservation.ValidatePath(expected);
    }

    private static string FinalDirectoryPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException("PHOTOSHOP_HOST_PATH_UNAVAILABLE");
        var path = new string(buffer, 0, (int)length);
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new InvalidDataException("PHOTOSHOP_HOST_PATH_INVALID");
        path = path[4..];
        BridgeOutputReservation.ValidatePath(path);
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("PHOTOSHOP_HOST_PATH_INVALID");
        return path;
    }

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
        _handles.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
}
