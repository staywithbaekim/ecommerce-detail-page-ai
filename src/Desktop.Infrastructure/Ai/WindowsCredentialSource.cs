using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Ai;

namespace Desktop.Infrastructure.Ai;

/// <summary>
/// Reads one Windows Credential Manager generic credential on demand. The
/// secret is kept only in the returned string and native memory is released
/// immediately; callers must not persist or log it.
/// </summary>
public sealed class WindowsCredentialSource : IAiCredentialSource, IAiCredentialManagementPort
{
    public const string DefaultTargetName = "AI-01-VolcArk-Test";
    private const uint GenericCredentialType = 1;
    private const uint PersistLocalMachine = 2;
    private const uint ErrorNotFound = 1168;

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
            if (error == ErrorNotFound)
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
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var value = DecodeCredential(bytes);
                return ValueTask.FromResult<string?>(string.IsNullOrWhiteSpace(value)
                    ? null
                    : AiCredentialRules.NormalizeAndValidate(value));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public async Task<AiCredentialStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return new(AiCredentialState.Unavailable, null, "AI_CREDENTIAL_WINDOWS_ONLY");
        }

        try
        {
            var value = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
            return value is null
                ? new(AiCredentialState.NotConfigured, null)
                : new(AiCredentialState.Configured, AiCredentialRules.MaskSuffix(value));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Status is intentionally redacted.  The exception is not returned
            // because it could contain provider-specific or machine details.
            return new(AiCredentialState.Unavailable, null, "AI_CREDENTIAL_READ_FAILED");
        }
    }

    public Task SaveAsync(string apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = AiCredentialRules.NormalizeAndValidate(apiKey);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager 仅在 Windows 上可用。");
        }

        var targetPointer = IntPtr.Zero;
        var userPointer = IntPtr.Zero;
        var blobPointer = IntPtr.Zero;
        var blob = Encoding.UTF8.GetBytes(normalized);
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(_targetName);
            userPointer = Marshal.StringToCoTaskMemUni("api-key");
            blobPointer = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, blobPointer, blob.Length);

            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)blob.Length),
                CredentialBlob = blobPointer,
                Persist = PersistLocalMachine,
                UserName = userPointer
            };
            if (!CredWriteW(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "无法写入 AI 凭据存储。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            if (blobPointer != IntPtr.Zero)
            {
                ZeroAndFree(blobPointer, blob.Length);
            }

            if (targetPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(targetPointer);
            }

            if (userPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(userPointer);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager 仅在 Windows 上可用。");
        }

        if (!CredDeleteW(_targetName, GenericCredentialType, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "无法移除 AI 凭据存储中的 Key。");
            }
        }

        return Task.CompletedTask;
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (length > 0)
        {
            Marshal.Copy(new byte[length], 0, pointer, length);
        }

        Marshal.FreeHGlobal(pointer);
    }

    private static string DecodeCredential(byte[] bytes)
    {
        // Generic Credential Manager blobs are commonly UTF-16LE when written
        // by cmdkey/PowerShell. Detect that representation before attempting
        // UTF-8 so internal NUL bytes never reach an HTTP header.
        if (bytes.Length >= 2
            && bytes.Length % 2 == 0
            && bytes.Where((value, index) => index % 2 == 1).All(value => value == 0))
        {
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        }

        // Otherwise accept UTF-8, rejecting replacement characters as a sign
        // that the blob should not be treated as a valid API key.
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(
        ref NativeCredential credential,
        uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(
        string target,
        uint type,
        uint flags);

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
