using System.Runtime.InteropServices;
using Desktop.Application.Psd;
using Microsoft.Win32.SafeHandles;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Only this preparation's new copies; unknown executions retain them for recovery.</summary>
public sealed class PreparedRenderResources
{
    private readonly IReadOnlyList<OwnedRenderResourceFile> _copies;

    internal PreparedRenderResources(IReadOnlyList<BridgeResourceSource> sources, PsdInspectionDocument template,
        IReadOnlyList<OwnedRenderResourceFile> copies, RenderTemporaryFileLedger? temporaryFiles = null)
    { Sources = sources; Template = template; _copies = copies; TemporaryFiles = temporaryFiles; }

    internal RenderTemporaryFileLedger? TemporaryFiles { get; }

    public IReadOnlyList<BridgeResourceSource> Sources { get; }
    public PsdInspectionDocument Template { get; }

    // Call only before submission or after a confirmed execution terminal has closed read leases.
    public void Release() => Release(_copies);

    internal static void Release(IEnumerable<OwnedRenderResourceFile> copies)
    {
        var failed = false;
        foreach (var copy in copies)
        {
            try { copy.Dispose(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        }
        if (failed) throw new IOException("RENDER_RESOURCE_RELEASE_FAILED");
    }
}

/// <summary>Deletes by a verified Windows file identity, never by a path after releasing its handle.</summary>
internal sealed class OwnedRenderResourceFile : IDisposable
{
    internal sealed record Identity(uint Volume, uint High, uint Low, long Created);
    private readonly string _path;
    private readonly Identity _identity;
    private bool _released;

    private OwnedRenderResourceFile(string path, Identity identity) { _path = path; _identity = identity; }

    internal Identity FileIdentity => _identity;
    internal static OwnedRenderResourceFile Restore(string path, Identity identity) => new(path, identity);

    internal static OwnedRenderResourceFile Capture(string path, SafeFileHandle handle)
        => new(path, ReadIdentity(handle));

    // Reading a file identity conveys no deletion authority. Public stage ownership uses
    // this while its original create handle remains open, before writing any bytes.
    internal static Identity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Invalid();
        if ((info.FileAttributes & 0x410) != 0 || info.NumberOfLinks != 1) throw Invalid();
        var identity = Identify(info);
        if (identity.Created <= 0 || identity.High == 0 && identity.Low == 0) throw Invalid();
        return identity;
    }

    public void Dispose()
    {
        if (_released) return;
        BridgeOutputReservation.ValidatePath(_path);
        // OPEN_EXISTING + OPEN_REPARSE_POINT; only our exact file gets DELETE access.
        // Unlike FileStream, a direct Win32 open needs the extended form for plan-derived paths over MAX_PATH.
        using var handle = CreateFileW(@"\\?\" + Path.GetFullPath(_path), 0x80010000, 1, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            // An earlier cleanup may have deleted this exact entry before its ledger flush.
            if (Marshal.GetLastWin32Error() is 2 or 3) { _released = true; return; }
            throw Invalid();
        }
        BridgeResourceLease.VerifyOpenedPath(handle, _path);
        if (!GetFileInformationByHandle(handle, out var actual)
            || Identify(actual) != _identity || actual.NumberOfLinks != 1
            || (actual.FileAttributes & 0x410) != 0) throw Invalid();
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw Invalid();
        _released = true;
        // Closing this handle removes only the verified copy. No directory enumeration/deletion.
    }

    private static IOException Invalid() => new("RENDER_RESOURCE_RELEASE_FAILED");

    private static Identity Identify(FileInformation value) => new(value.VolumeSerialNumber, value.FileIndexHigh,
        value.FileIndexLow, ((long)(uint)value.CreationTime.dwHighDateTime << 32) | (uint)value.CreationTime.dwLowDateTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint size);
}
