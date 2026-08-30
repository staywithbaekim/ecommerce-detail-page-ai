using Desktop.Application.Ai;
using Desktop.Application.Files;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desktop.Infrastructure.Ai;

/// <summary>
/// Reads application-owned, pre-compressed analysis copies by opaque logical
/// reference. No source path is accepted from the AI contract and copies are
/// released after a request unless a caller explicitly retains them.
/// </summary>
public sealed class FileAiAnalysisCopySource : IAiAnalysisCopySource
{
    private readonly IPathPolicy _pathPolicy;
    private readonly long _maxBytes;

    public FileAiAnalysisCopySource(
        IPathPolicy pathPolicy,
        long maxBytes = 8 * 1024 * 1024)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        if (maxBytes is < 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        _maxBytes = maxBytes;
    }

    public async Task<AiAnalysisCopy> OpenAsync(
        string analysisCopyReference,
        CancellationToken cancellationToken)
    {
        ValidateReference(analysisCopyReference);
        var relativePath = Path.Combine("ai-analysis", "copies", analysisCopyReference + ".bin");
        var path = _pathPolicy.ResolveOwnedPath(relativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("AI 分析副本不存在。", analysisCopyReference);
        }

        if (info.Length is <= 0 or > long.MaxValue)
        {
            throw new InvalidDataException("AI 分析副本大小无效。");
        }

        if (info.Length > _maxBytes)
        {
            throw new InvalidDataException("AI 分析副本超过大小限制。");
        }

        var metadataPath = _pathPolicy.ResolveOwnedPath(Path.Combine("ai-analysis", "copies", analysisCopyReference + ".json"));
        if (!File.Exists(metadataPath)) throw new InvalidDataException("AI 分析副本元数据缺失。");
        AiAnalysisCopyMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<AiAnalysisCopyMetadata>(await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false))
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("AI 分析副本元数据损坏。", exception);
        }
        if (!string.Equals(metadata.Reference, analysisCopyReference, StringComparison.Ordinal)
            || metadata.ByteLength != info.Length
            || metadata.ByteLength <= 0
            || metadata.ByteLength > _maxBytes
            || !IsSupportedMime(metadata.MimeType)
            || string.IsNullOrWhiteSpace(metadata.CompressionProfileVersion)
            || metadata.Digest.Length != 71
            || !metadata.Digest.StartsWith("sha256:", StringComparison.Ordinal)
            || metadata.Digest.Skip(7).Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            || metadata.EffectiveLeaseIds.Count == 0
            || metadata.EffectiveLeaseIds.Any(lease =>
                string.IsNullOrWhiteSpace(lease)
                || lease.Length > 128
                || lease.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))))
            throw new InvalidDataException("AI 分析副本元数据不闭合。");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, metadata.Digest, StringComparison.Ordinal))
            throw new InvalidDataException("AI 分析副本摘要不匹配。");
        var inspected = WicAnalysisImageCodec.Inspect(bytes);
        if (!string.Equals(inspected.MimeType, metadata.MimeType, StringComparison.Ordinal)
            || inspected.PixelWidth != metadata.PixelWidth
            || inspected.PixelHeight != metadata.PixelHeight)
            throw new InvalidDataException("AI 分析副本内容与元数据不闭合。");
        if (string.Equals(metadata.CompressionProfileVersion, AiAnalysisCopyProfiles.WicBoundedCandidateV1, StringComparison.Ordinal)
            && (metadata.MimeType is not ("image/jpeg" or "image/png")
                || Math.Max(metadata.PixelWidth, metadata.PixelHeight) > 1600))
            throw new InvalidDataException("AI 分析副本压缩配置不闭合。");
        return new AiAnalysisCopy(metadata.Reference, metadata.MimeType, metadata.PixelWidth, metadata.PixelHeight, bytes,
            metadata.Digest, metadata.ByteLength, metadata.CompressionProfileVersion, metadata.LeaseId);
    }

    public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(analysisCopyReference);
        var relativePath = Path.Combine("ai-analysis", "copies", analysisCopyReference + ".bin");
        var path = _pathPolicy.ResolveOwnedPath(relativePath);
        if (File.Exists(path)) File.Delete(path);
        var metadata = _pathPolicy.ResolveOwnedPath(Path.Combine("ai-analysis", "copies", analysisCopyReference + ".json"));
        if (File.Exists(metadata)) File.Delete(metadata);

        return Task.CompletedTask;
    }

    private static void ValidateReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)
            || reference.Length > 128
            || reference.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new InvalidDataException("AI 分析副本引用无效。");
        }
    }

    private static bool IsSupportedMime(string value) => value is "image/jpeg" or "image/png" or "image/tiff";
}
