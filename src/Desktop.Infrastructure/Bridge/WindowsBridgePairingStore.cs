using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Desktop.Infrastructure.Bridge;

public interface IBridgePairingSecretStore
{
    byte[]? Read();
    void Save(ReadOnlySpan<byte> secret);
}

/// <summary>Raw 32-byte bridge material, scoped to the current Windows user. Never an ordinary file.</summary>
public sealed class WindowsBridgePairingStore : IBridgePairingSecretStore
{
    public const string DefaultTargetName = "EcommerceDetailPage.RenderBridge.Pairing.v1";
    private const uint Generic = 1;
    private const int NotFound = 1168;
    private readonly string _target;

    public WindowsBridgePairingStore(string targetName = DefaultTargetName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || targetName.Length > 256 || targetName.Contains('\0'))
            throw new ArgumentException("BRIDGE_PAIRING_STORAGE_INVALID", nameof(targetName));
        _target = targetName;
    }

    /// <summary>Caller owns and must clear the returned array.</summary>
    public byte[]? Read() => ReadTarget(_target);

    public void Save(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32) throw new ArgumentException("BRIDGE_PAIRING_STORAGE_INVALID", nameof(secret));
        var previous = Read();
        var writeAttempted = false;
        try
        {
            // Rotation preserves the previous value only in the same protected store.
            // A failed backup never replaces the current credential.
            if (previous is not null) WriteVerified(_target + ".previous", previous);
            writeAttempted = true;
            WriteVerified(_target, secret);
        }
        catch
        {
            if (writeAttempted)
            {
                try
                {
                    if (previous is not null) WriteVerified(_target, previous);
                    else if (!CredDeleteW(_target, Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
                        throw StorageError();
                }
                catch { /* No successful setup is reported if rollback also fails. */ }
            }
            throw StorageError();
        }
        finally { if (previous is not null) CryptographicOperations.ZeroMemory(previous); }
    }

    private static void WriteVerified(string target, ReadOnlySpan<byte> secret)
    {
        byte[]? readback = null;
        try
        {
            WriteTarget(target, secret);
            readback = ReadTarget(target);
            if (readback is null || !CryptographicOperations.FixedTimeEquals(secret, readback)) throw StorageError();
        }
        finally { if (readback is not null) CryptographicOperations.ZeroMemory(readback); }
    }

    private static byte[]? ReadTarget(string target)
    {
        if (!OperatingSystem.IsWindows()) throw StorageError();
        if (!CredReadW(target, Generic, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            throw StorageError();
        }
        try
        {
            var value = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (value.CredentialBlobSize != 32 || value.CredentialBlob == IntPtr.Zero) throw StorageError();
            var bytes = new byte[32];
            Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    private static void WriteTarget(string target, ReadOnlySpan<byte> secret)
    {
        if (!OperatingSystem.IsWindows()) throw StorageError();
        var targetPointer = Marshal.StringToCoTaskMemUni(target);
        var userPointer = IntPtr.Zero;
        var blobPointer = IntPtr.Zero;
        var copy = secret.ToArray();
        try
        {
            userPointer = Marshal.StringToCoTaskMemUni("photoshop-renderer");
            blobPointer = Marshal.AllocHGlobal(copy.Length);
            Marshal.Copy(copy, 0, blobPointer, copy.Length);
            var value = new NativeCredential
            {
                Type = Generic, TargetName = targetPointer, UserName = userPointer,
                CredentialBlobSize = (uint)copy.Length, CredentialBlob = blobPointer, Persist = 2
            };
            if (!CredWriteW(ref value, 0)) throw StorageError();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
            if (blobPointer != IntPtr.Zero)
            {
                Marshal.Copy(copy, 0, blobPointer, copy.Length);
                Marshal.FreeHGlobal(blobPointer);
            }
            if (userPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(userPointer);
            Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    private static InvalidOperationException StorageError() => new("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
