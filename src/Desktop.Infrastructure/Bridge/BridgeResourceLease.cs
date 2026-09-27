using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Microsoft.Win32.SafeHandles;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Trusted desktop resource inventory; this type never crosses the wire.</summary>
public sealed record BridgeResourceSource(string ResourceId, string Purpose, string MediaType, string OwnedRelativePath)
{
    public override string ToString() => nameof(BridgeResourceSource);
}

/// <summary>Read-only handles held for one plan/session. UXP receives bytes into its own copies, never these paths.</summary>
public sealed class BridgeResourceLease : IAsyncDisposable, IDisposable
{
    public const int ChunkByteLimit = 256 * 1024;
    private readonly string _session;
    private readonly string _digest;
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private BridgeResourceLease(string session, string digest) { _session = session; _digest = digest; }

    public IReadOnlyList<BridgeResourceBindingV1> Bindings => _resources.Values.Select(r => r.Binding).ToArray();

    internal async Task<bool> MatchesAsync(string sessionId, string digest,
        IReadOnlyList<BridgeResourceBindingV1> bindings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return !_disposed && sessionId == _session && string.Equals(digest, _digest, StringComparison.OrdinalIgnoreCase)
                && bindings.Count == _resources.Count && bindings.All(b => _resources.TryGetValue(b.SessionHandle, out var resource)
                    && b.ResourceId == resource.Binding.ResourceId && b.Purpose == resource.Binding.Purpose
                    && b.ReadOnly && b.ByteLength == resource.Binding.ByteLength
                    && string.Equals(b.Sha256, resource.Binding.Sha256, StringComparison.OrdinalIgnoreCase));
        }
        finally { _gate.Release(); }
    }

    public static async Task<BridgeResourceLease> CreateAsync(string planJson, string sessionId,
        IReadOnlyList<BridgeResourceSource> sources, IPathPolicy paths, IRenderPlanValidator validator,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(planJson) > BridgeProtocol.PlanByteLimit
            || !validator.Validate(planJson).IsValid || string.IsNullOrWhiteSpace(sessionId)) throw Invalid();
        using var plan = JsonDocument.Parse(planJson);
        var declared = plan.RootElement.GetProperty("resources").EnumerateArray().ToArray();
        if (sources.Count != declared.Length || sources.Select(s => s.ResourceId).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw Invalid();
        var lease = new BridgeResourceLease(sessionId, plan.RootElement.GetProperty("plan_digest").GetString()!);
        try
        {
            foreach (var resource in declared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = resource.GetProperty("resource_id").GetString()!;
                var source = sources.SingleOrDefault(s => s.ResourceId == id);
                if (source is null || source.Purpose != resource.GetProperty("role").GetString()
                    || source.MediaType != resource.GetProperty("media_type").GetString()
                    || !Allowed(source.Purpose, source.MediaType)
                    || !resource.TryGetProperty("byte_length", out var length)) throw Invalid();
                var path = paths.ResolveOwnedPath(source.OwnedRelativePath);
                ValidateLocalPath(path);
                FileStream? stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                try
                {
                    // Resolve the opened handle as well as the name to reject a junction swap during open.
                    VerifyOpenedPath(stream.SafeFileHandle, path);
                    ValidateLocalPath(path);
                    if (stream.Length != length.GetInt64()) throw Invalid();
                    var header = new byte[8];
                    await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                    if (!MatchesMedia(header, source.MediaType)) throw Invalid();
                    stream.Position = 0;
                    var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(sha, resource.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw Invalid();
                    stream.Position = 0;
                    var handle = BridgeAdapterCryptography.RandomToken();
                    lease._resources.Add(handle, new(new(id, handle, sha.ToLowerInvariant(), source.Purpose, true, stream.Length), stream));
                    stream = null;
                }
                finally { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            }
            return lease;
        }
        catch (OperationCanceledException)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw Invalid();
        }
    }

    public async Task<byte[]> ReadChunkAsync(string sessionId, string planDigest, string handle,
        long offset, int count, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || sessionId != _session || !string.Equals(planDigest, _digest, StringComparison.OrdinalIgnoreCase)
                || !_resources.TryGetValue(handle, out var resource) || offset < 0 || count is < 1 or > ChunkByteLimit
                || offset >= resource.Stream.Length || count > resource.Stream.Length - offset) throw Invalid();
            var bytes = new byte[count];
            resource.Stream.Position = offset;
            await resource.Stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var resource in _resources.Values) await resource.Stream.DisposeAsync().ConfigureAwait(false);
            _resources.Clear();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var resource in _resources.Values) resource.Stream.Dispose();
            _resources.Clear();
        }
        finally { _gate.Release(); }
    }

    private static bool Allowed(string purpose, string media) => purpose switch
    {
        "template_copy" => media == "image/vnd.adobe.photoshop",
        "deterministic_size_table" => media == "image/png",
        "high_resolution_render_source" => media is "image/jpeg" or "image/png",
        _ => false
    };

    private static bool MatchesMedia(ReadOnlySpan<byte> bytes, string media) => media switch
    {
        "image/vnd.adobe.photoshop" => bytes.Length >= 6 && bytes[..6].SequenceEqual(new byte[] { 56, 66, 80, 83, 0, 1 }),
        "image/png" => bytes.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
        _ => false
    };

    private static void ValidateLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Length < 3 || path[1] != ':' || path[2..].Contains(':')) throw Invalid();
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
    }

    internal static void VerifyOpenedPath(SafeFileHandle handle, string expected)
    {
        var fullPath = Path.GetFullPath(expected);
        var actual = FinalPath(handle);
        if (string.Equals(actual, fullPath, StringComparison.OrdinalIgnoreCase)) return;

        // A packaged launcher can virtualize LocalAppData without a reparse point.
        // Resolve the approved parent through an OS handle, keeping it non-renamable
        // while checking the exact leaf. Never accept a different file or a link.
        ValidateLocalPath(fullPath);
        var parent = Path.GetDirectoryName(fullPath) ?? throw Invalid();
        using var directory = CreateFileW(@"\\?\" + parent, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (directory.IsInvalid) throw Invalid();
        var resolvedParent = FinalPath(directory);
        ValidateLocalPath(resolvedParent);
        if (!string.Equals(actual, Path.Combine(resolvedParent, Path.GetFileName(fullPath)), StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        ValidateLocalPath(fullPath);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw Invalid();
        var actual = new string(buffer, 0, checked((int)length));
        if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal)) throw Invalid();
        return actual[4..];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    private static InvalidDataException Invalid() => new("BRIDGE_RESOURCE_BINDING_INVALID");
    private sealed record Resource(BridgeResourceBindingV1 Binding, FileStream Stream);
}
