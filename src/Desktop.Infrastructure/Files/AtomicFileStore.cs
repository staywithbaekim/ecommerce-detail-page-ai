using System.Security.Cryptography;
using Desktop.Application.Files;

namespace Desktop.Infrastructure.Files;

public sealed class AtomicFileStore(IPathPolicy pathPolicy) : IFileStore
{
    public async Task WriteAtomicallyAsync(
        string relativePath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var targetPath = pathPolicy.ResolveOwnedPath(relativePath);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("PATH_PARENT_REQUIRED");
        Directory.CreateDirectory(targetDirectory);

        var temporaryPath = Path.Combine(targetDirectory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var expectedHash = Convert.ToHexString(SHA256.HashData(content.Span));
            var actualHash = await ComputeFileSha256Async(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new IOException("ATOMIC_WRITE_HASH_MISMATCH");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = pathPolicy.ResolveOwnedPath(relativePath);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken)
    {
        var path = pathPolicy.ResolveOwnedPath(relativePath);
        return File.Exists(path)
            ? await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = pathPolicy.ResolveOwnedPath(relativePath);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
