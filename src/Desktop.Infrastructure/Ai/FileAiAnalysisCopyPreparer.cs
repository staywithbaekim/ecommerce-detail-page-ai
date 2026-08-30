using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Ai;

/// <summary>
/// Creates application-owned analysis copies from the INPUT-01 registered
/// relative source. The source is opened read-only and is never deleted or
/// modified. The current encoder keeps already-compressed image bytes intact
/// while applying a bounded, versioned analysis-copy profile; a future encoder
/// can replace this implementation without changing the AI contract.
/// </summary>
public sealed class FileAiAnalysisCopyPreparer : IAiAnalysisCopyPreparer, IAiAnalysisCopyLeaseManager
{
    private readonly IPathPolicy _pathPolicy;
    private readonly IFileStore _files;
    private readonly long _maxBytes;
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public FileAiAnalysisCopyPreparer(IPathPolicy pathPolicy, IFileStore files, long maxBytes = 8 * 1024 * 1024)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        if (maxBytes is < 1024 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    public async Task<AiAnalysisCopyMetadata> PrepareAsync(
        AiAnalysisCopyPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateReference(request.AnalysisCopyReference);
        ArgumentNullException.ThrowIfNull(request.SourceAsset);
        if (string.IsNullOrWhiteSpace(request.CompressionProfileVersion)
            || request.CompressionProfileVersion.Length > 128)
            throw new InvalidDataException("AI_COPY_COMPRESSION_PROFILE_INVALID");

        var sourcePath = ResolveSourcePath(request.InputRoot, request.SourceAsset.SourceRelativePath);
        var before = await ReadSourceAsync(sourcePath, request.SourceAsset.Sha256, cancellationToken).ConfigureAwait(false);
        if (before.Bytes.Length > _maxBytes)
            throw new InvalidDataException("AI_COPY_SOURCE_TOO_LARGE");

        // The copy is deliberately derived from the bytes just read and is
        // hashed independently; source and copy digests are never conflated.
        var copyBytes = before.Bytes;
        var copyDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(copyBytes)).ToLowerInvariant();
        var relative = $"ai-analysis/copies/{request.AnalysisCopyReference}.bin";
        var metadataRelative = $"ai-analysis/copies/{request.AnalysisCopyReference}.json";
        await _files.WriteAtomicallyAsync(relative, copyBytes, cancellationToken).ConfigureAwait(false);
        var leaseId = "lease-" + Guid.NewGuid().ToString("N");
        var metadata = new AiAnalysisCopyMetadata(
            request.AnalysisCopyReference,
            copyDigest,
            copyBytes.Length,
            request.SourceAsset.MimeType,
            request.SourceAsset.PixelWidth,
            request.SourceAsset.PixelHeight,
            request.CompressionProfileVersion,
            leaseId);
        await _files.WriteAtomicallyAsync(metadataRelative, JsonSerializer.SerializeToUtf8Bytes(metadata), cancellationToken).ConfigureAwait(false);

        var after = await ReadSourceAsync(sourcePath, request.SourceAsset.Sha256, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(before.Digest, after.Digest, StringComparison.Ordinal))
            throw new IOException("AI_COPY_SOURCE_CHANGED");
        lock (_gate) _leases[request.AnalysisCopyReference] = 1;
        return metadata;
    }

    public Task AcquireAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(analysisCopyReference);
        ValidateLease(leaseId);
        lock (_gate) _leases[analysisCopyReference] = _leases.GetValueOrDefault(analysisCopyReference) + 1;
        return Task.CompletedTask;
    }

    public async Task ReleaseAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(analysisCopyReference);
        ValidateLease(leaseId);
        var delete = false;
        lock (_gate)
        {
            if (_leases.TryGetValue(analysisCopyReference, out var count))
            {
                count--;
                if (count <= 0) { _leases.Remove(analysisCopyReference); delete = true; }
                else _leases[analysisCopyReference] = count;
            }
        }
        if (delete)
        {
            await _files.DeleteAsync($"ai-analysis/copies/{analysisCopyReference}.bin", cancellationToken).ConfigureAwait(false);
            await _files.DeleteAsync($"ai-analysis/copies/{analysisCopyReference}.json", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(byte[] Bytes, string Digest)> ReadSourceAsync(string path, string expectedDigest, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        var bytes = memory.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AI_COPY_SOURCE_DIGEST_MISMATCH");
        return (bytes, digest);
    }

    private static string ResolveSourcePath(string inputRoot, string relative)
    {
        if (string.IsNullOrWhiteSpace(inputRoot) || Path.IsPathRooted(relative) || relative.Contains('\0'))
            throw new InvalidDataException("AI_COPY_SOURCE_REFERENCE_INVALID");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("AI_COPY_SOURCE_ROOT_INVALID");
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.Replace('\\', '/').Split('/').Length is < 1 or > 2
            || relative.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("AI_COPY_SOURCE_REFERENCE_INVALID");
        if (!File.Exists(candidate) || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("AI_COPY_SOURCE_NOT_FOUND");
        return candidate;
    }

    private static void ValidateReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidDataException("AI_COPY_REFERENCE_INVALID");
    }

    private static void ValidateLease(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidDataException("AI_COPY_LEASE_INVALID");
    }
}
