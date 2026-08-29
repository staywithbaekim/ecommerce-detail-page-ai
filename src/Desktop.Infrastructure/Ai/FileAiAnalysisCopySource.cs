using Desktop.Application.Ai;
using Desktop.Application.Files;

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

    public FileAiAnalysisCopySource(IPathPolicy pathPolicy, long maxBytes = 8 * 1024 * 1024)
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

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        // Dimensions are supplied by the versioned request contract. The
        // source intentionally does not inspect or rewrite customer images.
        return new AiAnalysisCopy(analysisCopyReference, "application/octet-stream", 0, 0, bytes);
    }

    public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(analysisCopyReference);
        var relativePath = Path.Combine("ai-analysis", "copies", analysisCopyReference + ".bin");
        var path = _pathPolicy.ResolveOwnedPath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

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
}
