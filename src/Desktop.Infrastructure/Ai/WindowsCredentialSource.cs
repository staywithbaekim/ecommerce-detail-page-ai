using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Desktop.Application.Ai;

namespace Desktop.Infrastructure.Ai;

/// <summary>
/// Reads one Windows Credential Manager generic credential on demand. The
/// secret is kept only in the returned string and native memory is released
/// immediately; callers must not persist or log it.
/// </summary>
public sealed class WindowsCredentialSource : IAiCredentialSource
{
    public const string DefaultTargetName = "AI-01-VolcArk-Test";
    private const uint GenericCredentialType = 1;

    private readonly string _targetName;

    public WindowsCredentialSource(string targetName = DefaultTargetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (targetName.Length > 256 || targetName.Contains('\0'))
        {
            throw new ArgumentException("Credential target is invalid.", nameof(targetName));
        }

        _targetName = targetName;
    }

    public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<string?>(null);
        }

        if (!CredReadW(_targetName, GenericCredentialType, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_NOT_FOUND is the normal missing-key path. Other failures
            // are deliberately surfaced without including the target secret.
            if (error == 1168)
            {
                return ValueTask.FromResult<string?>(null);
            }

            throw new Win32Exception(error, "无法读取 AI 凭据存储。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return ValueTask.FromResult<string?>(null);
            }

            if (credential.CredentialBlobSize > 16 * 1024)
            {
                throw new InvalidDataException("AI 凭据长度超出安全上限。");
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = DecodeCredential(bytes);
            return ValueTask.FromResult<string?>(string.IsNullOrWhiteSpace(value) ? null : value);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static string DecodeCredential(byte[] bytes)
    {
        // cmdkey/PowerShell integrations commonly write UTF-8; accept UTF-16
        // only when the blob has the characteristic alternating zero bytes.
        var utf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
        if (!utf8.Contains('\uFFFD', StringComparison.Ordinal))
        {
            return utf8;
        }

        return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);

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
