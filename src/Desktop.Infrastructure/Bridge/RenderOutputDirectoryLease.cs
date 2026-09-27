using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Directory identity, not a file cleanup identity or authority to delete contents.</summary>
internal sealed record RenderOutputDirectoryIdentity(uint Volume, uint High, uint Low, long Created);

/// <summary>Pins existing ancestry; explicit stage creation returns its original handle. Never deletes.</summary>
internal sealed class RenderOutputDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles;
    private readonly bool _canCreate;
    private readonly bool _allowVirtualizedLocalAppData;
    private bool _disposed;

    private RenderOutputDirectoryLease(string path, List<SafeFileHandle> handles, RenderOutputDirectoryIdentity identity,
        bool canCreate = false, bool allowVirtualizedLocalAppData = false)
    { Path = path; _handles = handles; Identity = identity; _canCreate = canCreate; _allowVirtualizedLocalAppData = allowVirtualizedLocalAppData; }

    internal string Path { get; private set; }
    internal string PhysicalPath => ReadFinalPath(_handles[^1]);
    internal RenderOutputDirectoryIdentity Identity { get; }

    internal static RenderOutputDirectoryLease Open(string path, bool requireCreateDirectoryAccess = false,
        bool requireRenameAccess = false, bool allowVirtualizedLocalAppData = false)
    {
        // Only the private evidence reader opts in. Never authorize an OS-mapped
        // source/destination or grant creation/rename rights through this exception.
        if (allowVirtualizedLocalAppData && (requireCreateDirectoryAccess || requireRenameAccess))
            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        path = RenderStyleOutputPlanner.LocalDirectoryPath(path);
        var handles = new List<SafeFileHandle>();
        try
        {
            var root = System.IO.Path.GetPathRoot(path)!;
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_UNAVAILABLE");
            var current = root;
            OpenOne(current, current.Equals(path, StringComparison.OrdinalIgnoreCase));
            foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = System.IO.Path.Combine(current, segment);
                OpenOne(current, current.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
            return new(path, handles, Identify(handles[^1]), requireCreateDirectoryAccess, allowVirtualizedLocalAppData);

            void OpenOne(string directory, bool leaf)
            {
                // LIST_DIRECTORY | READ_ATTRIBUTES [+ ADD_SUBDIRECTORY]. Attributes-only
                // handles do not enforce delete-sharing; a real data-access bit is required.
                // READ/WRITE sharing but no DELETE sharing,
                // OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT. Never follow a junction.
                var handle = CreateFileW(@"\\?\" + directory, 0x81u | (leaf && requireCreateDirectoryAccess ? 4u : 0u)
                    | (leaf && requireRenameAccess ? 0x10000u : 0u),
                    3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                handles.Add(handle);
                if (handle.IsInvalid)
                {
                    var code = Marshal.GetLastWin32Error();
                    throw new RenderOutputTargetException(code is 2 or 3
                        ? "RENDER_OUTPUT_DIRECTORY_UNAVAILABLE" : "RENDER_OUTPUT_DIRECTORY_ACCESS_DENIED");
                }
                _ = Identify(handle);
                VerifyPath(handle, directory, allowVirtualizedLocalAppData);
                var physical = ReadFinalPath(handle);
                if (!physical.Equals(directory, StringComparison.OrdinalIgnoreCase))
                {
                    // Preserve the existing approved OS mapping check above, but also pin the
                    // physical ancestry: a logical LocalAppData chain may omit package ancestors.
                    var chain = new Stack<string>();
                    for (var item = physical; item is not null; item = System.IO.Path.GetDirectoryName(item)) chain.Push(item);
                    foreach (var item in chain)
                    {
                        var pinned = CreateFileW(@"\\?\" + item, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        handles.Add(pinned);
                        if (pinned.IsInvalid) throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_ACCESS_DENIED");
                        _ = Identify(pinned);
                        if (!ReadFinalPath(pinned).Equals(item, StringComparison.OrdinalIgnoreCase))
                            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
                        if (item == physical && Identify(pinned) != Identify(handle))
                            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_IDENTITY_MISMATCH");
                    }
                    // The logical handle retains any ADD_SUBDIRECTORY rights requested above.
                    handles.Remove(handle);
                    handles.Add(handle);
                    VerifyPath(handle, directory, allowVirtualizedLocalAppData);
                }
            }
        }
        catch
        {
            foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose();
            throw;
        }
    }

    internal RenderOutputDirectoryLease CreateStagingChild(string name)
    {
        Verify();
        if (!_canCreate || !System.Text.RegularExpressions.Regex.IsMatch(name, "^\\.edp-stage-[a-f0-9]{64}$"))
            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        var path = System.IO.Path.Combine(Path, name);
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = nameBuffer
            }, namePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = _handles[^1].DangerousGetHandle(),
                ObjectName = namePointer, Attributes = 0x40 // OBJ_CASE_INSENSITIVE
            };
            // FILE_CREATE is exclusive; the returned handle identifies the directory at creation.
            // No CreateDirectory/Open gap, no adoption, no cleanup authority. Keep the parent alive.
            var status = NtCreateFile(out var handle, 0x81, ref attributes, out var result, IntPtr.Zero,
                0x10, 3, 2, 0x00200001, IntPtr.Zero, 0); // DIRECTORY_FILE | OPEN_REPARSE_POINT
            GC.KeepAlive(_handles[^1]);
            if (status != 0 || result.Information != (UIntPtr)2) // FILE_CREATED, not opened/superseded/pending
            {
                handle?.Dispose();
                throw new RenderOutputTargetException(status == unchecked((int)0xC0000035)
                    ? "RENDER_OUTPUT_DIRECTORY_CONFLICT" : "RENDER_OUTPUT_DIRECTORY_ACCESS_DENIED");
            }
            try
            {
                var identity = Identify(handle);
                VerifyPath(handle, path);
                return new(path, [handle], identity);
            }
            catch { handle.Dispose(); throw; } // A newly-created but unregistered directory is retained.
        }
        finally { Marshal.FreeHGlobal(namePointer); Marshal.FreeHGlobal(nameBuffer); }
    }

    internal void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Identify(_handles[^1]) != Identity)
            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_IDENTITY_MISMATCH");
        VerifyPath(_handles[^1], Path, _allowVirtualizedLocalAppData);
    }

    internal void RenameToChild(RenderOutputDirectoryLease parent, string finalName)
    {
        Verify(); parent.Verify();
        var destination = RenderStyleOutputPlanner.LocalDirectoryPath(System.IO.Path.Combine(parent.Path, finalName));
        if (!parent._canCreate || System.IO.Path.GetFileName(destination) != finalName
            || !System.IO.Path.GetDirectoryName(Path)!.Equals(parent.Path, StringComparison.OrdinalIgnoreCase)
            || Identity.Volume != parent.Identity.Volume) throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        // FILE_RENAME_INFORMATION, ReplaceIfExists=false. Destination is relative to the pinned parent,
        // and the source is this very handle, never a later path lookup or a multi-file move.
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var nameOffset = rootOffset + IntPtr.Size + 4;
        var bytes = System.Text.Encoding.Unicode.GetBytes(finalName);
        var length = nameOffset + bytes.Length;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(new byte[length], 0, buffer, length);
            Marshal.WriteIntPtr(buffer, rootOffset, parent._handles[^1].DangerousGetHandle());
            Marshal.WriteInt32(buffer, rootOffset + IntPtr.Size, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + nameOffset, bytes.Length);
            var status = NtSetInformationFile(_handles[^1], out _, buffer, (uint)length, 10);
            if (status != 0)
            {
                var error = checked((int)RtlNtStatusToDosError(status));
                throw new RenderOutputTargetException(error is 80 or 183
                    ? "RENDER_OUTPUT_DIRECTORY_CONFLICT" : "RENDER_OUTPUT_PUBLICATION_RECOVERY_REQUIRED",
                    innerException: new System.ComponentModel.Win32Exception(error));
            }
            Path = destination;
            GC.KeepAlive(parent._handles[^1]);
            Verify();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void VerifyPath(SafeFileHandle handle, string path, bool allowVirtualizedLocalAppData = false)
    {
        var buffer = new char[32768];
        var length = GetLongPathNameW(@"\\?\" + path, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length
            || !new string(buffer, 0, checked((int)length)).Equals(@"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        try { BridgeResourceLease.VerifyOpenedPath(handle, path); }
        catch (InvalidDataException)
        {
            // A merged LocalAppData view can resolve a new child under LocalCache
            // while its existing parent still resolves to the unvirtualized directory.
            // The opened OS handle (not a guessed package path) is the authority;
            // require the exact user-root/suffix mapping and pin/identify its full
            // physical ancestry in Open. Reparse points remain rejected by Identify.
            if (!allowVirtualizedLocalAppData || !IsLocalAppDataMapping(path, ReadFinalPath(handle),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
                throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        }
    }

    internal static bool IsLocalAppDataMapping(string logical, string physical, string localAppData)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(localAppData) + "\\";
        if (!logical.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = logical[root.Length..];
        if (suffix.Length == 0 || suffix.StartsWith("Packages\\", StringComparison.OrdinalIgnoreCase)) return false;
        var packages = root + "Packages\\";
        if (!physical.StartsWith(packages, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = physical[packages.Length..];
        var separator = rest.IndexOf('\\');
        if (separator <= 0 || rest[..separator].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            return false;
        return rest[(separator + 1)..].Equals("LocalCache\\Local\\" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        var path = new string(buffer, 0, checked((int)length));
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        return RenderStyleOutputPlanner.LocalDirectoryPath(path[4..]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var handle in _handles.AsEnumerable().Reverse()) handle.Dispose();
    }

    private static RenderOutputDirectoryIdentity Identify(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)
            || (info.FileAttributes & 0x410) != 0x10
            || info.FileIndexHigh == 0 && info.FileIndexLow == 0)
            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_IDENTITY_INVALID");
        var created = ((long)(uint)info.CreationTime.dwHighDateTime << 32) | (uint)info.CreationTime.dwLowDateTime;
        if (created <= 0) throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_IDENTITY_INVALID");
        return new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow, created);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, IntPtr eaBuffer, uint eaLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string path, [Out] char[] longPath, uint length);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status,
        IntPtr information, uint size, int informationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
