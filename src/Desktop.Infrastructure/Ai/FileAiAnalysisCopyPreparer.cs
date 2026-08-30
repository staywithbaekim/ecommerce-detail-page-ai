using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Ai;

/// <summary>
/// Creates application-owned analysis copies from INPUT-01's registered
/// relative source. Lease ownership is durable in the sidecar so a new
/// process can observe and release active references.
/// </summary>
public sealed class FileAiAnalysisCopyPreparer : IAiAnalysisCopyPreparer, IAiAnalysisCopyLeaseManager, IDisposable
{
    private readonly IPathPolicy _pathPolicy;
    private readonly IFileStore _files;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        if (!string.Equals(
                request.CompressionProfileVersion,
                AiAnalysisCopyProfiles.BytePreservingProvisionalV1,
                StringComparison.Ordinal))
            throw new InvalidDataException("AI_COPY_COMPRESSION_PROFILE_UNSUPPORTED");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sourcePath = ResolveSourcePath(request.InputRoot, request.SourceAsset.SourceRelativePath);
            var before = await ReadSourceAsync(sourcePath, request.SourceAsset.Sha256, cancellationToken).ConfigureAwait(false);
            if (before.Bytes.Length <= 0 || before.Bytes.Length > _maxBytes)
                throw new InvalidDataException("AI_COPY_SOURCE_TOO_LARGE");

            var relative = $"ai-analysis/copies/{request.AnalysisCopyReference}.bin";
            var metadataRelative = $"ai-analysis/copies/{request.AnalysisCopyReference}.json";
            var existing = await ReadMetadataAsync(metadataRelative, cancellationToken).ConfigureAwait(false);
            var existingBytes = await _files.ReadAsync(relative, cancellationToken).ConfigureAwait(false);
            var copyDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(before.Bytes)).ToLowerInvariant();
            if (existing is not null || existingBytes is not null)
            {
                if (existing is null || existingBytes is null
                    || !string.Equals(existing.Reference, request.AnalysisCopyReference, StringComparison.Ordinal)
                    || !string.Equals(existing.Digest, copyDigest, StringComparison.Ordinal)
                    || existing.ByteLength != before.Bytes.Length
                    || !string.Equals(existing.MimeType, request.SourceAsset.MimeType, StringComparison.Ordinal)
                    || existing.PixelWidth != request.SourceAsset.PixelWidth
                    || existing.PixelHeight != request.SourceAsset.PixelHeight
                    || !string.Equals(existing.CompressionProfileVersion, request.CompressionProfileVersion, StringComparison.Ordinal)
                    || !string.Equals("sha256:" + Convert.ToHexString(SHA256.HashData(existingBytes)).ToLowerInvariant(), existing.Digest, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("AI_COPY_REFERENCE_CONFLICT");
                }

                var leaseId = NewLeaseId();
                var leases = existing.EffectiveLeaseIds.ToHashSet(StringComparer.Ordinal);
                leases.Add(leaseId);
                var persisted = existing with { LeaseId = leaseId, LeaseIds = leases.Order(StringComparer.Ordinal).ToArray() };
                try
                {
                    await _files.WriteAtomicallyAsync(metadataRelative, JsonSerializer.SerializeToUtf8Bytes(persisted), cancellationToken).ConfigureAwait(false);
                    await VerifySourceUnchangedAsync(sourcePath, before.Digest, request.SourceAsset.Sha256, cancellationToken).ConfigureAwait(false);
                    return persisted;
                }
                catch
                {
                    await _files.WriteAtomicallyAsync(metadataRelative, JsonSerializer.SerializeToUtf8Bytes(existing), CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            var generatedLeaseId = NewLeaseId();
            var generated = new AiAnalysisCopyMetadata(
                request.AnalysisCopyReference,
                copyDigest,
                before.Bytes.Length,
                request.SourceAsset.MimeType,
                request.SourceAsset.PixelWidth,
                request.SourceAsset.PixelHeight,
                request.CompressionProfileVersion,
                generatedLeaseId,
                [generatedLeaseId]);
            var wroteCopy = false;
            var wroteMetadata = false;
            try
            {
                await _files.WriteAtomicallyAsync(relative, before.Bytes, cancellationToken).ConfigureAwait(false);
                wroteCopy = true;
                await _files.WriteAtomicallyAsync(metadataRelative, JsonSerializer.SerializeToUtf8Bytes(generated), cancellationToken).ConfigureAwait(false);
                wroteMetadata = true;
                await VerifySourceUnchangedAsync(sourcePath, before.Digest, request.SourceAsset.Sha256, cancellationToken).ConfigureAwait(false);
                return generated;
            }
            catch
            {
                if (wroteMetadata) _ = await _files.DeleteAsync(metadataRelative, CancellationToken.None).ConfigureAwait(false);
                if (wroteCopy) _ = await _files.DeleteAsync(relative, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task AcquireAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(analysisCopyReference);
        ValidateLease(leaseId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataRelative = $"ai-analysis/copies/{analysisCopyReference}.json";
            var metadata = await ReadMetadataAsync(metadataRelative, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI_COPY_METADATA_MISSING");
            var leases = metadata.EffectiveLeaseIds.ToHashSet(StringComparer.Ordinal);
            if (leases.Add(leaseId))
            {
                await _files.WriteAtomicallyAsync(
                    metadataRelative,
                    JsonSerializer.SerializeToUtf8Bytes(metadata with { LeaseIds = leases.Order(StringComparer.Ordinal).ToArray() }),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseAsync(string analysisCopyReference, string leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(analysisCopyReference);
        ValidateLease(leaseId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relative = $"ai-analysis/copies/{analysisCopyReference}.bin";
            var metadataRelative = $"ai-analysis/copies/{analysisCopyReference}.json";
            var metadata = await ReadMetadataAsync(metadataRelative, cancellationToken).ConfigureAwait(false);
            if (metadata is null) return;
            var leases = metadata.EffectiveLeaseIds.ToHashSet(StringComparer.Ordinal);
            if (!leases.Remove(leaseId)) return;
            if (leases.Count > 0)
            {
                await _files.WriteAtomicallyAsync(
                    metadataRelative,
                    JsonSerializer.SerializeToUtf8Bytes(metadata with { LeaseIds = leases.Order(StringComparer.Ordinal).ToArray() }),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            _ = await _files.DeleteAsync(relative, cancellationToken).ConfigureAwait(false);
            _ = await _files.DeleteAsync(metadataRelative, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<AiAnalysisCopyMetadata?> ReadMetadataAsync(string relativePath, CancellationToken cancellationToken)
    {
        var bytes = await _files.ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<AiAnalysisCopyMetadata>(bytes)
                ?? throw new JsonException();
            ValidateMetadata(metadata);
            return metadata;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidDataException)
        {
            throw new InvalidDataException("AI_COPY_METADATA_CORRUPT", exception);
        }
    }

    private static async Task VerifySourceUnchangedAsync(string path, string beforeDigest, string expectedDigest, CancellationToken cancellationToken)
    {
        var after = await ReadSourceAsync(path, expectedDigest, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(beforeDigest, after.Digest, StringComparison.Ordinal))
            throw new IOException("AI_COPY_SOURCE_CHANGED");
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
        if (string.IsNullOrWhiteSpace(inputRoot) || string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative) || relative.Contains('\0'))
            throw new InvalidDataException("AI_COPY_SOURCE_REFERENCE_INVALID");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("AI_COPY_SOURCE_ROOT_INVALID");
        var normalized = relative.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("AI_COPY_SOURCE_REFERENCE_INVALID");
        var candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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

    private static string NewLeaseId() => "lease-" + Guid.NewGuid().ToString("N");

    private static void ValidateMetadata(AiAnalysisCopyMetadata metadata)
    {
        ValidateReference(metadata.Reference);
        if (metadata.ByteLength <= 0 || metadata.ByteLength > 64 * 1024 * 1024
            || metadata.PixelWidth <= 0 || metadata.PixelHeight <= 0
            || metadata.Digest is null || metadata.Digest.Length != 71
            || !metadata.Digest.StartsWith("sha256:", StringComparison.Ordinal)
            || metadata.Digest.Skip(7).Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            || string.IsNullOrWhiteSpace(metadata.CompressionProfileVersion))
            throw new InvalidDataException("AI_COPY_METADATA_INVALID");
        if (metadata.EffectiveLeaseIds.Count == 0 || metadata.EffectiveLeaseIds.Any(lease =>
                string.IsNullOrWhiteSpace(lease) || lease.Length > 128
                || lease.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))))
            throw new InvalidDataException("AI_COPY_LEASE_INVALID");
    }

    public void Dispose() => _gate.Dispose();
}
